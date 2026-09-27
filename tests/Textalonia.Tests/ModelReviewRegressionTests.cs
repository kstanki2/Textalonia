using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class ModelReviewRegressionTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(2, -1)]
    [InlineData(4, 0)]
    [InlineData(2, 2)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    public void Paragraph_ranges_reject_invalid_arguments_without_rewriting_text(int start, int length)
    {
        var paragraph = new Paragraph("abc");
        Assert.Throws<ArgumentOutOfRangeException>(() => paragraph.Slice(start, length));
        Assert.Throws<ArgumentOutOfRangeException>(() => paragraph.Format(start, length, style => style with { Bold = true }));
        Assert.Equal("abc", paragraph.Text);
    }

    [Theory]
    [InlineData("#123456\n")]
    [InlineData("#78123456\n")]
    public void Color_validation_rejects_trailing_line_breaks(string color)
    {
        Assert.Throws<FormatException>(() => FlowDocument.ValidateColor(color));
        Assert.Throws<FormatException>(() => new FlowDocument([new Paragraph("text", new TextStyle { Foreground = color })]).Validate());
    }

    [Fact]
    public void Moving_ranges_across_nested_containers_preserves_all_text()
    {
        var table = Table.Create(1, 2)
            .SetCell(0, 0, new TableCell { Blocks = [new Paragraph("gh")] })
            .SetCell(0, 1, new TableCell { Blocks = [new Paragraph("ij")] });
        var document = new FlowDocument([
            new Paragraph("ab"),
            new Section { Blocks = [new Paragraph("cd"), new Paragraph("ef")] },
            table,
            new Paragraph("kl")
        ]);
        var expected = string.Concat(document.Text.Where(c => c != '\n').Order());
        var length = document.Text.Length;
        for (var start = 0; start < length; start++)
            for (var end = start + 1; end <= length; end++)
                for (var destination = 0; destination <= length; destination++)
                {
                    if (destination >= start && destination <= end) continue;
                    var session = new EditorSession(document);
                    session.Select(start, end);
                    var drag = session.CaptureContentDrag()!;
                    session.DropContent(drag.Fragment, destination, session.Revision, drag, move: true);
                    var actual = string.Concat(session.Document.Text.Where(c => c != '\n').Order());
                    Assert.True(expected == actual, $"Range [{start}, {end}) to {destination}: {session.Document.Text}");
                    session.Document.Validate();
                }
    }
}
