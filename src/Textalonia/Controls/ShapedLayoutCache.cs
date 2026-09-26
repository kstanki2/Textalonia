using Avalonia.Media.TextFormatting;

namespace Textalonia.Controls;

// Visuals retain checkpoints, not glyph ownership. A lease keeps exactly the
// layout being consumed alive; even visible layouts may otherwise be evicted.
internal sealed class ShapedLayoutCache(Action disposed, int layoutLimit = 256, long byteLimit = 16 * 1024 * 1024)
{
    internal const int LayoutLimit = 256;
    internal const long ByteLimit = 16 * 1024 * 1024;
    private readonly LinkedList<ParagraphLayout.Page> _lru = [];
    public int Count => _lru.Count;
    public long Bytes { get; private set; }
    public long PeakBytes { get; private set; }

    public readonly struct Lease(ShapedLayoutCache cache, ParagraphLayout.Page page) : IDisposable
    {
        public TextLayout Layout => page.Layout!;
        public void Dispose() { page.Users--; cache.Trim(); }
    }

    public Lease Acquire(ParagraphLayout.Page page)
    {
        page.Users++;
        if (page.CacheNode is { } node) { _lru.Remove(node); _lru.AddLast(node); }
        return new(this, page);
    }

    public void Add(ParagraphLayout.Page page)
    {
        page.CacheNode = _lru.AddLast(page);
        Bytes += page.Bytes;
        PeakBytes = Math.Max(PeakBytes, Bytes);
        Trim();
    }

    public TextLayout Take(ParagraphLayout.Page page)
    {
        var layout = page.Layout!;
        Remove(page);
        page.Owner.Forget(page);
        return layout;
    }

    private void Remove(ParagraphLayout.Page page)
    {
        if (page.CacheNode is not { } node) return;
        Bytes -= page.Bytes; _lru.Remove(node); page.CacheNode = null;
    }

    public void Release(ParagraphLayout.Page page)
    {
        if (page.Layout is not { } layout) return;
        Remove(page); page.Owner.Forget(page);
        layout.Dispose(); disposed();
    }

    private void Trim()
    {
        var node = _lru.First;
        while ((Count > layoutLimit || Bytes > byteLimit) && node is not null)
        {
            var next = node.Next;
            if (node.Value.Users == 0) Release(node.Value);
            node = next;
        }
    }

    public void Clear()
    {
        while (_lru.First is { } node) Release(node.Value);
    }
}
