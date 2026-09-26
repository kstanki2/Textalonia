using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class InlineViewLifecycleTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static InlineDescriptor Descriptor() => new()
    { Width = 100, Height = 32, AltText = "Test object", Payload = new ControlInlinePayload("test") };
    private static (Window Window, TextaloniaEditor Editor, InlineControlFactoryRegistry Registry) Create(IInlineControlFactory factory, params InlineDescriptor[] descriptors)
    {
        var registry = new InlineControlFactoryRegistry(); registry.Register("test", factory);
        var editor = new TextaloniaEditor
        {
            ShowToolbar = false, InlineControlFactories = registry,
            Document = new FlowDocument([new Paragraph(descriptors.Select(d => new RichRun(d)))])
        };
        var window = new Window { Width = 600, Height = 300, Content = editor };
        window.Show(); window.UpdateLayout();
        return (window, editor, registry);
    }

    [Theory]
    [InlineData(Failure.Attachment)]
    [InlineData(Failure.Measure)]
    [InlineData(Failure.Arrange)]
    public Task A_created_view_is_released_after_attachment_or_layout_failure(Failure failure) => Run(() =>
    {
        var factory = new Factory { Failure = failure };
        var (window, editor, _) = Create(factory, Descriptor());
        try
        {
            Assert.True(factory.Created > 0);
            Assert.Equal(factory.Created, factory.Released);
            Assert.NotNull(editor.LastError);
            Assert.Empty(editor.GetVisualDescendants().OfType<FailureControl>());
            Assert.Null(factory.Last!.GetLogicalParent());
            Assert.Null(factory.Last!.GetVisualParent());
        }
        finally { window.Close(); }
        Assert.Equal(factory.Created, factory.Released);
    });

    [Fact]
    public Task Failed_update_releases_old_view_and_leaves_descriptor_fallback() => Run(() =>
    {
        var factory = new Factory();
        var descriptor = Descriptor();
        var (window, editor, _) = Create(factory, descriptor);
        try
        {
            Assert.Equal(0, factory.Released);
            factory.Failure = Failure.Update;
            editor.UpdateInline(descriptor.Id, d => d with { Width = 150 });
            window.UpdateLayout();
            Assert.Equal(factory.Created, factory.Released);
            Assert.Empty(editor.GetVisualDescendants().OfType<FailureControl>());
            Assert.Equal(150, Assert.Single(editor.GetVisualDescendants().OfType<DocumentSurface>()).Layout.InlineVisuals().Single().Bounds.Width);
            Assert.Equal("\uFFFC", editor.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Detach_notification_failure_still_calls_release_once() => Run(() =>
    {
        var factory = new Factory { Failure = Failure.Detachment };
        var (window, editor, registry) = Create(factory, Descriptor());
        try
        {
            registry.Unregister("test"); window.UpdateLayout();
            Assert.Equal(factory.Created, factory.Released);
            Assert.NotNull(editor.LastError);
            Assert.Empty(editor.GetVisualDescendants().OfType<FailureControl>());
        }
        finally { window.Close(); }
        Assert.Equal(factory.Created, factory.Released);
    });

    [Fact]
    public Task Factory_cannot_reparent_an_existing_control() => Run(() =>
    {
        var outside = new StackPanel();
        var existing = new Button(); outside.Children.Add(existing);
        var factory = new Factory { Existing = existing };
        var (window, editor, _) = Create(factory, Descriptor());
        try
        {
            Assert.Same(outside, existing.GetLogicalParent());
            Assert.Equal(factory.Created, factory.Released);
            Assert.NotNull(editor.LastError);
            Assert.DoesNotContain(existing, editor.GetVisualDescendants());
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Rejecting_a_shared_control_does_not_remove_the_first_inline_view() => Run(() =>
    {
        var shared = new Button();
        var factory = new Factory { Existing = shared };
        var (window, editor, _) = Create(factory, Descriptor(), Descriptor());
        try
        {
            var surface = Assert.Single(editor.GetVisualDescendants().OfType<DocumentSurface>());
            Assert.Same(surface, shared.GetVisualParent());
            Assert.Same(surface, shared.GetLogicalParent());
            Assert.Equal(2, factory.Created);
            Assert.Equal(1, factory.Released);
            Assert.NotNull(editor.LastError);
        }
        finally { window.Close(); }
        Assert.Equal(factory.Created, factory.Released);
    });

    [Fact]
    public Task Selection_preserves_host_opacity_and_clip_and_children_follow_tab_order() => Run(() =>
    {
        var hostClip = new RectangleGeometry(new Rect(0, 0, 75, 24));
        var factory = new Factory { MakeButton = true, HostClip = hostClip };
        var (window, editor, _) = Create(factory, Descriptor(), Descriptor());
        try
        {
            var surface = Assert.Single(editor.GetVisualDescendants().OfType<DocumentSurface>());
            var buttons = surface.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Equal(2, buttons.Length);
            editor.FocusDocument();
            window.KeyPress(Key.Tab, RawInputModifiers.None);
            Assert.True(buttons[0].IsFocused);
            window.KeyPress(Key.Tab, RawInputModifiers.None);
            Assert.True(buttons[1].IsFocused);
            window.KeyPress(Key.Tab, RawInputModifiers.Shift);
            Assert.True(buttons[0].IsFocused);
            editor.Session.SelectAll();
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.All(buttons, button => { Assert.Equal(.4, button.Opacity); Assert.Same(hostClip, button.Clip); });
            editor.Session.Select(0, 0); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            using var cleared = window.CaptureRenderedFrame();
            Assert.All(buttons, button => Assert.Equal(.4, button.Opacity));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Inline_bounds_follow_alignment_RTL_wrapping_and_shared_selection_geometry() => Run(() =>
    {
        foreach (var rtl in new[] { false, true })
            foreach (var alignment in new[] { ParagraphAlignment.Left, ParagraphAlignment.Center, ParagraphAlignment.Right })
            {
                var descriptor = Descriptor() with { Width = 80, Height = 48 };
                var prefix = rtl ? "????? ????? " : "long word wrap ";
                var paragraph = new Paragraph([new RichRun(prefix), new RichRun(descriptor), new RichRun(" after")])
                { Style = new ParagraphStyle { RightToLeft = rtl, Alignment = alignment, FirstLineIndent = 12 } };
                using var layout = new DocumentLayout();
                layout.Build(new FlowDocument([paragraph]), 180, FontFamily.Default, Brushes.Black, Brushes.Gray, new Thickness(10));
                var inline = Assert.Single(layout.InlineVisuals());
                var selection = Assert.Single(layout.SelectionRects(prefix.Length, 1));
                Assert.Equal(prefix.Length, inline.Position);
                Assert.InRange(Math.Abs(inline.Bounds.X - selection.X), 0, .01);
                Assert.InRange(Math.Abs(inline.Bounds.Width - selection.Width), 0, .01);
                Assert.True(inline.Bounds.Top >= selection.Top - .01);
                Assert.True(inline.Bounds.Bottom <= selection.Bottom + .01);
            }
    });

    public enum Failure { None, Attachment, Measure, Arrange, Update, Detachment }
    private sealed class Factory : IInlineControlFactory
    {
        public Failure Failure;
        public int Created, Released;
        public Control? Last, Existing;
        public bool MakeButton;
        public Geometry? HostClip;
        public Control Create(InlineDescriptor descriptor)
        {
            Created++;
            return Last = Existing ?? (MakeButton ? new Button { Content = "Object", Opacity = .4, Clip = HostClip } : new FailureControl(Failure));
        }
        public void Update(Control control, InlineDescriptor descriptor)
        {
            if (Failure == Failure.Update) throw new InvalidOperationException("Update failed.");
        }
        public void Release(Control control) => Released++;
    }
    private sealed class FailureControl(Failure failure) : Control
    {
        protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
        {
            base.OnAttachedToLogicalTree(e);
            if (failure == Failure.Attachment) throw new InvalidOperationException("Attach failed.");
        }
        protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromLogicalTree(e);
            if (failure == Failure.Detachment) throw new InvalidOperationException("Detach failed.");
        }
        protected override Size MeasureOverride(Size availableSize) => failure == Failure.Measure ?
            throw new InvalidOperationException("Measure failed.") : new Size(100, 32);
        protected override Size ArrangeOverride(Size finalSize) => failure == Failure.Arrange ?
            throw new InvalidOperationException("Arrange failed.") : finalSize;
    }
}
