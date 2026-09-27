using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class BidiNavigationTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);

    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface) Create(string text, bool rtl = false, bool tableCell = false, double width = 700)
    {
        var paragraph = new Paragraph(text) with { Style = new ParagraphStyle { RightToLeft = rtl } };
        var document = new FlowDocument(tableCell ? [Table.Create(1, 1) with
        { Rows = [[new TableCell { Blocks = [paragraph] }]] }] : [paragraph]);
        var editor = new TextaloniaEditor { Document = document };
        var window = new Window { Width = width, Height = 350, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument(); Dispatcher.UIThread.RunJobs();
        var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
        return (window, editor, surface);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Hebrew_arrows_and_home_end_follow_physical_edges_in_paragraphs_and_cells(bool cell) => Run(() =>
    {
        var (window, editor, surface) = Create("\u05d0\u05d1\u05d2", rtl: true, tableCell: cell);
        try
        {
            editor.Session.Select(2, 2);
            var initial = surface.CaretRectangle.X;
            window.KeyPress(Key.Right, RawInputModifiers.None);
            Assert.Equal(1, editor.Session.Selection.Active);
            Assert.True(surface.CaretRectangle.X > initial);
            window.KeyPress(Key.Left, RawInputModifiers.None);
            Assert.Equal(2, editor.Session.Selection.Active);
            window.KeyPress(Key.Home, RawInputModifiers.None);
            Assert.Equal(3, editor.Session.Selection.Active);
            window.KeyPress(Key.End, RawInputModifiers.None);
            Assert.Equal(0, editor.Session.Selection.Active);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Mixed_runs_numbers_punctuation_and_emoji_move_monotonically_and_delete_logically(bool cell) => Run(() =>
    {
        const string text = "abc \u05d0\u05d1\u05d2 123 (\u0645\u0631\u062d\u0628\u0627) \U0001f469\u200d\U0001f469\u200d\U0001f467\u200d\U0001f466 e\u0301 xyz";
        var (window, editor, surface) = Create(text, tableCell: cell, width: 1100);
        try
        {
            var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToHashSet();
            window.KeyPress(Key.Home, RawInputModifiers.None);
            var previousX = surface.CaretRectangle.X;
            var positions = new List<int> { editor.Session.Selection.Active };
            for (var i = 0; i < text.Length * 2; i++)
            {
                window.KeyPress(Key.Right, RawInputModifiers.None);
                var x = surface.CaretRectangle.X;
                Assert.True(x >= previousX - .01);
                Assert.Contains(editor.Session.Selection.Active, boundaries);
                if (Math.Abs(x - previousX) < .01) break;
                positions.Add(editor.Session.Selection.Active); previousX = x;
            }
            Assert.Contains(Enumerable.Range(1, positions.Count - 1), i => positions[i] < positions[i - 1]);
            Assert.Equal(text.Length, editor.Session.Selection.Active);
            var emoji = text.IndexOf("\U0001f469", StringComparison.Ordinal);
            var emojiEnd = text.IndexOf(" e\u0301", StringComparison.Ordinal);
            editor.Session.Select(emojiEnd, emojiEnd);
            window.KeyPress(Key.Back, RawInputModifiers.None);
            Assert.Equal(text.Remove(emoji, emojiEnd - emoji), editor.Text);
            editor.Undo();
            editor.Session.Select(emoji, emoji);
            window.KeyPress(Key.Delete, RawInputModifiers.None);
            Assert.Equal(text.Remove(emoji, emojiEnd - emoji), editor.Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Forward_and_reverse_ranges_collapse_to_the_visual_edge(bool reverse) => Run(() =>
    {
        var (window, editor, surface) = Create("\u05d0\u05d1\u05d2", rtl: true);
        try
        {
            editor.Session.Select(reverse ? 2 : 1, reverse ? 1 : 2);
            window.KeyPress(Key.Left, RawInputModifiers.None);
            Assert.True(editor.Session.Selection.IsEmpty);
            Assert.Equal(2, editor.Session.Selection.Active);
            editor.Session.Select(reverse ? 2 : 1, reverse ? 1 : 2);
            window.KeyPress(Key.Right, RawInputModifiers.None);
            Assert.Equal(1, editor.Session.Selection.Active);
            window.KeyPress(Key.Left, RawInputModifiers.Shift);
            Assert.Equal(1, editor.Session.Selection.Anchor);
            Assert.Equal(2, editor.Session.Selection.Active);
            Assert.Equal("\u05d1", editor.SelectedText);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Zero_width_trailing_soft_break_remains_keyboard_reachable() => Run(() =>
    {
        var (window, editor, surface) = Create("abc\u2028");
        try
        {
            editor.Session.Select(3, 3);
            var before = surface.CaretRectangle;
            window.KeyPress(Key.Right, RawInputModifiers.None);
            Assert.Equal(4, editor.Session.Selection.Active);
            window.KeyPress(Key.Left, RawInputModifiers.None);
            Assert.Equal(3, editor.Session.Selection.Active);
            Assert.Equal(before.Y, surface.CaretRectangle.Y, 2);
        }
        finally { window.Close(); }
    });
    [Fact]
    public Task Word_arrows_follow_the_visual_direction_of_hebrew_runs() => Run(() =>
    {
        var (window, editor, _) = Create("\u05d0\u05d1\u05d2 \u05d3\u05d4\u05d5", rtl: true);
        try
        {
            var word = OperatingSystem.IsMacOS() ? RawInputModifiers.Alt : RawInputModifiers.Control;
            editor.Session.Select(2, 2);
            window.KeyPress(Key.Right, word);
            Assert.Equal(0, editor.Session.Selection.Active);
            editor.Session.Select(1, 1);
            window.KeyPress(Key.Left, word);
            Assert.Equal(4, editor.Session.Selection.Active);
        }
        finally { window.Close(); }
    });
    [Fact]
    public Task Pointer_and_keyboard_retain_both_affinities_at_a_mixed_run_boundary() => Run(() =>
    {
        var (window, editor, surface) = Create("abc \u05d0\u05d1\u05d2 xyz");
        try
        {
            var trailing = surface.Layout.Caret(new VisualCaret(3, 1, 0));
            var leading = surface.Layout.Caret(new VisualCaret(4, 0, 0));
            Assert.True(leading.X > trailing.X + 5);
            foreach (var rect in new[] { trailing, leading })
            {
                var point = surface.TranslatePoint(new Point(rect.X - .2, rect.Center.Y), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                Assert.Equal(4, editor.Session.Selection.Active);
                Assert.Equal(rect.X, surface.CaretRectangle.X, 2);
                var before = surface.CaretRectangle.X;
                window.KeyPress(Key.Right, RawInputModifiers.None);
                Assert.True(surface.CaretRectangle.X > before);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Wrapped_bidi_home_end_and_vertical_navigation_preserve_the_visual_column(bool cell) => Run(() =>
    {
        var (window, editor, surface) = Create("abc \u05d0\u05d1\u05d2 123 \u0645\u0631\u062d\u0628\u0627 xyz \u05d0\u05d1\u05d2 456 \u0645\u0631\u062d\u0628\u0627 abc \u05d0\u05d1\u05d2 123 \u0645\u0631\u062d\u0628\u0627 xyz \u05d0\u05d1\u05d2 456 \u0645\u0631\u062d\u0628\u0627", tableCell: cell, width: 260);
        try
        {
            editor.Session.Select(5, 5);
            var initial = surface.CaretRectangle;
            window.KeyPress(Key.Down, RawInputModifiers.None);
            Assert.True(surface.CaretRectangle.Y > initial.Y);
            window.KeyPress(Key.Up, RawInputModifiers.None);
            Assert.Equal(initial.X, surface.CaretRectangle.X, 2);
            window.KeyPress(Key.End, RawInputModifiers.None);
            var end = surface.CaretRectangle;
            window.KeyPress(Key.Home, RawInputModifiers.None);
            Assert.Equal(end.Y, surface.CaretRectangle.Y, 2);
            Assert.True(surface.CaretRectangle.X < end.X);
            window.KeyPress(Key.End, RawInputModifiers.None);
            window.KeyPress(Key.Right, RawInputModifiers.None);
            Assert.True(surface.CaretRectangle.Y > end.Y);
            window.KeyPress(Key.Left, RawInputModifiers.None);
            Assert.Equal(end.Y, surface.CaretRectangle.Y, 2);
            Assert.Equal(end.X, surface.CaretRectangle.X, 2);
        }
        finally { window.Close(); }
    });
}
