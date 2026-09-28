using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class TypographyLayoutTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static FontFamily Font => new("avares://Avalonia.Fonts.Inter/Assets#Inter");
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static ShapingTextLayout Shape(Paragraph p, double width = 1000) => DocumentLayout.CreateTextLayout(p, width, Font, Brushes.Black);

    [Fact]
    public Task Mixed_tracking_scale_and_baseline_use_adjusted_cluster_geometry() => Run(() =>
    {
        using var plain = Shape(new Paragraph("abcdefghij"));
        using var scaled = Shape(new Paragraph("abcdefghij", new() { HorizontalScale = 1.5 }));
        Assert.Equal(plain.Width * 1.5, scaled.Width, 3);
        using var tracked = Shape(new Paragraph([new RichRun("abcde"), new RichRun("fghij", new() { Tracking = 3, BaselineOffset = 5 })]));
        Assert.Equal(plain.Width + 15, tracked.Width, 3);
        Assert.True(tracked.Height > plain.Height);
        var run = Assert.IsType<ShapedTextRun>(tracked.TextLines[0].TextRuns[1]);
        Assert.All(run.ShapedBuffer, glyph => Assert.Equal(-5, glyph.GlyphOffset.Y));
        for (var offset = 0; offset <= 10; offset++)
        {
            var caret = tracked.HitTestTextPosition(offset);
            Assert.Equal(offset, tracked.HitTestPoint(new(caret.X + .01, caret.Height / 2)).TextPosition);
        }
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) scaled.Draw(context, default);
        Assert.NotEmpty(drawing.Children!);
    });

    [Theory]
    [InlineData(TabAlignment.Left)]
    [InlineData(TabAlignment.Center)]
    [InlineData(TabAlignment.Right)]
    [InlineData(TabAlignment.Decimal)]
    public Task Tab_alignment_and_leader_keep_storage_offsets(TabAlignment alignment) => Run(() =>
    {
        var p = new Paragraph("left\t12.34") { Style = new() { TabStops = [new(200, alignment, TabLeader.Dots)] } };
        using var layout = Shape(p);
        using var number = Shape(new Paragraph("12.34"));
        using var prefix = Shape(new Paragraph("12"));
        var expected = alignment switch { TabAlignment.Center => 200 - number.Width / 2, TabAlignment.Right => 200 - number.Width, TabAlignment.Decimal => 200 - prefix.Width, _ => 200 };
        Assert.Equal(expected, layout.HitTestTextPosition(5).X, 3);
        Assert.Equal(p.Length, layout.TextLines.Sum(l => l.Length));
        var tab = Assert.IsType<TabTextRun>(layout.TextLines[0].TextRuns[1]);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) tab.Draw(context, default);
        Assert.NotEmpty(drawing.Children!);
    });

    [Fact]
    public Task Tabs_recompute_after_wrap_and_soft_line_break() => Run(() =>
    {
        var p = new Paragraph("first words wrap onto another line\tend\u2028x\tlast")
        { Style = new() { TabStops = [new(120)] } };
        using var layout = Shape(p, 220);
        Assert.True(layout.TextLines.Count >= 3);
        Assert.Equal(p.Length, layout.TextLines.Sum(l => l.Length));
        var last = layout.TextLines.Last();
        Assert.Equal(120, last.GetDistanceFromCharacterHit(new(p.Text.LastIndexOf("last", StringComparison.Ordinal), 0)), 3);
    });

    [Theory]
    [InlineData(LineSpacingMode.Multiple, 2)]
    [InlineData(LineSpacingMode.Exact, 12)]
    [InlineData(LineSpacingMode.AtLeast, 40)]
    public Task Line_modes_use_measured_line_metrics(LineSpacingMode mode, double value) => Run(() =>
    {
        using var natural = Shape(new Paragraph("one\u2028two"));
        using var layout = Shape(new Paragraph("one\u2028two") { Style = new() { LineSpacingMode = mode, LineSpacing = value } });
        var expected = mode == LineSpacingMode.Multiple ? natural.Height * value : value * 2;
        Assert.Equal(expected, layout.Height, 3);
    });

    [Fact]
    public Task Words_only_underlines_caps_language_and_features_reach_shaper() => Run(() =>
    {
        var p = new Paragraph("alpha beta", new() { UnderlineKind = UnderlineKind.Double, UnderlineColor = "#FF0000", UnderlineWordsOnly = true,
            StrikeKind = StrikeKind.Double, AllCaps = true, Language = "tr-TR", KerningThreshold = 20 });
        using var layout = Shape(p);
        var runs = layout.TextLines[0].TextRuns;
        Assert.Equal("ALPHA", runs[0].Text.ToString());
        Assert.Equal(4, runs[0].Properties!.TextDecorations!.Count);
        Assert.Equal(2, runs[1].Properties!.TextDecorations!.Count);
        Assert.Equal("tr-TR", runs[0].Properties!.CultureInfo!.Name);
        Assert.Equal(0, Assert.Single(runs[0].Properties!.FontFeatures!).Value);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Bidi_transforms_preserve_logical_text_clusters_and_wrapping(bool rtl) => Run(() =>
    {
        const string text = "alpha \u05d0\u05d1\u05d2 beta \u0645\u0631\u062d\u0628\u0627 gamma";
        var plainParagraph = new Paragraph(text) { Style = new() { RightToLeft = rtl } };
        var transformed = new Paragraph(text, new() { HorizontalScale = 1.5 }) { Style = plainParagraph.Style };
        using var wide = Shape(transformed);
        using var plain = Shape(plainParagraph);
        Assert.Equal(plain.Width * 1.5, wide.Width, 3);
        for (var offset = 0; offset <= text.Length; offset++)
            Assert.Equal(plain.TextLines[0].GetDistanceFromCharacterHit(new(offset, 0)) * 1.5,
                wide.TextLines[0].GetDistanceFromCharacterHit(new(offset, 0)), 3);
        using var wrapped = Shape(transformed, 145);
        Assert.True(wrapped.TextLines.Count > 1);
        Assert.Equal(text.Length, wrapped.TextLines.Sum(l => l.Length));
        Assert.Equal(text.Length, wrapped.TextLines.SelectMany(l => l.TextRuns).Sum(r => r.Length));
        using var documentLayout = new DocumentLayout();
        documentLayout.Build(new([transformed]), 180, Font, Brushes.Black, Brushes.Gray, new Thickness(0), new Rect(0, 0, 180, 1000));
        foreach (var offset in new[] { 0, 2, 7, 11, 18, text.Length })
        {
            var rect = documentLayout.Caret(offset);
            var hit = documentLayout.HitTestCaret(new(rect.X + .01, rect.Y + rect.Height / 2));
            Assert.InRange(hit.Position, 0, text.Length);
            Assert.Equal(rect.X, documentLayout.Caret(hit).X, 3);
        }
    });

    [Fact]
    public Task Small_caps_preserve_text_storage_and_shrink_only_lowercase_display() => Run(() =>
    {
        var paragraph = new Paragraph("Title e\u0301", new() { SmallCaps = true });
        using var layout = Shape(paragraph);
        var runs = layout.TextLines[0].TextRuns;
        Assert.Equal("TITLE E\u0301", string.Concat(runs.Select(r => r.Text.ToString())));
        Assert.Equal("Title e\u0301", paragraph.Text);
        Assert.Equal(16, runs[0].Properties!.FontRenderingEmSize);
        Assert.Equal(12.8, runs[1].Properties!.FontRenderingEmSize, 3);
        Assert.Equal("E\u0301", runs[^1].Text.ToString());
        Assert.Equal(12.8, runs[^1].Properties!.FontRenderingEmSize, 3);
    });

    [Fact]
    public Task Run_tracking_adds_once_to_paragraph_letter_spacing() => Run(() =>
    {
        var paragraph = new Paragraph("abcdefghij") { Style = new() { LetterSpacing = 2 } };
        using var plain = Shape(paragraph);
        using var tracked = Shape(paragraph with { Runs = [new("abcdefghij", new() { Tracking = 3 })] });
        Assert.Equal(plain.Width + 30, tracked.Width, 3);
    });

    [Fact]
    public Task Script_fonts_are_selected_without_splitting_surrogate_pairs() => Run(() =>
    {
        var paragraph = new Paragraph("a\u6f22\ud840\udc00\u05d0", new() { FontFamily = "Latin", EastAsianFontFamily = "CJK", ComplexScriptFontFamily = "Complex" });
        var source = new InlineTextSource(paragraph, 0, paragraph.Text, Font, Brushes.Black);
        Assert.Equal("Latin", source.GetTextRun(0)!.Properties!.Typeface.FontFamily.Name);
        Assert.Equal("CJK", source.GetTextRun(1)!.Properties!.Typeface.FontFamily.Name);
        Assert.Equal(3, source.GetTextRun(1)!.Length);
        Assert.Equal("Complex", source.GetTextRun(4)!.Properties!.Typeface.FontFamily.Name);
    });

    [Fact]
    public Task Explicit_default_tab_width_and_trailing_empty_line_have_exact_geometry() => Run(() =>
    {
        using var layout = Shape(new Paragraph("a\tb\u2028") { Style = new() { DefaultTabWidth = 48, LineSpacingMode = LineSpacingMode.Multiple, LineSpacing = 2 } });
        Assert.Equal(48, layout.TextLines[0].GetDistanceFromCharacterHit(new(2, 0)), 3);
        Assert.Equal(2, layout.TextLines.Count);
        Assert.Equal(4, layout.TextLines[1].FirstTextSourceIndex);
        Assert.Equal(0, layout.TextLines[1].Length);
    });

    [Fact]
    public Task Transformed_glyphs_retain_justification() => Run(() =>
    {
        using var layout = Shape(new Paragraph("one two three four five six seven eight", new() { Tracking = 1, HorizontalScale = 1.1 })
        { Style = new() { Alignment = ParagraphAlignment.Justify } }, 140);
        Assert.True(layout.TextLines.Count > 1);
        Assert.Equal(140, layout.TextLines[0].Width, 3);
    });

    [Fact]
    public Task Custom_tabs_retain_native_justification_and_hit_geometry_after_wrapping() => Run(() =>
    {
        var paragraph = new Paragraph("x\tone two three four five six seven eight nine ten eleven")
        { Style = new() { TabStops = [new(60, TabAlignment.Left, TabLeader.Dots)], Alignment = ParagraphAlignment.Justify } };
        using var layout = Shape(paragraph, 220);
        Assert.True(layout.TextLines.Count > 1);
        Assert.Equal(220, layout.TextLines[0].Width, 3);
        Assert.Equal(paragraph.Length, layout.TextLines.Sum(l => l.Length));
        foreach (var line in layout.TextLines)
        {
            for (var position = line.FirstTextSourceIndex; position < line.FirstTextSourceIndex + line.Length - line.TrailingWhitespaceLength; position++)
            {
                var x = line.GetDistanceFromCharacterHit(new(position, 0));
                Assert.Equal(position, line.GetCharacterHitFromDistance(x + .01).FirstCharacterIndex);
            }
        }
    });
}
