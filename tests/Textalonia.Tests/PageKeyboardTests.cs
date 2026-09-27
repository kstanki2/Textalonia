using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Layout;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class PageKeyboardTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);

    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface) Create(double zoom, bool columns)
    {
        var paragraph = new Paragraph(string.Join("\u2028", Enumerable.Repeat("abcdefgh", 30)))
        {
            Style = new() { SpaceBefore = 0, SpaceAfter = 0, LineSpacingMode = LineSpacingMode.Exact,
                LineSpacing = 20, WidowControl = false }
        };
        var editor = new TextaloniaEditor
        {
            ShowToolbar = false, SynchronizeText = false, ViewMode = DocumentViewMode.PrintLayout,
            Zoom = zoom, PagesPerRow = 2,
            Document = new FlowDocument([paragraph])
            {
                Sections = [new DocumentSection { PageSettings = new PageSettings
                {
                    Width = 340, Height = 120, Margins = new EdgeInsets(10, 10, 10, 10),
                    Columns = columns ? [new PageColumn(), new PageColumn()] : [],
                    ColumnSpacing = 20, BalanceColumns = false
                } }]
            }
        };
        var window = new Window { Width = 640, Height = 300, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        return (window, editor, editor.GetVisualDescendants().OfType<DocumentSurface>().Single());
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers);
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    }

    [Fact]
    public Task Page_caret_navigation_skips_a_page_with_only_fully_clipped_fragments_in_both_directions() => Run(() =>
    {
        var first = new Paragraph("abcdefgh");
        var hidden = new Paragraph("clipped")
        {
            Style = new() { PageBreakBefore = true, Frame = new ParagraphFrame(0, 200, 100, 20) }
        };
        var last = new Paragraph("abcdefgh") { Style = new() { PageBreakBefore = true } };
        var settings = new PageSettings { Width = 240, Height = 120, Margins = new EdgeInsets(10, 10, 10, 10) };
        var document = new FlowDocument([first, hidden, last])
        {
            // A positioned frame leaves the flow cursor at the top of its page;
            // a section boundary guarantees a distinct final page for this fixture.
            Sections = [new DocumentSection { PageSettings = settings },
                new DocumentSection { StartParagraphId = last.Id, BreakKind = SectionBreakKind.NextPage, PageSettings = settings }]
        };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(3, pages.Pages.Length);
        var clipped = pages.Fragments.Where(fragment => fragment.PageIndex == 1).ToArray();
        Assert.NotEmpty(clipped);
        Assert.All(clipped, fragment => Assert.True(fragment.Bounds.Intersect(fragment.Clip).Height <= 0));

        var start = VisualCaret.Logical(3);
        var next = pages.MovePageCaret(start, true, pages.CaretColumnX(start));
        Assert.Equal(2, pages.GetPageIndex(next.Position));
        Assert.Equal(new DocumentIndex(document).ById(last.Id).Start + 3, next.Position);
        var previous = pages.MovePageCaret(next, false, pages.CaretColumnX(next));
        Assert.Equal(0, pages.GetPageIndex(previous.Position));
        Assert.Equal(start.Position, previous.Position);
    });

    [Fact]
    public Task Explicit_page_break_after_a_positioned_frame_starts_a_distinct_sheet() => Run(() =>
    {
        var first = new Paragraph("first");
        var frame = new Paragraph("frame")
        {
            Style = new() { PageBreakBefore = true, Frame = new ParagraphFrame(0, 200, 100, 20) }
        };
        var last = new Paragraph("last") { Style = new() { PageBreakBefore = true } };
        var document = new FlowDocument([first, frame, last])
        {
            Sections = [new DocumentSection { PageSettings = new PageSettings
                { Width = 240, Height = 120, Margins = new EdgeInsets(10, 10, 10, 10) } }]
        };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(3, pages.Pages.Length);
        Assert.Equal(0, Assert.Single(pages.Fragments, fragment => fragment.ParagraphId == first.Id).PageIndex);
        Assert.Equal(1, Assert.Single(pages.Fragments, fragment => fragment.ParagraphId == frame.Id).PageIndex);
        Assert.Equal(2, Assert.Single(pages.Fragments, fragment => fragment.ParagraphId == last.Id).PageIndex);
    });

    [Fact]
    public Task Draft_page_keys_advance_through_the_continuous_surface() => Run(() =>
    {
        var (window, editor, surface) = Create(1, false);
        try
        {
            editor.ViewMode = DocumentViewMode.Draft;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            editor.Session.Select(3, 3);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var x = surface.CaretRectangle.X;
            Press(window, Key.PageDown);
            Assert.True(editor.Session.Selection.Active > 3);
            Assert.Equal(x, surface.CaretRectangle.X, 2);
            Press(window, Key.PageUp);
            Assert.Equal(3, editor.Session.Selection.Active);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false, .5)]
    [InlineData(false, 2)]
    [InlineData(true, .5)]
    [InlineData(true, 2)]
    public Task Vertical_arrows_cross_page_and_column_boundaries_in_both_directions_and_extend_selection(bool columns, double zoom) => Run(() =>
    {
        var (window, editor, surface) = Create(zoom, columns);
        try
        {
            var pages = surface.PagedLayout!;
            var before = pages.Fragments[4];
            var after = pages.Fragments[5];
            Assert.Equal(0, before.PageIndex); Assert.Equal(0, before.ColumnIndex);
            Assert.Equal(columns ? 0 : 1, after.PageIndex); Assert.Equal(columns ? 1 : 0, after.ColumnIndex);
            var start = before.TextStart + 3;
            var next = after.TextStart + 3;
            editor.Session.Select(start, start);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var original = surface.CaretRectangle;

            Press(window, Key.Down);
            Assert.Equal(next, editor.Session.Selection.Active);
            var moved = surface.CaretRectangle;
            Assert.Equal(original.X - before.Bounds.Left * zoom, moved.X - after.Bounds.Left * zoom, 2);
            Assert.Equal(next, editor.Accessibility.RangeFromPoint(new Point(moved.Left, moved.Center.Y)).Start);
            Press(window, Key.Up);
            Assert.Equal(start, editor.Session.Selection.Active);
            Assert.Equal(start, editor.Session.Selection.Anchor);

            Press(window, Key.Down, RawInputModifiers.Shift);
            Assert.Equal(start, editor.Session.Selection.Anchor); Assert.Equal(next, editor.Session.Selection.Active);
            Assert.Equal(next - start, editor.Accessibility.SelectionRange.End - editor.Accessibility.SelectionRange.Start);
            Press(window, Key.Up, RawInputModifiers.Shift);
            Assert.Equal(start, editor.Session.Selection.Active); Assert.True(editor.Session.Selection.IsEmpty);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(.5)]
    [InlineData(2)]
    public Task Page_keys_move_between_physical_pages_with_multiple_pages_per_row(double zoom) => Run(() =>
    {
        var (window, editor, surface) = Create(zoom, false);
        try
        {
            var pages = surface.PagedLayout!;
            var start = pages.Fragments[1].TextStart + 3;
            var next = pages.Fragments[6].TextStart + 3;
            editor.Session.Select(start, start);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var original = surface.CaretRectangle;

            Press(window, Key.PageDown);
            Assert.Equal(next, editor.Session.Selection.Active);
            Assert.Equal(1, pages.GetPageIndex(editor.Session.Selection.Active));
            var moved = surface.CaretRectangle;
            Assert.Equal(original.X - pages.Pages[0].Bounds.Left * zoom, moved.X - pages.Pages[1].Bounds.Left * zoom, 2);
            Assert.Equal(original.Y - pages.Pages[0].Bounds.Top * zoom, moved.Y - pages.Pages[1].Bounds.Top * zoom, 2);
            Press(window, Key.PageUp);
            Assert.Equal(start, editor.Session.Selection.Active);

            Press(window, Key.PageDown, RawInputModifiers.Shift);
            Assert.Equal(start, editor.Session.Selection.Anchor); Assert.Equal(next, editor.Session.Selection.Active);
            Press(window, Key.PageUp, RawInputModifiers.Shift);
            Assert.Equal(start, editor.Session.Selection.Active); Assert.True(editor.Session.Selection.IsEmpty);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    });
}
