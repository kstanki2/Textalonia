using System.Runtime.CompilerServices;
using Textalonia.Model;

namespace Textalonia.Controls;

// Mutable geometry over an immutable, persistent document tree. Reused branches
// acquire their current parent when a snapshot changes; shaping lives elsewhere.
internal sealed class LayoutHeightIndex
{
    internal sealed class Branch
    {
        public required StorageTree<OrderKey, DocumentNode> Source;
        public required Node Value;
        public Branch? Left, Right, Parent;
        public Node? Owner;
        public double Height;
        public int[] ListAdds = new int[9];
        public bool[] ListResets = new bool[9];
        public void InitializeLists()
        {
            for (var i = 0; i < 9; i++)
            {
                var add = Left?.ListAdds[i] ?? 0; var reset = Left?.ListResets[i] ?? false;
                if (Value.Source.Source is Paragraph p)
                {
                    if (p.Style.List == ListKind.None || p.Style.List == ListKind.Numbered && i > p.Style.ListLevel) { add = 0; reset = true; }
                    if (p.Style.List == ListKind.Numbered && i == p.Style.ListLevel) add++;
                }
                if (Right is not null)
                {
                    add = Right.ListResets[i] ? Right.ListAdds[i] : add + Right.ListAdds[i];
                    reset |= Right.ListResets[i];
                }
                ListAdds[i] = add; ListResets[i] = reset;
            }
        }
        public int NumberBefore(OrderKey key, int level)
        {
            var number = 0; Branch? branch = this;
            while (branch is not null)
            {
                if (key.CompareTo(branch.Source.Key) <= 0) { branch = branch.Left; continue; }
                if (branch.Left is { } left) number = left.ListResets[level] ? left.ListAdds[level] : number + left.ListAdds[level];
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
            Height = (Left?.Height ?? 0) + Value.Height + (Right?.Height ?? 0);
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
            if (node.Children is not null) { node.Children.Parent = null; node.Children.Owner = node; }
        }
        node.Update(); _nodes.Add(source, node); return node;
    }
    private Branch? GetBranch(StorageTree<OrderKey, DocumentNode>? source, double width, Table? table)
    {
        if (source is null) return null;
        if (_branches.TryGetValue(source, out var existing)) return existing;
        var available = table is null ? width : width / table.ColumnCount * ((TableCell)source.Value.Source!).ColumnSpan;
        var branch = new Branch { Source = source, Value = GetNode(source.Value, available),
            Left = GetBranch(source.Left, width, table), Right = GetBranch(source.Right, width, table) };
        branch.Value.Parent = branch;
        if (branch.Left is not null) { branch.Left.Parent = branch; branch.Left.Owner = null; }
        if (branch.Right is not null) { branch.Right.Parent = branch; branch.Right.Owner = null; }
        branch.Height = (branch.Left?.Height ?? 0) + branch.Value.Height + (branch.Right?.Height ?? 0);
        branch.InitializeLists();
        _branches.Add(source, branch); return branch;
    }
}
