using System.Collections.Immutable;
using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class MarkdownCodecTests
{
    private static readonly MarkdownDocumentFormat Format = new();
    private static MemoryStream Source(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Dialect_fixture_preserves_structures_text_styles_resources_and_code_language()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Markdown", "dialect.md"));
        using var input = Source(source);
        var imported = await Format.LoadWithReportAsync(input, new() { Mode = ConversionMode.Strict });
        using var output = new MemoryStream();
        await Format.SaveWithReportAsync(imported.Document, output, new() { Mode = ConversionMode.Strict });
        output.Position = 0;
        var again = await Format.LoadWithReportAsync(output, new() { Mode = ConversionMode.Strict });
        Assert.Equal(imported.Document.PlainText, again.Document.PlainText);
        Assert.Equal(Semantics(imported.Document), Semantics(again.Document));
        var code = Assert.Single(imported.Document.Blocks.OfType<Section>(), s => s.Semantic == SectionSemantic.CodeBlock);
        Assert.Equal("csharp", code.CodeLanguage);
        Assert.Equal("var value = \"```\";\n\n// end", new FlowDocument(code.Blocks).Text);
        Assert.Equal("resource:demo.logo", Assert.Single(imported.Document.Resources).Value.Location);
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    [Theory]
    [InlineData("a`b")]
    [InlineData("``")]
    [InlineData(" padded ")]
    [InlineData(" ")]
    [InlineData("*not emphasis* [x](unsafe)")]
    public void Inline_code_selects_a_safe_delimiter_and_preserves_exact_text(string code)
    {
        var document = new FlowDocument([new Paragraph([new RichRun(code, TextStyle.Default with { IsCode = true, FontFamily = "monospace" })])]);
        var restored = Format.Parse(Format.Serialize(document));
        Assert.Equal(code, restored.Text);
        Assert.True(Assert.IsType<Paragraph>(restored.Blocks[0]).Runs[0].Style.IsCode);
    }

    [Fact]
    public void Export_escapes_literal_block_and_inline_syntax()
    {
        var document = FlowDocument.FromText("# ordinary heading\n1. ordinary list\n> ordinary quote\n``` not code\n[a](https://x.test) *literal* _literal_ \\ & <b> \u2028break");
        Assert.Equal(document.Text, Format.Parse(Format.Serialize(document)).Text);
    }

    [Fact]
    public async Task Raw_html_unsafe_links_and_optional_extensions_are_reported_without_activation()
    {
        using var source = Source("<script>alert(1)</script> [bad](javascript:alert(1)) ![local](file:///private.png)\n\n- [x] todo\n\n| Name | Value |\n| --- | --- |\n| x | y |");
        var loaded = await Format.LoadWithReportAsync(source);
        Assert.Empty(loaded.Document.Resources);
        Assert.All(new DocumentIndex(loaded.Document).Paragraphs.SelectMany(p => p.Paragraph.Runs), r => Assert.Null(r.Style.Hyperlink));
        Assert.Contains("<script>alert(1)</script>", loaded.Document.PlainText);
        foreach (var code in new[] { "markdown.raw-html", "markdown.unsafe-link", "markdown.image", "markdown.task-list", "markdown.table" })
            Assert.Contains(loaded.Report.Diagnostics, d => d.Code == code);
        source.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => Format.LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Unsupported_formatting_and_inline_controls_are_diagnosed_before_strict_output()
    {
        var document = new FlowDocument([new Paragraph([
            new RichRun("formatted", TextStyle.Default with { Underline = true, Foreground = "#123456" }),
            new RichRun(new InlineDescriptor { Payload = new ControlInlinePayload("example"), AltText = "control" })
        ]) { Style = ParagraphStyle.Default with { Alignment = ParagraphAlignment.Center } }, Table.Create(1, 1)]);
        using var output = new MemoryStream();
        output.WriteByte(42);
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() => Format.SaveWithReportAsync(document, output, new() { Mode = ConversionMode.Strict }));
        Assert.Equal(new byte[] { 42 }, output.ToArray());
        foreach (var code in new[] { "markdown.character-formatting", "markdown.paragraph-formatting", "markdown.inline", "markdown.table" })
            Assert.Contains(error.Report.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public async Task Embedded_raster_images_round_trip_and_remote_references_remain_opaque()
    {
        using var source = Source("![pixel](data:image/png;base64,AQID) ![remote](https://example.com/image.png)");
        var loaded = await Format.LoadWithReportAsync(source, new() { Mode = ConversionMode.Strict });
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.Document.Resources.Values.Single(r => r.Kind == DocumentResourceKind.Embedded).Data);
        var host = loaded.Document.Resources.Values.Single(r => r.Kind == DocumentResourceKind.Host);
        Assert.Equal("https://example.com/image.png", host.Location);
        var again = Format.Parse(Format.Serialize(loaded.Document));
        Assert.Equal(loaded.Document.PlainText, again.PlainText);
        Assert.Equal(loaded.Document.Resources.Values.OrderBy(r => r.Kind), again.Resources.Values.OrderBy(r => r.Kind));
    }

    [Fact]
    public async Task Unfinished_stream_fence_is_visible_and_diagnosed_then_closing_it_preserves_code()
    {
        using var source = Source("```cs\nfirst\n");
        var loaded = await Format.LoadWithReportAsync(source);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "markdown.unclosed-fence");
        Assert.Equal("first\n", new FlowDocument(Assert.IsType<Section>(loaded.Document.Blocks[0]).Blocks).Text);
        var again = Format.Parse(Format.Serialize(loaded.Document));
        Assert.Equal(loaded.Document.Text, again.Text);
    }

    [Fact]
    public void Parser_rejects_excessive_nesting_and_observes_cancellation()
    {
        Assert.Throws<FormatException>(() => Format.Parse(new string('>', 34) + "too deep"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Format.Parse("text", cancellation.Token));
    }

    [Fact]
    public void Native_schema_preserves_quote_code_and_inline_semantics()
    {
        var original = Format.Parse("> Quote\n\n```csharp\ncode\n```\n\n`inline`");
        var json = DocumentFormats.Json.Serialize(original);
        Assert.Contains("\"version\": 5", json);
        Assert.Equal(json, DocumentFormats.Json.Serialize(DocumentFormats.Json.Parse(json)));
    }

    [Fact]
    public void Invalid_section_metadata_is_rejected()
    {
        Assert.Throws<FormatException>(() => new FlowDocument([new Section { CodeLanguage = "cs" }]).Validate());
        Assert.Throws<FormatException>(() => new FlowDocument([new Section { Semantic = SectionSemantic.CodeBlock, CodeLanguage = "cs\nscript" }]).Validate());
    }

    [Fact]
    public async Task Styled_code_link_labels_and_angle_destinations_preserve_literal_delimiters()
    {
        var original = Format.Parse("**before `** ]` after** and [`a]b`](<https://example.com/a (b)>)");
        using var output = new MemoryStream();
        await Format.SaveWithReportAsync(original, output, new() { Mode = ConversionMode.Strict });
        output.Position = 0;
        var restored = await Format.LoadWithReportAsync(output, new() { Mode = ConversionMode.Strict });
        Assert.Equal(Semantics(original), Semantics(restored.Document));
        Assert.Equal("before ** ] after and a]b", restored.Document.Text);
    }

    [Fact]
    public async Task Alternative_text_line_breaks_and_custom_code_typefaces_report_their_loss()
    {
        var document = new FlowDocument([new Paragraph([
            new RichRun(new InlineDescriptor { AltText = "two\nlines", Payload = new ControlInlinePayload("widget") }),
            new RichRun("code", TextStyle.Default with { IsCode = true, FontFamily = "Custom font" })
        ])]);
        using var output = new MemoryStream();
        var result = await Format.SaveWithReportAsync(document, output);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "markdown.image-alt-break");
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "markdown.character-formatting");
        Assert.Equal("two linescode", Format.Parse(Encoding.UTF8.GetString(output.ToArray())).Text);
    }

    [Fact]
    public async Task Headings_use_editor_heading_sizes_and_round_trip_without_style_loss()
    {
        var document = Format.Parse("# Title\n\n## *Subtitle*\n\n###### Small");
        var headings = document.Blocks.Cast<Paragraph>().ToArray();
        Assert.Equal(new double[] { 32, 26, 16 }, headings.Select(p => p.DefaultStyle.FontSize));
        Assert.All(headings, p => { Assert.True(p.DefaultStyle.Bold); Assert.Equal(12, p.Style.SpaceBefore); });
        Assert.True(headings[1].Runs[0].Style.Italic);
        using var stream = new MemoryStream();
        await Format.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        stream.Position = 0;
        var restored = await Format.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Equal(Semantics(document), Semantics(restored.Document));
        Assert.Equal(headings.Select(p => p.DefaultStyle), restored.Document.Blocks.Cast<Paragraph>().Select(p => p.DefaultStyle));
    }

    [Fact]
    public async Task Nested_quote_diagnostics_report_absolute_source_lines()
    {
        using var stream = Source("intro\n\n> outer\n>\n> > [bad](javascript:evil)");
        var result = await Format.LoadWithReportAsync(stream);
        Assert.Equal("line 5", Assert.Single(result.Report.Diagnostics, d => d.Code == "markdown.unsafe-link").SourceLocation);
    }

    private static string Semantics(FlowDocument document)
    {
        var builder = new StringBuilder();
        void Blocks(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
                if (block is Section section) { builder.Append($"section:{section.Semantic}:{section.CodeLanguage}["); Blocks(section.Blocks); builder.Append(']'); }
                else if (block is Paragraph p)
                {
                    builder.Append($"paragraph:{p.Style.HeadingLevel}:{p.Style.List}:{p.Style.ListLevel}:");
                    foreach (var run in p.Runs) builder.Append($"({run.PlainText}|{run.Style.Bold}|{run.Style.Italic}|{run.Style.IsCode}|{run.Style.Hyperlink})");
                    builder.AppendLine();
                }
        }
        Blocks(document.Blocks); return builder.ToString();
    }
}
