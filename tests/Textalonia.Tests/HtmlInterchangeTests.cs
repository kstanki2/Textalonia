using System.Collections.Immutable;
using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class HtmlInterchangeTests
{
    private static FlowDocument RoundTrip(FlowDocument document) => DocumentFormats.Html.Parse(DocumentFormats.Html.Serialize(document));
    private static Paragraph[] Paragraphs(FlowDocument document) => new DocumentIndex(document).Paragraphs.Select(p => p.Paragraph).ToArray();

    [Fact]
    public void Nested_lists_preserve_identity_starts_restarts_and_continuation_across_sections()
    {
        var id = Guid.NewGuid();
        var definition = new ListDefinition { Levels = [new() { Marker = ListMarkerStyle.UpperRoman, Start = 3 }, new() { Marker = ListMarkerStyle.LowerLetter }] };
        Paragraph Item(string text, int level = 0, int? start = null, bool restart = false) => new(text)
        { Style = new() { List = ListKind.Numbered, ListId = id, ListDefinition = definition, ListLevel = level, ListStart = start, ListRestart = restart } };
        var source = new FlowDocument([Item("parent", start: 4), Item("child", 1), Item("another"), new Paragraph("gap"), new Section { Blocks = [Item("continued"), Item("restart", restart: true)] }]);
        var restored = RoundTrip(source);
        Assert.Equal(source.Text, restored.Text);
        Assert.Equal(Paragraphs(source).Select(p => p.Style), Paragraphs(restored).Select(p => p.Style));
        Assert.Equal(Paragraphs(source).Select(p => ListNumbering.GetMarker(source, p.Id)?.Text), Paragraphs(restored).Select(p => ListNumbering.GetMarker(restored, p.Id)?.Text));
        Assert.Contains("<ol", DocumentFormats.Html.Serialize(source));
    }

    [Fact]
    public void Ordinary_browser_lists_keep_nested_items_and_li_value()
    {
        var document = DocumentFormats.Html.Parse("<ol start='4' type='I'><li>parent<ol type='a'><li>child</li><li value='5'>restart</li></ol></li><li><p>next</p></li></ol>");
        var paragraphs = Paragraphs(document);
        Assert.Equal(new[] { "parent", "child", "restart", "next" }, paragraphs.Select(p => p.Text));
        Assert.Equal(new[] { 0, 1, 1, 0 }, paragraphs.Select(p => p.Style.ListLevel));
        Assert.Single(paragraphs.Select(p => p.Style.ListId).Distinct());
        Assert.Equal(new[] { "IV.", "a.", "e.", "V." }, paragraphs.Select(p => ListNumbering.GetMarker(document, p.Id)!.Text));
        Assert.Equal(document.Text, RoundTrip(document).Text);
    }

    [Fact]
    public void Empty_and_skipped_level_list_items_round_trip_without_phantom_paragraphs()
    {
        var source = new FlowDocument([
            new Paragraph() { Style = new() { List = ListKind.Numbered, ListLevel = 2 } },
            new Paragraph("child") { Style = new() { List = ListKind.Bullet, ListLevel = 3 } },
            new Paragraph("  \t\u2028") { Style = new() { List = ListKind.Numbered } },
            new Paragraph("end")]);
        var restored = RoundTrip(source);
        Assert.Equal(source.Text, restored.Text);
        Assert.Equal(Paragraphs(source).Select(p => p.Style), Paragraphs(restored).Select(p => p.Style));
    }

    [Fact]
    public void Rich_typography_paragraph_metrics_and_empty_defaults_round_trip()
    {
        var textStyle = new TextStyle { FontFamily = "Example Font", FontSize = 23.5, FontWeight = 650, FontStretch = 3, Bold = false,
            Italic = true, Underline = true, Strikethrough = true, Foreground = "#80123456", Background = "#FEDCBA", Baseline = Baseline.Superscript, Hyperlink = "https://example.com/?a=1&b=2" };
        var paragraphStyle = new ParagraphStyle { HeadingLevel = 2, SpaceBefore = 7, SpaceAfter = 9, Indent = 11, RightIndent = 21,
            FirstLineIndent = -4, LineHeight = 28.5, LetterSpacing = 1.75, Alignment = ParagraphAlignment.Justify, RightToLeft = true };
        var source = new FlowDocument([new Paragraph([new RichRun("a  b\tc\u2028d", textStyle), new RichRun("plain", TextStyle.Default)]) { DefaultStyle = textStyle, Style = paragraphStyle },
            new Paragraph() { DefaultStyle = textStyle, Style = paragraphStyle }]);
        var restored = RoundTrip(source);
        var paragraphs = Paragraphs(restored);
        Assert.Equal(source.Text, restored.Text);
        Assert.All(paragraphs, p => { Assert.Equal(paragraphStyle, p.Style); Assert.Equal(textStyle, p.DefaultStyle); });
        Assert.Equal(Paragraphs(source)[0].Runs.Select(r => r.Style), paragraphs[0].Runs.Select(r => r.Style));
    }

    [Fact]
    public void Sections_nested_tables_edges_and_sizing_round_trip()
    {
        var inner = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("nested")] });
        var edges = new EdgeInsets(3, 4, 5, 6);
        var borders = new BlockBorders(new(2, "#123456"), null, new(4, "#80ABCDEF"));
        var table = Table.Create(2, 2) with { ColumnWidths = [2, 5], RowSizing = [new() { Mode = TableRowHeightMode.AtLeast, Height = 23 }, new() { Mode = TableRowHeightMode.Exact, Height = 40 }] };
        table = table.SetCell(0, 0, new TableCell { Blocks = [new Paragraph("outer"), inner], Padding = edges, Borders = borders, Background = "#AABBCC" });
        var source = new FlowDocument([new Section { Blocks = [table], Padding = 8, PaddingEdges = edges, Borders = borders, BorderColor = "#112233", Background = "#DDEEFF" }]);
        var section = Assert.IsType<Section>(RoundTrip(source).Blocks[0]);
        Assert.Equal(edges, section.PaddingEdges); Assert.Equal(borders, section.Borders); Assert.Equal(8, section.Padding); Assert.Equal("#112233", section.BorderColor);
        var restored = Assert.IsType<Table>(Assert.Single(section.Blocks));
        Assert.Equal(table.ColumnWidths.ToArray(), restored.ColumnWidths.ToArray()); Assert.Equal(table.RowSizing.ToArray(), restored.RowSizing.ToArray());
        var cell = restored.Rows[0][0];
        Assert.Equal(edges, cell.Padding); Assert.Equal(borders, cell.Borders); Assert.Equal("#AABBCC", cell.Background);
        Assert.Equal("nested", Assert.IsType<Paragraph>(Assert.IsType<Table>(cell.Blocks[1]).Rows[0][0].Blocks[0]).Text);
        Assert.Equal(source.Text, new FlowDocument([section]).Text);
    }

    [Fact]
    public void Embedded_images_preserve_bytes_dimensions_alt_text_and_deduplicate_resources()
    {
        var data = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==").ToImmutableArray();
        var inline = new InlineDescriptor { AltText = "A & B", Width = 37.5, Height = 19, Payload = new ImageInlinePayload("image") };
        var source = new FlowDocument([new Paragraph([new RichRun("before"), new RichRun(inline), new RichRun(inline with { Id = Guid.NewGuid() }), new RichRun("after")])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", new() { MediaType = "image/png", Data = data }) };
        var restored = RoundTrip(source);
        Assert.Equal(source.Text, restored.Text); Assert.Equal(source.PlainText, restored.PlainText);
        var resource = Assert.Single(restored.Resources).Value; Assert.Equal(data.ToArray(), resource.Data.ToArray()); Assert.Equal("image/png", resource.MediaType);
        var images = Paragraphs(restored)[0].Runs.Where(r => r.Inline is not null).Select(r => r.Inline!).ToArray();
        Assert.Equal(2, images.Length); Assert.NotEqual(images[0].Id, images[1].Id);
        Assert.All(images, image => { Assert.Equal(37.5, image.Width); Assert.Equal(19, image.Height); Assert.Equal("A & B", image.AltText); });
    }

    [Fact]
    public async Task Unsupported_elements_styles_links_and_external_resources_have_deterministic_diagnostics()
    {
        const string html = "<p style='position:absolute'>safe<script>hidden</script><a href='javascript:alert(1)'>link</a><img src='https://example.invalid/a.png' alt='remote'></p>";
        using var first = new MemoryStream(Encoding.UTF8.GetBytes(html));
        using var second = new MemoryStream(Encoding.UTF8.GetBytes(html));
        var a = await DocumentFormats.Html.LoadWithReportAsync(first);
        var b = await DocumentFormats.Html.LoadWithReportAsync(second);
        Assert.Equal("safelinkremote", a.Document.Text);
        Assert.Equal(a.Report.Diagnostics.ToArray(), b.Report.Diagnostics.ToArray());
        Assert.Equal(new[] { "html.unsupported-style", "html.unsupported-element", "html.unsafe-link", "html.resource" }, a.Report.Diagnostics.Select(d => d.Code));
        Assert.All(a.Report.Diagnostics, diagnostic => Assert.NotNull(diagnostic.SourceLocation));
    }

    [Fact]
    public async Task Unsupported_inline_and_browser_approximations_are_reported_on_export()
    {
        var paragraph = new Paragraph([new RichRun(new InlineDescriptor { AltText = "control", Payload = new ControlInlinePayload("widget") })])
        { Style = new() { List = ListKind.Numbered, ListDefinition = new() { Levels = [new() { Prefix = "(", Suffix = ")" }] } } };
        var table = Table.Create(1, 1) with { RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 20 }] };
        using var stream = new MemoryStream();
        var result = await DocumentFormats.Html.SaveWithReportAsync(new FlowDocument([paragraph, table]), stream);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "html.resource");
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "html.list-marker");
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "html.row-height");
        Assert.Contains("control", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Theory]
    [InlineData("<p> a  <b> b </b> c </p>", "a b c")]
    [InlineData("<p>a<span> </span> <i>b</i>&nbsp;&nbsp;c<br> d </p>", "a b\u00a0\u00a0c\u2028d")]
    [InlineData("<div style='white-space:pre-wrap'><p> a  <b> b </b>\tc </p></div>", " a   b \tc ")]
    [InlineData("<pre style='white-space:normal'> a  b </pre>", "a b")]
    [InlineData("<p>a<span style='white-space:pre'>  b  </span> c</p>", "a  b   c")]
    public void Browser_whitespace_collapses_across_inline_boundaries_and_preserves_inherited_pre(string html, string expected)
    {
        Assert.Equal(expected, DocumentFormats.Html.Parse(html).Text);
    }

    [Fact]
    public async Task Numeric_approximations_report_loss_and_strict_import_rejects_them()
    {
        const string html = "<p style='font-size:999px;line-height:20000px;margin-left:2000px'>large</p><p><img src='data:image/png;base64,AQ==' width='20000' height='-1'></p>";
        using var tolerant = new MemoryStream(Encoding.UTF8.GetBytes(html));
        var result = await DocumentFormats.Html.LoadWithReportAsync(tolerant);
        Assert.True(result.Report.Diagnostics.Count(d => d.Code == "html.value-range") >= 5);
        var paragraph = Paragraphs(result.Document)[0];
        Assert.Equal(512, paragraph.DefaultStyle.FontSize); Assert.Equal(10000, paragraph.Style.LineHeight); Assert.Equal(1000, paragraph.Style.Indent);
        using var strict = new MemoryStream(Encoding.UTF8.GetBytes(html));
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Html.LoadWithReportAsync(strict, new() { Mode = ConversionMode.Strict }));
    }

    [Theory]
    [InlineData("<table><tr><td colspan='-1'>bad</td></tr></table>")]
    [InlineData("<table><tr><td rowspan='2'>bad</td></tr></table>")]
    [InlineData("<table><tr><td>A</td><td rowspan='2'>B</td></tr><tr><td colspan='2'>overlap</td></tr></table>")]
    [InlineData("<p><img src='data:image/png;base64,!!!'></p>")]
    [InlineData("<ol data-textalonia-list-id='not-a-guid'><li>bad</li></ol>")]
    [InlineData("<ol><li data-textalonia-list='true' data-textalonia-list-start='0'>bad</li></ol>")]
    [InlineData("<p data-textalonia-bold='not-boolean'>bad</p>")]
    public void Malformed_images_metadata_and_table_geometry_are_errors(string html) => Assert.Throws<FormatException>(() => DocumentFormats.Html.Parse(html));
}
