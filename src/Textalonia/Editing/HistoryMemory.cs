using Textalonia.Model;
using System.Runtime.InteropServices;

namespace Textalonia.Editing;

// Refcounts refer to graph edges, not a per-snapshot sum. Adding/removing a root
// traverses only newly owned/released branches; shared nodes are charged once.
internal sealed class RetentionGraph
{
    private struct Ownership { public int Total, Current; }
    private readonly Dictionary<object, Ownership> _references = new(ReferenceEqualityComparer.Instance);
    public long Bytes { get; private set; }
    private long _currentBytes;
    public long HistoryBytes => Bytes - _currentBytes;
    public void EnsureCapacity(int count) => _references.EnsureCapacity(count);
    public void Clear()
    {
        _references.Clear(); Bytes = 0; _currentBytes = 0;
    }
    public void Add(object value, bool current = false) => Add(value, true, current);
    private void Add(object value, bool total, bool current)
    {
        // One lookup and one traversal for the two ownership domains. Do not
        // keep the dictionary ref across recursion (a child can resize it).
        ref var ownership = ref CollectionsMarshal.GetValueRefOrAddDefault(_references, value, out _);
        total = total && ownership.Total++ == 0;
        current = current && ownership.Current++ == 0;
        if (!total && !current) return;
        var bytes = Size(value);
        if (total) Bytes += bytes;
        if (current) _currentBytes += bytes;
        foreach (var child in Children(value)) Add(child, total, current);
    }
    public void Remove(object value, bool current = false) => Remove(value, true, current);
    private void Remove(object value, bool total, bool current)
    {
        ref var ownership = ref CollectionsMarshal.GetValueRefOrNullRef(_references, value);
        total = total && --ownership.Total == 0;
        current = current && --ownership.Current == 0;
        if (!total && !current) return;
        if (total) _references.Remove(value);
        var bytes = Size(value);
        if (total) Bytes -= bytes;
        if (current) _currentBytes -= bytes;
        foreach (var child in Children(value)) Remove(child, total, current);
    }
    private static long Size(object value) => value switch
    {
        IRetained node => node.Bytes,
        string text => 24 + text.Length * 2L,
        TableCell[] cells => 24 + cells.Length * 8L,
        TableCell cell => 96 + (cell.Paragraphs.Length + cell.MergeOriginal.Length) * 8L,
        TextStyle => 128,
        ParagraphStyle => 64,
        _ => 32
    };
    private static IEnumerable<object> Children(object value)
    {
        switch (value)
        {
            case IRetained node:
                foreach (var child in node.References) yield return child;
                break;
            // Row arrays contribute allocation only. Visible cells are already
            // owned by indexed nodes; covered cells are in HiddenCellStorage.
            case TableCell cell:
                foreach (var paragraph in cell.Paragraphs.Concat(cell.MergeOriginal)) yield return DocumentNode.HiddenParagraph(paragraph);
                if (cell.Background is not null) yield return cell.Background;
                break;
            case TextStyle style:
                if (style.FontFamily is not null) yield return style.FontFamily;
                if (style.Foreground is not null) yield return style.Foreground;
                if (style.Background is not null) yield return style.Background;
                if (style.Hyperlink is not null) yield return style.Hyperlink;
                break;
        }
    }
}

/// <summary>A paragraph-relative position valid only for its originating session revision.</summary>
public readonly record struct DocumentPosition(int Revision, Guid ParagraphId, int Offset)
{
    internal Guid Scope { get; init; }
}

internal sealed record DocumentEdit(int BeforeRevision, int AfterRevision, int Start, int RemovedLength,
    int InsertedLength, IReadOnlyCollection<Guid> ChangedParagraphs, bool Reset = false);
