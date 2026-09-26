using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class RichFormattingValidationTests
{
    [Fact]
    public void Rich_font_values_enforce_their_declared_ranges()
    {
        foreach (var style in new[]
        {
            new TextStyle { FontWeight = 0 }, new TextStyle { FontWeight = 1001 },
            new TextStyle { FontStretch = 0 }, new TextStyle { FontStretch = 10 }
        })
            Assert.Throws<FormatException>(() => new FlowDocument([new Paragraph("text", style)]).Validate());
        foreach (var style in new[]
        {
            new TextStyle { FontWeight = 1, FontStretch = 1 },
            new TextStyle { FontWeight = 1000, FontStretch = 9 }
        })
            new FlowDocument([new Paragraph("text", style)]).Validate();
    }

    [Fact]
    public void Paragraph_measurements_reject_nonfinite_and_out_of_range_values()
    {
        foreach (var style in new[]
        {
            new ParagraphStyle { LineHeight = 0 }, new ParagraphStyle { LineHeight = double.NaN },
            new ParagraphStyle { LineHeight = double.PositiveInfinity }, new ParagraphStyle { LineHeight = 10001 },
            new ParagraphStyle { RightIndent = -1 }, new ParagraphStyle { RightIndent = double.PositiveInfinity },
            new ParagraphStyle { FirstLineIndent = double.NaN }, new ParagraphStyle { FirstLineIndent = -100001 },
            new ParagraphStyle { LetterSpacing = double.NegativeInfinity }, new ParagraphStyle { LetterSpacing = 1001 }
        })
            Assert.Throws<FormatException>(() => new FlowDocument([new Paragraph("text") { Style = style }]).Validate());
        new FlowDocument([new Paragraph("text") { Style = new() { FirstLineIndent = -25, RightIndent = 20, LetterSpacing = -1, LineHeight = 12 } }]).Validate();
    }

    [Fact]
    public void Independent_section_sides_validate_even_when_no_border_is_drawn()
    {
        foreach (var section in new[]
        {
            new Section { PaddingEdges = new(Top: double.NaN) },
            new Section { PaddingEdges = new(Bottom: -1) },
            new Section { Borders = new(Right: new(double.PositiveInfinity)) },
            new Section { Borders = new(Bottom: new(-1)) },
            new Section { Borders = new(Top: new(0, "invalid-color")) }
        })
            Assert.Throws<FormatException>(() => new FlowDocument([section]).Validate());
    }
}
