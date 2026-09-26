using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class InlineLayoutTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);
    private static InlineDescriptor Image(double width = 60, double height = 40) => new()
    { AltText = "Missing image", Width = width, Height = height, Payload = new ImageInlinePayload("missing") };

    [Fact]
    public Task Inline_uses_shared_line_geometry_for_baseline_carets_hit_testing_and_selection() => Run(() =>
    {
        var descriptor = Image();
        var paragraph = new Paragraph([new RichRun("A"), new RichRun(descriptor), new RichRun("B")]);
        using var layout = new DocumentLayout();
        layout.Build(new FlowDocument([paragraph]), 500, FontFamily.Default, Brushes.Black, Brushes.Gray, new Thickness(10));
        var inline = Assert.Single(layout.InlineVisuals());
        Assert.Equal(1, inline.Position);
        Assert.Equal(60, inline.Bounds.Width);
        Assert.Equal(40, inline.Bounds.Height);
        Assert.True(inline.Bounds.Y >= 10);
        Assert.InRange(layout.Caret(2).X - layout.Caret(1).X, 59.5, 60.5);
        var selection = Assert.Single(layout.SelectionRects(1, 1));
        Assert.InRange(selection.Width, 59.5, 60.5);
        Assert.True(selection.Height >= 40);
        Assert.Equal(1, layout.HitTest(inline.Bounds.TopLeft + new Vector(2, 20)));
        Assert.Equal(2, layout.HitTest(inline.Bounds.TopRight + new Vector(-2, 20)));
    });

    [Fact]
    public Task Resize_undo_redo_and_viewer_use_descriptor_dimensions() => Run(() =>
    {
        var descriptor = Image();
        var viewer = new TextaloniaViewer { Document = new FlowDocument([new Paragraph([new RichRun(descriptor)])]) };
        var window = new Window { Width = 600, Height = 350, Content = viewer };
        window.Show(); window.UpdateLayout();
        try
        {
            var surface = viewer.GetVisualDescendants().OfType<DocumentSurface>().Single();
            Assert.Equal(60, Assert.Single(surface.Layout.InlineVisuals()).Bounds.Width);
            viewer.IsReadOnly = false;
            viewer.UpdateInline(descriptor.Id, d => d with { Width = 100, Height = 70 });
            window.UpdateLayout();
            Assert.Equal(100, Assert.Single(surface.Layout.InlineVisuals()).Bounds.Width);
            viewer.Undo(); window.UpdateLayout();
            Assert.Equal(60, Assert.Single(surface.Layout.InlineVisuals()).Bounds.Width);
            viewer.Redo(); window.UpdateLayout();
            Assert.Equal(70, Assert.Single(surface.Layout.InlineVisuals()).Bounds.Height);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Controls_recycle_preserve_data_and_receive_keys_before_document_shortcuts() => Run(() =>
    {
        var descriptor = new InlineDescriptor { AltText = "Counter", Width = 100, Height = 32, Payload = new ControlInlinePayload("counter") };
        var factory = new TrackingFactory();
        var registry = new InlineControlFactoryRegistry(); registry.Register("counter", factory);
        var editor = new TextaloniaEditor
        {
            ShowToolbar = false, InlineControlFactories = registry,
            Document = new FlowDocument([new Paragraph([new RichRun(descriptor)]), .. Enumerable.Range(0, 200).Select(i => new Paragraph("Paragraph " + i))])
        };
        var window = new Window { Width = 600, Height = 350, Content = editor };
        window.Show(); window.UpdateLayout();
        try
        {
            var child = Assert.Single(editor.GetVisualDescendants().OfType<TextBox>());
            Assert.Equal("Counter", Avalonia.Automation.AutomationProperties.GetName(child));
            child.Focus(); window.KeyTextInput("local");
            Assert.Equal("local", child.Text);
            Assert.StartsWith("\uFFFC\n", editor.Text);
            var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "PART_ScrollViewer");
            scroll.Offset = new Vector(0, 4000); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(factory.Created, factory.Released);
            scroll.Offset = default; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(factory.Created >= 2);
            Assert.Equal(factory.Created - 1, factory.Released);
            editor.UpdateInline(descriptor.Id, d => d with { AltText = "Updated", Width = 120 }); window.UpdateLayout();
            Assert.True(factory.Updated > 0);
            registry.Unregister("counter"); window.UpdateLayout();
            Assert.Equal(factory.Created, factory.Released);
            Assert.Empty(editor.GetVisualDescendants().OfType<TextBox>());
        }
        finally { window.Close(); }
        Assert.Equal(factory.Created, factory.Released);
    });

    private sealed class TrackingFactory : IInlineControlFactory
    {
        public int Created, Released, Updated;
        public Control Create(InlineDescriptor descriptor) { Created++; return new TextBox(); }
        public void Update(Control control, InlineDescriptor descriptor) => Updated++;
        public void Release(Control control) => Released++;
    }
}
