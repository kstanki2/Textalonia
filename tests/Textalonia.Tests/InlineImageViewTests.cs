using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class InlineImageViewTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);

    [Fact]
    public Task Embedded_image_renders_at_descriptor_bounds_survives_typing_and_reloads_after_detach() => Run(() =>
    {
        var descriptor = Descriptor();
        var resource = new DocumentResource
        { Kind = DocumentResourceKind.Embedded, MediaType = "image/bmp", Data = RedBitmap().ToImmutableArray() };
        var resolver = new EmbeddedCounter();
        var (window, editor, surface) = Create(descriptor, resource, resolver);
        try
        {
            PumpUntil(() => surface.InlineImageCache?.CachedImageCount == 1);
            var cache = surface.InlineImageCache!;
            var decoded = cache.Request("image", resource);
            Assert.NotNull(decoded);
            var visual = Assert.Single(surface.Layout.InlineVisuals());
            Assert.Equal(new Size(72, 48), visual.Bounds.Size);
            AssertRedAtImageCenter(window, surface, visual.Bounds);

            editor.Session.Select(editor.Session.Index.Length, editor.Session.Index.Length);
            editor.Session.InsertText(" typed");
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Same(cache, surface.InlineImageCache);
            Assert.Same(decoded, cache.Request("image", editor.Document.Resources["image"]));
            Assert.Equal(1, resolver.Calls);

            editor.UpdateInline(descriptor.Id, inline => inline with { Width = 56, Height = 36 });
            window.UpdateLayout();
            Assert.Equal(new Size(56, 36), Assert.Single(surface.Layout.InlineVisuals()).Bounds.Size);
            Assert.Same(decoded, cache.Request("image", editor.Document.Resources["image"]));
            Assert.Equal(1, resolver.Calls);

            window.Content = null;
            Assert.Null(surface.InlineImageCache);
            Assert.Equal(0, cache.CachedImageCount);
            Assert.Throws<ObjectDisposedException>(() => cache.Request("image", resource));
            window.Content = editor;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            PumpUntil(() => surface.InlineImageCache?.CachedImageCount == 1);
            Assert.NotSame(cache, surface.InlineImageCache);
            Assert.Equal(2, resolver.Calls);

            var reattachedCache = surface.InlineImageCache!;
            editor.Document = FlowDocument.FromText("replacement");
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, reattachedCache.CachedImageCount);
            Assert.Empty(surface.Layout.InlineVisuals());
        }
        finally { window.Close(); }
        Assert.Null(surface.InlineImageCache);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Document_replacement_and_detach_cancel_pending_host_images(bool detach) => Run(() =>
    {
        var resolver = new PausedResolver();
        var resource = new DocumentResource { Kind = DocumentResourceKind.Host, Location = "opaque-host-key", MediaType = "image/bmp" };
        var (window, editor, surface) = Create(Descriptor(), resource, resolver);
        try
        {
            Assert.True(resolver.Started.Wait(TimeSpan.FromSeconds(5)));
            var cache = surface.InlineImageCache!;
            if (detach) window.Content = null;
            else editor.Document = FlowDocument.FromText("replacement");
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(resolver.Token.IsCancellationRequested);
            var stream = new ObservedStream(RedBitmap());
            resolver.Completion.SetResult(stream);
            PumpUntil(() => cache.PendingLoadCount == 0);
            Assert.True(stream.WasDisposed);
            Assert.Equal(0, cache.CachedImageCount);
            if (detach) Assert.Null(surface.InlineImageCache);
            else Assert.Empty(surface.Layout.InlineVisuals());
        }
        finally { window.Close(); }
    });

    private static InlineDescriptor Descriptor() => new()
    { AltText = "Red image", Width = 72, Height = 48, Payload = new ImageInlinePayload("image") };

    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface) Create(
        InlineDescriptor descriptor, DocumentResource resource, IInlineResourceResolver resolver)
    {
        var editor = new TextaloniaEditor
        {
            ShowToolbar = false, Background = Brushes.White, Foreground = Brushes.Black,
            InlineResourceResolver = resolver,
            Document = new FlowDocument([new Paragraph([new RichRun(descriptor), new RichRun(" tail")])])
            { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", resource) }
        };
        var window = new Window { Width = 500, Height = 300, Content = editor };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        return (window, editor, editor.GetVisualDescendants().OfType<DocumentSurface>().Single());
    }

    private static void AssertRedAtImageCenter(Window window, DocumentSurface surface, Rect imageBounds)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var pixels = new WriteableBitmap(frame.PixelSize, frame.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = pixels.Lock();
        frame.CopyPixels(buffer);
        var point = surface.TranslatePoint(imageBounds.Center, window)!.Value;
        var x = (int)(point.X * window.RenderScaling);
        var y = (int)(point.Y * window.RenderScaling);
        var address = buffer.Address + y * buffer.RowBytes + x * 4;
        Assert.Equal(0, Marshal.ReadByte(address));
        Assert.Equal(0, Marshal.ReadByte(address, 1));
        Assert.Equal(255, Marshal.ReadByte(address, 2));
        Assert.Equal(255, Marshal.ReadByte(address, 3));
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5))
        { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Assert.True(condition(), "Inline image view did not reach the expected state.");
    }

    private static byte[] RedBitmap()
    {
        var bytes = new byte[70];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), 16);
        bytes[56] = bytes[59] = bytes[64] = bytes[67] = 255;
        return bytes;
    }

    private sealed class EmbeddedCounter : IInlineResourceResolver
    {
        public int Calls;
        public ValueTask<Stream?> OpenReadAsync(string resourceId, DocumentResource? resource, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return EmbeddedInlineResourceResolver.Instance.OpenReadAsync(resourceId, resource, cancellationToken);
        }
    }

    private sealed class PausedResolver : IInlineResourceResolver
    {
        public readonly ManualResetEventSlim Started = new();
        public readonly TaskCompletionSource<Stream?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public ValueTask<Stream?> OpenReadAsync(string resourceId, DocumentResource? resource, CancellationToken cancellationToken)
        {
            Token = cancellationToken; Started.Set();
            return new ValueTask<Stream?>(Completion.Task);
        }
    }

    private sealed class ObservedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed;
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
