using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class InlineResourceTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);

    [Fact]
    public async Task Default_resolver_only_returns_owned_embedded_streams()
    {
        var resolver = EmbeddedInlineResourceResolver.Instance;
        foreach (var kind in new[] { DocumentResourceKind.Local, DocumentResourceKind.Host })
            Assert.Null(await resolver.OpenReadAsync("resource", new DocumentResource { Kind = kind, Location = "https://example.invalid/image.png" }, CancellationToken.None));
        Assert.Null(await resolver.OpenReadAsync("missing", null, CancellationToken.None));
        var resource = Embedded(Bmp(2, 1));
        await using var first = await resolver.OpenReadAsync("resource", resource, CancellationToken.None);
        await using var second = await resolver.OpenReadAsync("resource", resource, CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(resource.Data.ToArray(), ((MemoryStream)first).ToArray());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await resolver.OpenReadAsync("resource", resource, cancellation.Token));
    }

    [Fact]
    public Task Cache_bounds_pixels_and_entries_and_releases_offscreen_images() => Run(() =>
    {
        using var cache = new InlineImageCache(options: new InlineImageOptions { MaximumDecodedPixels = 4, MaximumCacheEntries = 2 });
        var resource = Embedded(Bmp(2, 2));
        Assert.Null(cache.Request("first", resource));
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.NotNull(cache.Request("first", resource));
        Assert.Equal(1, cache.CachedImageCount);
        Assert.Equal(4, cache.CachedPixelCount);
        cache.Request("second", resource);
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.Null(cache.Request("second", resource));
        Assert.NotNull(cache.Request("first", resource));
        Assert.Equal(1, cache.CachedImageCount);
        Assert.Equal(4, cache.CachedPixelCount);
        cache.Retain(new HashSet<string> { "second" });
        cache.Request("second", resource);
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.NotNull(cache.Request("second", resource));
        cache.Retain(new HashSet<string>());
        Assert.Equal(0, cache.CachedImageCount);
        Assert.Equal(0, cache.CachedPixelCount);
        cache.Reset();
    });

    [Fact]
    public Task Visible_set_larger_than_capacity_does_not_cause_an_invalidation_decode_loop() => Run(() =>
    {
        var resolver = new CountingResolver(() => new MemoryStream(Bmp(1, 1)));
        using var cache = new InlineImageCache(resolver, new InlineImageOptions { MaximumCacheEntries = 2 });
        var changes = 0;
        cache.Changed += (_, _) => changes++;
        cache.Request("first", null);
        cache.Request("second", null);
        PumpUntil(() => cache.PendingLoadCount == 0);
        for (var frame = 0; frame < 10; frame++)
        {
            Assert.NotNull(cache.Request("first", null));
            Assert.NotNull(cache.Request("second", null));
            Assert.Null(cache.Request("third", null));
        }
        Assert.Equal(2, resolver.Calls);
        Assert.Equal(2, changes);
        cache.Retain(new HashSet<string> { "third" });
        cache.Request("third", null);
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.NotNull(cache.Request("third", null));
        Assert.Equal(3, resolver.Calls);
    });

    [Fact]
    public Task Failed_loads_are_cached_until_the_resource_changes_or_cache_resets() => Run(() =>
    {
        var resolver = new CountingResolver(() => new MemoryStream([1, 2, 3]));
        using var cache = new InlineImageCache(resolver);
        var resource = new DocumentResource { Kind = DocumentResourceKind.Host };
        var changes = 0;
        cache.Changed += (_, _) => changes++;
        cache.Request("bad", resource);
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.Null(cache.Request("bad", resource));
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(1, changes);
        cache.Request("bad", resource with { Location = "changed" });
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.Equal(2, resolver.Calls);
        cache.Reset();
        cache.Request("bad", resource);
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.Equal(3, resolver.Calls);
    });

    [Fact]
    public Task Encoded_and_decoded_limits_reject_resources_before_retaining_images() => Run(() =>
    {
        var stream = new ObservedStream(new byte[128]);
        using (var cache = new InlineImageCache(new CountingResolver(() => stream), new InlineImageOptions { MaximumEncodedBytes = 8 }))
        {
            cache.Request("oversized", null);
            PumpUntil(() => cache.PendingLoadCount == 0);
            Assert.Equal(9, stream.ReadBytes);
            Assert.True(stream.WasDisposed);
            Assert.Equal(0, cache.CachedImageCount);
        }
        using (var cache = new InlineImageCache(options: new InlineImageOptions { MaximumDimension = 1 }))
        {
            cache.Request("too-wide", Embedded(Bmp(2, 1)));
            PumpUntil(() => cache.PendingLoadCount == 0);
            Assert.Equal(0, cache.CachedImageCount);
        }
        using (var cache = new InlineImageCache(options: new InlineImageOptions { MaximumDecodedPixels = 3 }))
        {
            cache.Request("too-many-pixels", Embedded(Bmp(2, 2)));
            PumpUntil(() => cache.PendingLoadCount == 0);
            Assert.Equal(0, cache.CachedImageCount);
        }
    });

    [Fact]
    public Task Disposal_cancels_work_and_rejects_a_late_result_even_when_host_ignores_cancellation() => Run(() =>
    {
        var resolver = new PausedResolver();
        var cache = new InlineImageCache(resolver);
        var changes = 0;
        cache.Changed += (_, _) => changes++;
        cache.Request("image", null);
        Assert.True(resolver.Started.Wait(TimeSpan.FromSeconds(5)));
        cache.Dispose();
        Assert.True(resolver.Token.IsCancellationRequested);
        var stream = new ObservedStream(Bmp(1, 1));
        resolver.Completion.SetResult(stream);
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.True(stream.WasDisposed);
        Assert.Equal(0, cache.CachedImageCount);
        Assert.Equal(0, changes);
        Assert.Throws<ObjectDisposedException>(() => cache.Request("image", null));
    });

    [Fact]
    public Task Reset_cancels_stale_work_without_exceeding_concurrency_bound() => Run(() =>
    {
        var resolver = new PausedResolver();
        using var cache = new InlineImageCache(resolver, new InlineImageOptions { MaximumConcurrentLoads = 1 });
        cache.Request("old", null);
        Assert.True(resolver.Started.Wait(TimeSpan.FromSeconds(5)));
        cache.Reset();
        cache.Request("new", null);
        Assert.Equal(1, cache.PendingLoadCount);
        Assert.True(resolver.Token.IsCancellationRequested);
        resolver.Completion.SetResult(new MemoryStream(Bmp(1, 1)));
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.Equal(0, cache.CachedImageCount);
        cache.Request("new", null);
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.Equal(2, resolver.Calls);
    });

    [Fact]
    public Task Replacement_of_same_resource_id_rejects_the_previous_late_result() => Run(() =>
    {
        var resolver = new ReplacementResolver();
        using var cache = new InlineImageCache(resolver);
        var previous = new DocumentResource { Kind = DocumentResourceKind.Host, Location = "old" };
        var current = previous with { Location = "new" };
        cache.Request("shared-id", previous);
        Assert.True(resolver.Started.Wait(TimeSpan.FromSeconds(5)));
        cache.Request("shared-id", current);
        PumpUntil(() => cache.CachedImageCount == 1);
        Assert.True(resolver.PreviousToken.IsCancellationRequested);
        var oldStream = new ObservedStream(Bmp(1, 1));
        resolver.Previous.SetResult(oldStream);
        PumpUntil(() => cache.PendingLoadCount == 0);
        Assert.True(oldStream.WasDisposed);
        Assert.Equal(2, cache.Request("shared-id", current)!.PixelSize.Width);
        Assert.Equal(1, cache.CachedImageCount);
    });

    [Fact]
    public void Registry_only_uses_explicit_factories_and_does_not_create_controls_on_lookup()
    {
        var registry = new InlineControlFactoryRegistry();
        var factory = new CountingFactory();
        var changes = 0;
        registry.Changed += (_, _) => changes++;
        registry.Register("counter", factory);
        Assert.True(registry.TryGet("counter", out var registered));
        Assert.Same(factory, registered);
        Assert.False(registry.TryGet("Avalonia.Controls.Button, Avalonia.Controls", out _));
        Assert.Equal(0, factory.Created);
        Assert.True(registry.Unregister("counter"));
        Assert.False(registry.Unregister("counter"));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Header_preflight_rejects_malformed_and_unrecognized_formats()
    {
        Assert.Equal((3, 2), ImageDimensions.Read(Bmp(3, 2)));
        Assert.Throws<InvalidDataException>(() => ImageDimensions.Read([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => ImageDimensions.Read([0xff, 0xd8, 0xff, 0xc0, 0, 9]));
        var png = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 64);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 48);
        Assert.Equal((64, 48), ImageDimensions.Read(png));
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.True(condition(), "Background resource load did not complete.");
    }

    private static DocumentResource Embedded(byte[] bytes) => new()
    { Kind = DocumentResourceKind.Embedded, MediaType = "image/bmp", Data = bytes.ToImmutableArray() };

    private static byte[] Bmp(int width, int height)
    {
        var pixels = ((width * 3 + 3) / 4 * 4) * height;
        var bytes = new byte[54 + pixels];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), pixels);
        return bytes;
    }

    private sealed class CountingResolver(Func<Stream?> open) : IInlineResourceResolver
    {
        public int Calls;
        public ValueTask<Stream?> OpenReadAsync(string resourceId, DocumentResource? resource, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(open());
        }
    }

    private sealed class PausedResolver : IInlineResourceResolver
    {
        public readonly ManualResetEventSlim Started = new();
        public readonly TaskCompletionSource<Stream?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public int Calls;
        public ValueTask<Stream?> OpenReadAsync(string resourceId, DocumentResource? resource, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Token = cancellationToken;
            Started.Set();
            return new ValueTask<Stream?>(Completion.Task);
        }
    }

    private sealed class ObservedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int ReadBytes;
        public bool WasDisposed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            ReadBytes += count;
            return count;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class ReplacementResolver : IInlineResourceResolver
    {
        public readonly ManualResetEventSlim Started = new();
        public readonly TaskCompletionSource<Stream?> Previous = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken PreviousToken;
        public ValueTask<Stream?> OpenReadAsync(string resourceId, DocumentResource? resource, CancellationToken cancellationToken)
        {
            if (resource?.Location == "new") return ValueTask.FromResult<Stream?>(new MemoryStream(Bmp(2, 1)));
            PreviousToken = cancellationToken;
            Started.Set();
            return new ValueTask<Stream?>(Previous.Task);
        }
    }

    private sealed class CountingFactory : IInlineControlFactory
    {
        public int Created;
        public Control Create(InlineDescriptor descriptor) { Created++; return new Button(); }
    }
}
