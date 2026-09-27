using System.Collections.Immutable;
using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class RtfInterchangeTests
{
    [Fact]
    public void Tables_merges_row_sizes_sections_and_links_survive_two_round_trips()
    {
        var table = Table.Create(2, 3) with { ColumnWidths = [80, 120, 100], RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 40 }, new() { Mode = TableRowHeightMode.AtLeast, Height = 24 }] };
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Paragraph("merged", new() { Hyperlink = "https://example.com/?a=1&b=2" })], Background = "#DDEEFF" });
        table = table.MergeCells(0, 0, 2, 2);
        table = table.SetCell(0, 2, table.Rows[0][2] with { Blocks = [new Paragraph("right"), new Paragraph("second")] });
        var original = new FlowDocument([new Paragraph("before"), new Section { Blocks = [table, new Paragraph("inside")] }, new Paragraph("after"), Table.Create(1, 1), Table.Create(1, 1)]);
        var document = original;
        for (var pass = 0; pass < 2; pass++)
        {
            document = DocumentFormats.Rtf.Parse(DocumentFormats.Rtf.Serialize(document));
            Assert.Equal(original.Text, document.Text);
            Assert.Equal(3, document.Blocks.Length);
            var section = Assert.IsType<Section>(document.Blocks[1]);
            var restored = Assert.IsType<Table>(section.Blocks[0]);
            Assert.Equal(2, restored.Rows[0][0].RowSpan);
            Assert.Equal(2, restored.Rows[0][0].ColumnSpan);
            Assert.Equal("#DDEEFF", restored.Rows[0][0].Background);
            Assert.Equal(new double[] { 80, 120, 100 }, restored.ColumnWidths);
            Assert.Equal(TableRowHeightMode.Exact, restored.RowSizing[0].Mode);
            Assert.Equal(40, restored.RowSizing[0].Height);
            Assert.Equal(TableRowHeightMode.AtLeast, restored.RowSizing[1].Mode);
            Assert.Equal("https://example.com/?a=1&b=2", restored.Rows[0][0].Paragraphs[0].Runs[0].Style.Hyperlink);
            var trailing = Assert.IsType<Section>(document.Blocks[2]);
            Assert.Equal("after", Assert.IsType<Paragraph>(trailing.Blocks[0]).Text);
            Assert.IsType<Table>(trailing.Blocks[1]);
            Assert.IsType<Table>(trailing.Blocks[2]);
            document.Validate();
        }
    }

    [Fact]
    public void Standard_list_tables_preserve_identity_levels_continuation_and_visible_restarts()
    {
        var id = Guid.NewGuid();
        var definition = new ListDefinition { Levels = [new() { Start = 3, Marker = ListMarkerStyle.UpperRoman, Suffix = ")" }, new() { Marker = ListMarkerStyle.LowerLetter, IncludeAncestors = true, Prefix = "(", Suffix = ")" }] };
        Paragraph Item(string value, int level = 0, int? start = null) => new(value) { Style = new() { List = ListKind.Numbered, ListId = id, ListDefinition = definition, ListLevel = level, ListStart = start } };
        var original = new FlowDocument([Item("first"), Item("child", 1), new Paragraph("gap"), new Section { Blocks = [Item("continued"), Item("restart", start: 8), Item("after restart")] }]);
        var rtf = DocumentFormats.Rtf.Serialize(original);
        Assert.Contains("\\listtable", rtf);
        Assert.Contains("\\listoverridetable", rtf);
        var restored = DocumentFormats.Rtf.Parse(rtf);
        var before = new DocumentIndex(original).Paragraphs.Where(p => p.Paragraph.Style.List != ListKind.None).Select(p => ListNumbering.GetMarker(original, p.Paragraph.Id)!.Text).ToArray();
        var restoredParagraphs = new DocumentIndex(restored).Paragraphs.Select(p => p.Paragraph).Where(p => p.Style.List != ListKind.None).ToArray();
        Assert.Equal(before, restoredParagraphs.Select(p => ListNumbering.GetMarker(restored, p.Id)!.Text));
        Assert.Equal(restoredParagraphs[0].Style.ListId, restoredParagraphs[2].Style.ListId);
        Assert.Equal(1, restoredParagraphs[1].Style.ListLevel);
        Assert.Equal(restoredParagraphs[3].Style.ListId, restoredParagraphs[4].Style.ListId);
    }

    [Fact]
    public void Imports_independently_written_table_and_numbering_syntax()
    {
        const string rtf = "{\\rtf1\\ansi{\\*\\listtable{\\list{\\listlevel\\levelnfc0\\levelstartat4{\\leveltext\\'02\\'00.;}{\\levelnumbers\\'01;}}\\listid7}}{\\*\\listoverridetable{\\listoverride\\listid7\\listoverridecount0\\ls3}}\\pard\\ls3 item\\par\\trowd\\cellx1500\\cellx3000\\pard\\intbl left\\cell right\\cell\\row}";
        var document = DocumentFormats.Rtf.Parse(rtf);
        var paragraph = Assert.IsType<Paragraph>(document.Blocks[0]);
        Assert.Equal("4.", ListNumbering.GetMarker(document, paragraph.Id)!.Text);
        var table = Assert.IsType<Table>(document.Blocks[1]);
        Assert.Equal("left", table.Rows[0][0].Paragraphs[0].Text);
        Assert.Equal("right", table.Rows[0][1].Paragraphs[0].Text);
    }

    [Fact]
    public void Typography_and_empty_paragraph_default_style_are_preserved()
    {
        var text = new TextStyle { FontFamily = "Aptos", FontSize = 20, FontStretch = 7, Italic = true, Underline = true, Baseline = Baseline.Superscript };
        var style = new ParagraphStyle { Indent = 24, RightIndent = 30, FirstLineIndent = -12, SpaceBefore = 4, SpaceAfter = 6, LineHeight = 28, LetterSpacing = 2, HeadingLevel = 2, RightToLeft = true };
        var original = new FlowDocument([new Paragraph("  a\tb\u2028\u4E2D {\\} \U0001F600", text) { Style = style }, new Paragraph("", text) { Style = style }]);
        var restored = DocumentFormats.Rtf.Parse(DocumentFormats.Rtf.Serialize(original));
        Assert.Equal(original.Text, restored.Text);
        foreach (var paragraph in restored.Blocks.Cast<Paragraph>()) { Assert.Equal(style, paragraph.Style); Assert.Equal(text, paragraph.DefaultStyle); }
        Assert.Equal(text, ((Paragraph)restored.Blocks[0]).Runs[0].Style);
    }

    [Fact]
    public async Task Embedded_png_is_preserved_and_opaque_resources_are_never_fetched()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jB2kAAAAASUVORK5CYII=");
        var image = new InlineDescriptor { Width = 64, Height = 48, Payload = new ImageInlinePayload("png") };
        var remote = new InlineDescriptor { AltText = "remote image", Payload = new ImageInlinePayload("remote") };
        var original = new FlowDocument([new Paragraph([new RichRun("before"), new RichRun(image), new RichRun(remote)])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("png", new() { MediaType = "image/png", Data = bytes.ToImmutableArray() }).Add("remote", new() { Kind = DocumentResourceKind.Host, Location = "https://never-fetch.invalid/a.png" }) };
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Rtf.SaveWithReportAsync(original, stream);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == "rtf.inline-fallback" && d.ModelId == remote.Id);
        stream.Position = 0;
        var restored = (await DocumentFormats.Rtf.LoadWithReportAsync(stream)).Document;
        var inline = Assert.Single(new DocumentIndex(restored).Paragraphs[0].Paragraph.Runs.Where(r => r.Inline is not null)).Inline!;
        Assert.Equal(64, inline.Width); Assert.Equal(48, inline.Height);
        Assert.Equal(bytes, Assert.Single(restored.Resources).Value.Data.ToArray());
        Assert.Contains("remote image", restored.PlainText);
    }

    [Fact]
    public async Task Unsafe_links_unknown_destinations_and_unsupported_fields_have_stable_diagnostics()
    {
        const string rtf = "{\\rtf1 safe{\\*\\unknown hidden}{\\field{\\*\\fldinst HYPERLINK \"javascript:alert(1)\"}{\\fldrslt link}}{\\field{\\*\\fldinst INCLUDEPICTURE \"https://never-fetch.invalid/a.png\"}{\\fldrslt image}}}";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
        var result = await DocumentFormats.Rtf.LoadWithReportAsync(stream);
        Assert.Equal("safelinkimage", result.Document.Text);
        Assert.Equal(new[] { "rtf.unsupported-destination", "rtf.unsafe-link", "rtf.unsupported-field" }, result.Report.Diagnostics.Select(d => d.Code));
        Assert.All(result.Report.Diagnostics, d => Assert.StartsWith("rtf:", d.SourceLocation));
        Assert.All(new DocumentIndex(result.Document).Paragraphs.SelectMany(p => p.Paragraph.Runs), r => Assert.Null(r.Style.Hyperlink));
        stream.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Rtf.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Export_reports_nested_structures_and_font_approximations_by_model_id()
    {
        var nested = Table.Create(1, 1);
        var outer = Table.Create(1, 1);
        outer = outer.SetCell(0, 0, outer.Rows[0][0] with { Blocks = [nested] });
        var paragraph = new Paragraph("weight", new() { FontWeight = 500, FontSize = 13.1 });
        var section = new Section { Background = "#EEEEEE", Blocks = [outer, paragraph] };
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Rtf.SaveWithReportAsync(new([section]), stream);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == "rtf.nested-table" && d.ModelId == nested.Id);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == "rtf.section-decoration" && d.ModelId == section.Id);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == "rtf.font-weight" && d.ModelId == paragraph.Id);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == "rtf.font-size-precision" && d.ModelId == paragraph.Id);
    }

    [Theory]
    [InlineData("{\\rtf1 unclosed")]
    [InlineData("{\\rtf1} trailing")]
    [InlineData("{\\rtf1}{\\rtf1}")]
    [InlineData("{\\rtf1 \\'xz}")]
    [InlineData("{\\rtf1 \\u999999?}")]
    [InlineData("{\\rtf1 \\fs99999999999999999999999}")]
    [InlineData("{\\rtf1\\ansi\\ansicpg932 \\'82}")]
    [InlineData("{\\rtf1{\\*\\unknown \\bin10 abc}}")]
    [InlineData("{\\rtf1{\\pict\\pngblip ABC}}")]
    [InlineData("{\\rtf1\\trowd\\cellx1000 missing cell\\row}")]
    public void Malformed_input_is_rejected_instead_of_becoming_empty_success(string rtf) => Assert.Throws<FormatException>(() => DocumentFormats.Rtf.Parse(rtf));

    [Fact]
    public void Ignored_destinations_cannot_bypass_nesting_limits() => Assert.Throws<FormatException>(() => DocumentFormats.Rtf.Parse("{\\rtf1{\\*\\unknown " + new string('{', 130) + "hidden" + new string('}', 132)));

    [Fact]
    public async Task Numeric_clamping_and_dimension_rounding_are_reported()
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes("{\\rtf1\\fs5000\\li-120 text}"));
        var imported = await DocumentFormats.Rtf.LoadWithReportAsync(input);
        Assert.Contains(imported.Report.Diagnostics, d => d.Code == "rtf.value-range" && d.UnsupportedFeature == "Font size");
        Assert.Contains(imported.Report.Diagnostics, d => d.Code == "rtf.value-range" && d.UnsupportedFeature == "Left indent");
        var paragraph = new Paragraph("fraction") { Style = new() { Indent = 0.01, LineHeight = 0.01 } };
        using var output = new MemoryStream();
        var exported = await DocumentFormats.Rtf.SaveWithReportAsync(new([paragraph]), output);
        Assert.Contains(exported.Report.Diagnostics, d => d.Code == "rtf.dimension-precision" && d.ModelId == paragraph.Id);
        output.Position = 0;
        Assert.NotNull(Assert.IsType<Paragraph>((await DocumentFormats.Rtf.LoadAsync(output)).Blocks[0]).Style.LineHeight);
    }

    [Fact]
    public async Task Declared_code_page_decodes_raw_bytes_and_contiguous_multibyte_escapes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var cyrillic = new MemoryStream(Encoding.GetEncoding(1251).GetBytes("{\\rtf1\\ansi\\ansicpg1251 Привет}"));
        Assert.Equal("Привет", (await DocumentFormats.Rtf.LoadAsync(cyrillic)).Text);
        Assert.Equal("あ", DocumentFormats.Rtf.Parse("{\\rtf1\\ansi\\ansicpg932 \\'82\\'a0}").Text);
        Assert.Equal("あ!", DocumentFormats.Rtf.Parse("{\\rtf1\\ansi\\ansicpg932\\uc2\\u12354\\'82\\'a0!}").Text);
        using var japanese = new MemoryStream(Encoding.GetEncoding(932).GetBytes("{\\rtf1\\ansi\\ansicpg932 あいう}"));
        Assert.Equal("あいう", (await DocumentFormats.Rtf.LoadAsync(japanese)).Text);
    }

    [Fact]
    public async Task Declared_default_font_is_applied_initially_and_after_plain_reset()
    {
        var document = DocumentFormats.Rtf.Parse("{\\rtf1\\ansi\\deff1{\\fonttbl{\\f0 Arial;}{\\f1 Courier New;}}text{\\f0 other}\\plain reset}");
        var runs = Assert.IsType<Paragraph>(document.Blocks[0]).Runs;
        Assert.Equal("Courier New", runs[0].Style.FontFamily);
        Assert.Equal("Arial", runs[1].Style.FontFamily);
        Assert.Equal("Courier New", runs[2].Style.FontFamily);
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("{\\rtf1\\f9\\cf7\\highlight8 text}"));
        var result = await DocumentFormats.Rtf.LoadWithReportAsync(stream);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "rtf.undefined-font");
        Assert.Equal(2, result.Report.Diagnostics.Count(d => d.Code == "rtf.undefined-color"));
    }

    [Fact]
    public void Section_break_creates_both_content_regions_without_a_phantom_trailing_section()
    {
        var document = DocumentFormats.Rtf.Parse("{\\rtf1 one\\sect two}");
        Assert.Equal(new[] { "one", "two" }, document.Blocks.Cast<Section>().Select(s => Assert.IsType<Paragraph>(Assert.Single(s.Blocks)).Text));
        Assert.Single(DocumentFormats.Rtf.Parse("{\\rtf1 one\\sect}").Blocks);
    }

    [Theory]
    [InlineData("{\\rtf1{\\listtext 1.\\tab}Item}", "rtf.orphan-list-marker")]
    [InlineData("{\\rtf1{\\pntext 1.\\tab}Item}", "rtf.orphan-list-marker")]
    [InlineData("{\\rtf1{\\*\\listtable{\\list{\\listlevel\\levelnfc0{\\leveltext\\'01x;}}\\listid1}}}", "rtf.list-pattern")]
    [InlineData("{\\rtf1{\\*\\listtable{\\list{\\listlevel\\levelnfc0{\\leveltext\\'03\\'00.\\'00;}}\\listid1}}}", "rtf.list-pattern")]
    public async Task Unresolved_or_unrepresentable_list_markers_are_reported(string rtf, string code)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
        var result = await DocumentFormats.Rtf.LoadWithReportAsync(stream);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == code);
    }

    [Theory]
    [InlineData("{\\rtf1\\trowd\\cellx3000\\pard\\intbl before\\par\\trowd\\cellx1000\\cellx2000\\pard\\intbl\\itap2 nested one\\nestcell nested two\\nestcell\\nestrow\\pard\\intbl\\itap1 after\\cell\\row}")]
    [InlineData("{\\rtf1\\trowd\\cellx3000\\pard\\intbl before\\par\\pard\\intbl\\itap2 nested one\\nestcell nested two\\nestcell{\\*\\nesttableprops\\trowd\\cellx1000\\cellx2000\\nestrow}{\\nonesttables\\par}\\pard\\intbl\\itap1 after\\cell\\row}")]
    public async Task Nested_rtf_rows_flatten_inside_parent_cell_with_an_explicit_diagnostic(string rtf)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
        var result = await DocumentFormats.Rtf.LoadWithReportAsync(stream);
        var table = Assert.IsType<Table>(Assert.Single(result.Document.Blocks));
        Assert.Equal(new[] { "before", "nested one", "nested two", "after" }, table.Rows[0][0].Paragraphs.Select(p => p.Text));
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "rtf.nested-table");
        result.Document.Validate();
    }

    [Fact]
    public void List_definition_inherits_across_identified_items_and_anonymous_lists_stay_container_local()
    {
        var first = new Paragraph("first") { Style = new() { List = ListKind.Numbered, ListId = Guid.NewGuid(), ListDefinition = new() { Levels = [new() { Start = 4, Marker = ListMarkerStyle.LowerLetter, Suffix = ";" }] } } };
        var next = new Paragraph("next") { Style = first.Style with { ListDefinition = null } };
        var outer = new Paragraph("outer") { Style = new() { List = ListKind.Numbered } };
        var inner = new Paragraph("inner") { Style = new() { List = ListKind.Numbered } };
        var document = new FlowDocument([first, next, outer, new Section { Blocks = [inner] }]);
        var restored = DocumentFormats.Rtf.Parse(DocumentFormats.Rtf.Serialize(document));
        var markers = new DocumentIndex(restored).Paragraphs.Select(p => ListNumbering.GetMarker(restored, p.Paragraph.Id)!.Text);
        Assert.Equal(new[] { "d;", "e;", "1.", "1." }, markers);
    }

    [Fact]
    public void Nested_list_restart_preserves_current_ancestor_and_later_default_restart()
    {
        var style = new ParagraphStyle { List = ListKind.Numbered, ListId = Guid.NewGuid(), ListDefinition = new() { Levels = [new(), new() { IncludeAncestors = true }] } };
        Paragraph Item(string text, int level = 0, int? start = null) => new(text) { Style = style with { ListLevel = level, ListStart = start } };
        var document = new FlowDocument([Item("one"), Item("two"), Item("child", 1), Item("restart child", 1, 8), Item("next child", 1), Item("three"), Item("default child", 1)]);
        var restored = DocumentFormats.Rtf.Parse(DocumentFormats.Rtf.Serialize(document));
        Assert.Equal(new[] { "1.", "2.", "2.1.", "2.8.", "2.9.", "3.", "3.1." }, new DocumentIndex(restored).Paragraphs.Select(p => ListNumbering.GetMarker(restored, p.Paragraph.Id)!.Text));
    }
}
