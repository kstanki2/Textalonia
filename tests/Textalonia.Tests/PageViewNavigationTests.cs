using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class PageViewNavigationTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Repeated_next_page_advances_across_fitted_page_rows() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Document = EightPages(), ShowToolbar = false,
            ViewMode = DocumentViewMode.PrintLayout, PagesPerRow = 2 };
        var window = new Window { Width = 900, Height = 600, Content = editor };
        try
        {
            window.Show(); Settle(); editor.FitWidth(); Settle();
            Assert.Equal(8, editor.PageCount);
            Assert.Equal(0, editor.Scroller!.Offset.X);
            var document = editor.Document; var selection = editor.Session.Selection;
            for (var expected = 2; expected <= editor.PageCount; expected++)
            {
                // Same operation as the toolbar's Next page button.
                editor.GoToPage(Math.Min(editor.PageCount - 1, editor.CurrentPageNumber));
                Settle();
                Assert.Equal(expected, editor.CurrentPageNumber);
                AssertPageVisible(editor, expected - 1);
            }
            Assert.Same(document, editor.Document); Assert.Equal(selection, editor.Session.Selection);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }

        void Settle() { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }, CancellationToken.None);

    [Fact]
    public Task Immediate_view_and_zoom_change_publishes_extent_before_distant_navigation() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Document = EightPages(), ShowToolbar = false };
        var window = new Window { Width = 900, Height = 600, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(1, editor.PageCount);
            var document = editor.Document; var selection = editor.Session.Selection;
            editor.ViewMode = DocumentViewMode.PrintLayout;
            editor.Zoom = 2;
            editor.GoToPage(7); // Deliberately no intervening host layout/dispatcher pass.
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(8, editor.PageCount); Assert.Equal(8, editor.CurrentPageNumber);
            Assert.True(editor.Scroller!.Offset.Y > 1000);
            AssertPageVisible(editor, 7);
            Assert.Same(document, editor.Document); Assert.Equal(selection, editor.Session.Selection);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    private static FlowDocument EightPages() => new(Enumerable.Range(0, 8).Select(index =>
        new Paragraph($"Physical page {index + 1}") { Style = new() { PageBreakBefore = index > 0 } }))
    {
        Sections = [new DocumentSection { PageSettings = new PageSettings { Width = 300, Height = 400, Margins = new(24, 24, 24, 24) } }]
    };

    private static void AssertPageVisible(TextaloniaEditor editor, int pageIndex)
    {
        var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
        var bounds = surface.PagedLayout!.Pages[pageIndex].Bounds;
        var scaled = new Rect(bounds.X * editor.Zoom, bounds.Y * editor.Zoom, bounds.Width * editor.Zoom, bounds.Height * editor.Zoom);
        var scroller = editor.Scroller!;
        Assert.True(scaled.Intersects(new Rect(scroller.Offset.X, scroller.Offset.Y, scroller.Viewport.Width, scroller.Viewport.Height)));
    }
}
