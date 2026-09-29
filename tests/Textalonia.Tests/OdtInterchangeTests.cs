using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class OdtInterchangeTests
{
    private const string MediaType = "application/vnd.oasis.opendocument.text";
    private const string Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private const string TableNamespace = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private const string Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private const string Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private const string XLink = "http://www.w3.org/1999/xlink";
    private static readonly byte[] PixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");

    [Fact]
    public async Task Embedded_raster_image_round_trips_with_manifest_alt_text_and_dimensions()
    {
        var image = new InlineDescriptor { Payload = new ImageInlinePayload("pixel"), AltText = "Tiny logo", Width = 96, Height = 48 };
        var source = new FlowDocument([new Paragraph([new RichRun("before "), new RichRun(image), new RichRun(" after")])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("pixel", new DocumentResource
            { MediaType = "image/png", Data = ImmutableArray.CreateRange(PixelPng) }) };
        using var output = new MemoryStream();
        var saved = await DocumentFormats.Odt.SaveWithReportAsync(source, output, new() { Mode = ConversionMode.Strict });
        Assert.False(saved.Report.HasLoss);
        output.Position = 0;
        using (var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Equal(PixelPng, ReadBytes(zip.GetEntry("Pictures/image0001.png")!));
            var content = XDocument.Load(zip.GetEntry("content.xml")!.Open());
            XNamespace draw = Draw, svg = Svg, xlink = XLink;
            var frame = Assert.Single(content.Descendants(draw + "frame"));
            Assert.Equal("1in", (string?)frame.Attribute(svg + "width"));
            Assert.Equal("0.5in", (string?)frame.Attribute(svg + "height"));
            Assert.Equal("Tiny logo", (string?)frame.Element(svg + "title"));
            Assert.Equal("Pictures/image0001.png", (string?)frame.Element(draw + "image")?.Attribute(xlink + "href"));
            var manifest = XDocument.Load(zip.GetEntry("META-INF/manifest.xml")!.Open());
            XNamespace m = Manifest;
            Assert.Contains(manifest.Descendants(m + "file-entry"), e =>
                (string?)e.Attribute(m + "full-path") == "Pictures/image0001.png" &&
                (string?)e.Attribute(m + "media-type") == "image/png");
        }
        output.Position = 0;
        var loaded = await DocumentFormats.Odt.LoadWithReportAsync(output, new() { Mode = ConversionMode.Strict });
        Assert.False(loaded.Report.HasLoss);
        var paragraph = Assert.IsType<Paragraph>(Assert.Single(loaded.Document.Blocks));
        Assert.Equal("before Tiny logo after", loaded.Document.PlainText);
        var restored = Assert.Single(paragraph.Runs, run => run.Inline is not null).Inline!;
        var payload = Assert.IsType<ImageInlinePayload>(restored.Payload);
        Assert.Equal("Tiny logo", restored.AltText);
        Assert.Equal(96, restored.Width);
        Assert.Equal(48, restored.Height);
        Assert.Equal("image/png", loaded.Document.Resources[payload.ResourceId].MediaType);
        Assert.Equal(PixelPng, loaded.Document.Resources[payload.ResourceId].Data.ToArray());
    }

    [Fact]
    public async Task Imports_independent_manifest_declared_raster_frame()
    {
        using var input = PackageWithImage(ImageContent("Pictures/pixel.png"), "Pictures/pixel.png", "image/png", PixelPng);
        var loaded = await DocumentFormats.Odt.LoadWithReportAsync(input, new() { Mode = ConversionMode.Strict });
        Assert.False(loaded.Report.HasLoss);
        var frame = Assert.Single(((Paragraph)Assert.Single(loaded.Document.Blocks)).Runs, run => run.Inline is not null).Inline!;
        Assert.Equal("Tiny logo", frame.AltText);
        Assert.Equal(96, frame.Width);
        Assert.Equal(48, frame.Height);
        var payload = Assert.IsType<ImageInlinePayload>(frame.Payload);
        Assert.Equal(PixelPng, loaded.Document.Resources[payload.ResourceId].Data.ToArray());
    }

    [Fact]
    public async Task Svg_export_reports_image_loss_and_keeps_alternative_text()
    {
        var image = new InlineDescriptor { Payload = new ImageInlinePayload("vector"), AltText = "Chart" };
        var source = new FlowDocument([new Paragraph([new RichRun(image)])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("vector", new DocumentResource
            { MediaType = "image/svg+xml", Data = ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("<svg/>")) }) };
        using var output = new MemoryStream();
        var saved = await DocumentFormats.Odt.SaveWithReportAsync(source, output);
        Assert.Contains(saved.Report.Diagnostics, diagnostic => diagnostic.Code == "odt.image" && diagnostic.ModelId == image.Id);
        output.Position = 0;
        var loaded = await DocumentFormats.Odt.LoadAsync(output);
        Assert.Equal("Chart", loaded.PlainText);
        Assert.Empty(loaded.Resources);
    }

    [Theory]
    [InlineData("https://example.invalid/image.png", "image/png", "")]
    [InlineData("../private.png", "image/png", "")]
    [InlineData("Pictures/shape.svg", "image/svg+xml", "<svg xmlns='http://www.w3.org/2000/svg'/>")]
    public async Task External_unsafe_or_vector_images_fall_back_to_alt_text_without_fetching(
        string href, string mediaType, string part)
    {
        var content = ImageContent(href);
        using var input = part.Length == 0 ? Package(content) : PackageWithImage(content, href, mediaType, Encoding.UTF8.GetBytes(part));
        var loaded = await DocumentFormats.Odt.LoadWithReportAsync(input);
        Assert.Equal("A Tiny logo Z", loaded.Document.PlainText);
        Assert.Empty(loaded.Document.Resources);
        Assert.Contains(loaded.Report.Diagnostics, diagnostic => diagnostic.Code == "odt.image");
    }

    [Fact]
    public async Task Embedded_object_frame_reports_loss_and_retains_title()
    {
        var content = ImageContent("Object 1").Replace("<draw:image xlink:href=\"Object 1\"/>",
            "<draw:object xlink:href=\"Object 1\"/>");
        using var input = Package(content);
        var loaded = await DocumentFormats.Odt.LoadWithReportAsync(input);
        Assert.Equal("A Tiny logo Z", loaded.Document.PlainText);
        Assert.Empty(loaded.Document.Resources);
        Assert.Contains(loaded.Report.Diagnostics, diagnostic => diagnostic.Code == "odt.object");
    }

    [Fact]
    public async Task Oversized_embedded_image_is_rejected_even_when_zip_is_small()
    {
        var content = ImageContent("Pictures/large.png");
        using var input = PackageWithImage(content, "Pictures/large.png", "image/png",
            new byte[DocumentResource.MaximumEmbeddedBytes + 1]);
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Odt.LoadAsync(input));
    }

    [Fact]
    public async Task Rich_paragraphs_round_trip_as_odf_content_and_styles()
    {
        Assert.Same(DocumentFormats.Odt, DocumentFormats.ForPath("sample.odt"));
        var original = new FlowDocument([
            new Paragraph([new RichRun("plain "), new RichRun("emphasis", new TextStyle
                { Bold = true, Italic = true, Foreground = "#123456" })])
            { Style = new ParagraphStyle { Alignment = ParagraphAlignment.Center, SpaceAfter = 12 } },
            new Paragraph("second")
        ]);

        using var output = new MemoryStream();
        await DocumentFormats.Odt.SaveAsync(original, output);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Equal(MediaType, Read(zip.GetEntry("mimetype")!));
            Assert.NotNull(zip.GetEntry("content.xml"));
            Assert.NotNull(zip.GetEntry("styles.xml"));
            Assert.NotNull(zip.GetEntry("META-INF/manifest.xml"));
            var content = XDocument.Load(zip.GetEntry("content.xml")!.Open());
            XNamespace text = Text;
            Assert.Equal(2, content.Descendants(text + "p").Count());
            Assert.Contains(content.Descendants(text + "span"), span => span.Value == "emphasis");
        }

        output.Position = 0;
        var loaded = await DocumentFormats.Odt.LoadAsync(output);
        Assert.Equal(original.Text, loaded.Text);
        var first = Assert.IsType<Paragraph>(loaded.Blocks[0]);
        var resolver = new DocumentStyleResolver(loaded);
        Assert.Equal(ParagraphAlignment.Center, resolver.ResolveParagraphStyle(first.Style).Alignment);
        var emphasis = Assert.Single(first.Runs, run => run.Text == "emphasis");
        var formatting = resolver.ResolveText(first, emphasis.Style);
        Assert.True(formatting.EffectiveBold);
        Assert.True(formatting.Italic);
        Assert.Equal("#123456", formatting.Foreground);
        Assert.True(output.CanRead && output.CanWrite);
    }

    [Fact]
    public async Task Lists_tables_and_page_settings_round_trip()
    {
        var listId = Guid.NewGuid();
        var list = new ListDefinition { Levels = [new() { Kind = ListKind.Numbered, Marker = ListMarkerStyle.Decimal, Suffix = "." }] };
        Paragraph Item(string value) => new(value) { Style = new ParagraphStyle
            { List = ListKind.Numbered, ListId = listId, ListDefinition = list } };
        var table = Table.Create(2, 2)
            .SetCell(0, 0, new TableCell { Blocks = [new Paragraph("A1")] })
            .SetCell(0, 1, new TableCell { Blocks = [new Paragraph("B1")] })
            .SetCell(1, 0, new TableCell { Blocks = [new Paragraph("A2")] })
            .SetCell(1, 1, new TableCell { Blocks = [new Paragraph("B2")] });
        var original = new FlowDocument([Item("one"), Item("two"), table])
        {
            Sections = [new DocumentSection { StartParagraphId = Guid.Empty, PageSettings = new PageSettings
                { Width = 794, Height = 1123, Margins = new EdgeInsets(72, 84, 72, 84) } }]
        };

        using var output = new MemoryStream();
        await DocumentFormats.Odt.SaveAsync(original, output);
        output.Position = 0;
        var loaded = await DocumentFormats.Odt.LoadAsync(output);
        var paragraphs = loaded.Blocks.Take(2).Cast<Paragraph>().ToArray();
        Assert.Equal(new[] { "1.", "2." }, paragraphs.Select(p => ListNumbering.GetMarker(loaded, p.Id)!.Text));
        Assert.Equal(paragraphs[0].Style.ListId, paragraphs[1].Style.ListId);
        var restoredTable = Assert.IsType<Table>(loaded.Blocks[2]);
        Assert.Equal(2, restoredTable.Rows.Length);
        Assert.Equal(2, restoredTable.ColumnCount);
        Assert.Equal("B2", Assert.IsType<Paragraph>(restoredTable.Rows[1][1].Blocks[0]).Text);
        var page = Assert.Single(loaded.Sections).PageSettings;
        Assert.InRange(page.Width, 793.5, 794.5);
        Assert.InRange(page.Height, 1122.5, 1123.5);
        Assert.InRange(page.Margins.Left, 71.5, 72.5);
        Assert.InRange(page.Margins.Top, 83.5, 84.5);
    }

    [Fact]
    public async Task Imports_external_style_references_and_odf_space_elements()
    {
        var styles = $"""
            <office:document-styles xmlns:office="{Office}" xmlns:style="{Style}" xmlns:text="{Text}" xmlns:fo="{Fo}" office:version="1.2">
              <office:styles>
                <style:style style:name="Centered" style:family="paragraph">
                  <style:paragraph-properties fo:text-align="center"/>
                </style:style>
                <style:style style:name="Strong" style:family="text">
                  <style:text-properties fo:font-weight="bold" fo:font-style="italic"/>
                </style:style>
              </office:styles>
              <office:automatic-styles>
                <style:page-layout style:name="A4">
                  <style:page-layout-properties fo:page-width="210mm" fo:page-height="297mm" fo:margin-left="20mm" fo:margin-right="20mm" fo:margin-top="25mm" fo:margin-bottom="25mm"/>
                </style:page-layout>
              </office:automatic-styles>
              <office:master-styles><style:master-page style:name="Standard" style:page-layout-name="A4"/></office:master-styles>
            </office:document-styles>
            """;
        var content = $"""
            <office:document-content xmlns:office="{Office}" xmlns:style="{Style}" xmlns:text="{Text}" xmlns:table="{TableNamespace}" office:version="1.2">
              <office:body><office:text>
                <text:p text:style-name="Centered">A<text:s text:c="2"/><text:span text:style-name="Strong">bold</text:span><text:tab/>end</text:p>
              </office:text></office:body>
            </office:document-content>
            """;
        using var input = Package(content, styles);
        var loaded = await DocumentFormats.Odt.LoadAsync(input);
        var paragraph = Assert.IsType<Paragraph>(Assert.Single(loaded.Blocks));
        Assert.Equal("A  bold\tend", paragraph.Text);
        var resolver = new DocumentStyleResolver(loaded);
        Assert.Equal(ParagraphAlignment.Center, resolver.ResolveParagraphStyle(paragraph.Style).Alignment);
        var strong = Assert.Single(paragraph.Runs, run => run.Text == "bold");
        Assert.True(resolver.ResolveText(paragraph, strong.Style).EffectiveBold);
        Assert.True(resolver.ResolveText(paragraph, strong.Style).Italic);
        Assert.InRange(Assert.Single(loaded.Sections).PageSettings.Width, 793.5, 794.5);
    }

    [Fact]
    public async Task Rejects_duplicate_package_entries()
    {
        var content = EmptyContent();
        using var duplicate = Package(content, extra: [("content.xml", content)]);
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Odt.LoadAsync(duplicate));
    }

    [Fact]
    public async Task Rejects_dtd_in_content_xml()
    {
        using var dtd = Package("<!DOCTYPE office:document-content [<!ENTITY x 'bad'>]>" + EmptyContent());
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Odt.LoadAsync(dtd));
    }

    [Fact]
    public async Task Rejects_oversized_uncompressed_content()
    {
        var content = EmptyContent().Replace("</office:text>", new string('x', 33 * 1024 * 1024) + "</office:text>");
        using var oversized = Package(content);
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Odt.LoadAsync(oversized));
    }

    [Fact]
    public async Task Strict_export_rejects_unsupported_inline_without_touching_destination()
    {
        var inline = new InlineDescriptor { AltText = "widget", Payload = new ControlInlinePayload("widget") };
        var document = new FlowDocument([new Paragraph([new RichRun(inline)])]);
        using var tolerant = new MemoryStream();
        var report = await DocumentFormats.Odt.SaveWithReportAsync(document, tolerant);
        Assert.Contains(report.Report.Diagnostics, diagnostic => diagnostic.ModelId == inline.Id &&
            diagnostic.Severity != ConversionDiagnosticSeverity.Information);
        using var destination = new MemoryStream([9, 8, 7]);
        destination.Position = 1;
        var before = destination.ToArray();
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Odt.SaveWithReportAsync(
            document, destination, new() { Mode = ConversionMode.Strict }));
        Assert.Equal(1, destination.Position);
        Assert.Equal(before, destination.ToArray());
    }

    private static string EmptyContent() => $"""
        <office:document-content xmlns:office="{Office}" xmlns:text="{Text}" office:version="1.2">
          <office:body><office:text><text:p>safe</text:p></office:text></office:body>
        </office:document-content>
        """;

    private static string ImageContent(string href) => $"""
        <office:document-content xmlns:office="{Office}" xmlns:text="{Text}" xmlns:draw="{Draw}" xmlns:svg="{Svg}" xmlns:xlink="{XLink}" office:version="1.2">
          <office:body><office:text><text:p>A <draw:frame text:anchor-type="as-char" svg:width="1in" svg:height="0.5in"><draw:image xlink:href="{href}"/><svg:title>Tiny logo</svg:title></draw:frame> Z</text:p></office:text></office:body>
        </office:document-content>
        """;

    private static MemoryStream PackageWithImage(string content, string path, string mediaType, byte[] bytes)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "mimetype", MediaType, CompressionLevel.NoCompression);
            Write(zip, "content.xml", content);
            Write(zip, "META-INF/manifest.xml", $"""
                <manifest:manifest xmlns:manifest="{Manifest}" manifest:version="1.2">
                  <manifest:file-entry manifest:full-path="/" manifest:media-type="{MediaType}"/>
                  <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
                  <manifest:file-entry manifest:full-path="{path}" manifest:media-type="{mediaType}"/>
                </manifest:manifest>
                """);
            using var part = zip.CreateEntry(path).Open();
            part.Write(bytes);
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream Package(string content, string? styles = null, params (string Name, string Content)[] extra)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "mimetype", MediaType, CompressionLevel.NoCompression);
            Write(zip, "content.xml", content);
            if (styles is not null) Write(zip, "styles.xml", styles);
            var styleManifest = styles is null ? "" : "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>";
            Write(zip, "META-INF/manifest.xml", $"""
                <manifest:manifest xmlns:manifest="{Manifest}" manifest:version="1.2">
                  <manifest:file-entry manifest:full-path="/" manifest:media-type="{MediaType}"/>
                  <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
                  {styleManifest}
                </manifest:manifest>
                """);
            foreach (var (name, value) in extra) Write(zip, name, value);
        }
        stream.Position = 0;
        return stream;
    }

    private static void Write(ZipArchive zip, string name, string value, CompressionLevel compression = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, compression).Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
