using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class MergeFieldInterchangeTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static IDocumentFormat Format(bool docx) => docx ? DocumentFormats.Docx : DocumentFormats.Rtf;
    private static MemoryStream Package(string body, string? settings = null)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false)))
                writer.Write($"<w:document xmlns:w='{W}'><w:body>{body}</w:body></w:document>");
            if (settings is not null)
            {
                using var writer = new StreamWriter(zip.CreateEntry("word/settings.xml").Open(), new UTF8Encoding(false));
                writer.Write($"<w:settings xmlns:w='{W}'>{settings}</w:settings>");
            }
        }
        stream.Position = 0;
        return stream;
    }
    private static string Simple(string instruction, string cached) =>
        new XElement(W + "fldSimple", new XAttribute(W + "instr", instruction),
            new XElement(W + "r", new XElement(W + "t", cached))).ToString(SaveOptions.DisableFormatting);
    private static string EscapeRtf(string text) => text.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
    private static MemoryStream Input(bool docx, string instruction, string cached = "Alice") => docx
        ? Package("<w:p>" + Simple(instruction, cached) + "</w:p>")
        : new(Encoding.ASCII.GetBytes(@"{\rtf1{\field{\*\fldinst " + EscapeRtf(instruction) + @"}{\fldrslt " + EscapeRtf(cached) + "}}}"));
    private static RichRun OnlyField(FlowDocument document)
    {
        var run = Assert.Single(Assert.IsType<Paragraph>(Assert.Single(document.Blocks)).Runs);
        Assert.IsType<MergeFieldInlinePayload>(Assert.IsType<InlineDescriptor>(run.Inline).Payload);
        return run;
    }

    [Theory]
    [InlineData(true, "Name")]
    [InlineData(false, "Name")]
    [InlineData(true, "Given Name")]
    [InlineData(false, "Given Name")]
    [InlineData(true, "氏名")]
    [InlineData(false, "氏名")]
    [InlineData(true, "A\"B\\C")]
    [InlineData(false, "A\"B\\C")]
    public async Task Field_names_cached_values_and_character_style_round_trip(bool docx, string name)
    {
        var style = new TextStyle { FontFamily = "Arial", Bold = true, Italic = true, Foreground = "#112233" };
        var document = new FlowDocument([new Paragraph([new RichRun(new InlineDescriptor
        {
            Payload = new MergeFieldInlinePayload(name), AltText = "Zoë & Co", Width = 120, Height = 24
        }, style)])]);
        using var output = new MemoryStream();
        var saved = await Format(docx).SaveWithReportAsync(document, output, new() { Mode = ConversionMode.Strict });
        Assert.False(saved.Report.HasLoss);
        output.Position = 0;
        var loaded = await Format(docx).LoadWithReportAsync(output, new() { Mode = ConversionMode.Strict });
        var run = OnlyField(loaded.Document);
        Assert.Equal(name, ((MergeFieldInlinePayload)run.Inline!.Payload).Name);
        Assert.Equal("Zoë & Co", run.Inline.AltText);
        var paragraph = Assert.IsType<Paragraph>(loaded.Document.Blocks[0]);
        Assert.Equal(style, new DocumentStyleResolver(loaded.Document).ResolveText(paragraph, run.Style));
        Assert.Equal("\uFFFC", loaded.Document.Text);
        Assert.Equal("Zoë & Co", loaded.Document.PlainText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Plain_and_quoted_instructions_and_empty_cached_results_are_supported(bool docx)
    {
        foreach (var instruction in new[] { "MERGEFIELD Name", " mergefield \"Name\" \\* MERGEFORMAT " })
        {
            using var source = Input(docx, instruction, "");
            var loaded = await Format(docx).LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict });
            Assert.Equal("", OnlyField(loaded.Document).Inline!.AltText);
            using var saved = new MemoryStream();
            await Format(docx).SaveWithReportAsync(loaded.Document, saved, new() { Mode = ConversionMode.Strict });
            saved.Position = 0;
            Assert.Equal("", OnlyField((await Format(docx).LoadWithReportAsync(saved)).Document).Inline!.AltText);
        }
    }

    [Fact]
    public async Task Complex_docx_field_joins_split_instructions_and_keeps_cached_style_and_surrounding_text()
    {
        using var source = Package("""
            <w:p><w:r><w:t>Dear </w:t></w:r>
            <w:r><w:fldChar w:fldCharType="begin"/></w:r>
            <w:r><w:instrText xml:space="preserve"> MERGE</w:instrText></w:r>
            <w:r><w:instrText xml:space="preserve">FIELD "Given Name" \* MERGEFORMAT </w:instrText></w:r>
            <w:r><w:fldChar w:fldCharType="separate"/></w:r>
            <w:r><w:rPr><w:b/></w:rPr><w:t>Ali</w:t></w:r>
            <w:r><w:rPr><w:b/></w:rPr><w:t>ce</w:t></w:r>
            <w:r><w:fldChar w:fldCharType="end"/></w:r>
            <w:r><w:t>!</w:t></w:r></w:p>
            """);
        var result = await DocumentFormats.Docx.LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict });
        var field = Assert.Single(Assert.IsType<Paragraph>(result.Document.Blocks[0]).Runs.Where(r => r.Inline is not null));
        Assert.Equal("Given Name", Assert.IsType<MergeFieldInlinePayload>(field.Inline!.Payload).Name);
        Assert.Equal("Alice", field.Inline.AltText);
        Assert.True(field.Style.Bold);
        Assert.Equal("Dear Alice!", result.Document.PlainText);
        Assert.Equal("Dear \uFFFC!", result.Document.Text);
    }

    [Fact]
    public async Task Empty_simple_docx_field_retains_cached_run_style()
    {
        using var source = Package("""<w:p><w:fldSimple w:instr="MERGEFIELD Name"><w:r><w:rPr><w:i/></w:rPr></w:r></w:fldSimple></w:p>""");
        var result = await DocumentFormats.Docx.LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict });
        var field = OnlyField(result.Document);
        Assert.Equal("", field.Inline!.AltText);
        Assert.True(field.Style.Italic);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unsupported_semantic_switches_are_reported_and_rejected_by_strict_import(bool docx)
    {
        using var source = Input(docx, "MERGEFIELD Name \\b \"Hello \"");
        var result = await Format(docx).LoadWithReportAsync(source);
        Assert.Equal("Name", ((MergeFieldInlinePayload)OnlyField(result.Document).Inline!.Payload).Name);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == (docx ? "docx" : "rtf") + ".merge-field-switch");
        source.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => Format(docx).LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict }));
    }

    [Theory]
    [InlineData(true, "MERGEFIELD")]
    [InlineData(false, "MERGEFIELD")]
    [InlineData(true, "MERGEFIELD \"unclosed")]
    [InlineData(false, "MERGEFIELD \"unclosed")]
    [InlineData(true, "MERGEFIELD \"\"")]
    [InlineData(false, "MERGEFIELD \"\"")]
    [InlineData(true, "MERGEFIELD \\* MERGEFORMAT")]
    [InlineData(false, "MERGEFIELD \\* MERGEFORMAT")]
    [InlineData(true, "\"MERGEFIELD\" Name")]
    [InlineData(false, "\"MERGEFIELD\" Name")]
    [InlineData(true, "DATE")]
    [InlineData(false, "DATE")]
    public async Task Malformed_or_unsupported_instructions_flatten_with_a_loss_report(bool docx, string instruction)
    {
        using var source = Input(docx, instruction);
        var result = await Format(docx).LoadWithReportAsync(source);
        Assert.Equal("Alice", result.Document.PlainText);
        Assert.All(Assert.IsType<Paragraph>(result.Document.Blocks[0]).Runs, r => Assert.Null(r.Inline));
        Assert.True(result.Report.HasLoss);
        source.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => Format(docx).LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Nested_complex_docx_fields_keep_only_the_outer_cached_display()
    {
        using var source = Package("""
            <w:p>
            <w:r><w:fldChar w:fldCharType="begin"/><w:instrText> IF </w:instrText></w:r>
            <w:r><w:fldChar w:fldCharType="begin"/><w:instrText> MERGEFIELD Name </w:instrText><w:fldChar w:fldCharType="separate"/><w:t>Alice</w:t><w:fldChar w:fldCharType="end"/></w:r>
            <w:r><w:instrText> = "Alice" "Yes" "No" </w:instrText><w:fldChar w:fldCharType="separate"/><w:t>Yes</w:t><w:fldChar w:fldCharType="end"/></w:r>
            </w:p>
            """);
        var result = await DocumentFormats.Docx.LoadWithReportAsync(source);
        Assert.Equal("Yes", result.Document.PlainText);
        Assert.All(Assert.IsType<Paragraph>(result.Document.Blocks[0]).Runs, r => Assert.Null(r.Inline));
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.nested-field");
    }

    [Theory]
    [InlineData("""<w:r><w:fldChar w:fldCharType="begin"/><w:instrText> MERGEFIELD Name </w:instrText><w:fldChar w:fldCharType="separate"/><w:t>Alice</w:t></w:r>""")]
    [InlineData("""<w:r><w:fldChar w:fldCharType="separate"/><w:t>Alice</w:t><w:fldChar w:fldCharType="end"/></w:r>""")]
    [InlineData("""<w:r><w:fldChar w:fldCharType="begin"/><w:instrText> MERGEFIELD Name </w:instrText><w:t>Alice</w:t><w:fldChar w:fldCharType="end"/></w:r>""")]
    public async Task Unmatched_or_malformed_complex_fields_do_not_become_active_fields(string body)
    {
        using var source = Package("<w:p>" + body + "</w:p>");
        var result = await DocumentFormats.Docx.LoadWithReportAsync(source);
        Assert.Equal("Alice", result.Document.PlainText);
        Assert.All(Assert.IsType<Paragraph>(result.Document.Blocks[0]).Runs, r => Assert.Null(r.Inline));
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.field-structure");
        source.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Docx.LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Nested_rtf_fields_flatten_instead_of_activating_inner_merge_fields()
    {
        const string input = """{\rtf1{\field{\*\fldinst MERGEFIELD Outer}{\fldrslt {\field{\*\fldinst MERGEFIELD Inner}{\fldrslt Alice}}}}}""";
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(input));
        var result = await DocumentFormats.Rtf.LoadWithReportAsync(source);
        Assert.Equal("Alice", result.Document.PlainText);
        Assert.All(Assert.IsType<Paragraph>(result.Document.Blocks[0]).Runs, r => Assert.Null(r.Inline));
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "rtf.nested-field");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Native_format_and_missing_value_fallback_are_explicit_export_losses(bool docx)
    {
        var document = new FlowDocument([new Paragraph([new RichRun(new InlineDescriptor
        {
            Payload = new MergeFieldInlinePayload("Balance") { Format = "C2", FallbackText = "Unavailable" },
            AltText = "$12.34", Width = 120, Height = 24
        })])]);
        using var output = new MemoryStream();
        var result = await Format(docx).SaveWithReportAsync(document, output);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == (docx ? "docx" : "rtf") + ".merge-field-format");
        Assert.Contains(result.Report.Diagnostics, d => d.Code == (docx ? "docx" : "rtf") + ".merge-field-fallback");
        output.Position = 0;
        var field = Assert.IsType<MergeFieldInlinePayload>(OnlyField((await Format(docx).LoadWithReportAsync(output)).Document).Inline!.Payload);
        Assert.Equal("Balance", field.Name);
        Assert.Null(field.Format);
        Assert.Null(field.FallbackText);
        using var rejected = new MemoryStream();
        await Assert.ThrowsAsync<DocumentConversionException>(() => Format(docx).SaveWithReportAsync(document, rejected, new() { Mode = ConversionMode.Strict }));
        Assert.Equal(0, rejected.Length);
    }
    [Fact]
    public async Task Linked_recipient_settings_are_diagnosed_without_losing_fields()
    {
        using var source = Package("<w:p>" + Simple("MERGEFIELD Name", "Alice") + "</w:p>",
            """<w:mailMerge><w:mainDocumentType w:val="formLetters"/><w:connectString w:val="https://never-fetch.invalid/recipients"/></w:mailMerge>""");
        var result = await DocumentFormats.Docx.LoadWithReportAsync(source);
        Assert.Equal("Name", ((MergeFieldInlinePayload)OnlyField(result.Document).Inline!.Payload).Name);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.mail-merge-source" && d.SourceLocation!.StartsWith("word/settings.xml:"));
        source.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Docx.LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Excessive_complex_field_nesting_is_bounded_independently_of_xml_depth()
    {
        using var source = Package("<w:p>" + string.Concat(Enumerable.Repeat("""<w:r><w:fldChar w:fldCharType="begin"/></w:r>""", 33)) + "</w:p>");
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Docx.LoadAsync(source));
    }

    [Fact]
    public async Task Matched_complex_field_without_cached_result_remains_an_empty_field()
    {
        using var source = Package("""<w:p><w:r><w:fldChar w:fldCharType="begin"/><w:instrText>MERGEFIELD Name</w:instrText><w:fldChar w:fldCharType="end"/></w:r></w:p>""");
        var result = await DocumentFormats.Docx.LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict });
        Assert.Equal("", OnlyField(result.Document).Inline!.AltText);
    }

    [Fact]
    public async Task Malformed_complex_marker_inside_simple_field_does_not_activate_the_field()
    {
        using var source = Package("""<w:p><w:fldSimple w:instr="MERGEFIELD Name"><w:r><w:fldChar w:fldCharType="end"/><w:t>Alice</w:t></w:r></w:fldSimple></w:p>""");
        var result = await DocumentFormats.Docx.LoadWithReportAsync(source);
        Assert.Equal("Alice", result.Document.PlainText);
        Assert.All(Assert.IsType<Paragraph>(result.Document.Blocks[0]).Runs, r => Assert.Null(r.Inline));
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.field-structure");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mixed_cached_styles_are_diagnosed_when_collapsed_into_an_atomic_field(bool docx)
    {
        using var source = docx
            ? Package("""<w:p><w:fldSimple w:instr="MERGEFIELD Name"><w:r><w:rPr><w:b/></w:rPr><w:t>Ali</w:t></w:r><w:r><w:rPr><w:i/></w:rPr><w:t>ce</w:t></w:r></w:fldSimple></w:p>""")
            : new MemoryStream(Encoding.ASCII.GetBytes("""{\rtf1{\field{\*\fldinst MERGEFIELD Name}{\fldrslt {\b Ali}{\i ce}}}}"""));
        var result = await Format(docx).LoadWithReportAsync(source);
        Assert.Equal("Alice", OnlyField(result.Document).Inline!.AltText);
        Assert.True(OnlyField(result.Document).Style.Bold);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == (docx ? "docx" : "rtf") + ".field-result-formatting");
        source.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => Format(docx).LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict }));
    }

}
