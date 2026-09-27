using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Xunit;

namespace Textalonia.Tests;

public class SelectionAutoscrollTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);

    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface, DefaultPointerComponent Pointer) Create()
    {
        var pointer = new DefaultPointerComponent();
        var editor = new TextaloniaEditor
        {
            Text = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"Line {i}: Arabic \u0627\u0644\u0639\u0631\u0628\u064a\u0629 Hebrew \u05e2\u05d1\u05e8\u05d9\u05ea and text")),
            ShowToolbar = false, PointerComponent = pointer
        };
        var window = new Window { Width = 600, Height = 320, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument(); Dispatcher.UIThread.RunJobs();
        var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
        return (window, editor, surface, pointer);
    }

    [Fact]
    public Task Stationary_edge_drag_keeps_scrolling_and_preserves_anchor_without_history() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            window.MouseDown(surface.TranslatePoint(new Point(40, 38), window)!.Value, MouseButton.Left);
            var anchor = editor.Session.Selection.Anchor;
            window.MouseMove(new Point(110, 390), RawInputModifiers.LeftMouseButton);
            var scroll = pointer.AutoScroller!;
            Assert.True(scroll.IsTimerRunning);
            var offset = editor.Scroller!.Offset.Y;
            var active = editor.Session.Selection.Active;
            for (var i = 0; i < 12; i++) scroll.Advance(TimeSpan.FromMilliseconds(40));
            Assert.True(editor.Scroller.Offset.Y > offset);
            Assert.True(editor.Session.Selection.Active > active);
            Assert.Equal(anchor, editor.Session.Selection.Anchor);
            Assert.False(editor.Session.CanUndo);
            window.MouseUp(new Point(110, 390), MouseButton.Left);
            var releasedOffset = editor.Scroller.Offset.Y;
            scroll.Advance(TimeSpan.FromMilliseconds(40));
            Assert.Equal(releasedOffset, editor.Scroller.Offset.Y);
            Assert.False(scroll.IsTimerRunning);
            Assert.False(surface.IsSelectingWithPointer);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Reversed_drag_scrolls_up_and_readonly_selection_still_works() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            editor.IsReadOnly = true;
            editor.Scroller!.Offset = new Vector(0, 1000);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            window.MouseDown(new Point(100, 150), MouseButton.Left);
            var anchor = editor.Session.Selection.Anchor;
            Assert.True(anchor > 0);
            window.MouseMove(new Point(100, -70), RawInputModifiers.LeftMouseButton);
            var offset = editor.Scroller.Offset.Y;
            for (var i = 0; i < 8; i++) pointer.AutoScroller!.Advance(TimeSpan.FromMilliseconds(40));
            Assert.True(editor.Scroller.Offset.Y < offset);
            Assert.Equal(anchor, editor.Session.Selection.Anchor);
            Assert.True(editor.Session.Selection.Active < anchor);
            Assert.False(editor.Session.CanUndo);
            window.MouseUp(new Point(100, -70), MouseButton.Left);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("focus")]
    [InlineData("capture")]
    [InlineData("detach")]
    [InlineData("replace")]
    [InlineData("edit")]
    public Task Gesture_cancellation_stops_clock_and_restores_normal_selection(string reason) => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            IPointer? captured = null;
            surface.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, e) => captured = e.Pointer, handledEventsToo: true);
            window.MouseDown(new Point(80, 40), MouseButton.Left);
            window.MouseMove(new Point(110, 390), RawInputModifiers.LeftMouseButton);
            var scroll = pointer.AutoScroller!;
            Assert.True(scroll.IsActive);
            switch (reason)
            {
                case "focus": window.Focusable = true; Assert.True(window.Focus()); break;
                case "capture": captured!.Capture(null); break;
                case "detach": window.Content = null; break;
                case "replace": editor.PointerComponent = new DefaultPointerComponent(); break;
                case "edit": editor.InsertText("changed"); scroll.Advance(TimeSpan.FromMilliseconds(40)); break;
            }
            Assert.False(scroll.IsActive);
            Assert.False(scroll.IsTimerRunning);
            Assert.False(surface.IsSelectingWithPointer);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Repeated_resize_after_virtualized_scroll_can_render_and_detach_without_pending_gestures() => Run(() =>
    {
        var (window, editor, surface, pointer) = Create();
        try
        {
            editor.Text = string.Join('\n', Enumerable.Range(0, 10000).Select(i => $"Paragraph {i}: words that wrap when the viewport changes its width."));
            window.UpdateLayout();
            editor.Scroller!.Offset = new Vector(0, editor.Scroller.Extent.Height / 2);
            window.UpdateLayout();
            for (var i = 0; i < 12; i++)
            {
                window.Width = i % 2 == 0 ? 380 : 700;
                window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                Dispatcher.UIThread.RunJobs();
                Assert.Null(editor.LayoutError);
                Assert.False(surface.IsSelectingWithPointer);
                Assert.False(pointer.AutoScroller!.IsTimerRunning);
            }
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Edge_speed_scales_with_distance_is_bounded_and_symmetric()
    {
        Assert.Equal(0, SelectionAutoScroller.EdgeVelocity(100, 0, 200));
        Assert.True(SelectionAutoScroller.EdgeVelocity(225, 0, 200) > SelectionAutoScroller.EdgeVelocity(201, 0, 200));
        Assert.Equal(-SelectionAutoScroller.EdgeVelocity(-25, 0, 200), SelectionAutoScroller.EdgeVelocity(225, 0, 200));
        Assert.Equal(1200, SelectionAutoScroller.EdgeVelocity(10000, 0, 200));
        Assert.Equal(0, SelectionAutoScroller.EdgeVelocity(double.NaN, 0, 200));
    }
}
