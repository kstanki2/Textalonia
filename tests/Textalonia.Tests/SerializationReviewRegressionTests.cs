using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class SerializationReviewRegressionTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    [InlineData("utf32le")]
    [InlineData("utf32be")]
    public async Task Malformed_BOM_encoded_text_is_rejected_before_parsing(string encoding)
    {
        byte[] bytes = encoding switch
        {
            "utf8" => [0xEF, 0xBB, 0xBF, 0xC3, 0x28],
            "utf16le" => [0xFF, 0xFE, 0x00, 0xD8],
            "utf16be" => [0xFE, 0xFF, 0xD8, 0x00],
            "utf32le" => [0xFF, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x11, 0x00],
            _ => [0x00, 0x00, 0xFE, 0xFF, 0x00, 0x11, 0x00, 0x00]
        };
        foreach (var format in new IDocumentFormat[]
            { DocumentFormats.PlainText, DocumentFormats.Markdown, DocumentFormats.Html, DocumentFormats.Json, DocumentFormats.Xaml })
        {
            using var stream = new MemoryStream(bytes);
            await Assert.ThrowsAsync<DecoderFallbackException>(() => format.LoadWithReportAsync(stream));
            Assert.True(stream.CanRead);
        }
    }

    [Fact]
    public async Task Valid_BOM_encoded_text_keeps_unicode_and_omits_only_the_initial_BOM()
    {
        const string text = "Unicode \u4E2D \U0001F642 \uFEFF";
        foreach (var encoding in new Encoding[]
            { new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true),
              new UTF32Encoding(false, true), new UTF32Encoding(true, true) })
            foreach (var format in new IDocumentFormat[] { DocumentFormats.PlainText, DocumentFormats.Markdown })
            {
                using var stream = new MemoryStream([.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
                var document = await format.LoadAsync(stream);
                Assert.Equal(text, document.Text);
            }
    }

    [Fact]
    public void Native_schema_rejects_duplicate_members_instead_of_overwriting_content()
    {
        const int version = 4;
        var documents = "{\"version\":" + version + ",\"document\":{},\"document\":{}}";
        var blocks = "{\"version\":" + version + ",\"document\":{\"blocks\":[],\"blocks\":[]}}";
        var run = "{\"version\":" + version + ",\"document\":{\"blocks\":[{\"kind\":\"paragraph\",\"runs\":[{\"text\":\"original\",\"te\\u0078t\":\"replacement\",\"style\":{}}]}]}}";
        foreach (var json in new[] { documents, blocks, run })
            Assert.Throws<FormatException>(() => DocumentFormats.Json.Parse(json));
    }

    [Fact]
    public void Future_native_schema_still_reports_unsupported_version_before_inspecting_its_shape() =>
        Assert.Throws<NotSupportedException>(() => DocumentFormats.Json.Parse("{\"version\":99,\"document\":{\"future\":1,\"future\":2}}"));

    [Theory]
    [InlineData("before\u2028after", true, 0)]
    [InlineData("before\u2028after", false, 1)]
    [InlineData("\u2028after", false, 0)]
    [InlineData("before\u2028", false, 0)]
    [InlineData("before\u2028\u2028after", false, 0)]
    public async Task Markdown_reports_unrepresentable_soft_breaks_and_strict_save_preserves_destination(string text, bool list, int heading)
    {
        var document = new FlowDocument([new Paragraph(text)
            { Style = ParagraphStyle.Default with { List = list ? ListKind.Bullet : ListKind.None, HeadingLevel = heading } }]);
        using var tolerant = new MemoryStream();
        var result = await DocumentFormats.Markdown.SaveWithReportAsync(document, tolerant);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "markdown.soft-break");
        tolerant.Position = 0;
        Assert.NotEqual(document.Text, (await DocumentFormats.Markdown.LoadAsync(tolerant)).Text);
        using var strict = new MemoryStream();
        strict.WriteByte(42);
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() =>
            DocumentFormats.Markdown.SaveWithReportAsync(document, strict, new() { Mode = ConversionMode.Strict }));
        Assert.Contains(error.Report.Diagnostics, d => d.Code == "markdown.soft-break");
        Assert.Equal(new byte[] { 42 }, strict.ToArray());
    }

    [Theory]
    [InlineData("before\u2028after")]
    [InlineData("before\u2028")]
    public async Task Markdown_preservable_styled_soft_breaks_still_save_strictly(string text)
    {
        var document = new FlowDocument([new Paragraph(text, TextStyle.Default with { Italic = true })]);
        using var stream = new MemoryStream();
        var result = await DocumentFormats.Markdown.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(result.Report.Diagnostics);
        stream.Position = 0;
        Assert.Equal(text, (await DocumentFormats.Markdown.LoadAsync(stream)).Text);
    }
}
