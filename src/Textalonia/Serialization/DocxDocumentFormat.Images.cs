using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class DocxDocumentFormat
{
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";
    private static readonly XNamespace Svg = "http://schemas.microsoft.com/office/drawing/2016/SVG/main";

    private static ImagePlacement? ReadImagePlacement(XElement drawing)
    {
        var container = drawing.Element(Wp + "anchor") ?? drawing.Element(Wp + "inline");
        var crop = drawing.Descendants(A + "srcRect").FirstOrDefault();
        var transform = drawing.Descendants(A + "xfrm").FirstOrDefault();
        if (transform?.Attributes().Any(a => a.Name.LocalName is "flipH" or "flipV" && a.Value is not ("0" or "false")) == true)
            Loss("image-mirroring", "Mirrored image", "Image crop and rotation retained without mirroring.", transform);
        if (container?.Attribute(Tx + "placement") is { } data)
        {
            var placement = JsonSerializer.Deserialize<ImagePlacement>(data.Value, JsonDocumentFormat.Options) ?? throw new FormatException("Invalid image placement.");
            placement.Validate();
            return placement;
        }
        var anchor = container?.Name == Wp + "anchor";
        var rotation = Dimension(transform, "rot") / 60000;
        var lockAspect = drawing.Descendants(A + "graphicFrameLocks").FirstOrDefault()?.Attribute("noChangeAspect")?.Value is not ("0" or "false");
        if (!anchor && crop is null && rotation == 0 && lockAspect) return null;
        var h = container?.Element(Wp + "positionH"); var v = container?.Element(Wp + "positionV");
        var page = (string?)v?.Attribute("relativeFrom") == "page";
        if (anchor && ((string?)h?.Attribute("relativeFrom") != (page ? "page" : "column") ||
            (string?)v?.Attribute("relativeFrom") != (page ? "page" : "paragraph") || h?.Element(Wp + "align") is not null || v?.Element(Wp + "align") is not null))
            Loss("image-position-reference", "Image alignment or unsupported position reference", "Offsets retained relative to the page or paragraph column; alignment defaults to zero offset.", container);
        var distances = new[] { "distT", "distB", "distL", "distR" }.Select(name => Dimension(container, name) / 9525).ToArray();
        if (distances.Distinct().Count() > 1) Loss("image-wrap-distances", "Different image wrap distances", "Largest distance applied to each edge.", container);
        var polygon = container?.Element(Wp + "wrapTight")?.Element(Wp + "wrapPolygon") ?? container?.Element(Wp + "wrapThrough")?.Element(Wp + "wrapPolygon");
        var points = polygon?.Elements().Select(e => new ImageContourPoint(Dimension(e, "x") / 21600, Dimension(e, "y") / 21600)).ToImmutableArray() ?? [];
        if (points.Length > 1 && points[0] == points[^1]) points = points.RemoveAt(points.Length - 1);
        double Offset(XElement? position) => double.TryParse(position?.Element(Wp + "posOffset")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value / 9525 : 0;
        var result = new ImagePlacement
        {
            Anchor = !anchor ? ImageAnchorKind.Inline : page ? ImageAnchorKind.Page : ImageAnchorKind.Paragraph,
            Wrap = container?.Element(Wp + "wrapNone") is not null ? ((string?)container.Attribute("behindDoc") is "1" or "true" ? ImageWrapKind.BehindText : ImageWrapKind.InFrontOfText) :
                container?.Element(Wp + "wrapTopAndBottom") is not null ? ImageWrapKind.TopBottom : polygon is not null ? ImageWrapKind.Contour : ImageWrapKind.Square,
            X = Offset(h), Y = Offset(v), Distance = anchor ? distances.Max() : 4,
            Rotation = rotation, LockAspectRatio = lockAspect, Contour = points,
            Crop = new ImageCrop { Left = Dimension(crop, "l") / 100000, Top = Dimension(crop, "t") / 100000, Right = Dimension(crop, "r") / 100000, Bottom = Dimension(crop, "b") / 100000 }
        };
        result.Validate(); return result;
    }

    private static XElement WriteImageDrawing(InlineDescriptor inline, string relationshipId, int id, string? previewId = null, bool svg = false)
    {
        var placement = inline.Placement;
        var floating = placement is { Anchor: not ImageAnchorKind.Inline };
        var cx = (long)Math.Round(inline.Width * 9525); var cy = (long)Math.Round(inline.Height * 9525);
        var container = new XElement(Wp + (floating ? "anchor" : "inline"),
            new[] { "distT", "distB", "distL", "distR" }.Select(name => new XAttribute(name, floating ? (long)Math.Round(placement!.Distance * 9525) : 0)),
            placement is null ? null : new XAttribute(Tx + "placement", JsonSerializer.Serialize(placement, JsonDocumentFormat.Options)));
        if (floating)
        {
            container.Add(new XAttribute("simplePos", 0), new XAttribute("relativeHeight", id), new XAttribute("behindDoc", placement!.Wrap == ImageWrapKind.BehindText ? 1 : 0),
                new XAttribute("locked", 0), new XAttribute("layoutInCell", 1), new XAttribute("allowOverlap", 1),
                new XElement(Wp + "simplePos", new XAttribute("x", 0), new XAttribute("y", 0)),
                new XElement(Wp + "positionH", new XAttribute("relativeFrom", placement.Anchor == ImageAnchorKind.Page ? "page" : "column"), new XElement(Wp + "posOffset", (long)Math.Round(placement.X * 9525))),
                new XElement(Wp + "positionV", new XAttribute("relativeFrom", placement.Anchor == ImageAnchorKind.Page ? "page" : "paragraph"), new XElement(Wp + "posOffset", (long)Math.Round(placement.Y * 9525))));
        }
        container.Add(new XElement(Wp + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)));
        if (floating)
        {
            var wrap = placement!.Wrap switch
            {
                ImageWrapKind.BehindText or ImageWrapKind.InFrontOfText => new XElement(Wp + "wrapNone"),
                ImageWrapKind.TopBottom => new XElement(Wp + "wrapTopAndBottom"),
                ImageWrapKind.Contour when placement.Contour.Length >= 3 => new XElement(Wp + "wrapTight", new XAttribute("wrapText", "bothSides"),
                    new XElement(Wp + "wrapPolygon", new XAttribute("edited", 1), placement.Contour.Append(placement.Contour[0]).Select((point, index) =>
                        new XElement(Wp + (index == 0 ? "start" : "lineTo"), new XAttribute("x", Math.Round(point.X * 21600)), new XAttribute("y", Math.Round(point.Y * 21600)))))),
                _ => new XElement(Wp + "wrapSquare", new XAttribute("wrapText", "bothSides"))
            };
            container.Add(wrap);
        }
        var blip = new XElement(A + "blip", new XAttribute(R + "embed", previewId ?? relationshipId));
        if (previewId is not null)
            blip.Add(new XElement(A + "extLst", new XElement(A + "ext", new XAttribute("uri", svg ? "{96DAC541-7B7A-43D3-8B79-37D633B846F1}" : "urn:textalonia:image-original"),
                new XElement(svg ? Svg + "svgBlip" : Tx + "original", new XAttribute(R + "embed", relationshipId)))));
        var crop = placement?.Crop;
        container.Add(new XElement(Wp + "docPr", new XAttribute("id", id), new XAttribute("name", "Image " + id), new XAttribute("descr", inline.AltText)),
            new XElement(Wp + "cNvGraphicFramePr", new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", placement?.LockAspectRatio == false ? 0 : 1))),
            new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", Pic.NamespaceName),
                new XElement(Pic + "pic", new XElement(Pic + "nvPicPr", new XElement(Pic + "cNvPr", new XAttribute("id", id), new XAttribute("name", "Image " + id)), new XElement(Pic + "cNvPicPr")),
                    new XElement(Pic + "blipFill", blip,
                        crop is null ? null : new XElement(A + "srcRect", new XAttribute("l", Math.Round(crop.Left * 100000)), new XAttribute("t", Math.Round(crop.Top * 100000)), new XAttribute("r", Math.Round(crop.Right * 100000)), new XAttribute("b", Math.Round(crop.Bottom * 100000))),
                        new XElement(A + "stretch", new XElement(A + "fillRect"))),
                    new XElement(Pic + "spPr", new XElement(A + "xfrm", placement is null ? null : new XAttribute("rot", Math.Round(placement.Rotation * 60000)),
                        new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                        new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")))))));
        return new XElement(W + "drawing", container);
    }

    private static XElement WriteOleObject(InlineDescriptor inline, OleInlinePayload ole, string packageId, string previewId, int id)
    {
        var shapeId = "_x0000_i" + (1024 + id);
        return new XElement(W + "object", new XAttribute(W + "dxaOrig", Twips(inline.Width)), new XAttribute(W + "dyaOrig", Twips(inline.Height)),
            new XAttribute(Tx + "fileName", ole.FileName),
            inline.Placement is null ? null : new XAttribute(Tx + "placement", JsonSerializer.Serialize(inline.Placement, JsonDocumentFormat.Options)),
            new XElement(V + "shape", new XAttribute("id", shapeId), new XAttribute("coordsize", "21600,21600"), new XAttribute("path", "m0,0l21600,0,21600,21600,0,21600xe"), new XAttribute("alt", inline.AltText),
                new XAttribute("style", FormattableString.Invariant($"width:{inline.Width * .75}pt;height:{inline.Height * .75}pt")),
                new XElement(V + "imagedata", new XAttribute(R + "id", previewId), new XAttribute(O + "title", inline.AltText))),
            new XElement(O + "OLEObject", new XAttribute("Type", "Embed"), new XAttribute("ProgID", ole.ProgramId), new XAttribute("ShapeID", shapeId),
                new XAttribute("DrawAspect", "Content"), new XAttribute("ObjectID", "_" + id), new XAttribute(R + "id", packageId)));
    }

    private static XElement WriteWatermarkPicture(DocumentWatermark watermark, string? imageId, int id)
    {
        var shape = new XElement(V + "shape", new XAttribute("id", "TextaloniaWatermark" + id), new XAttribute(Tx + "watermark", 1),
            new XAttribute("style", FormattableString.Invariant($"position:absolute;width:{watermark.Width * .75}pt;height:{watermark.Height * .75}pt;rotation:{watermark.Rotation};z-index:-251654144;mso-position-horizontal:center;mso-position-horizontal-relative:page;mso-position-vertical:center;mso-position-vertical-relative:page")),
            new XAttribute("stroked", "f"), new XAttribute("fillcolor", watermark.Color),
            new XElement(V + "fill", new XAttribute("opacity", watermark.Opacity)));
        if (imageId is not null) shape.Add(new XAttribute("coordsize", "21600,21600"), new XAttribute("path", "m0,0l21600,0,21600,21600,0,21600xe"), new XElement(V + "imagedata", new XAttribute(R + "id", imageId)));
        else
        {
            shape.Add(new XAttribute("coordsize", "21600,21600"), new XAttribute("path", "m0,10800l21600,10800e"),
                new XElement(V + "path", new XAttribute("textpathok", "t")),
                new XElement(V + "textpath", new XAttribute("on", "t"), new XAttribute("fitshape", "t"), new XAttribute("string", watermark.Text!),
                    new XAttribute("style", FormattableString.Invariant($"font-family:{watermark.FontFamily};font-size:{watermark.FontSize * .75}pt"))));
        }
        return new XElement(W + "p", new XElement(W + "r", new XElement(W + "pict", shape)));
    }
}
