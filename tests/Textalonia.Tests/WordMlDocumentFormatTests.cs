using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class WordMlDocumentFormatTests
{
    private const string Open = "<w:wordDocument xmlns:w='http://schemas.microsoft.com/office/word/2003/wordml' xmlns:wx='http://schemas.microsoft.com/office/word/2003/auxHint'><w:body>";
    private const string Close = "</w:body></w:wordDocument>";
    private readonly WordMlDocumentFormat _format = new();

    [Fact]
    public async Task Supported_paragraphs_round_trip_in_strict_mode()
    {
        var document = new FlowDocument([
            new Paragraph([
                new RichRun("  bold\t", new TextStyle { Bold = true, Italic = true }),
                new RichRun("under\u2028line", new TextStyle { Underline = true }),
                new RichRun(" & end")]) { Style = new ParagraphStyle { Alignment = ParagraphAlignment.Justify } },
            new Paragraph()
        ]);
        using var stream = new MemoryStream();
        var saved = await _format.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(saved.Report.Diagnostics);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        Assert.Equal(XName.Get("wordDocument", WordMlDocumentFormat.NamespaceUri), xml.Root!.Name);
        Assert.Contains("xml:space=\"preserve\"", Encoding.UTF8.GetString(stream.ToArray()));
        stream.Position = 0;
        var loaded = await _format.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(loaded.Report.Diagnostics);
        var paragraphs = new DocumentIndex(loaded.Document).Paragraphs.Select(entry => entry.Paragraph).ToArray();
        Assert.Equal(2, paragraphs.Length);
        Assert.Equal("  bold\tunder\u2028line & end", paragraphs[0].Text);
        Assert.Equal(ParagraphAlignment.Justify, paragraphs[0].Style.Alignment);
        Assert.True(paragraphs[0].Runs[0].Style.Bold);
        Assert.True(paragraphs[0].Runs[0].Style.Italic);
        Assert.Contains(paragraphs[0].Runs, run => run.Style.Underline);
        Assert.Empty(paragraphs[1].Runs);
    }

    [Fact]
    public async Task Word_2003_section_wrapper_retains_text_and_reports_page_loss()
    {
        var xml = Open + "<wx:sect><w:p><w:r><w:t>First</w:t></w:r></w:p><w:p><w:r><w:t>Second</w:t></w:r></w:p></wx:sect>" + Close;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var loaded = await _format.LoadWithReportAsync(stream);
        Assert.Equal(new[] { "First", "Second" }, new DocumentIndex(loaded.Document).Paragraphs.Select(p => p.Paragraph.Text));
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "wordml.section" && d.SourceLocation is not null);
    }

    [Fact]
    public async Task Unsupported_elements_and_attributes_are_explicit_and_strict_import_rejects()
    {
        var xml = Open + "\n<w:p><w:pPr><w:spacing w:before='10'/></w:pPr><w:r w:rsidR='1'><w:t>safe</w:t></w:r></w:p>\n<w:tbl><w:tr><w:tc><w:p><w:r><w:t>omitted</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" + Close;
        using var tolerantStream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var tolerant = await _format.LoadWithReportAsync(tolerantStream);
        Assert.Equal("safe", tolerant.Document.Text);
        Assert.Contains(tolerant.Report.Diagnostics, d => d.Code == "wordml.unsupported-element" && d.SourceLocation!.StartsWith("line "));
        Assert.Contains(tolerant.Report.Diagnostics, d => d.Code == "wordml.unsupported-attribute");
        using var strictStream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await Assert.ThrowsAsync<DocumentConversionException>(() => _format.LoadWithReportAsync(strictStream, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Export_loss_is_reported_before_strict_mode_touches_destination()
    {
        var table = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("cell")] });
        var document = new FlowDocument([new Paragraph([new RichRun("font", new TextStyle { FontFamily = "Example" })]), table]);
        using var stream = new MemoryStream([1, 2, 3]);
        stream.Position = 1;
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() =>
            _format.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict }));
        Assert.Contains(error.Report.Diagnostics, d => d.Code == "wordml.table");
        Assert.Contains(error.Report.Diagnostics, d => d.Code == "wordml.run-style");
        Assert.Equal(new byte[] { 1, 2, 3 }, stream.ToArray());
        Assert.Equal(1, stream.Position);
        using var tolerantStream = new MemoryStream();
        await _format.SaveWithReportAsync(document, tolerantStream);
        tolerantStream.Position = 0;
        var loaded = await _format.LoadWithReportAsync(tolerantStream);
        Assert.Equal("font\ncell", loaded.Document.Text);
    }

    [Fact]
    public async Task Xml_parser_rejects_dtd_foreign_roots_and_excessive_depth()
    {
        Assert.Throws<FormatException>(() => _format.Parse("<!DOCTYPE w:wordDocument [<!ENTITY x 'unsafe'>]>" + Open + Close));
        Assert.Throws<FormatException>(() => _format.Parse("<Document xmlns='urn:textalonia:document:1' Version='6'/>") );
        var nested = Open + string.Concat(Enumerable.Repeat("<w:unknown>", 130)) +
            string.Concat(Enumerable.Repeat("</w:unknown>", 130)) + Close;
        Assert.Throws<FormatException>(() => _format.Parse(nested));

        var utf16 = "<?xml version='1.0' encoding='utf-16'?>" + Open + "<w:p><w:r><w:t>Unicode \u03c0</w:t></w:r></w:p>" + Close;
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(utf16)).ToArray();
        using var stream = new MemoryStream(bytes);
        var loaded = await _format.LoadAsync(stream);
        Assert.Equal("Unicode \u03c0", loaded.Text);
    }
}

