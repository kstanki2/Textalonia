using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class PageAccessibilityTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);

    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface) Create(
        FlowDocument document, DocumentViewMode viewMode)
    {
        var editor = new TextaloniaEditor
        {
            ShowToolbar = false, SynchronizeText = false, ViewMode = viewMode,
            Document = document with
            {
                Sections = [new DocumentSection { PageSettings = new PageSettings { Width = 300, Height = 200, Margins = new EdgeInsets(20, 20, 20, 20) } }]
            }
        };
        var window = new Window { Width = 620, Height = 320, Content = editor };
        window.Show(); window.UpdateLayout();
        return (window, editor, editor.GetVisualDescendants().OfType<DocumentSurface>().Single());
    }

    private static TextInputMethodClient Client(DocumentSurface surface)
    {
        var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        surface.RaiseEvent(request);
        return Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
    }

    private static void Equal(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 2); Assert.Equal(expected.Y, actual.Y, 2);
        Assert.Equal(expected.Width, actual.Width, 2); Assert.Equal(expected.Height, actual.Height, 2);
    }

    private static Rect Scale(Rect bounds, double zoom) =>
        new(bounds.X * zoom, bounds.Y * zoom, bounds.Width * zoom, bounds.Height * zoom);

    [Theory]
    [InlineData(DocumentViewMode.Simple, .5)]
    [InlineData(DocumentViewMode.Simple, 1)]
    [InlineData(DocumentViewMode.Simple, 2)]
    [InlineData(DocumentViewMode.Draft, .5)]
    [InlineData(DocumentViewMode.Draft, 1)]
    [InlineData(DocumentViewMode.Draft, 2)]
    [InlineData(DocumentViewMode.PrintLayout, .5)]
    [InlineData(DocumentViewMode.PrintLayout, 1)]
    [InlineData(DocumentViewMode.PrintLayout, 2)]
    public Task Zoom_keeps_caret_selection_point_ranges_and_ime_in_surface_coordinates(DocumentViewMode mode, double zoom) => Run(() =>
    {
        var (window, editor, surface) = Create(FlowDocument.FromText("first line\nsecond line"), mode);
        try
        {
            editor.FocusDocument(); editor.Session.Select(3, 3);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var provider = editor.Accessibility;
            var caret = Assert.Single(provider.CaretRange.GetBoundingRectangles());
            var inactiveCaret = Assert.Single(provider.Range(7, 7).GetBoundingRectangles());
            var selection = Assert.Single(provider.Range(0, 5).GetBoundingRectangles());
            var revision = editor.Session.Revision;
            var document = editor.Document;
            editor.Zoom = zoom;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();

            var actualCaret = Assert.Single(provider.CaretRange.GetBoundingRectangles());
            Equal(Scale(caret, zoom), actualCaret);
            Equal(Scale(inactiveCaret, zoom), Assert.Single(provider.Range(7, 7).GetBoundingRectangles()));
            Equal(Scale(selection, zoom), Assert.Single(provider.Range(0, 5).GetBoundingRectangles()));
            Equal(actualCaret, surface.CaretRectangle);
            var client = Client(surface);
            Equal(actualCaret, client.CursorRectangle);
            Assert.Equal(3, provider.RangeFromPoint(new Point(actualCaret.Left, actualCaret.Center.Y)).Start);
            Assert.Equal("first line\n", provider.CaretRange.Expand(DocumentTextUnit.Line).GetText());
            Assert.Same(document, editor.Document);
            Assert.Equal(revision, editor.Session.Revision);
            Assert.False(editor.Session.CanUndo);

            client.SetPreeditText("preview");
            window.UpdateLayout();
            Equal(surface.CaretRectangle, client.CursorRectangle);
            Assert.Equal("first line\nsecond line", provider.DocumentRange.GetText());
            Assert.Throws<InvalidOperationException>(() => provider.CaretRange.GetBoundingRectangles());
            surface.CancelComposition();
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(2)]
    public Task Paged_selection_and_scrolling_share_visible_geometry_without_changing_selection(double zoom) => Run(() =>
    {
        var document = new FlowDocument(Enumerable.Range(0, 60).Select(i => (Block)new Paragraph($"Paragraph {i:00}: content")));
        var (window, editor, _) = Create(document, DocumentViewMode.PrintLayout);
        try
        {
            editor.Zoom = zoom; editor.PagesPerRow = 2;
            window.UpdateLayout();
            Assert.True(editor.PageCount > 4);
            var target = editor.Session.Index.Paragraphs[45];
            editor.Session.Select(3, target.Start + 5);
            var selection = editor.Session.Selection;
            var revision = editor.Session.Revision;
            var provider = editor.Accessibility;
            var rectangles = provider.SelectionRange.GetBoundingRectangles();
            Assert.True(rectangles.Count > 45);
            Assert.All(rectangles, rect => Assert.True(rect.Width > 0 && rect.Height > 0));
            Assert.True(rectangles.Max(rect => rect.Bottom) > 400 * zoom);
            Assert.Throws<InvalidOperationException>(() => provider.SelectionRange.GetBoundingRectangles(1));

            var range = provider.Range(target.Start, target.End);
            range.ScrollIntoView();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var scroller = editor.Scroller!;
            Assert.True(scroller.Offset.Y > 0);
            var view = new Rect(scroller.Offset.X, scroller.Offset.Y, scroller.Viewport.Width, scroller.Viewport.Height);
            Assert.Contains(range.GetBoundingRectangles(), rect => rect.Intersects(view));
            Assert.Contains(provider.GetVisibleRanges(), visible => visible.Start < target.End && visible.End > target.Start);
            Assert.Equal(selection, editor.Session.Selection);
            Assert.Equal(revision, editor.Session.Revision);

            var caret = Assert.Single(provider.Range(target.Start, target.Start).GetBoundingRectangles());
            Assert.Equal(target.Start, provider.RangeFromPoint(new Point(caret.Left, caret.Center.Y)).Start);
            Assert.Equal($"Paragraph 45: content\n", provider.Range(target.Start, target.Start).Expand(DocumentTextUnit.Line).GetText());
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Paged_inline_objects_and_graphemes_keep_atomic_accessibility_ranges_at_double_zoom() => Run(() =>
    {
        var inline = new InlineDescriptor { AltText = "Atomic image", Payload = new ImageInlinePayload("missing") };
        var paragraph = new Paragraph([new RichRun("A\U0001F469\u200D\U0001F4BB"), new RichRun(inline), new RichRun("B")]);
        var (window, editor, _) = Create(new FlowDocument([paragraph]), DocumentViewMode.PrintLayout);
        try
        {
            editor.Zoom = 2; window.UpdateLayout();
            var provider = editor.Accessibility;
            var emoji = provider.Range(2, 2).Expand(DocumentTextUnit.Character);
            Assert.Equal(1, emoji.Start); Assert.Equal(6, emoji.End);
            Assert.Equal("\U0001F469\u200D\U0001F4BB", emoji.GetText());
            Assert.NotEmpty(emoji.GetBoundingRectangles());
            var image = provider.Range(6, 6).Expand(DocumentTextUnit.Character);
            Assert.Equal(7, image.End); Assert.Equal("\uFFFC", image.GetText());
            Assert.NotEmpty(image.GetBoundingRectangles());
            Assert.Equal("Atomic image", Assert.Single(image.GetDescriptions()).Text);
            var line = image.Expand(DocumentTextUnit.Line);
            Assert.True(line.Start <= emoji.Start && line.End >= image.End);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(.5)]
    [InlineData(2)]
    public Task Simple_zoom_preserves_bounded_offscreen_accessibility_shaping(double zoom) => Run(() =>
    {
        var document = new FlowDocument(Enumerable.Range(0, 1200).Select(i => (Block)new Paragraph($"Paragraph {i}: content")));
        var (window, editor, surface) = Create(document, DocumentViewMode.Simple);
        try
        {
            editor.Zoom = zoom; window.UpdateLayout();
            var target = editor.Session.Index.Paragraphs[1050];
            var before = surface.Layout.ShapedParagraphs;
            var range = editor.Accessibility.Range(target.Start, target.Start + 9);
            Assert.All(range.GetBoundingRectangles(), bounds => Assert.True(bounds.Y > 1000 && bounds.Width > 0));
            Assert.InRange(surface.Layout.ShapedParagraphs - before, 1, 80);
            Assert.InRange(surface.Layout.CachedParagraphs, 1, 256);
            Assert.Equal(0, editor.Session.Index.FullTextReads);
            range.ScrollIntoView(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(editor.Scroller!.Offset.Y > 1000);
            Assert.InRange(surface.Layout.CachedParagraphs, 1, 256);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Caret_at_wrapped_page_boundary_retains_upstream_affinity_for_accessibility_and_ime() => Run(() =>
    {
        var document = FlowDocument.FromText(string.Join(" ", Enumerable.Repeat("wrapped words", 150)));
        var (window, editor, surface) = Create(document, DocumentViewMode.PrintLayout);
        try
        {
            editor.Zoom = 2; editor.FocusDocument();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var pages = surface.PagedLayout!;
            var previousLine = pages.Fragments.Last(fragment => fragment.PageIndex == 0);
            var upstream = new VisualCaret(previousLine.TextEnd, 0, previousLine.TextStart);
            surface.SelectVisualCaret(upstream, false);
            var caret = Assert.Single(editor.Accessibility.CaretRange.GetBoundingRectangles());
            Equal(surface.CaretRectangle, caret);
            Equal(Client(surface).CursorRectangle, caret);
            Equal(Scale(pages.Caret(upstream), 2), caret);
            Assert.True(caret.Bottom < pages.Caret(previousLine.TextEnd).Top * 2);
            Assert.Equal(previousLine.TextEnd, editor.Accessibility.CaretPosition);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task View_fit_and_page_navigation_preserve_document_selection_and_both_history_directions() => Run(() =>
    {
        var document = new FlowDocument(Enumerable.Range(0, 40).Select(i => (Block)new Paragraph($"Paragraph {i}: content")));
        var (window, editor, _) = Create(document, DocumentViewMode.Simple);
        try
        {
            editor.Session.InsertText("X"); editor.Session.BreakUndoGroup();
            editor.Session.InsertText("Y"); editor.Undo();
            Assert.True(editor.Session.CanUndo); Assert.True(editor.Session.CanRedo);
            var target = editor.Session.Index.Paragraphs[30];
            editor.Session.Select(target.End, target.Start);
            document = editor.Document;
            var revision = editor.Session.Revision;
            var selection = editor.Session.Selection;
            var range = editor.Accessibility.SelectionRange;
            var selectedText = range.GetText();

            ChangeView(() => editor.Zoom = 2);
            ChangeView(() => editor.ViewMode = DocumentViewMode.Draft);
            ChangeView(editor.FitWidth);
            ChangeView(editor.FitPage);
            Assert.Equal(DocumentViewMode.PrintLayout, editor.ViewMode);
            Assert.True(editor.PageCount > 1);
            ChangeView(() => editor.PagesPerRow = 2);
            ChangeView(() => editor.GoToPage(editor.PageCount - 1));
            ChangeView(() => range.ScrollIntoView(false));
            var scroller = editor.Scroller!;
            var view = new Rect(scroller.Offset.X, scroller.Offset.Y, scroller.Viewport.Width, scroller.Viewport.Height);
            Assert.Contains(range.GetBoundingRectangles(), rect => rect.Intersects(view));
            ChangeView(() => editor.ViewMode = DocumentViewMode.Simple);
            Assert.Equal(1, editor.PageCount);

            void ChangeView(Action change)
            {
                change(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Same(document, editor.Document);
                Assert.Equal(revision, editor.Session.Revision);
                Assert.Equal(selection, editor.Session.Selection);
                Assert.True(editor.Session.CanUndo); Assert.True(editor.Session.CanRedo);
                Assert.Equal(selectedText, range.GetText());
            }
        }
        finally { window.Close(); }
    });
}
