using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Demo;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public sealed class IntegrationDemoTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Markdown_viewer_uses_host_links_resources_and_accessible_inline_text() => fixture.Session.Dispatch(() =>
    {
        var resolver = new RecordingResolver();
        var viewer = new MarkdownViewer { InlineResourceResolver = resolver };
        string? activated = null;
        viewer.HyperlinkActivated += (_, args) => activated = args.Uri;
        var window = new Window { Width = 700, Height = 400, Content = viewer };
        try
        {
            window.Show();
            MarkdownViewerTests.Pump(viewer.UpdateMarkdownAsync("[Host link](https://example.com) ![Image](resource:demo.logo)"));
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame();
            MarkdownViewerTests.Pump(resolver.Requested.Task);
            viewer.OpenLink("https://example.com");
            Assert.Equal("https://example.com", activated);
            Assert.Equal("resource:demo.logo", resolver.Location);
            viewer.Session.SelectAll();
            Assert.Equal("Host link Image", viewer.SelectedText);
            Assert.Equal(viewer.Document.Text, viewer.Accessibility.DocumentRange.GetText());
            Assert.Contains('\uFFFC', viewer.Document.Text);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Integration_demo_renders_markdown_and_optional_highlighting() => fixture.Session.Dispatch(() =>
    {
        var window = new IntegrationWindow();
        try
        {
            window.Show();
            var tabs = (TabControl)((Grid)window.Content!).Children[1];
            var pair = (Grid)((TabItem)tabs.Items[0]!).Content!;
            var viewer = Assert.IsType<MarkdownViewer>(pair.Children[1]);
            MarkdownViewerTests.Pump(viewer.WaitForParsingAsync());
            MarkdownViewerTests.Pump(viewer.WaitForHighlightingAsync());
            Assert.Null(viewer.ParseError); Assert.Null(viewer.HighlightError);
            Assert.Contains("Markdown integrations", viewer.Document.PlainText);
            window.UpdateLayout();
            var surface = viewer.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (surface.InlineImageCache?.CachedImageCount != 1 && started.Elapsed < TimeSpan.FromSeconds(3))
            { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
            Assert.Equal(1, surface.InlineImageCache?.CachedImageCount);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var directory = Environment.GetEnvironmentVariable("TEXTALONIA_FAILURE_DIR") ?? "artifacts";
            Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory, "phase7-demo.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    private sealed class RecordingResolver : IInlineResourceResolver
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Location { get; private set; }
        public ValueTask<Stream?> OpenReadAsync(string resourceId, DocumentResource? resource, CancellationToken cancellationToken)
        {
            Location = resource?.Location;
            Requested.TrySetResult();
            return ValueTask.FromResult<Stream?>(null);
        }
    }
}
