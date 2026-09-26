using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Textalonia.Model;

internal sealed record DocumentPath(DocumentPath? Parent, OrderKey Key) : IRetained
{
    public long Bytes => 64 + Key.Numerator.GetByteCount();
    public IEnumerable<object> References { get { if (Parent is not null) yield return Parent; } }
    public IEnumerable<OrderKey> Keys()
    {
        if (Parent is not null) foreach (var key in Parent.Keys()) yield return key;
        yield return Key;
    }
}

internal sealed class DocumentNode(object? source, StorageTree<OrderKey, DocumentNode>? children = null, int row = -1, int column = -1,
    HiddenCellStorage? hidden = null) : IRetained
{
    public object? Source { get; } = source;
    public Guid Id => Source switch { Block b => b.Id, TableCell c => c.Id, _ => Guid.Empty };
    public StorageTree<OrderKey, DocumentNode>? Children { get; } = children;
    public int Length { get; } = source is Paragraph p ? p.Length + 1 : children?.Length ?? 0;
    public int ParagraphCount { get; } = source is Paragraph ? 1 : children?.Paragraphs ?? 0;
    public int Row { get; } = row;
    public int Column { get; } = column;
    public HiddenCellStorage? Hidden { get; } = hidden;
    public DocumentNode WithChildren(StorageTree<OrderKey, DocumentNode> children, DocumentNode? changedCell = null)
    {
        object? next = Source switch
        {
            Section s => s.WithChildren(children),
            TableCell c => c.WithChildren(children),
            Table t when changedCell is not null => t.SetCell(changedCell.Row, changedCell.Column, (TableCell)changedCell.Source!),
            null => null,
            _ => throw new InvalidOperationException("Invalid indexed container.")
        };
        return new(next, children, Row, Column, Hidden);
    }
    public long Bytes => 144 + (Source switch
    {
        Paragraph p => 96 + p.Runs.Length * 48L,
        Table t => 96 + t.Rows.Length * 16L, // row arrays are separate retained objects below
        _ => 80 + (Children?.Count ?? 0) * 8L
    });
    public IEnumerable<object> References
    {
        get
        {
            if (Children is not null) yield return Children;
            if (Hidden is not null) yield return Hidden;
            if (Source is Paragraph p)
            {
                yield return p.DefaultStyle; yield return p.Style;
                foreach (var run in p.Runs) { yield return run.Storage; yield return run.Style; }
                yield return ParagraphText.For(p);
            }
            // Table snapshots also own covered cells and merge backups, outside visible indexing.
            if (Source is Table table)
                foreach (var row in table.Rows) yield return System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(row)!;
            if (Source is TableCell cell)
                foreach (var paragraph in cell.MergeOriginal) yield return HiddenParagraph(paragraph);
            if (Source is Section section)
            {
                if (section.Background is not null) yield return section.Background;
                if (section.BorderColor is not null) yield return section.BorderColor;
            }
            if (Source is TableCell { Background: { } background }) yield return background;
        }
    }
    private static readonly ConditionalWeakTable<Paragraph, DocumentNode> HiddenParagraphs = new();
    internal static DocumentNode HiddenParagraph(Paragraph paragraph) => HiddenParagraphs.GetValue(paragraph, p => new(p));
}

internal sealed class HiddenCellStorage(IReadOnlyList<TableCell> cells) : IRetained
{
    public long Bytes => 32 + cells.Count * 8L;
    public IEnumerable<object> References => cells;
}

internal sealed class DocumentTree : IRetained
{
    private static readonly ConditionalWeakTable<FlowDocument, DocumentTree> Trees = new();
    public DocumentNode Root { get; }
    public StorageTree<Guid, DocumentPath>? Paths { get; }
    public int UpdatedNodes { get; }
    private DocumentTree(DocumentNode root, StorageTree<Guid, DocumentPath>? paths, int updatedNodes)
    { Root = root; Paths = paths; UpdatedNodes = updatedNodes; }
    public static DocumentTree For(FlowDocument document) => Trees.GetValue(document, Build);
    private static DocumentTree Build(FlowDocument document)
    {
        var paths = new List<(Guid Key, DocumentPath Value, int Length, int Paragraphs)>();
        var count = 0;
        DocumentNode Visit(object source, DocumentPath path, int row = -1, int column = -1)
        {
            count++;
            var children = new List<(OrderKey Key, DocumentNode Value, int Length, int Paragraphs)>();
            var hiddenCells = new List<TableCell>();
            void Add(object child, int index, int r = -1, int c = -1)
            {
                var key = new OrderKey(index);
                var node = Visit(child, new(path, key), r, c);
                children.Add((key, node, node.Length, node.ParagraphCount));
            }
            switch (source)
            {
                case Section section:
                    var blocks = section.Blocks;
                    for (var i = 0; i < blocks.Length; i++) Add(blocks[i], i);
                    break;
                case Table table:
                    for (var r = 0; r < table.Rows.Length; r++)
                        for (var c = 0; c < table.ColumnCount; c++)
                            if (!table.IsCovered(r, c)) Add(table.Rows[r][c], r * table.ColumnCount + c, r, c);
                            else hiddenCells.Add(table.Rows[r][c]);
                    break;
                case TableCell cell:
                    var paragraphs = cell.Paragraphs;
                    for (var i = 0; i < paragraphs.Length; i++) Add(paragraphs[i], i);
                    break;
            }
            var result = new DocumentNode(source, StorageTree<OrderKey, DocumentNode>.FromOrdered(children), row, column, hiddenCells.Count == 0 ? null : new(hiddenCells));
            paths.Add((result.Id, path, 0, 0));
            return result;
        }
        var roots = new List<(OrderKey Key, DocumentNode Value, int Length, int Paragraphs)>();
        var top = document.Blocks;
        for (var i = 0; i < top.Length; i++)
        {
            var key = new OrderKey(i); var node = Visit(top[i], new(null, key));
            roots.Add((key, node, node.Length, node.ParagraphCount));
        }
        return new(new(null, StorageTree<OrderKey, DocumentNode>.FromOrdered(roots)),
            StorageTree<Guid, DocumentPath>.FromOrdered(paths.OrderBy(p => p.Key).ToArray()), count);
    }
    public (DocumentNode Node, int Start, Guid Container, Guid Top) Locate(Guid id)
    {
        var path = Paths?.Find(id)?.Value ?? throw new KeyNotFoundException("The block is not visible in this snapshot.");
        var node = Root; var start = 0; var container = Guid.Empty; var top = Guid.Empty;
        foreach (var key in path.Keys())
        {
            if (node.Source is Section or TableCell) container = node.Id;
            start += node.Children!.Prefix(key).Length;
            node = node.Children.Find(key)!.Value;
            if (top == Guid.Empty) top = node.Id;
        }
        return (node, start, container, top);
    }
    // Only affected paths are visited. Fractional keys keep all other paths valid.
    public FlowDocument Rewrite(FlowDocument document, IReadOnlyDictionary<Guid, ImmutableArray<Paragraph>> replacements)
    {
        var root = Root; var paths = Paths; var updated = 0; var rebase = false;
        foreach (var (id, requested) in replacements)
        {
            var path = paths?.Find(id)?.Value;
            if (path is null) continue;
            var keys = path.Keys().ToArray();
            DocumentNode RewriteNode(DocumentNode parent, int depth)
            {
                updated++;
                var key = keys[depth]; var children = parent.Children!;
                if (depth + 1 == keys.Length)
                {
                    var replacement = requested;
                    if (replacement.IsEmpty && children.Count == 1) replacement = [new Paragraph()];
                    var nextKey = children.NextOrderKey(key);
                    children = StorageTree<OrderKey, DocumentNode>.Remove(children, key)!;
                    paths = StorageTree<Guid, DocumentPath>.Remove(paths, id);
                    var insertKeys = OrderKey.Insertions(key, nextKey, replacement.Length);
                    for (var i = 0; i < replacement.Length; i++)
                    {
                        var insertKey = insertKeys[i];
                        rebase |= insertKey.Scale > 256;
                        var paragraph = replacement[i]; var node = new DocumentNode(paragraph);
                        children = StorageTree<OrderKey, DocumentNode>.Set(children, insertKey, node, node.Length, 1);
                        paths = StorageTree<Guid, DocumentPath>.Set(paths, paragraph.Id, new(path.Parent, insertKey));
                    }
                    return parent.WithChildren(children);
                }
                var child = RewriteNode(children.Find(key)!.Value, depth + 1);
                children = StorageTree<OrderKey, DocumentNode>.Set(children, key, child, child.Length, child.ParagraphCount);
                return parent.WithChildren(children, child);
            }
            root = RewriteNode(root, 0);
        }
        var result = document.WithChildren(root.Children);
        // Rebase unusually dense insertion labels through the full fallback.
        // Old snapshots retain their own valid paths; ordinary edits never rebase.
        if (!rebase) Trees.Add(result, new(root, paths, updated));
        return result;
    }
    public long Bytes => 64;
    public IEnumerable<object> References { get { yield return Root; if (Paths is not null) yield return Paths; } }
}
