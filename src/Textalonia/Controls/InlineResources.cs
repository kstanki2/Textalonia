using System.Buffers.Binary;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>
/// Resolves resources on a background thread. The returned stream belongs to the caller. Hosts
/// opt into local or remote access here and must honor cancellation; the default does neither.
/// A host resource need not have an entry in the document resource table.
/// </summary>
public interface IInlineResourceResolver
{
    ValueTask<Stream?> OpenReadAsync(string resourceId, DocumentResource? resource, CancellationToken cancellationToken);
}

/// <summary>Resolves embedded bytes only, without opening files or making network requests.</summary>
public sealed class EmbeddedInlineResourceResolver : IInlineResourceResolver
{
    public static EmbeddedInlineResourceResolver Instance { get; } = new();

    public ValueTask<Stream?> OpenReadAsync(string resourceId, DocumentResource? resource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream?>(resource is { Kind: DocumentResourceKind.Embedded } && !resource.Data.IsDefaultOrEmpty
            ? new MemoryStream(resource.Data.ToArray(), writable: false) : null);
    }
}

/// <summary>Per-surface limits, independent of document history and descriptor dimensions.</summary>
public sealed record InlineImageOptions
{
    public int MaximumEncodedBytes { get; init; } = 16 * 1024 * 1024;
    public long MaximumDecodedPixels { get; init; } = 16 * 1024 * 1024;
    public int MaximumDimension { get; init; } = 8192;
    public int MaximumCacheEntries { get; init; } = 32;
    public int MaximumConcurrentLoads { get; init; } = 2;

    internal void Validate()
    {
        if (MaximumEncodedBytes < 1 || MaximumDecodedPixels < 1 || MaximumDimension < 1 ||
            MaximumCacheEntries < 1 || MaximumConcurrentLoads < 1)
            throw new ArgumentOutOfRangeException(nameof(InlineImageOptions), "Image limits must be positive.");
    }
}

/// <summary>
/// A bounded, view-owned image cache. Access its methods and returned bitmaps only on the UI
/// thread. Request returns a borrowed bitmap for immediate drawing, or null while loading or on
/// failure. Do not retain returned bitmaps: replacement, Retain, Reset and Dispose release them.
/// Visible resources beyond capacity use their fallback until Retain frees capacity or Reset is
/// called. Stable admission prevents repeated decoding when the visible set exceeds the budget.
/// </summary>
public sealed class InlineImageCache : IDisposable
{
    private sealed class Entry(string id, DocumentResource? resource)
    {
        public string Id { get; } = id;
        public DocumentResource? Resource { get; } = resource;
        public CancellationTokenSource Cancellation { get; } = new();
        public Bitmap? Image;
        public long Pixels;
        public bool Pending = true;
        public bool BudgetRejected;
    }

    private readonly IInlineResourceResolver _resolver;
    private readonly InlineImageOptions _options;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _pending;
    private bool _disposed;

    public InlineImageCache(IInlineResourceResolver? resolver = null, InlineImageOptions? options = null)
    {
        _resolver = resolver ?? EmbeddedInlineResourceResolver.Instance;
        _options = options ?? new();
        _options.Validate();
    }

    /// <summary>Raised on the UI thread after a load finishes, including missing or failed loads.</summary>
    public event EventHandler? Changed;
    public int CachedImageCount => _entries.Values.Count(entry => entry.Image is not null);
    public long CachedPixelCount { get; private set; }
    internal int PendingLoadCount => _pending;

    public Bitmap? Request(string resourceId, DocumentResource? resource)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (_entries.TryGetValue(resourceId, out var existing))
        {
            if (Equals(existing.Resource, resource))
            {
                return existing.Image;
            }
            Remove(existing);
        }
        // Bound work as well as retained data. Another completion invalidates the surface so
        // visible requests which could not start yet get another opportunity.
        if (_pending >= _options.MaximumConcurrentLoads || _entries.Count >= _options.MaximumCacheEntries) return null;
        var entry = new Entry(resourceId, resource);
        _entries.Add(resourceId, entry);
        _pending++;
        _ = Task.Run(() => LoadAsync(entry));
        return null;
    }

    /// <summary>Cancels and releases resources no longer used by the current visible viewport.</summary>
    public void Retain(IReadOnlySet<string> resourceIds)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(resourceIds);
        var previousPixels = CachedPixelCount;
        foreach (var entry in _entries.Values.Where(entry => !resourceIds.Contains(entry.Id)).ToArray()) Remove(entry);
        if (CachedPixelCount < previousPixels)
            foreach (var entry in _entries.Values.Where(entry => entry.BudgetRejected).ToArray()) Remove(entry);
    }

    /// <summary>Clears failures as well as images, allowing host resources to be resolved again.</summary>
    public void Reset()
    {
        Dispatcher.UIThread.VerifyAccess();
        foreach (var entry in _entries.Values.ToArray()) Remove(entry);
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        Reset();
        Changed = null;
    }

    private void Remove(Entry entry)
    {
        _entries.Remove(entry.Id);
        entry.Cancellation.Cancel();
        // The worker owns disposal of the cancellation source after the stream has closed.
        if (!entry.Pending) entry.Cancellation.Dispose();
        CachedPixelCount -= entry.Pixels;
        entry.Image?.Dispose();
        entry.Image = null;
        entry.Pixels = 0;
    }

    private async Task LoadAsync(Entry entry)
    {
        Bitmap? bitmap = null;
        var token = entry.Cancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            // Avoid duplicating an already oversized embedded buffer in the default resolver.
            if (entry.Resource is { Kind: DocumentResourceKind.Embedded } embedded && embedded.Data.Length > _options.MaximumEncodedBytes)
                throw new InvalidDataException("The encoded image exceeds its byte limit.");
            await using var stream = await _resolver.OpenReadAsync(entry.Id, entry.Resource, token).ConfigureAwait(false);
            if (stream is not null)
            {
                using var encoded = await ReadBoundedAsync(stream, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var (width, height) = ImageDimensions.Read(encoded.GetBuffer().AsSpan(0, checked((int)encoded.Length)));
                CheckDimensions(width, height);
                bitmap = new Bitmap(encoded);
                CheckDimensions(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception)
        {
            // A bad resource produces the descriptor's fallback, never a failed layout pass.
            bitmap?.Dispose();
            bitmap = null;
        }
        var completed = bitmap;
        Dispatcher.UIThread.Post(() => Complete(entry, completed));
    }

    private void Complete(Entry entry, Bitmap? bitmap)
    {
        _pending--;
        var current = !_disposed && !entry.Cancellation.IsCancellationRequested &&
            _entries.TryGetValue(entry.Id, out var present) && ReferenceEquals(entry, present);
        entry.Pending = false;
        if (!current)
        {
            entry.Cancellation.Dispose();
            bitmap?.Dispose();
            if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (bitmap is not null)
        {
            var pixels = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;
            if (pixels <= _options.MaximumDecodedPixels - CachedPixelCount)
            {
                entry.Image = bitmap;
                entry.Pixels = pixels;
                CachedPixelCount += pixels;
            }
            else
            {
                entry.BudgetRejected = true;
                bitmap.Dispose();
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CheckDimensions(int width, int height)
    {
        if (width < 1 || height < 1 || width > _options.MaximumDimension || height > _options.MaximumDimension ||
            (long)width * height > _options.MaximumDecodedPixels)
            throw new InvalidDataException("The decoded image exceeds its dimension or pixel limit.");
    }

    private async Task<MemoryStream> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        var result = new MemoryStream();
        try
        {
            var buffer = new byte[Math.Min(81920, _options.MaximumEncodedBytes)];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0,
                    (int)Math.Min(buffer.Length, (long)_options.MaximumEncodedBytes - result.Length + 1)), token).ConfigureAwait(false);
                if (read == 0) break;
                if (result.Length + read > _options.MaximumEncodedBytes)
                    throw new InvalidDataException("The encoded image exceeds its byte limit.");
                result.Write(buffer, 0, read);
            }
            result.Position = 0;
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}

/// <summary>Checks raster headers before asking a native decoder to allocate image memory.</summary>
internal static class ImageDimensions
{
    internal static (int Width, int Height) Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            return (ReadBig(bytes[16..]), ReadBig(bytes[20..]));
        if (bytes.Length >= 10 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        if (bytes.Length >= 26 && bytes[..2].SequenceEqual("BM"u8))
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
            if (header == 12) return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]));
            if (header >= 40)
            {
                var width = BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
                var height = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]);
                return (width, height == int.MinValue ? 0 : Math.Abs(height));
            }
        }
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            if (bytes.Length >= 30 && bytes.Slice(12, 4).SequenceEqual("VP8X"u8))
                return (Read24(bytes[24..]) + 1, Read24(bytes[27..]) + 1);
            if (bytes.Length >= 30 && bytes.Slice(12, 4).SequenceEqual("VP8 "u8) && bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                return (BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3fff);
            if (bytes.Length >= 25 && bytes.Slice(12, 4).SequenceEqual("VP8L"u8) && bytes[20] == 0x2f)
                return (1 + (((bytes[22] & 0x3f) << 8) | bytes[21]), 1 + (((bytes[24] & 0x0f) << 10) | (bytes[23] << 2) | (bytes[22] >> 6)));
        }
        if (bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            var position = 2;
            while (position < bytes.Length)
            {
                if (bytes[position++] != 0xff) break;
                while (position < bytes.Length && bytes[position] == 0xff) position++;
                if (position >= bytes.Length) break;
                var marker = bytes[position++];
                if (marker is 0xd9 or 0xda) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
                if (position > bytes.Length - 2) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
                if (length < 2 || length > bytes.Length - position) break;
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc) && length >= 8)
                    return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 3)..]));
                position += length;
            }
        }
        throw new InvalidDataException("Unsupported or malformed image header. Use PNG, JPEG, GIF, BMP or WebP.");
    }

    private static int ReadBig(ReadOnlySpan<byte> bytes) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes));
    private static int Read24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
}
