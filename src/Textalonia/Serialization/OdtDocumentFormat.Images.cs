using System.Collections.Immutable;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class OdtDocumentFormat
{
    private static string? ImageExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tif",
        "image/webp" => ".webp",
        _ => null
    };

    private static bool SafeImagePartName(string path) => SafePartName(path) &&
        !path.Any(char.IsControl) && !path.Contains('?') && !path.Contains('#') && !path.Contains('%') &&
        path.Split('/').All(segment => segment.Length > 0);

    private sealed partial class ReadContext
    {
        private readonly Func<string, byte[]> _part;
        private readonly IReadOnlyDictionary<string, string> _mediaTypes;
        private readonly Dictionary<string, string> _imageIds = new(StringComparer.Ordinal);
        private readonly ImmutableDictionary<string, DocumentResource>.Builder _resources =
            ImmutableDictionary.CreateBuilder<string, DocumentResource>(StringComparer.Ordinal);
        private long _imageBytes;

        public ImmutableDictionary<string, DocumentResource> Resources => _resources.ToImmutable();
        public IReadOnlySet<string> ImportedParts => _imageIds.Keys.ToHashSet(StringComparer.Ordinal);

        private void ReadFrame(XElement frame, TextStyle inherited, List<RichRun> runs)
        {
            ReportAttributes(frame, Draw + "name", Text + "anchor-type", Svg + "width", Svg + "height");
            var alt = (string?)frame.Element(Svg + "title") ?? (string?)frame.Element(Svg + "desc") ??
                (string?)frame.Attribute(Draw + "name") ?? "";
            if (frame.Element(Svg + "title") is not null && frame.Element(Svg + "desc") is not null)
                Loss("image-description", "Separate ODT image title and description",
                    "The title was retained as alternative text; the description was omitted.", frame);
            if (alt.Length > 16_384) throw new FormatException("ODT image alternative text exceeds the limit.");
            var image = frame.Element(Draw + "image");
            if (image is null)
            {
                Loss(frame.Elements().Any(element => element.Name == Draw + "object" || element.Name == Draw + "object-ole")
                    ? "object" : "frame", "Unsupported ODT frame or embedded object",
                    "Only the frame alternative text was retained; no embedded object was activated.", frame);
                runs.Add(new RichRun(alt, inherited));
                return;
            }
            ReportAttributes(image, XLink + "href", XLink + "type", XLink + "show", XLink + "actuate");
            if (frame.Elements(Draw + "image").Skip(1).Any() || frame.Elements().Any(element =>
                    element.Name != Draw + "image" && element.Name != Svg + "title" && element.Name != Svg + "desc"))
                Loss("image-frame", "Additional frame content", "Only the first supported raster image was retained.", frame);
            var anchor = (string?)frame.Attribute(Text + "anchor-type");
            if (anchor is not (null or "as-char"))
                Loss("image-anchor", "ODT image anchoring and wrapping", "Image bytes and inline dimensions were retained in text flow.", frame);
            var href = (string?)image.Attribute(XLink + "href");
            if (href?.StartsWith("./", StringComparison.Ordinal) == true) href = href[2..];
            if (href is null || !SafeImagePartName(href) || !_mediaTypes.TryGetValue(href, out var mediaType) ||
                ImageExtension(mediaType) is null)
            {
                Loss("image", "External, missing or unsupported ODT image", "Only alternative text was retained; no external resource was fetched.", image);
                runs.Add(new RichRun(alt, inherited));
                return;
            }
            if (!_imageIds.TryGetValue(href, out var id))
            {
                var bytes = _part(href);
                if (bytes.Length > DocumentResource.MaximumEmbeddedBytes ||
                    _imageBytes + bytes.Length > DocumentResource.MaximumDocumentEmbeddedBytes)
                    throw new FormatException("ODT embedded images exceed the document resource limit.");
                _imageBytes += bytes.Length;
                id = "odt-image-" + (_imageIds.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _resources.Add(id, new DocumentResource
                { MediaType = mediaType.ToLowerInvariant(), Data = ImmutableArray.CreateRange(bytes) });
                _imageIds.Add(href, id);
            }
            var width = FrameDimension(frame, Svg + "width");
            var height = FrameDimension(frame, Svg + "height");
            if (frame.Attribute(Svg + "width") is null || frame.Attribute(Svg + "height") is null)
                Loss("image-size", "ODT image dimensions", "A 32-pixel fallback was used for missing dimensions.", frame);
            runs.Add(new RichRun(new InlineDescriptor
            { Payload = new ImageInlinePayload(id), AltText = alt, Width = width, Height = height }, inherited));
        }

        private static double FrameDimension(XElement frame, XName name)
        {
            var value = Measure((string?)frame.Attribute(name), 32);
            if (!double.IsFinite(value) || value is <= 0 or > 10_000)
                throw new FormatException("Invalid ODT image dimensions.");
            return value;
        }
    }

    private sealed partial class WriteContext
    {
        private bool TryWriteImage(XElement parent, InlineDescriptor inline, ImageInlinePayload image)
        {
            if (!document.Resources.TryGetValue(image.ResourceId, out var resource) ||
                resource.Kind != DocumentResourceKind.Embedded || ImageExtension(resource.MediaType) is not { } extension)
            {
                ConversionDiagnostics.Report("odt.image", "External, unavailable or unsupported raster image",
                    "Only alternative text was exported; no external resource was fetched.", inline.Id);
                return false;
            }
            if (!_images.TryGetValue(image.ResourceId, out var part))
            {
                var path = "Pictures/image" + (_images.Count + 1).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + extension;
                part = (path, resource.MediaType.ToLowerInvariant(), resource.Data.ToArray());
                _images.Add(image.ResourceId, part);
            }
            var frame = new XElement(Draw + "frame", new XAttribute(Draw + "name", "Image" + inline.Id.ToString("N")),
                new XAttribute(Text + "anchor-type", "as-char"),
                new XAttribute(Svg + "width", Inches(inline.Width)), new XAttribute(Svg + "height", Inches(inline.Height)),
                new XElement(Draw + "image", new XAttribute(XLink + "href", part.Path),
                    new XAttribute(XLink + "type", "simple"), new XAttribute(XLink + "show", "embed"),
                    new XAttribute(XLink + "actuate", "onLoad")));
            if (inline.AltText.Length > 0) frame.Add(new XElement(Svg + "title", inline.AltText));
            parent.Add(frame);
            return true;
        }
    }
}
