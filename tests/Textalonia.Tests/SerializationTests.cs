using System.IO.Compression;
using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class SerializationTests
{
    private static FlowDocument RichDocument() => new([
        new Paragraph([
            new RichRun("Hello ", TextStyle.Default with { Bold = true }),
            new RichRun("world", TextStyle.Default with { Italic = true, Underline = true, Foreground = "#123ABC", Background = "#FFEABB", FontSize = 20 }),
            new RichRun(" \U0001F469\u200D\U0001F4BB {\\} \u4E2D\u6587")
        ]) { Style = new() { Alignment = ParagraphAlignment.Center } },
        new Paragraph("Second paragraph"),
        new Paragraph("")
    ]);

    [Theory]
    [InlineData("json")]
    [InlineData("html")]
    [InlineData("rtf")]
    [InlineData("docx")]
    [InlineData("txt")]
    public async Task Formats_round_trip_text_and_leave_streams_open(string name)
    {
        var format = name switch
        {
            "json" => (IDocumentFormat)DocumentFormats.Json, "html" => DocumentFormats.Html,
            "rtf" => DocumentFormats.Rtf, "docx" => DocumentFormats.Docx, _ => DocumentFormats.PlainText
        };
        var original = RichDocument();
        using var stream = new MemoryStream();
        await format.SaveAsync(original, stream);
        Assert.True(stream.CanWrite);
        stream.Position = 0;
        var loaded = await format.LoadAsync(stream);
        Assert.True(stream.CanRead);
        Assert.Equal(original.Text, loaded.Text);
        if (name != "txt")
        {
            var first = new DocumentIndex(loaded).Paragraphs[0].Paragraph;
            Assert.True(first.Runs[0].Style.Bold);
            var styled = first.Runs.First(r => r.Text.Contains("world"));
            Assert.True(styled.Style.Italic);
            Assert.True(styled.Style.Underline);
            Assert.Equal("#123ABC", styled.Style.Foreground!.ToUpperInvariant());
            Assert.Equal(20, styled.Style.FontSize, 3);
        }
    }

    [Fact]
    public void Native_format_preserves_sections_and_hidden_merged_cells()
    {
        var table = Table.Create(2, 2).MergeCells(0, 0, 2, 2);
        var document = new FlowDocument([new Section { Blocks = [new Paragraph("nested"), table], Background = "#EEEEEE" }]);
        var encoded = DocumentFormats.Json.Serialize(document);
        Assert.Equal(encoded, DocumentFormats.Json.Serialize(DocumentFormats.Json.Parse(encoded)));
    }

    [Theory]
    [InlineData("html")]
    [InlineData("docx")]
    public async Task Interchange_preserves_table_spans_cell_color_and_hyperlinks(string name)
    {
        IDocumentFormat format = name == "html" ? DocumentFormats.Html : DocumentFormats.Docx;
        var table = Table.Create(3, 3);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Paragraphs = [new Paragraph("top")] });
        table = table.MergeCells(0, 0, 2, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Background = "#DDEEFF" });
        var document = new FlowDocument([table, new Paragraph("example", TextStyle.Default with { Hyperlink = "https://example.com" })]);
        using var stream = new MemoryStream();
        await format.SaveAsync(document, stream); stream.Position = 0;
        var loaded = await format.LoadAsync(stream);
        var restored = Assert.IsType<Table>(loaded.Blocks[0]);
        Assert.Equal(2, restored.Rows[0][0].RowSpan);
        Assert.Equal(2, restored.Rows[0][0].ColumnSpan);
        Assert.Equal("#DDEEFF", restored.Rows[0][0].Background);
        Assert.Equal("https://example.com", Assert.IsType<Paragraph>(loaded.Blocks[1]).Runs[0].Style.Hyperlink);
    }

    [Fact]
    public void Html_import_does_not_execute_or_retain_scripts_or_unsafe_links()
    {
        var document = DocumentFormats.Html.Parse("<p>ok<script>alert(1)</script><a href='javascript:alert(1)'>text</a><img src='https://remote.test/x' alt='image'></p>");
        Assert.Equal("oktextimage", document.Text);
        Assert.All(new DocumentIndex(document).Paragraphs.SelectMany(p => p.Paragraph.Runs), r => Assert.Null(r.Style.Hyperlink));
    }

    [Fact]
    public void Html_round_trip_preserves_multiple_spaces_tabs_and_soft_breaks()
    {
        var document = FlowDocument.FromText("a  b\tc\u2028d");
        Assert.Equal(document.Text, DocumentFormats.Html.Parse(DocumentFormats.Html.Serialize(document)).Text);
    }

    [Fact]
    public void Rtf_handles_unicode_fallback_group_scoping_and_ignored_destinations()
    {
        var document = DocumentFormats.Rtf.Parse("{\\rtf1\\ansi before {\\b bold} plain \\uc1\\u20013?{\\*\\unknown hidden}\\par}");
        Assert.Equal("before bold plain \u4E2D", document.Text);
        var runs = new DocumentIndex(document).Paragraphs[0].Paragraph.Runs;
        Assert.True(runs.First(r => r.Text == "bold").Style.Bold);
        Assert.False(runs[^1].Style.Bold);
        Assert.Throws<FormatException>(() => DocumentFormats.Rtf.Parse("{\\rtf1 unclosed"));
    }

    [Fact]
    public void Invalid_native_documents_and_unsupported_versions_are_rejected()
    {
        var json = DocumentFormats.Json.Serialize(new FlowDocument());
        Assert.Throws<NotSupportedException>(() => DocumentFormats.Json.Parse(json.Replace("\"version\": 1", "\"version\": 99")));
        Assert.Throws<FormatException>(() => new FlowDocument([new Paragraph("a\nb")]).Validate());
        Assert.Throws<FormatException>(() => new FlowDocument([new Paragraph("x", TextStyle.Default with { FontSize = double.NaN })]).Validate());
    }

    [Fact]
    public async Task Docx_rejects_external_entities()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write("<!DOCTYPE doc [<!ENTITY leak SYSTEM 'file:///never-read'>]><doc>&leak;</doc>");
        }
        stream.Position = 0;
        await Assert.ThrowsAsync<System.Xml.XmlException>(() => DocumentFormats.Docx.LoadAsync(stream));
    }
}
