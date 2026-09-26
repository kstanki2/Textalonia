using System.Runtime.CompilerServices;
using Textalonia.Model;

namespace Textalonia.Controls;

// Mutable geometry over an immutable, persistent document tree. Reused branches
// acquire their current parent when a snapshot changes; shaping lives elsewhere.
internal sealed class LayoutHeightIndex
{
    internal sealed class Branch(LayoutHeightIndex index, double width, Table? table)
    {
        public required StorageTree<OrderKey, DocumentNode> Source;
        private Node? _value;
        private Branch? _left, _right;
        private bool _listsInitialized;
        public Branch? Parent;
        private double EstimateWidth => table is null ? width : width / table.ColumnCount;
        public Node Value
        {
            get
            {
                if (_value is null)
                {
                    var available = table is null ? width : width / table.ColumnCount * ((TableCell)Source.Value.Source!).ColumnSpan;
                    _value = index.GetNode(Source.Value, available);
                    Adjust(_value.Height - Estimate(Source.Value.Length, Source.Value.ParagraphCount, EstimateWidth));
                }
                _value.Parent = this;
                return _value;
            }
        }
        public Branch? Left => Child(Source.Left, ref _left);
        public Branch? Right => Child(Source.Right, ref _right);
        private Branch? Child(StorageTree<OrderKey, DocumentNode>? source, ref Branch? child)
        {
            if (source is null) return null;
            if (child is null)
            {
                child = index.GetBranch(source, width, table)!;
                Adjust(child.Height - Estimate(source.Length, source.Paragraphs, EstimateWidth));
            }
            child.Parent = this; child.Owner = null;
            return child;
        }
        private void Adjust(double delta)
        {
            if (delta == 0) return;
            Height += delta;
            if (Parent is not null) Parent.Adjust(delta); else Owner?.Update();
        }
        public Node? Owner;
        public double Height;
        public int[]? ListAdds;
        public int ListResets;
        public void InitializeLists()
        {
            if (_listsInitialized) return;
            _listsInitialized = true;
            Left?.InitializeLists(); Right?.InitializeLists();
            // Most text has no numbering. A reset bitmask needs no per-branch
            // arrays; allocate counters only on paths containing numbered lists.
            if (Left?.ListAdds is null && Right?.ListAdds is null &&
                Value.Source.Source is not Paragraph { Style.List: ListKind.Numbered })
            {
                ListResets = (Left?.ListResets ?? 0) | (Right?.ListResets ?? 0) |
                    (Value.Source.Source is Paragraph { Style.List: ListKind.None } ? 511 : 0);
                return;
            }
            ListAdds = new int[9];
            for (var i = 0; i < 9; i++)
            {
                var add = Left?.ListAdds?[i] ?? 0; var reset = ((Left?.ListResets ?? 0) & (1 << i)) != 0;
                if (Value.Source.Source is Paragraph p)
                {
                    if (p.Style.List == ListKind.None || p.Style.List == ListKind.Numbered && i > p.Style.ListLevel) { add = 0; reset = true; }
                    if (p.Style.List == ListKind.Numbered && i == p.Style.ListLevel) add++;
                }
                if (Right is not null)
                {
                    var resets = (Right.ListResets & (1 << i)) != 0;
                    add = resets ? Right.ListAdds?[i] ?? 0 : add + (Right.ListAdds?[i] ?? 0);
                    reset |= resets;
                }
                ListAdds[i] = add; if (reset) ListResets |= 1 << i;
            }
        }
        public int NumberBefore(OrderKey key, int level)
        {
            InitializeLists();
            var number = 0; Branch? branch = this;
            while (branch is not null)
            {
                if (key.CompareTo(branch.Source.Key) <= 0) { branch = branch.Left; continue; }
                if (branch.Left is { } left) number = (left.ListResets & (1 << level)) != 0 ? left.ListAdds?[level] ?? 0 : number + (left.ListAdds?[level] ?? 0);
                if (branch.Value.Source.Source is Paragraph p)
                {
                    if (p.Style.List == ListKind.None || p.Style.List == ListKind.Numbered && level > p.Style.ListLevel) number = 0;
                    if (p.Style.List == ListKind.Numbered && level == p.Style.ListLevel) number++;
                }
                branch = branch.Right;
            }
            return number;
        }
        public void Update(Node? changedCell = null)
        {
            Height = (_left?.Height ?? (Source.Left is { } l ? Estimate(l.Length, l.Paragraphs, EstimateWidth) : 0)) +
                (_value?.Height ?? Estimate(Source.Value.Length, Source.Value.ParagraphCount, EstimateWidth)) +
                (_right?.Height ?? (Source.Right is { } r ? Estimate(r.Length, r.Paragraphs, EstimateWidth) : 0));
            if (Parent is not null) Parent.Update(changedCell); else Owner?.Update(changedCell);
        }
        public double Prefix(OrderKey key)
        {
            var result = 0d; Branch? branch = this;
            while (branch is not null)
            {
                if (key.CompareTo(branch.Source.Key) <= 0) branch = branch.Left;
                else { result += (branch.Left?.Height ?? 0) + branch.Value.Height; branch = branch.Right; }
            }
            return result;
        }
        public Node Find(OrderKey key)
        {
            var branch = this;
            while (true)
            {
                var comparison = key.CompareTo(branch.Source.Key);
                if (comparison == 0) return branch.Value;
                branch = comparison < 0 ? branch.Left! : branch.Right!;
            }
        }
        public IEnumerable<Node> Values()
        {
            if (Left is not null) foreach (var node in Left.Values()) yield return node;
            yield return Value;
            if (Right is not null) foreach (var node in Right.Values()) yield return node;
        }
    }
    internal sealed class Node
    {
        public required DocumentNode Source;
        public Branch? Children, Parent;
        public double Available, Height, ContentHeight;
        public double[] Rows = [], RowOffsets = [];
        private Node[][] _rowCells = [], _rowDependencies = [];
        private Node[] _spans = [];
        public double Indent => Source.Source is Paragraph p ? p.Style.Indent + (p.Style.List == ListKind.None ? 0 : 28 + p.Style.ListLevel * 24) : 0;
        public void Update(Node? changedCell = null)
        {
            Height = Source.Source switch
            {
                Paragraph p => ContentHeight + p.Style.SpaceBefore + p.Style.SpaceAfter,
                Section s => (Children?.Height ?? 0) + s.Padding * 2 + 10,
                TableCell => (Children?.Height ?? 0) + 12,
                Table => TableHeight(changedCell),
                _ => Children?.Height ?? 0
            };
            Parent?.Update(Source.Source is TableCell ? this : null);
        }
        private double TableHeight(Node? changedCell)
        {
            var table = (Table)Source.Source!;
            if (Rows.Length != table.Rows.Length)
            {
                Rows = new double[table.Rows.Length]; RowOffsets = new double[table.Rows.Length + 1];
                var cells = Children!.Values().ToArray();
                var dependencies = Enumerable.Range(0, Rows.Length).Select(_ => new List<Node>()).ToArray();
                var singles = Enumerable.Range(0, Rows.Length).Select(_ => new List<Node>()).ToArray();
                foreach (var cell in cells)
                {
                    var span = ((TableCell)cell.Source.Source!).RowSpan;
                    if (span == 1) singles[cell.Source.Row].Add(cell);
                    for (var r = cell.Source.Row; r < cell.Source.Row + span; r++) dependencies[r].Add(cell);
                }
                _rowCells = singles.Select(row => row.ToArray()).ToArray();
                _rowDependencies = dependencies.Select(row => row.ToArray()).ToArray();
                _spans = cells.Where(cell => ((TableCell)cell.Source.Source!).RowSpan > 1).ToArray();
            }
            var first = changedCell is not null && _spans.Length == 0 ? changedCell.Source.Row : 0;
            var last = changedCell is not null && _spans.Length == 0 ? first + 1 : Rows.Length;
            for (var r = first; r < last; r++)
            {
                Rows[r] = 36;
                foreach (var cell in _rowCells[r]) Rows[r] = Math.Max(Rows[r], cell.Height);
            }
            foreach (var cell in _spans)
            {
                var span = ((TableCell)cell.Source.Source!).RowSpan;
                var height = 0d;
                for (var r = cell.Source.Row; r < cell.Source.Row + span; r++) height += Rows[r];
                if (height < cell.Height) Rows[cell.Source.Row + span - 1] += cell.Height - height;
            }
            for (var r = first; r < Rows.Length; r++) RowOffsets[r + 1] = RowOffsets[r] + Rows[r];
            return RowOffsets[^1] + 12;
        }
        public IEnumerable<Node> IntersectingCells(double top, double bottom)
        {
            var first = Array.BinarySearch(RowOffsets, top);
            if (first < 0) first = ~first - 1;
            first = Math.Clamp(first, 0, Rows.Length - 1);
            HashSet<Node>? seen = _spans.Length == 0 ? null : [];
            for (var r = first; r < Rows.Length && RowOffsets[r] <= bottom; r++)
                foreach (var cell in _rowDependencies[r])
                    if (seen is null || seen.Add(cell)) yield return cell;
        }
    }
    private ConditionalWeakTable<DocumentNode, Node> _nodes = new();
    private ConditionalWeakTable<StorageTree<OrderKey, DocumentNode>, Branch> _branches = new();
    public Node Root { get; private set; } = null!;
    public int CreatedNodes { get; private set; }
    public void Synchronize(DocumentTree tree, double width)
    {
        // An undo may revisit a former root whose mutable geometry parent links
        // have since been reassigned. Reconstruct metadata for that reset.
        if (_nodes.TryGetValue(tree.Root, out var old) && !ReferenceEquals(old, Root))
        { _nodes = new(); _branches = new(); }
        Root = GetNode(tree.Root, width);
    }
    private Node GetNode(DocumentNode source, double available)
    {
        if (_nodes.TryGetValue(source, out var existing)) return existing;
        CreatedNodes++;
        var node = new Node { Source = source, Available = available };
        if (source.Source is Paragraph paragraph)
        {
            var fontSize = paragraph.DefaultStyle.FontSize;
            node.ContentHeight = Math.Max(1, Math.Ceiling(paragraph.Length * fontSize * .52 / Math.Max(16, available - node.Indent))) * fontSize * 1.25;
        }
        else
        {
            var childWidth = source.Source switch { Section s => Math.Max(16, available - s.Padding * 2), TableCell => Math.Max(16, available - 16), _ => available };
            node.Children = GetBranch(source.Children, childWidth, source.Source as Table);
            if (node.Children is not null) { node.Children.Parent = null; node.Children.Owner = null; }
        }
        node.Update();
        if (node.Children is not null) node.Children.Owner = node;
        _nodes.Add(source, node); return node;
    }
    private static double Estimate(int length, int paragraphs, double width) =>
        paragraphs * 28d + length * (16 * .52 * 20 / Math.Max(16, width));

    private Branch? GetBranch(StorageTree<OrderKey, DocumentNode>? source, double width, Table? table)
    {
        if (source is null) return null;
        if (_branches.TryGetValue(source, out var existing)) return existing;
        // Estimates are additive across tree weights. Creating a branch does not
        // visit its descendants; measured corrections propagate through parents.
        var branch = new Branch(this, width, table)
        {
            Source = source,
            Height = Estimate(source.Length, source.Paragraphs, table is null ? width : width / table.ColumnCount)
        };
        _branches.Add(source, branch); return branch;
    }
}
