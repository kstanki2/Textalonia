using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class MergeFieldRtfRegressionTests
{
    [Theory]
    [InlineData(0, @"\u233Name")]
    [InlineData(1, @"\u233?Name")]
    [InlineData(2, @"\u233??Name")]
    public async Task Field_instruction_names_honor_inherited_unicode_fallback_length(int fallback, string encodedName)
    {
        var rtf = "{\\rtf1\\ansi\\uc" + fallback + "{\\field{\\*\\fldinst MERGEFIELD \"" + encodedName + "\"}{\\fldrslt Cached}}}";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
        var result = await new RtfDocumentFormat().LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(result.Report.Diagnostics);
        var run = Assert.Single(new DocumentIndex(result.Document).Paragraphs.SelectMany(p => p.Paragraph.Runs));
        Assert.Equal("\u00E9Name", Assert.IsType<MergeFieldInlinePayload>(run.Inline!.Payload).Name);
        Assert.Equal("Cached", run.Inline.AltText);
    }

    [Theory]
    [InlineData(@"\sectd")]
    [InlineData(@"\par")]
    [InlineData(@"\sect")]
    public async Task Structural_field_results_preserve_surrounding_and_cached_text_without_atomic_extraction(string control)
    {
        var rtf = @"{\rtf1 Before{\field{\*\fldinst MERGEFIELD Name}{\fldrslt " + control + @" Cached}}After}";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
        var format = new RtfDocumentFormat();
        var result = await format.LoadWithReportAsync(stream);
        Assert.Equal("BeforeCachedAfter", string.Concat(new DocumentIndex(result.Document).Paragraphs.Select(p => p.Paragraph.PlainText)));
        Assert.All(new DocumentIndex(result.Document).Paragraphs.SelectMany(p => p.Paragraph.Runs), run => Assert.Null(run.Inline));
        Assert.Contains(result.Report.Diagnostics, diagnostic => diagnostic.Code == "rtf.field-result");
        stream.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => format.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Empty_section_reset_field_result_does_not_use_an_invalid_run_range()
    {
        const string rtf = @"{\rtf1 Before{\field{\*\fldinst MERGEFIELD Name}{\fldrslt \sectd}}After}";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
        var result = await new RtfDocumentFormat().LoadWithReportAsync(stream);
        Assert.Equal("BeforeAfter", string.Concat(new DocumentIndex(result.Document).Paragraphs.Select(p => p.Paragraph.PlainText)));
        Assert.Contains(result.Report.Diagnostics, diagnostic => diagnostic.Code == "rtf.field-result");
    }
}
