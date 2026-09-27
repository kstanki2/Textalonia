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

public class InteractionGeometryTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static TextInputMethodClient Client(DocumentSurface surface)
    {
        var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        surface.RaiseEvent(request);
        return Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
    }
    private static void EqualGeometry(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 2); Assert.Equal(expected.Y, actual.Y, 2);
        Assert.Equal(expected.Width, actual.Width, 2); Assert.Equal(expected.Height, actual.Height, 2);
    }
    private static Rect AssertSharedCaret(TextaloniaEditor editor, DocumentSurface surface, TextInputMethodClient client)
    {
        var caret = surface.CaretRectangle;
        EqualGeometry(caret, client.CursorRectangle);
        EqualGeometry(caret, Assert.Single(editor.Accessibility.CaretRange.GetBoundingRectangles()));
        return caret;
    }

    [Fact]
    public Task Table_resize_preview_commit_and_undo_keep_ime_accessibility_and_caret_aligned() => fixture.Session.Dispatch(() =>
    {
        var target = new Paragraph("second column target");
        var table = Table.Create(2, 2).SetCell(0, 1, new TableCell { Blocks = [target] });
        var editor = new TextaloniaEditor { ShowToolbar = false, Document = new FlowDocument([table]) };
        var window = new Window { Width = 700, Height = 400, Content = editor }; window.Show(); window.UpdateLayout();
        try
        {
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            editor.FocusDocument(); var entry = editor.Session.Index.ById(target.Id); editor.Session.Select(entry.Start + 3, entry.Start + 3);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); var client = Client(surface);
            var original = AssertSharedCaret(editor, surface, client); var revision = editor.Session.Revision;
            var oldRange = editor.Accessibility.CaretRange;
            var track = surface.Layout.TableCells().First(cell => cell.Row == 0 && cell.Column == 0);
            Assert.True(editor.BeginTableResize(table.Id, TableResizeAxis.Column, 0, track.ColumnWidth));
            editor.PreviewTableResize(track.ColumnWidth + 70); window.UpdateLayout();
            var preview = AssertSharedCaret(editor, surface, client);
            Assert.True(preview.X > original.X + 50); Assert.Equal(revision, editor.Session.Revision);
            EqualGeometry(preview, Assert.Single(oldRange.GetBoundingRectangles()));
            Assert.True(editor.CommitTableResize()); window.UpdateLayout();
            EqualGeometry(preview, AssertSharedCaret(editor, surface, client));
            Assert.Throws<InvalidOperationException>(() => oldRange.GetBoundingRectangles());
            editor.Undo(); window.UpdateLayout();
            EqualGeometry(original, AssertSharedCaret(editor, surface, client)); Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Bidi_caret_ime_and_accessibility_keep_affinity_after_virtualized_scroll_and_viewport_resize() => fixture.Session.Dispatch(() =>
    {
        var target = new Paragraph("abc \u05d0\u05d1\u05d2 xyz");
        var table = Table.Create(1, 2).SetCell(0, 0, new TableCell { Blocks = [target] });
        var blocks = Enumerable.Range(0, 250).Select(i => (Block)new Paragraph($"Before {i}")).Append(table)
            .Concat(Enumerable.Range(0, 80).Select(i => (Block)new Paragraph($"After {i}")));
        var editor = new TextaloniaEditor { ShowToolbar = false, SynchronizeText = false, Document = new FlowDocument(blocks) };
        var window = new Window { Width = 700, Height = 400, Content = editor }; window.Show(); window.UpdateLayout();
        try
        {
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            editor.FocusDocument(); var entry = editor.Session.Index.ById(target.Id);
            editor.Session.Select(entry.Start + 4, entry.Start + 4);
            editor.Accessibility.CaretRange.ScrollIntoView(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(editor.Scroller!.Offset.Y > 1000);
            surface.SelectVisualCaret(new VisualCaret(entry.Start + 3, 1, entry.Start), false);
            var client = Client(surface); var before = AssertSharedCaret(editor, surface, client);
            Assert.True(surface.Layout.Caret(entry.Start + 4).X > before.X + 5);
            var originalViewport = editor.Scroller.Viewport;
            // Resize the hosted editor directly: the headless native window does not
            // synchronously publish platform resize notifications from Width/Height.
            editor.Width = 520; editor.Height = 260; window.UpdateLayout();
            Assert.True(editor.Scroller.Viewport.Width < originalViewport.Width);
            Assert.True(editor.Scroller.Viewport.Height < originalViewport.Height);
            editor.Scroller.Offset = new Vector(0, Math.Max(0, editor.Scroller.Offset.Y - 30)); window.UpdateLayout();
            AssertSharedCaret(editor, surface, client);
            Assert.Equal(entry.Start + 4, editor.Accessibility.CaretPosition);
            Assert.Equal("abc \u05d0\u05d1\u05d2 xyz", editor.Accessibility.Range(entry.Start, entry.End).GetText());
        }
        finally { window.Close(); }
    }, CancellationToken.None);
}
