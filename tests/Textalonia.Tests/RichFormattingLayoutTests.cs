using Avalonia;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class RichFormattingLayoutTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static FontFamily Font => new("avares://Avalonia.Fonts.Inter/Assets#Inter");
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static void Build(DocumentLayout layout, FlowDocument document, double width = 500) =>
        layout.Build(document, width, Font, Brushes.Black, Brushes.Gray, new Thickness(0), new Rect(0, 0, width, 10000));

    [Fact]
    public Task Explicit_weight_stretch_and_paragraph_spacing_reach_the_shaper() => Run(() =>
    {
        var style = TextStyle.Default with { Bold = true, FontWeight = 300, FontStretch = 7 };
        Assert.Equal(300, style.EffectiveFontWeight);
        Assert.False(style.EffectiveBold);
        var typeface = DocumentLayout.Typeface(style, Font);
        Assert.Equal((FontWeight)300, typeface.Weight);
        Assert.Equal((FontStretch)7, typeface.Stretch);
        Assert.Equal(Font, typeface.FontFamily);
        var paragraph = new Paragraph("letter spacing changes measurement", style);
        using var normal = DocumentLayout.CreateTextLayout(paragraph, 1000, Font, Brushes.Black);
        using var spaced = DocumentLayout.CreateTextLayout(paragraph with { Style = new() { LetterSpacing = 3, LineHeight = 48 } }, 1000, Font, Brushes.Black);
        Assert.True(spaced.Width > normal.Width + 50);
        Assert.Equal(48, spaced.Height, 5);
        Assert.Equal(3, spaced.LetterSpacing);
        Assert.Equal((FontWeight)300, spaced.TextLines[0].TextRuns[0].Properties!.Typeface.Weight);
    });

    [Theory]
    [InlineData(40)]
    [InlineData(-25)]
    public Task First_line_and_right_indents_affect_wrapping_carets_and_hits(double firstIndent) => Run(() =>
    {
        var paragraph = new Paragraph(string.Join(" ", Enumerable.Repeat("alpha beta gamma delta", 100)))
        { Style = new() { Indent = 30, RightIndent = 80, FirstLineIndent = firstIndent, LineHeight = 32 } };
        using var layout = new DocumentLayout();
        Build(layout, new([paragraph]), 400);
        var first = layout.At(0)!;
        Assert.Equal(30 + firstIndent, first.Origin.X, 5);
        Assert.Equal(290 - firstIndent, first.AvailableWidth, 5);
        Assert.Equal(1, first.Page.LineCount);
        var second = layout.At(first.Page.End)!;
        Assert.Equal(30, second.Origin.X, 5);
        Assert.Equal(32, second.Origin.Y - first.Origin.Y, 5);
        using var narrow = DocumentLayout.CreateTextLayout(paragraph with { Style = new() { LineHeight = 32 } }, 290, Font, Brushes.Black);
        Assert.True(layout.Height >= narrow.Height);
        foreach (var offset in new[] { 0, first.Page.End, paragraph.Length / 2, paragraph.Length })
        {
            var caret = layout.Caret(offset);
            Assert.Equal(offset, layout.HitTest(new(caret.X + .1, caret.Y + caret.Height / 2)));
            Assert.Equal(32, caret.Height, 5);
        }
        Assert.Equal(paragraph.Length, layout.Paragraphs.Sum(v => v.Page.End - v.Page.Start));
    });

    [Fact]
    public Task First_line_indent_preserves_justification_and_short_explicit_line_height() => Run(() =>
    {
        var paragraph = new Paragraph(string.Join(" ", Enumerable.Repeat("alpha beta gamma", 40)))
        { Style = new() { Alignment = ParagraphAlignment.Justify, FirstLineIndent = 25, LineHeight = 12 } };
        using var layout = new DocumentLayout();
        Build(layout, new([paragraph]), 350);
        var first = layout.At(0)!;
        using var lease = first.Acquire();
        Assert.Equal(325, lease.Layout.TextLines[0].Width, 4);
        Assert.Equal(12, layout.Caret(0).Height, 5);
    });

    [Fact]
    public Task Nested_tables_sections_column_weights_and_padding_share_measured_geometry() => Run(() =>
    {
        var text = new Paragraph("inside nested table");
        var inner = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [text], Padding = new(3, 5, 7, 9), Background = "#FFFFFF" });
        var section = new Section { Blocks = [inner], PaddingEdges = new(11, 13, 17, 19), Background = "#EEEEEE" };
        var outer = Table.Create(1, 2) with { ColumnWidths = [1, 3] };
        outer = outer.SetCell(0, 1, outer.Rows[0][1] with { Blocks = [section], Padding = new(2, 4, 6, 8), Background = "#CCCCCC" });
        var document = new FlowDocument([outer]);
        using var layout = new DocumentLayout();
        Build(layout, document, 600);
        var visual = layout.Paragraphs.Single(v => v.Position.Paragraph.Id == text.Id);
        Assert.Equal(150 + 2 + 11 + 3, visual.Origin.X, 5);
        Assert.Equal(4 + 13 + 5, visual.Origin.Y, 5);
        Assert.Equal(450 - 2 - 6 - 11 - 17 - 3 - 7, visual.AvailableWidth, 5);
        var index = new DocumentIndex(document);
        var caret = layout.Caret(index.ById(text.Id).Start);
        Assert.Equal(index.ById(text.Id).Start, layout.HitTest(new(caret.X + .1, caret.Y + caret.Height / 2)));
        // Parent backgrounds must precede their nested decorations in drawing order.
        var outerDecoration = layout.Decorations.FindIndex(d => d.Fill is SolidColorBrush b && b.Color == Color.Parse("#CCCCCC"));
        var sectionDecoration = layout.Decorations.FindIndex(d => d.Fill is SolidColorBrush b && b.Color == Color.Parse("#EEEEEE"));
        var innerDecoration = layout.Decorations.FindIndex(d => d.Fill is SolidColorBrush b && b.Color == Color.Parse("#FFFFFF"));
        Assert.True(outerDecoration < sectionDecoration && sectionDecoration < innerDecoration);
        var changed = outer with { ColumnWidths = [3, 1] };
        Build(layout, new([changed]), 600);
        var after = layout.Paragraphs.Single(v => v.Position.Paragraph.Id == text.Id);
        Assert.Equal(450 + 2 + 11 + 3, after.Origin.X, 5);
        Assert.Equal(150 - 2 - 6 - 11 - 17 - 3 - 7, after.AvailableWidth, 5);
    });

    [Fact]
    public Task Row_policies_measure_minimums_and_clip_exact_rows() => Run(() =>
    {
        var paragraph = new Paragraph("line one\u2028line two\u2028line three") { Style = new() { LineHeight = 40 } };
        var table = Table.Create(2, 1) with { RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 45 }, new() { Mode = TableRowHeightMode.AtLeast, Height = 75 }] };
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [paragraph], Padding = new(0, 0, 0, 0) });
        using var layout = new DocumentLayout();
        Build(layout, new([table]));
        Assert.Equal(45 + 75 + 12, layout.Height, 5);
        var visual = layout.Paragraphs.Single(p => p.Position.Paragraph.Id == paragraph.Id);
        Assert.Equal(new Rect(0, 0, 500, 45), visual.Clip);
        Assert.Equal(45, layout.Decorations[0].Bounds.Height, 5);
        Assert.Equal(75, layout.Decorations[1].Bounds.Height, 5);
        Assert.All(layout.SelectionRects(0, paragraph.Length), rect => Assert.True(rect.Bottom <= 45));
    });

    [Fact]
    public Task Offscreen_edit_in_a_large_nested_cell_reuses_visible_shapes_and_geometry() => Run(() =>
    {
        var paragraphs = Enumerable.Range(0, 10000).Select(i => (Block)new Paragraph("Nested paragraph " + i)).ToArray();
        var inner = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [new Section { Blocks = [.. paragraphs] }] });
        var outer = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [inner] });
        var session = new EditorSession(new FlowDocument([outer]));
        using var layout = new DocumentLayout();
        void View() => layout.Build(session.Document, 600, Font, Brushes.Black, Brushes.Gray,
            new Thickness(0), new Rect(0, 0, 600, 400));
        View();
        Assert.InRange(layout.ShapedParagraphs, 1, 60);
        Assert.InRange(layout.GeometryNodes, 1, 200);
        var shapes = layout.ShapedParagraphs;
        var nodes = layout.GeometryNodes;
        var first = layout.Caret(0);
        session.Select(session.Index.Length, session.Index.Length);
        session.InsertText(" offscreen edit");
        View();
        Assert.Equal(shapes, layout.ShapedParagraphs);
        Assert.InRange(layout.GeometryNodes - nodes, 1, 12);
        Assert.Equal(first, layout.Caret(0));
    });

    [Fact]
    public Task Independent_borders_draw_each_solid_side_and_inherit_theme_colors() => Run(() =>
    {
        var borders = new BlockBorders(new(2, "#FF0000"), new(3), new(4, "#0000FF"), new(5));
        var decoration = new BlockDecoration(new Rect(0, 0, 100, 80), null, Brushes.Green, false, borders);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) decoration.Draw(context);
        var group = Assert.IsType<DrawingGroup>(Assert.Single(drawing.Children!));
        var sides = group.Children!.Cast<GeometryDrawing>().ToArray();
        Assert.Equal(4, sides.Length);
        Assert.Equal(new Rect(0, 0, 2, 80), sides[0].Geometry!.Bounds);
        Assert.Equal(new Rect(0, 0, 100, 3), sides[1].Geometry!.Bounds);
        Assert.Equal(new Rect(96, 0, 4, 80), sides[2].Geometry!.Bounds);
        Assert.Equal(new Rect(0, 75, 100, 5), sides[3].Geometry!.Bounds);
        Assert.Equal(Brushes.Green, sides[1].Brush);
        Assert.Equal(Brushes.Green, sides[3].Brush);
    });

    [Fact]
    public Task Empty_paragraph_inherits_typing_format_and_fixed_line_height() => Run(() =>
    {
        var session = new EditorSession(new FlowDocument([new Paragraph() { Style = new() { LineHeight = 42 } }]));
        session.ApplyStyle(style => style with { FontWeight = 650, FontStretch = 6, Foreground = "#AABBCC" });
        session.InsertText("inherited");
        var paragraph = session.Index.Paragraphs[0].Paragraph;
        Assert.Equal(650, paragraph.Runs[0].Style.FontWeight);
        Assert.Equal(6, paragraph.Runs[0].Style.FontStretch);
        using var layout = new DocumentLayout();
        Build(layout, session.Document);
        Assert.Equal(42, layout.Caret(0).Height, 5);
    });
}

