using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Textalonia.Model;

internal sealed record DocumentPath(DocumentPath? Parent, OrderKey Key) : IRetained
{
    public long Bytes => 64 + Key.Numerator.GetByteCount();
    public void VisitReferences(Action<object> visit) { if (Parent is not null) visit(Parent); }
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
    public void VisitReferences(Action<object> visit)
    {
        if (Children is not null) visit(Children);
        if (Hidden is not null) visit(Hidden);
        if (Source is Paragraph p)
        {
            visit(p.DefaultStyle); visit(p.Style);
            foreach (var run in p.Runs) { visit(run.Storage); visit(run.Style); if (run.Inline is not null) visit(run.Inline); }
            visit(ParagraphText.For(p));
        }
        // Table snapshots also own covered cells and merge backups, outside visible indexing.
        if (Source is Table table)
        {
            foreach (var row in table.Rows) visit(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(row)!);
            VisitTableSizing(table, visit);
        }
        if (Source is TableCell cell)
            foreach (var block in cell.MergeOriginalBlocks) visit(HiddenBlock(block));
        if (Source is Section section)
        {
            if (section.Background is not null) visit(section.Background);
            if (section.BorderColor is not null) visit(section.BorderColor);
            if (section.CodeLanguage is not null) visit(section.CodeLanguage);
        }
        if (Source is TableCell { Background: { } background }) visit(background);
        if (Source is TableCell styledCell)
        {
            if (styledCell.Borders is not null) visit(styledCell.Borders);
            if (styledCell.Padding is not null) visit(styledCell.Padding);
        }
        if (Source is Section styledSection)
        {
            if (styledSection.Borders is not null) visit(styledSection.Borders);
            if (styledSection.PaddingEdges is not null) visit(styledSection.PaddingEdges);
        }
    }
    internal static void VisitTableSizing(Table table, Action<object> visit)
    {
        if (table.StyleId is not null) visit(table.StyleId);
        if (table.StyleOverrides is not null) visit(table.StyleOverrides);
        if (!table.ColumnWidths.IsDefaultOrEmpty) visit(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(table.ColumnWidths)!);
        if (!table.RowSizing.IsDefaultOrEmpty) visit(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(table.RowSizing)!);
    }
    private static readonly ConditionalWeakTable<Block, HiddenBlockStorage> HiddenBlocks = new();
    internal static HiddenBlockStorage HiddenBlock(Block block) => HiddenBlocks.GetValue(block, b => new(b));
}

// Retains full hidden subtrees without adding them to visible positions or paths.
internal sealed class HiddenBlockStorage(Block block) : IRetained
{
    public long Bytes => block switch
    {
        Paragraph paragraph => 96 + paragraph.Runs.Length * 48L,
        Section section => 96 + section.Blocks.Length * 8L,
        Table table => 112 + table.Rows.Length * 16L,
        _ => 80
    };
    public void VisitReferences(Action<object> visit)
    {
        switch (block)
        {
            case Paragraph paragraph:
                visit(paragraph.Style); visit(paragraph.DefaultStyle);
                foreach (var run in paragraph.Runs) { visit(run.Storage); visit(run.Style); if (run.Inline is not null) visit(run.Inline); }
                visit(ParagraphText.For(paragraph));
                break;
            case Section section:
                foreach (var child in section.Blocks) visit(DocumentNode.HiddenBlock(child));
                if (section.Background is not null) visit(section.Background);
                if (section.BorderColor is not null) visit(section.BorderColor);
                if (section.CodeLanguage is not null) visit(section.CodeLanguage);
                if (section.Borders is not null) visit(section.Borders);
                if (section.PaddingEdges is not null) visit(section.PaddingEdges);
                break;
            case Table table:
                DocumentNode.VisitTableSizing(table, visit);
                foreach (var row in table.Rows)
                {
                    visit(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(row)!);
                    foreach (var cell in row) visit(cell);
                }
                break;
        }
    }
}

internal sealed class HiddenCellStorage(IReadOnlyList<TableCell> cells) : IRetained
{
    public long Bytes => 32 + cells.Count * 8L;
    public void VisitReferences(Action<object> visit) { for (var i = 0; i < cells.Count; i++) visit(cells[i]); }
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
    // Full parser snapshots can still share the index and geometry of immutable blocks.
    // Only ordinal-key trees are reconciled; editor-generated fractional keys use the normal rebuild.
    internal static FlowDocument ReuseSnapshot(FlowDocument previous, FlowDocument next, CancellationToken cancellationToken)
    {
        var before = For(previous);
        var after = For(next);
        var updated = 0;
        DocumentNode ReuseNode(DocumentNode oldNode, DocumentNode newNode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (newNode.Source is not null && ReferenceEquals(oldNode.Source, newNode.Source) &&
                oldNode.Row == newNode.Row && oldNode.Column == newNode.Column && ReferenceEquals(oldNode.Hidden, newNode.Hidden)) return oldNode;
            updated++;
            if (oldNode.Source?.GetType() != newNode.Source?.GetType() || oldNode.Children is null || newNode.Children is null)
                return newNode;
            var oldItems = oldNode.Children.Items().ToArray();
            var newItems = newNode.Children.Items().ToArray();
            if (oldItems.Where((item, i) => item.Key != new OrderKey(i)).Any() ||
                newItems.Where((item, i) => item.Key != new OrderKey(i)).Any()) return newNode;
            var children = oldNode.Children;
            for (var i = 0; i < newItems.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var child = i < oldItems.Length ? ReuseNode(oldItems[i].Value, newItems[i].Value) : newItems[i].Value;
                if (i < oldItems.Length && ReferenceEquals(child, oldItems[i].Value)) continue;
                children = StorageTree<OrderKey, DocumentNode>.Set(children, newItems[i].Key, child, child.Length, child.ParagraphCount);
            }
            for (var i = newItems.Length; i < oldItems.Length; i++)
                children = StorageTree<OrderKey, DocumentNode>.Remove(children, oldItems[i].Key);
            return new(newNode.Source, children, newNode.Row, newNode.Column, newNode.Hidden);
        }
        var root = ReuseNode(before.Root, after.Root);
        var result = next with { };
        Trees.Add(result, new(root, after.Paths, updated));
        return result;
    }
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
                    var cellBlocks = cell.Blocks;
                    for (var i = 0; i < cellBlocks.Length; i++) Add(cellBlocks[i], i);
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
        Visit(path);
        void Visit(DocumentPath current)
        {
            if (current.Parent is not null) Visit(current.Parent);
            var key = current.Key;
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
    public void VisitReferences(Action<object> visit) { visit(Root); if (Paths is not null) visit(Paths); }
}
