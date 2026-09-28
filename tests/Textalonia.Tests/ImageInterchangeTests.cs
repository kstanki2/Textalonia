using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class ImageInterchangeTests
{
    private static DocumentResource Png => new() { MediaType = "image/png", Data = [137, 80, 78, 71] };
    private static FlowDocument CreateDocument()
    {
        var image = new InlineDescriptor { Payload = new ImageInlinePayload("vector") { PreviewResourceId = "preview" }, Width = 120, Height = 80, AltText = "Diagram",
            Placement = new ImagePlacement { Anchor = ImageAnchorKind.Page, Wrap = ImageWrapKind.Contour, X = 48, Y = 96, Distance = 6, Rotation = 30,
                Crop = new ImageCrop { Left = .1, Top = .2, Right = .1 }, LockAspectRatio = false,
                Contour = [new(0, 0), new(1, 0), new(.5, 1)] } };
        var ole = new InlineDescriptor { Payload = new OleInlinePayload("package", "preview") { ProgramId = "Package", FileName = "report.bin" }, AltText = "Report", Width = 48, Height = 32 };
        var next = new Paragraph("Second section");
        return new FlowDocument([new Paragraph([new RichRun(image), new RichRun(ole)]), next])
        {
            Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("vector", new() { MediaType = "image/svg+xml", Data = ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")) })
                .Add("preview", Png).Add("package", new() { MediaType = "application/vnd.openxmlformats-officedocument.oleObject", Data = [1, 2, 3, 4, 5] }),
            Sections = [new DocumentSection { Watermark = new DocumentWatermark { Text = "DRAFT", Opacity = .25, Rotation = -45 } },
                new DocumentSection { StartParagraphId = next.Id, Watermark = new DocumentWatermark { ResourceId = "preview", Rotation = 0, Width = 200, Height = 100 } }]
        };
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xaml")]
    public async Task Native_formats_preserve_image_geometry_watermarks_and_ole_bytes(string format)
    {
        var document = CreateDocument();
        TextDocumentFormat codec = format == "json" ? DocumentFormats.Json : DocumentFormats.Xaml;
        var saved = codec.Serialize(document);
        var loaded = codec.Parse(saved);
        Assert.Equal(DocumentFormats.Json.Serialize(document), DocumentFormats.Json.Serialize(loaded));
        using var stream = new MemoryStream();
        var export = await codec.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        Assert.False(export.Report.HasLoss); stream.Position = 0;
        var import = await codec.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.False(import.Report.HasLoss);
        Assert.Equal(DocumentFormats.Json.Serialize(document), DocumentFormats.Json.Serialize(import.Document));
    }

    [Fact]
    public async Task Docx_preserves_images_watermarks_and_ole_relationships_with_visible_standard_markup()
    {
        var document = CreateDocument();
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Docx.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        Assert.False(saved.Report.HasLoss);
        stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
        {
            using var xml = zip.GetEntry("word/document.xml")!.Open(); var root = XDocument.Load(xml);
            XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
            XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            XNamespace o = "urn:schemas-microsoft-com:office:office";
            Assert.Single(root.Descendants(wp + "anchor"));
            Assert.Single(root.Descendants(wp + "wrapTight"));
            Assert.Equal("10000", (string?)root.Descendants(a + "srcRect").Single().Attribute("l"));
            Assert.Single(root.Descendants(o + "OLEObject"));
            Assert.Contains(zip.Entries, entry => entry.FullName.StartsWith("word/embeddings/"));
            Assert.Contains(zip.Entries, entry => entry.FullName.StartsWith("word/header"));
        }
        stream.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.False(loaded.Report.HasLoss);
        var runs = new DocumentIndex(loaded.Document).Paragraphs[0].Paragraph.Runs;
        var image = Assert.IsType<ImageInlinePayload>(runs[0].Inline!.Payload);
        Assert.Equal(((Paragraph)document.Blocks[0]).Runs[0].Inline!.Placement, runs[0].Inline!.Placement);
        Assert.Equal(document.Resources["vector"], loaded.Document.Resources[image.ResourceId]);
        Assert.Equal(Png, loaded.Document.Resources[image.PreviewResourceId!]);
        var ole = Assert.IsType<OleInlinePayload>(runs[1].Inline!.Payload);
        Assert.Equal("Package", ole.ProgramId); Assert.Equal("report.bin", ole.FileName);
        Assert.Equal(document.Resources["package"], loaded.Document.Resources[ole.ResourceId]);
        Assert.Equal(Png, loaded.Document.Resources[ole.PreviewResourceId]);
        Assert.Equal(document.Sections[0].Watermark, loaded.Document.Sections[0].Watermark);
        Assert.Equal(Png, loaded.Document.Resources[loaded.Document.Sections[1].Watermark!.ResourceId!]);
        Assert.True(loaded.Document.Sections[1].HeaderFooter.PrimaryHeader.LinkToPrevious);
    }

    [Theory]
    [InlineData(ImageWrapKind.Square)]
    [InlineData(ImageWrapKind.TopBottom)]
    [InlineData(ImageWrapKind.BehindText)]
    [InlineData(ImageWrapKind.InFrontOfText)]
    [InlineData(ImageWrapKind.Contour)]
    public async Task Standard_docx_drawing_geometry_imports_without_private_extensions(ImageWrapKind wrap)
    {
        var placement = new ImagePlacement { Anchor = ImageAnchorKind.Paragraph, Wrap = wrap, X = 40, Y = 20, Rotation = 90,
            Crop = new ImageCrop { Top = .25 }, Contour = wrap == ImageWrapKind.Contour ? [new(0, 0), new(1, 0), new(.5, 1)] : [] };
        var inline = new InlineDescriptor { Payload = new ImageInlinePayload("image"), Placement = placement };
        var document = new FlowDocument([new Paragraph([new RichRun(inline)])]) { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", Png) };
        using var stream = new MemoryStream(); await DocumentFormats.Docx.SaveAsync(document, stream);
        RewriteXml(stream, "word/document.xml", xml => xml.Descendants().Attributes().Where(attribute => attribute.Name.LocalName == "placement").Remove());
        stream.Position = 0; var loaded = await DocumentFormats.Docx.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Equal(placement, ((Paragraph)loaded.Document.Blocks[0]).Runs[0].Inline!.Placement);
    }

    [Theory]
    [InlineData("rtf")]
    [InlineData("html")]
    [InlineData("markdown")]
    public async Task Unsupported_formats_diagnose_geometry_watermarks_and_ole_and_strict_writes_nothing(string name)
    {
        IDocumentFormat format = name switch { "rtf" => DocumentFormats.Rtf, "html" => DocumentFormats.Html, _ => DocumentFormats.Markdown };
        using var tolerant = new MemoryStream(); var result = await format.SaveWithReportAsync(CreateDocument(), tolerant);
        foreach (var code in new[] { "conversion.image-placement", "conversion.watermark", "conversion.ole", "conversion.image-preview" })
            Assert.Contains(result.Report.Diagnostics, diagnostic => diagnostic.Code == code);
        using var strict = new MemoryStream();
        await Assert.ThrowsAsync<DocumentConversionException>(() => format.SaveWithReportAsync(CreateDocument(), strict, new() { Mode = ConversionMode.Strict }));
        Assert.Equal(0, strict.Length);
    }

    [Fact]
    public async Task External_ole_relationship_is_diagnosed_without_accessing_its_target()
    {
        using var stream = new MemoryStream(); await DocumentFormats.Docx.SaveAsync(CreateDocument(), stream);
        RewriteXml(stream, "word/_rels/document.xml.rels", xml =>
        {
            var relation = xml.Root!.Elements().Single(element => ((string?)element.Attribute("Type"))!.EndsWith("/oleObject"));
            relation.SetAttributeValue("TargetMode", "External"); relation.SetAttributeValue("Target", "file:///untrusted/package.bin");
        });
        stream.Position = 0; var result = await DocumentFormats.Docx.LoadWithReportAsync(stream);
        Assert.Contains(result.Report.Diagnostics, diagnostic => diagnostic.Code == "docx.oleObject-unavailable");
        Assert.Contains("Report", result.Document.PlainText);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/bmp")]
    [InlineData("image/tiff")]
    [InlineData("image/svg+xml")]
    [InlineData("image/emf")]
    [InlineData("image/wmf")]
    [InlineData("image/webp")]
    [InlineData("image/x-icon")]
    public async Task Original_and_supplied_preview_survive_header_part_relationships(string mediaType)
    {
        var inline = new InlineDescriptor { Payload = new ImageInlinePayload("original") { PreviewResourceId = "preview" }, AltText = "Original" };
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph([new RichRun(inline)])] };
        var document = new FlowDocument([new Paragraph("Body")])
        {
            Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("original", new DocumentResource { MediaType = mediaType, Data = [1, 2, 3] }).Add("preview", Png),
            Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header),
            Sections = [new DocumentSection { HeaderFooter = new HeaderFooterSettings { PrimaryHeader = new StoryReference { StoryId = header.Id, LinkToPrevious = false } } }]
        };
        using var stream = new MemoryStream(); await DocumentFormats.Docx.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        stream.Position = 0; var result = await DocumentFormats.Docx.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        var loaded = Assert.IsType<ImageInlinePayload>(((Paragraph)Assert.Single(result.Document.Stories.Values).Blocks[0]).Runs[0].Inline!.Payload);
        Assert.Equal(document.Resources["original"], result.Document.Resources[loaded.ResourceId]);
        Assert.Equal(Png, result.Document.Resources[loaded.PreviewResourceId!]);
    }

    [Fact]
    public async Task Positioned_ole_preview_retains_geometry_and_diagnoses_other_editor_display()
    {
        var document = CreateDocument(); var paragraph = (Paragraph)document.Blocks[0]; var run = paragraph.Runs[1];
        var placement = new ImagePlacement { Anchor = ImageAnchorKind.Page, X = 36, Rotation = 45, Crop = new ImageCrop { Right = .1 } };
        document = document with { Blocks = document.Blocks.SetItem(0, paragraph with { Runs = paragraph.Runs.SetItem(1, run with { Inline = run.Inline! with { Placement = placement } }) }) };
        using var stream = new MemoryStream(); var report = await DocumentFormats.Docx.SaveWithReportAsync(document, stream);
        Assert.Contains(report.Report.Diagnostics, diagnostic => diagnostic.Code == "docx.ole-placement");
        stream.Position = 0; var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        Assert.Equal(placement, ((Paragraph)loaded.Blocks[0]).Runs[1].Inline!.Placement);
        using var strict = new MemoryStream();
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Docx.SaveWithReportAsync(document, strict, new() { Mode = ConversionMode.Strict }));
        Assert.Equal(0, strict.Length);
    }

    [Theory]
    [InlineData("../../outside.png")]
    [InlineData("https://example.invalid/image.png")]
    [InlineData("C:\\private\\image.png")]
    public async Task Unsafe_image_parts_are_never_resolved(string target)
    {
        var document = CreateDocument(); using var stream = new MemoryStream(); await DocumentFormats.Docx.SaveAsync(document, stream);
        RewriteXml(stream, "word/_rels/document.xml.rels", xml =>
        {
            foreach (var relation in xml.Root!.Elements().Where(element => ((string?)element.Attribute("Type"))!.EndsWith("/image"))) relation.SetAttributeValue("Target", target);
        });
        stream.Position = 0; var loaded = await DocumentFormats.Docx.LoadWithReportAsync(stream);
        Assert.Contains(loaded.Report.Diagnostics, diagnostic => diagnostic.Code == "docx.image-unavailable");
        Assert.Contains("Diagram", loaded.Document.PlainText);
    }

    private static void RewriteXml(MemoryStream stream, string name, Action<XDocument> change)
    {
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Update, true);
        var entry = zip.GetEntry(name)!; XDocument xml;
        using (var input = entry.Open()) xml = XDocument.Load(input);
        change(xml); entry.Delete();
        using var output = zip.CreateEntry(name).Open(); xml.Save(output);
    }
}
