using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class MergeFieldSerializationTests
{
    private static InlineDescriptor Field(string name = "Customer", string? format = null, string? fallback = null) => new()
    {
        AltText = "\u00AB" + name + "\u00BB",
        Payload = new MergeFieldInlinePayload(name) { Format = format, FallbackText = fallback }
    };

    private static TextDocumentFormat Format(string name) => name switch
    {
        "json" => DocumentFormats.Json,
        "xaml" => new XamlDocumentFormat(),
        "html" => new HtmlDocumentFormat(),
        "markdown" => new MarkdownDocumentFormat(),
        "text" => new PlainTextDocumentFormat(),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static string Xml(string payload, string inlineAttributes = "") =>
        $"<Document xmlns='{XamlDocumentFormat.NamespaceUri}' Version='1'><Blocks><Paragraph><Runs><Run><Inline {inlineAttributes}>{payload}</Inline></Run></Runs></Paragraph></Blocks></Document>";

    [Theory]
    [InlineData("json")]
    [InlineData("xaml")]
    public async Task Native_formats_preserve_field_metadata_cached_text_styles_and_nested_fields(string name)
    {
        var field = Field("Balance & Credit", "N2", "n/a & <pending>\t") with
        {
            Width = 144, Height = 24, AltText = "cached & <result>"
        };
        var style = TextStyle.Default with { Bold = true, FontSize = 18, Foreground = "#123456" };
        var table = Table.Create(1, 1).SetCell(0, 0, new TableCell
        {
            Blocks = [new Paragraph([new RichRun(Field("Account"))])]
        });
        var document = new FlowDocument([new Section
        {
            Blocks = [new Paragraph([new RichRun("Balance: "), new RichRun(field, style)]), table]
        }]);
        var format = Format(name);
        using var stream = new MemoryStream();
        var saved = await format.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(saved.Report.Diagnostics);
        stream.Position = 0;
        var loaded = await format.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(loaded.Report.Diagnostics);
        Assert.Equal(DocumentFormats.Json.Serialize(document), DocumentFormats.Json.Serialize(loaded.Document));
        var loadedFields = new DocumentIndex(loaded.Document).Paragraphs.SelectMany(p => p.Paragraph.Runs)
            .Select(run => run.Inline).OfType<InlineDescriptor>().ToArray();
        Assert.Equal(2, loadedFields.Length);
        Assert.Equal(field, loadedFields[0]);
        Assert.Null(Assert.IsType<MergeFieldInlinePayload>(loadedFields[1].Payload).Format);
        Assert.Null(Assert.IsType<MergeFieldInlinePayload>(loadedFields[1].Payload).FallbackText);
    }

    [Fact]
    public void Native_clipboard_preserves_fields_and_repeated_paste_remaps_identities()
    {
        var original = Field("Total", "C2", "No balance");
        var style = TextStyle.Default with { Italic = true };
        var source = new EditorSession(new FlowDocument([new Paragraph([new RichRun(original, style)])]));
        source.SelectAll();
        var sourceFragment = source.CopyFragment();
        var fragmentInline = Assert.IsType<Paragraph>(Assert.Single(sourceFragment.Document.Blocks)).Runs[0].Inline!;
        Assert.NotEqual(original.Id, fragmentInline.Id);
        Assert.Equal(original with { Id = fragmentInline.Id }, fragmentInline);
        var fragment = ClipboardInterchange.Parse(ClipboardInterchange.Serialize(sourceFragment));
        var copied = Assert.IsType<Paragraph>(Assert.Single(fragment.Document.Blocks)).Runs[0];
        Assert.Equal(fragmentInline, copied.Inline);
        Assert.Equal(style, copied.Style);
        var target = new EditorSession();
        target.InsertFragment(fragment);
        target.InsertFragment(fragment);
        var pasted = target.Index.Paragraphs.SelectMany(p => p.Paragraph.Runs).Select(run => run.Inline)
            .OfType<InlineDescriptor>().ToArray();
        Assert.Equal(2, pasted.Length);
        Assert.Equal(2, pasted.Select(field => field.Id).Distinct().Count());
        Assert.All(pasted, field =>
        {
            Assert.NotEqual(original.Id, field.Id);
            Assert.Equal(original.Payload, field.Payload);
            Assert.Equal(original.AltText, field.AltText);
        });
        target.Document.Validate();
        target.Undo();
        Assert.Equal(original.AltText, target.Document.PlainText);
        target.Undo();
        Assert.Equal("", target.Document.PlainText);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("markdown")]
    [InlineData("text")]
    public async Task Unsupported_formats_flatten_fields_with_actionable_loss_and_strict_rejection(string name)
    {
        var inline = Field() with { AltText = "Cached Customer" };
        var document = new FlowDocument([new Paragraph([new RichRun(inline)])]);
        var format = Format(name);
        using var output = new MemoryStream();
        var saved = await format.SaveWithReportAsync(document, output);
        var loss = Assert.Single(saved.Report.Diagnostics.Where(d => d.Code == name + ".merge-field"));
        Assert.Equal(inline.Id, loss.ModelId);
        Assert.Contains("can no longer be merged", loss.Fallback);
        Assert.Contains("native JSON", loss.Fallback);
        var flattened = format.Parse(Encoding.UTF8.GetString(output.ToArray()));
        Assert.Contains("Cached Customer", flattened.PlainText);
        Assert.All(new DocumentIndex(flattened).Paragraphs.SelectMany(p => p.Paragraph.Runs), run => Assert.Null(run.Inline));
        using var strictOutput = new MemoryStream();
        strictOutput.WriteByte(42);
        var exception = await Assert.ThrowsAsync<DocumentConversionException>(() =>
            format.SaveWithReportAsync(document, strictOutput, new() { Mode = ConversionMode.Strict }));
        Assert.Contains(exception.Report.Diagnostics, d => d.Code == name + ".merge-field");
        Assert.Equal(new byte[] { 42 }, strictOutput.ToArray());
    }

    [Fact]
    public async Task Markdown_code_sections_report_field_loss_even_when_flattening_the_whole_section()
    {
        var inline = Field();
        var document = new FlowDocument([new Section
        {
            Semantic = SectionSemantic.CodeBlock, Blocks = [new Paragraph([new RichRun(inline)])]
        }]);
        using var stream = new MemoryStream();
        var saved = await new MarkdownDocumentFormat().SaveWithReportAsync(document, stream);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == "markdown.merge-field" && d.ModelId == inline.Id);
        Assert.Contains(inline.AltText, Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void Minimal_xaml_field_gets_a_visible_label()
    {
        var document = new XamlDocumentFormat().Parse(Xml("<MergeField Name='Customer' />"));
        Assert.Equal("\u00ABCustomer\u00BB", document.PlainText);
        var run = Assert.IsType<Paragraph>(document.Blocks[0]).Runs[0];
        Assert.Equal("\uFFFC", run.Text);
        Assert.Equal("Customer", Assert.IsType<MergeFieldInlinePayload>(run.Inline!.Payload).Name);
    }

    [Theory]
    [InlineData("<MergeField />")]
    [InlineData("<MergeField Name='' />")]
    [InlineData("<MergeField Name=' ' />")]
    [InlineData("<MergeField Name='A' /><MergeField Name='B' />")]
    [InlineData("<MergeField Name='A' /><Control Type='label' />")]
    [InlineData("<MergeField Name='A' /><Image ResourceId='photo' />")]
    public void Malformed_xaml_fields_are_rejected(string payload) =>
        Assert.Throws<FormatException>(() => new XamlDocumentFormat().Parse(Xml(payload)));

    [Theory]
    [InlineData("<MergeField Name='Customer' Execute='Dangerous.Type' />", "xaml.unsupported-attribute")]
    [InlineData("<UnknownField Name='Customer' />", "xaml.unsupported-element")]
    [InlineData("<MergeField Name='Customer'><Control Type='Dangerous.Type' /></MergeField>", "xaml.unsupported-element")]
    public async Task Unknown_xaml_field_content_is_inert_and_rejected_in_strict_mode(string payload, string code)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Xml(payload, "AltText='cached'")));
        var format = new XamlDocumentFormat();
        var loaded = await format.LoadWithReportAsync(stream);
        Assert.Equal("cached", loaded.Document.PlainText);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == code);
        stream.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => format.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict }));
    }

    [Theory]
    [InlineData("missing-name")]
    [InlineData("null-name")]
    [InlineData("empty-name")]
    [InlineData("unknown-kind")]
    [InlineData("unknown-member")]
    public void Malformed_or_unknown_native_field_definitions_are_rejected(string corruption)
    {
        var document = new FlowDocument([new Paragraph([new RichRun(Field())])]);
        var json = JsonNode.Parse(DocumentFormats.Json.Serialize(document))!;
        var payload = json["document"]!["blocks"]![0]!["runs"]![0]!["inline"]!["payload"]!.AsObject();
        switch (corruption)
        {
            case "missing-name": payload.Remove("name"); break;
            case "null-name": payload["name"] = null; break;
            case "empty-name": payload["name"] = ""; break;
            case "unknown-kind": payload["kind"] = "unknownMergeField"; break;
            case "unknown-member": payload["execute"] = "Dangerous.Type"; break;
        }
        var exception = Record.Exception(() => DocumentFormats.Json.Parse(json.ToJsonString()));
        Assert.True(exception is FormatException or JsonException, exception?.ToString() ?? "Malformed field was accepted.");
    }
}
