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

public class TouchSelectionTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);
    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface, DefaultPointerComponent Pointer) Create(bool readOnly = false)
    {
        var pointer = new DefaultPointerComponent();
        var editor = new TextaloniaEditor { Text = "alpha beta gamma delta", ShowToolbar = false, PointerComponent = pointer, IsReadOnly = readOnly };
        var window = new Window { Width = 600, Height = 300, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument(); Dispatcher.UIThread.RunJobs();
        return (window, editor, editor.GetVisualDescendants().OfType<DocumentSurface>().Single(), pointer);
    }
    private static Point At(DocumentSurface surface, Window window, int position) =>
        surface.TranslatePoint(surface.Layout.Caret(position).Center, window)!.Value;
    private static Point InWindow(DocumentSurface surface, Window window, Point point) => surface.TranslatePoint(point, window)!.Value;
    private static Point TranslateHandle(Point handle, Rect origin, Rect target) =>
        new(handle.X + target.Center.X - origin.Center.X, handle.Y + target.Center.Y - origin.Center.Y);

    [Fact]
    public Task Touch_pan_does_not_select_or_start_an_edit() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            editor.Session.Select(0, 5);
            var original = editor.Session.Selection;
            var point = At(surface, window, 7);
            using var contact = window.TouchBegin(point);
            Assert.True(pointer.TouchSelection!.IsPending);
            Assert.Equal(original, editor.Session.Selection);
            window.TouchMove(contact, point + new Point(0, 40));
            pointer.TouchSelection.CompleteLongPress();
            Assert.False(pointer.TouchSelection.IsPending);
            Assert.False(pointer.TouchSelection.IsDragging);
            window.TouchEnd(contact, point + new Point(0, 40));
            Assert.Equal(original, editor.Session.Selection);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Long_press_readonly_selects_word_and_offers_copy_without_edits() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create(readOnly: true);
        try
        {
            var revision = editor.Session.Revision;
            var point = At(surface, window, 7);
            using var contact = window.TouchBegin(point);
            pointer.TouchSelection!.CompleteLongPress();
            Assert.Equal("beta", editor.SelectedText);
            Assert.True(pointer.TouchSelection.IsDragging);
            Assert.NotNull(pointer.TouchSelection.HandlePositions().Start);
            Assert.NotNull(pointer.TouchSelection.HandlePositions().End);
            window.TouchEnd(contact, point);
            Assert.True(surface.ContextMenu!.IsOpen);
            Assert.True(editor.CopyCommand.CanExecute(null));
            Assert.False(editor.CutCommand.CanExecute(null));
            Assert.False(editor.PasteCommand.CanExecute(null));
            Assert.Equal(revision, editor.Session.Revision);
            Assert.False(editor.Session.CanUndo);
            surface.ContextMenu.Close();
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Range_handle_crossing_preserves_the_opposite_logical_endpoint() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            var point = At(surface, window, 7);
            using (var contact = window.TouchBegin(point))
            {
                pointer.TouchSelection!.CompleteLongPress();
                window.TouchEnd(contact, point);
            }
            surface.ContextMenu!.Close(); editor.FocusDocument(); window.UpdateLayout();
            var start = pointer.TouchSelection!.HandlePositions().Start!.Value;
            var destination = TranslateHandle(start, surface.Layout.Caret(6), surface.Layout.Caret(16));
            using var handle = window.TouchBegin(InWindow(surface, window, start));
            window.TouchMove(handle, InWindow(surface, window, destination));
            Assert.Equal(10, editor.Session.Selection.Anchor);
            Assert.Equal(16, editor.Session.Selection.Active);
            window.TouchEnd(handle, InWindow(surface, window, destination));
            Assert.False(pointer.TouchSelection.IsDragging);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Caret_handle_moves_a_collapsed_selection() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            var point = At(surface, window, 0);
            using (var contact = window.TouchBegin(point)) window.TouchEnd(contact, point);
            var handlePoint = pointer.TouchSelection!.HandlePositions().End!.Value;
            Assert.Null(pointer.TouchSelection.HandlePositions().Start);
            var destination = TranslateHandle(handlePoint, surface.Layout.Caret(0), surface.Layout.Caret(9));
            using var handle = window.TouchBegin(InWindow(surface, window, handlePoint));
            window.TouchMove(handle, InWindow(surface, window, destination));
            window.TouchEnd(handle, InWindow(surface, window, destination));
            Assert.True(editor.Session.Selection.IsEmpty);
            Assert.Equal(9, editor.Session.Selection.Active);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Focus_loss_viewport_change_and_capture_loss_cancel_gestures() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            window.Focusable = true;
            var point = At(surface, window, 7);
            using (var contact = window.TouchBegin(point))
            {
                Assert.True(pointer.TouchSelection!.IsPending);
                window.Focus();
                pointer.TouchSelection.CompleteLongPress();
                Assert.False(pointer.TouchSelection.IsPending);
                Assert.False(pointer.TouchSelection.IsDragging);
                window.TouchEnd(contact, point);
            }
            editor.FocusDocument();
            using (var contact = window.TouchBegin(point))
            {
                window.Width = 380; window.UpdateLayout();
                Assert.False(pointer.TouchSelection!.IsPending);
                window.TouchEnd(contact, point);
            }
            point = At(surface, window, 7);
            var captured = window.TouchBegin(point);
            pointer.TouchSelection!.CompleteLongPress();
            Assert.True(pointer.TouchSelection.IsDragging);
            captured.Dispose();
            Assert.False(pointer.TouchSelection.IsDragging);
            Assert.Equal("beta", editor.SelectedText);
        }
        finally { window.Close(); }
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Tap_and_long_press_hit_test_committed_geometry_after_cancelling_preedit(bool hold) => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            editor.Text = "WWWW beta gamma delta";
            window.UpdateLayout();
            var point = At(surface, window, 7);
            surface.SetPreedit(new string('i', 40), 20);
            window.UpdateLayout();
            Assert.True(surface.HasComposition);
            using var contact = window.TouchBegin(point);
            if (hold) pointer.TouchSelection!.CompleteLongPress();
            window.TouchEnd(contact, point);
            Assert.False(surface.HasComposition);
            if (hold) Assert.Equal("beta", editor.SelectedText);
            else Assert.Equal(7, editor.Session.Selection.Active);
            Assert.Equal("WWWW beta gamma delta", editor.Text);
            surface.ContextMenu!.Close();
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Handles_keep_visual_bidi_affinity_for_collapsed_and_range_endpoints() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            editor.Text = "abc \u05d0\u05d1\u05d2 xyz"; window.UpdateLayout();
            foreach (var visual in new[] { new VisualCaret(3, 1, 0), new VisualCaret(4, 0, 0) })
            {
                pointer.TouchSelection!.Cancel(hideHandles: true);
                var rect = surface.Layout.Caret(visual);
                var point = InWindow(surface, window, new Point(rect.X - .2, rect.Center.Y));
                using (var contact = window.TouchBegin(point)) window.TouchEnd(contact, point);
                Assert.Equal(4, editor.Session.Selection.Active);
                Assert.Equal(rect.X, surface.CaretRectangle.X, 2);
                Assert.Equal(surface.CaretRectangle.Center.X, pointer.TouchSelection.HandlePositions().End!.Value.X, 2);
                surface.SelectVisualCaret(VisualCaret.Logical(10), extend: true);
                Assert.Equal(rect.Center.X, pointer.TouchSelection.HandlePositions().Start!.Value.X, 2);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Exact_row_overflow_has_no_touch_endpoint_handle() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            var paragraph = new Paragraph("line one\u2028line two\u2028line three") { Style = new() { LineHeight = 40 } };
            var table = Table.Create(2, 1) with { RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 45 }, new() { Mode = TableRowHeightMode.AtLeast, Height = 75 }] };
            table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [paragraph], Padding = new(0, 0, 0, 0) });
            editor.Document = new FlowDocument([table]); window.UpdateLayout();
            var point = At(surface, window, 0);
            using (var contact = window.TouchBegin(point)) window.TouchEnd(contact, point);
            editor.Session.Select(0, 20);
            var (start, end) = pointer.TouchSelection!.HandlePositions();
            Assert.NotNull(start);
            Assert.Null(end);
            var clip = surface.Layout.Paragraphs.Single(p => p.Position.Paragraph.Id == paragraph.Id).Clip!.Value;
            Assert.True(start.Value.Y + 7 <= clip.Bottom);
        }
        finally { window.Close(); }
    });
    [Fact]
    public Task Detach_and_document_replacement_disarm_pending_long_press() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            var point = At(surface, window, 7);
            using var contact = window.TouchBegin(point);
            var touch = pointer.TouchSelection!;
            editor.Text = "replacement";
            Assert.False(touch.IsPending);
            touch.CompleteLongPress();
            Assert.True(editor.Session.Selection.IsEmpty);
            editor.PointerComponent = null;
            touch.CompleteLongPress();
            Assert.False(touch.HandlesVisible);
            Assert.False(touch.IsDragging);
            window.TouchEnd(contact, point);
            Assert.Equal("replacement", editor.Text);
        }
        finally { window.Close(); }
    });
}


