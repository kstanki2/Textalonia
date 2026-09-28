using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class PageRenderingTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private sealed class InlineFactory : IInlineControlFactory
    {
        public Border? Control { get; private set; }
        public Control Create(InlineDescriptor descriptor) => Control = new Border { Background = Brushes.Coral };
    }

    [Theory]
    [InlineData(DocumentViewMode.Simple)]
    [InlineData(DocumentViewMode.PrintLayout)]
    public Task Inline_controls_scale_their_content_and_restore_the_host_transform(DocumentViewMode mode) => fixture.Session.Dispatch(() =>
    {
        var descriptor = new InlineDescriptor { Width = 80, Height = 30, Payload = new ControlInlinePayload("scale") };
        var registry = new InlineControlFactoryRegistry(); var factory = new InlineFactory(); registry.Register("scale", factory);
        var editor = new TextaloniaEditor { ShowToolbar = false, InlineControlFactories = registry, ViewMode = mode,
            Document = new FlowDocument([new Paragraph([new RichRun(descriptor)])]) };
        var window = new Window { Width = 700, Height = 400, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout();
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var control = Assert.IsType<Border>(factory.Control);
            foreach (var zoom in new[] { 2d, .5, 1d })
            {
                editor.Zoom = zoom; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal(new Size(80, 30), control.Bounds.Size);
                var top = control.TranslatePoint(new Point(0, 0), surface)!.Value;
                var bottom = control.TranslatePoint(new Point(80, 30), surface)!.Value;
                Assert.Equal(80 * zoom, bottom.X - top.X, 2);
                Assert.Equal(30 * zoom, bottom.Y - top.Y, 2);
            }
            Assert.Null(control.RenderTransform);
            editor.Zoom = 2; window.UpdateLayout();
        }
        finally { window.Close(); }
        Assert.Null(factory.Control!.RenderTransform);
    }, CancellationToken.None);

    [Fact]
    public Task Multiple_pages_render_at_different_zoom_levels_and_keep_page_breaks() => fixture.Session.Dispatch(() =>
    {
        var title = new Paragraph("A paginated document", new TextStyle { FontSize = 24, Bold = true })
            { Style = new ParagraphStyle { KeepWithNext = true, SpaceAfter = 12 } };
        var body = new Paragraph("Pagination shares exact shaped lines with selection and navigation. This example includes Latin text, " +
            "a list, a table, and a second section. Font metrics decide the page breaks.")
            { Style = new ParagraphStyle { SpaceAfter = 12 } };
        var table = Table.Create(2, 2)
            .SetCell(0, 0, new TableCell { Blocks = [new Paragraph("Feature")] })
            .SetCell(0, 1, new TableCell { Blocks = [new Paragraph("Shared geometry")] })
            .SetCell(1, 0, new TableCell { Blocks = [new Paragraph("Caret and selection")] })
            .SetCell(1, 1, new TableCell { Blocks = [new Paragraph("Zoom keeps offsets stable")] });
        var second = new Paragraph("A second paper size", new TextStyle { FontSize = 22, Bold = true });
        var document = new FlowDocument([title, body, table, second, new Paragraph("Two columns share the available page width. " +
            string.Concat(Enumerable.Repeat("The text continues across measured line fragments. ", 10)))])
        {
            Sections = [new DocumentSection { PageSettings = new PageSettings { Width = 420, Height = 540, Margins = new(32, 32, 32, 32) } },
                new DocumentSection { StartParagraphId = second.Id, PageSettings = new PageSettings
                    { Width = 500, Height = 420, Margins = new(28, 28, 28, 28), Columns = [new(), new()], ColumnSpacing = 20 } }]
        };
        var editor = new TextaloniaEditor { Document = document, ShowToolbar = false, ViewMode = DocumentViewMode.PrintLayout,
            PagesPerRow = 2, FontFamily = new FontFamily("Inter") };
        var window = new Window { Width = 1100, Height = 700, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var breaks = surface.PagedLayout!.Fragments.Select(fragment => (fragment.Start, fragment.PageIndex, fragment.ColumnIndex)).ToArray();
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/pagination"));
            Directory.CreateDirectory(directory);
            foreach (var zoom in new[] { .5, 1d, 2d })
            {
                editor.Zoom = zoom;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                Assert.Equal(breaks, surface.PagedLayout!.Fragments.Select(fragment => (fragment.Start, fragment.PageIndex, fragment.ColumnIndex)).ToArray());
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(directory, $"pages-{zoom * 100:0}.png"), PngBitmapEncoderOptions.Default);
            }
            Assert.Same(document, editor.Document);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }, CancellationToken.None);
}
