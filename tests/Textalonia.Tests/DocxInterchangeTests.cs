using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class DocxInterchangeTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static MemoryStream Package(string body, string? styles = null, string? numbering = null, string? relationships = null)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Part(string name, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
            Part("word/document.xml", $"<w:document xmlns:w='{W}' xmlns:r='{R}'><w:body>{body}</w:body></w:document>");
            if (styles is not null) Part("word/styles.xml", $"<w:styles xmlns:w='{W}'>{styles}</w:styles>");
            if (numbering is not null) Part("word/numbering.xml", $"<w:numbering xmlns:w='{W}'>{numbering}</w:numbering>");
            if (relationships is not null) Part("word/_rels/document.xml.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>{relationships}</Relationships>");
        }
        stream.Position = 0;
        return stream;
    }
    private static async Task<FlowDocument> RoundTrip(FlowDocument document)
    {
        using var stream = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(document, stream);
        stream.Position = 0;
        return await DocumentFormats.Docx.LoadAsync(stream);
    }

    [Fact]
    public async Task Numbering_preserves_identity_levels_marker_formats_restart_and_continuation_across_sections()
    {
        var identity = Guid.NewGuid();
        var definition = new ListDefinition { Levels = [
            new() { Marker = ListMarkerStyle.UpperRoman, Start = 3, Prefix = "(", Suffix = ")" },
            new() { Marker = ListMarkerStyle.LowerLetter, IncludeAncestors = true, Suffix = ")" }
        ] };
        Paragraph Item(string text, int level = 0, int? start = null) => new(text)
        { Style = new() { List = ListKind.Numbered, ListId = identity, ListDefinition = definition, ListLevel = level, ListStart = start, ListRestart = start is not null } };
        var original = new FlowDocument([Item("first"), Item("child", 1), new Paragraph("gap"),
            new Section { Padding = 0, Blocks = [Item("continued"), Item("restart", start: 8), Item("after")] }]);
        var loaded = await RoundTrip(original);
        Assert.IsType<Section>(loaded.Blocks[3]);
        var expected = new DocumentIndex(original).Paragraphs.Where(p => p.Paragraph.Style.List != ListKind.None).Select(p => ListNumbering.GetMarker(original, p.Paragraph.Id)!.Text);
        var paragraphs = new DocumentIndex(loaded).Paragraphs.Where(p => p.Paragraph.Style.List != ListKind.None).Select(p => p.Paragraph).ToArray();
        Assert.Equal(expected, paragraphs.Select(p => ListNumbering.GetMarker(loaded, p.Id)!.Text));
        Assert.All(paragraphs, p => Assert.Equal(identity, p.Style.ListId));
        Assert.Equal(8, paragraphs[3].Style.ListStart);
        Assert.True(paragraphs[3].Style.ListRestart);
        Assert.Equal(new[] { "(III)", "III.a)", "(IV)", "(VIII)", "(IX)" }, paragraphs.Select(p => ListNumbering.GetMarker(loaded, p.Id)!.Text));
    }

    [Fact]
    public async Task Foreign_numbering_instances_with_same_abstract_definition_remain_independent()
    {
        string P(string num, string text) => $"<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='{num}'/></w:numPr></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p>";
        using var stream = Package(P("1", "a") + P("2", "b") + P("1", "c"), numbering: """
            <w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="2"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%1)"/></w:lvl></w:abstractNum>
            <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
            <w:num w:numId="2"><w:abstractNumId w:val="0"/><w:lvlOverride w:ilvl="0"><w:startOverride w:val="8"/></w:lvlOverride></w:num>
            """);
        var document = await DocumentFormats.Docx.LoadAsync(stream);
        var paragraphs = document.Blocks.Cast<Paragraph>().ToArray();
        Assert.NotEqual(paragraphs[0].Style.ListId, paragraphs[1].Style.ListId);
        Assert.Equal(paragraphs[0].Style.ListId, paragraphs[2].Style.ListId);
        Assert.Equal(new[] { "b)", "h)", "c)" }, paragraphs.Select(p => ListNumbering.GetMarker(document, p.Id)!.Text));
    }

    [Fact]
    public async Task Styles_apply_document_defaults_based_on_character_styles_and_direct_false_overrides()
    {
        using var stream = Package("""
            <w:p><w:pPr><w:pStyle w:val="Derived"/><w:ind w:left="450"/></w:pPr><w:r><w:rPr><w:rStyle w:val="Emphasis"/><w:b w:val="0"/></w:rPr><w:t>styled</w:t></w:r></w:p>
            <w:p><w:r><w:t>default</w:t></w:r></w:p>
            """, styles: """
            <w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="30"/><w:color w:val="123456"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="300"/></w:pPr></w:pPrDefault></w:docDefaults>
            <w:style w:type="paragraph" w:default="1" w:styleId="Base"><w:rPr><w:b/><w:rFonts w:ascii="Arial"/></w:rPr><w:pPr><w:jc w:val="center"/></w:pPr></w:style>
            <w:style w:type="paragraph" w:styleId="Derived"><w:basedOn w:val="Base"/><w:rPr><w:i/></w:rPr></w:style>
            <w:style w:type="character" w:styleId="Emphasis"><w:rPr><w:u w:val="single"/></w:rPr></w:style>
            """);
        var document = await DocumentFormats.Docx.LoadAsync(stream);
        var p = Assert.IsType<Paragraph>(document.Blocks[0]); var style = Assert.Single(p.Runs).Style;
        Assert.False(style.Bold); Assert.True(style.Italic); Assert.True(style.Underline);
        Assert.Equal(20, style.FontSize); Assert.Equal("#123456", style.Foreground); Assert.Equal("Arial", style.FontFamily);
        Assert.Equal(ParagraphAlignment.Center, p.Style.Alignment); Assert.Equal(30, p.Style.Indent); Assert.Equal(20, p.Style.SpaceAfter);
        Assert.True(Assert.IsType<Paragraph>(document.Blocks[1]).Runs[0].Style.Bold);
    }

    [Fact]
    public async Task Nested_tables_merge_geometry_cell_edges_and_sizing_survive()
    {
        var nested = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("inside")] });
        var table = Table.Create(2, 3) with { ColumnWidths = [1, 2, 3], RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 40 }, new() { Mode = TableRowHeightMode.AtLeast, Height = 24 }] };
        table = table.MergeCells(0, 0, 2, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with
        {
            Blocks = [new Paragraph("anchor"), nested], Background = "#AABBCC", Padding = new(2, 4, 6, 8),
            Borders = new(new(1, "#112233"), new(2, "#334455"), new(3, "#556677"), new(4, "#778899"))
        });
        var original = new FlowDocument([table]); var document = await RoundTrip(original);
        Assert.Equal(original.Text, document.Text);
        var actual = Assert.IsType<Table>(document.Blocks[0]); var cell = actual.Rows[0][0];
        Assert.Equal(2, cell.ColumnSpan); Assert.Equal(2, cell.RowSpan); Assert.Equal((0, 0), actual.OwnerOf(1, 1));
        Assert.IsType<Table>(cell.Blocks[1]); Assert.Equal(2, cell.Blocks.Length);
        Assert.Equal(table.Rows[0][0].Padding, cell.Padding); Assert.Equal(table.Rows[0][0].Borders, cell.Borders);
        Assert.Equal(table.RowSizing.ToArray(), actual.RowSizing.ToArray()); Assert.Equal(2, actual.ColumnWidths[1] / actual.ColumnWidths[0], 5);
        Assert.Equal(3, actual.ColumnWidths[2] / actual.ColumnWidths[0], 5);
    }

    [Fact]
    public async Task Embedded_images_are_written_as_package_relationships_and_reused_without_fetching()
    {
        var data = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=").ToImmutableArray();
        var inline = new InlineDescriptor { AltText = "pixel & image", Width = 24, Height = 18, Payload = new ImageInlinePayload("pixel") };
        var document = new FlowDocument([new Paragraph([new RichRun("before"), new RichRun(inline), new RichRun(inline with { Id = Guid.NewGuid() }), new RichRun("after")])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("pixel", new() { MediaType = "image/png", Data = data }) };
        using var stream = new MemoryStream(); await DocumentFormats.Docx.SaveAsync(document, stream); stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
        {
            Assert.Single(zip.Entries, e => e.FullName.StartsWith("word/media/"));
            using var source = zip.GetEntry("word/document.xml")!.Open(); var xml = XDocument.Load(source);
            Assert.Equal(2, xml.Descendants(XName.Get("drawing", W)).Count());
        }
        stream.Position = 0; var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        Assert.Equal(document.Text, loaded.Text); Assert.Equal(document.PlainText, loaded.PlainText);
        var inlines = Assert.IsType<Paragraph>(loaded.Blocks[0]).Runs.Where(r => r.Inline is not null).Select(r => r.Inline!).ToArray();
        Assert.Equal(2, inlines.Length); Assert.Equal(24, inlines[0].Width); Assert.Equal(18, inlines[0].Height);
        Assert.NotEqual(inlines[0].Id, inlines[1].Id);
        Assert.Equal(data.ToArray(), Assert.Single(loaded.Resources).Value.Data.ToArray());
    }

    [Fact]
    public async Task External_images_revisions_and_page_layout_are_reported_without_fetching()
    {
        using var stream = Package($"""
            <w:p><w:r><w:t>kept</w:t></w:r><w:del><w:r><w:delText>removed</w:delText></w:r></w:del><w:r><w:drawing>
            <wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"><wp:docPr id="1" name="image" descr="alt"/>
            <a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData><a:blip r:link="image"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
            <w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:headerReference w:type="default" r:id="header"/></w:sectPr>
            """, relationships: $"<Relationship Id='image' Type='{R}/image' Target='https://never-fetch.invalid/image.png' TargetMode='External'/>");
        var result = await DocumentFormats.Docx.LoadWithReportAsync(stream);
        Assert.Equal("keptalt", result.Document.Text); Assert.Empty(result.Document.Resources);
        Assert.Equal(new[] { "docx.revision", "docx.page-layout", "docx.image-unavailable" }, result.Report.Diagnostics.Select(d => d.Code));
        Assert.All(result.Report.Diagnostics, d => Assert.NotNull(d.SourceLocation));
    }

    [Fact]
    public async Task Rich_typography_and_empty_paragraph_default_style_round_trip()
    {
        var style = new TextStyle { FontSize = 22, Bold = true, Italic = true, Underline = true, Strikethrough = true, Baseline = Baseline.Superscript, FontFamily = "Arial", Foreground = "#112233", Background = "#FFEEDD" };
        var ps = new ParagraphStyle { Indent = 24, RightIndent = 18, FirstLineIndent = -12, LineHeight = 32, LetterSpacing = 2, RightToLeft = true, SpaceBefore = 4, SpaceAfter = 6 };
        var original = new FlowDocument([new Paragraph("a  b\tc\u2028d", style) { Style = ps }, new Paragraph("") { DefaultStyle = style, Style = ps }]);
        var loaded = await RoundTrip(original);
        var p = Assert.IsType<Paragraph>(loaded.Blocks[0]);
        var resolver = new DocumentStyleResolver(loaded);
        var expectedParagraph = ps with { DefaultTabWidth = 48, LetterSpacing = 0, LineSpacingMode = LineSpacingMode.Exact, LineSpacing = 32 };
        var expectedText = style with { Tracking = 2, UnderlineKind = UnderlineKind.Single, StrikeKind = StrikeKind.Single };
        // Word stores tracking on runs and an exact line-spacing rule. Compare the effective
        // representation while retaining the newly imported sparse formatting metadata.
        Assert.Equal(original.Text, loaded.Text); Assert.Equal(expectedParagraph, resolver.ResolveParagraphStyle(p.Style));
        Assert.Equal(expectedText, resolver.ResolveText(p, Assert.Single(p.Runs).Style));
        var empty = Assert.IsType<Paragraph>(loaded.Blocks[1]);
        Assert.Equal(expectedText, resolver.ResolveText(empty, empty.DefaultStyle));
        Assert.Equal(expectedParagraph, resolver.ResolveParagraphStyle(empty.Style));
    }

    [Fact]
    public async Task Unsupported_export_features_have_actionable_diagnostics_and_strict_rejection_is_atomic()
    {
        var inline = new InlineDescriptor { AltText = "widget", Payload = new ControlInlinePayload("widget") };
        var document = new FlowDocument([new Section { Background = "#EEEEEE", Blocks = [new Paragraph([new RichRun(inline, new TextStyle { FontWeight = 500, FontStretch = 7, Foreground = "#80112233" })])] }]);
        using var stream = new MemoryStream();
        var result = await DocumentFormats.Docx.SaveWithReportAsync(document, stream);
        var codes = result.Report.Diagnostics.Select(d => d.Code).ToArray();
        Assert.Contains("docx.section-decoration", codes); Assert.Contains("docx.font-weight", codes); Assert.Contains("docx.font-stretch", codes); Assert.Contains("docx.color-alpha", codes); Assert.Contains("docx.inline-fallback", codes);
        using var rejected = new MemoryStream();
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Docx.SaveWithReportAsync(document, rejected, new() { Mode = ConversionMode.Strict }));
        Assert.Equal(0, rejected.Length);
    }

    [Fact]
    public async Task Invalid_merge_geometry_duplicate_package_parts_and_excessive_nesting_are_errors()
    {
        using var malformed = Package("<w:tbl><w:tr><w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p/></w:tc></w:tr></w:tbl>");
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Docx.LoadAsync(malformed));
        using var nested = Package(string.Concat(Enumerable.Repeat("<w:sdt><w:sdtContent>", 34)) + "<w:p/>" + string.Concat(Enumerable.Repeat("</w:sdtContent></w:sdt>", 34)));
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Docx.LoadAsync(nested));
        using var duplicate = new MemoryStream();
        using (var zip = new ZipArchive(duplicate, ZipArchiveMode.Create, true)) { zip.CreateEntry("word/document.xml"); zip.CreateEntry("word/document.xml"); }
        duplicate.Position = 0;
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Docx.LoadAsync(duplicate));
    }
    [Fact]
    public async Task Geometry_quantization_and_import_clamping_are_reported()
    {
        var table = Table.Create(1, 1).SetCell(0, 0, new TableCell { Padding = new(0.01, 0, 0, 0), Borders = new(Left: new(40, "#123456")) });
        var document = new FlowDocument([new Paragraph("geometry") { Style = new() { Indent = 0.01 } }, table]);
        using var output = new MemoryStream();
        var saved = await DocumentFormats.Docx.SaveWithReportAsync(document, output);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == "docx.dimension-precision");
        using var input = Package("<w:p><w:pPr><w:ind w:left='30000'/></w:pPr><w:r><w:t>wide</w:t></w:r></w:p>");
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(input);
        Assert.Equal(1000, Assert.IsType<Paragraph>(loaded.Document.Blocks[0]).Style.Indent);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.dimension-range");
        using var malformed = Package("<w:p><w:pPr><w:ind w:left='NaN'/></w:pPr></w:p>");
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Docx.LoadAsync(malformed));
    }

    [Fact]
    public async Task Style_cycles_and_unsupported_number_patterns_are_diagnosed()
    {
        using var input = Package("<w:p><w:pPr><w:pStyle w:val='A'/><w:numPr><w:numId w:val='1'/></w:numPr></w:pPr><w:r><w:t>x</w:t></w:r></w:p>",
            styles: "<w:style w:styleId='A'><w:basedOn w:val='B'/></w:style><w:style w:styleId='B'><w:basedOn w:val='A'/></w:style>",
            numbering: "<w:abstractNum w:abstractNumId='0'><w:lvl w:ilvl='0'><w:numFmt w:val='ordinal'/><w:lvlText w:val='%2-%1'/></w:lvl></w:abstractNum><w:num w:numId='1'><w:abstractNumId w:val='0'/></w:num>");
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(input);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.style-cycle" && d.SourceLocation!.StartsWith("word/styles.xml:"));
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.number-format" && d.SourceLocation!.StartsWith("word/numbering.xml:"));
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.numbering-pattern");
    }
    [Theory]
    [InlineData("1", "2147483647")]
    [InlineData("2147483647", "2")]
    [InlineData("0", "101")]
    public async Task Overflowing_grid_geometry_is_rejected_before_model_allocation(string before, string span)
    {
        using var input = Package($"<w:tbl><w:tblGrid><w:gridCol w:w='100'/><w:gridCol w:w='100'/></w:tblGrid><w:tr><w:trPr><w:gridBefore w:val='{before}'/></w:trPr><w:tc><w:tcPr><w:gridSpan w:val='{span}'/></w:tcPr><w:p/></w:tc></w:tr></w:tbl>");
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Docx.LoadAsync(input));
    }

    [Theory]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.NoCompression)]
    public async Task Forged_zip_image_sizes_do_not_bypass_bounds_or_integrity_checks(CompressionLevel compression)
    {
        using var input = Package("""
            <w:p><w:r><w:drawing><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"><wp:docPr id="1" name="image" descr="image"/><a:graphic xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><a:graphicData><a:blip r:embed="image"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
            """, relationships: $"<Relationship Id='image' Type='{R}/image' Target='media/image.png'/>");
        using (var zip = new ZipArchive(input, ZipArchiveMode.Update, true))
        {
            using var image = zip.CreateEntry("word/media/image.png", compression).Open();
            image.Write(new byte[DocumentResource.MaximumEmbeddedBytes + 1]);
        }
        var bytes = input.ToArray(); var patched = false;
        for (var i = 0; i <= bytes.Length - 46; i++)
        {
            if (BitConverter.ToUInt32(bytes, i) != 0x02014b50) continue;
            var nameLength = BitConverter.ToUInt16(bytes, i + 28);
            if (i + 46 + nameLength > bytes.Length || Encoding.UTF8.GetString(bytes, i + 46, nameLength) != "word/media/image.png") continue;
            BitConverter.GetBytes(1).CopyTo(bytes, i + 24); patched = true; break;
        }
        Assert.True(patched);
        using var malicious = new MemoryStream(bytes);
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Docx.LoadAsync(malicious));
    }

    [Fact]
    public async Task Definition_changes_continue_counters_and_mixed_list_kinds_survive()
    {
        var id = Guid.NewGuid();
        var original = new FlowDocument([
            new Paragraph("decimal") { Style = new() { ListId = id, List = ListKind.Numbered } },
            new Paragraph("letter") { Style = new() { ListId = id, List = ListKind.Numbered, ListDefinition = new() { Levels = [new() { Marker = ListMarkerStyle.UpperLetter }] } } }
        ]);
        using var stream = new MemoryStream(); await DocumentFormats.Docx.SaveAsync(original, stream); stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
        {
            using var xml = zip.GetEntry("word/numbering.xml")!.Open(); var numbering = XDocument.Load(xml);
            Assert.Contains(numbering.Descendants(XName.Get("startOverride", W)), e => (string?)e.Attribute(XName.Get("val", W)) == "2");
        }
        stream.Position = 0; var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        Assert.Equal("B.", ListNumbering.GetMarker(loaded, loaded.Blocks[1].Id)!.Text);
        var mixedId = Guid.NewGuid();
        var mixed = await RoundTrip(new FlowDocument([
            new Paragraph("number") { Style = new() { ListId = mixedId, List = ListKind.Numbered } },
            new Paragraph("bullet") { Style = new() { ListId = mixedId, List = ListKind.Bullet } }
        ]));
        Assert.Equal(ListKind.Numbered, Assert.IsType<Paragraph>(mixed.Blocks[0]).Style.List);
        Assert.Equal(ListKind.Bullet, Assert.IsType<Paragraph>(mixed.Blocks[1]).Style.List);
    }

    [Fact]
    public async Task Direct_non_heading_outline_overrides_heading_style_name_and_empty_section_break_is_reported()
    {
        using var input = Package("<w:p><w:pPr><w:pStyle w:val='Heading1'/><w:outlineLvl w:val='9'/><w:sectPr/></w:pPr><w:r><w:rPr><w:color w:themeColor='accent1'/></w:rPr><w:t>ordinary</w:t></w:r></w:p>",
            styles: "<w:style w:styleId='Heading1'><w:pPr><w:outlineLvl w:val='0'/></w:pPr></w:style>");
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(input);
        Assert.Equal(0, Assert.IsType<Paragraph>(loaded.Document.Blocks[0]).Style.HeadingLevel);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.theme-color");
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.page-layout");
    }
}
