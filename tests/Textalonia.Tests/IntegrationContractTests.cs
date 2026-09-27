using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class IntegrationContractTests
{
    [Theory]
    [InlineData("test.TXAML", "Textalonia XAML data")]
    [InlineData("test.xaml", "Textalonia XAML data")]
    public void Xaml_extension_selects_data_codec(string path, string name) =>
        Assert.Equal(name, DocumentFormats.ForPath(path).Name);

    [Theory]
    [InlineData("test.md")]
    [InlineData("test.MARKDOWN")]
    public void Markdown_extensions_select_codec(string path) => Assert.Same(DocumentFormats.Markdown, DocumentFormats.ForPath(path));

    [Theory]
    [InlineData(".txaml")]
    [InlineData(".md")]
    public async Task New_codecs_leave_streams_open_on_success_and_cancellation(string extension)
    {
        var format = DocumentFormats.ForPath("sample" + extension);
        using var stream = new MemoryStream();
        await format.SaveWithReportAsync(FlowDocument.FromText("Text"), stream, new() { Mode = ConversionMode.Strict });
        Assert.True(stream.CanWrite);
        stream.Position = 0;
        var loaded = await format.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Equal("Text", loaded.Document.PlainText);
        Assert.True(stream.CanRead);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => format.LoadAsync(stream, new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => format.SaveAsync(loaded.Document, stream, new CancellationToken(true)));
        Assert.True(stream.CanWrite);
    }

    [Theory]
    [InlineData(".html")]
    [InlineData(".rtf")]
    [InlineData(".docx")]
    public async Task Other_codecs_report_lost_markdown_semantics_before_strict_output(string extension)
    {
        var document = DocumentFormats.Markdown.Parse("> Quote\n\n```cs\nclass Example {}\n```\n\n`inline`");
        var format = DocumentFormats.ForPath("sample" + extension);
        using var stream = new MemoryStream();
        var saved = await format.SaveWithReportAsync(document, stream);
        Assert.Contains(saved.Report.Diagnostics, item => item.Code == "conversion.section-semantic");
        Assert.Contains(saved.Report.Diagnostics, item => item.Code == "conversion.inline-code");
        using var strict = new MemoryStream(Encoding.UTF8.GetBytes("untouched"), writable: true);
        await Assert.ThrowsAsync<DocumentConversionException>(() => format.SaveWithReportAsync(document, strict, new() { Mode = ConversionMode.Strict }));
        Assert.Equal("untouched", Encoding.UTF8.GetString(strict.ToArray()));
    }
}
