using Textalonia.Model;

namespace Textalonia.Editing;

// Refcounts refer to graph edges, not a per-snapshot sum. Adding/removing a root
// traverses only newly owned/released branches; shared nodes are charged once.
internal sealed class RetentionGraph
{
    private readonly Dictionary<object, int> _references = new(ReferenceEqualityComparer.Instance);
    public long Bytes { get; private set; }
    public void Add(object value)
    {
        if (_references.TryGetValue(value, out var count)) { _references[value] = count + 1; return; }
        _references.Add(value, 1); Bytes += Size(value);
        foreach (var child in Children(value)) Add(child);
    }
    public void Remove(object value)
    {
        var count = _references[value];
        if (count > 1) { _references[value] = count - 1; return; }
        _references.Remove(value); Bytes -= Size(value);
        foreach (var child in Children(value)) Remove(child);
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
