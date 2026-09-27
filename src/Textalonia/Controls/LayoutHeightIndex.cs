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
        public double Width => width;
        public Table? Table => table;
        private Node? _value;
        private Branch? _left, _right;
        public Branch? Parent;
        private double EstimateWidth => table is null ? width : width / table.ColumnCount;
        public Node Value
        {
            get
            {
                if (_value is null)
                {
                    var available = table is null ? width : ColumnWidth(table, width, Source.Value.Column, ((TableCell)Source.Value.Source!).ColumnSpan);
                    _value = index.GetNode(Source.Value, available, table);
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
        public Paragraph? Paragraph;
        public TableCell? Cell;
        public TableStyle? InheritedTableStyle;
        public double SpaceBefore, SpaceAfter;
        public Branch? Children, Parent;
        public double Available, Height, ContentHeight;
        public double[] Rows = [], RowOffsets = [];
        private Node[][] _rowCells = [], _rowDependencies = [];
        private Node[] _spans = [];
        public double TextWidth => Math.Max(16, Available - Indent - (Paragraph?.Style.RightIndent ?? 0));
        public double Indent => Paragraph is { } p ? p.Style.Indent + (p.Style.List == ListKind.None ? 0 : 28 + p.Style.ListLevel * 24) : 0;
        public void Update(Node? changedCell = null)
        {
            Height = Source.Source switch
            {
                Textalonia.Model.Paragraph => ContentHeight + SpaceBefore + SpaceAfter,
                Section s => (Children?.Height ?? 0) + SectionPadding(s).Top + SectionPadding(s).Bottom + 10,
                TableCell cell => (Children?.Height ?? 0) + CellPadding(Cell ?? cell).Top + CellPadding(Cell ?? cell).Bottom,
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
                var sizing = table.RowSizing.IsDefaultOrEmpty ? new TableRowSizing() : table.RowSizing[r];
                Rows[r] = sizing.Mode == TableRowHeightMode.Auto ? 36 : sizing.Height;
                if (sizing.Mode != TableRowHeightMode.Exact)
                    foreach (var cell in _rowCells[r]) Rows[r] = Math.Max(Rows[r], cell.Height);
            }
            foreach (var cell in _spans)
            {
                var span = ((TableCell)cell.Source.Source!).RowSpan;
                var height = 0d;
                for (var r = cell.Source.Row; r < cell.Source.Row + span; r++) height += Rows[r];
                if (height < cell.Height)
                    for (var r = cell.Source.Row + span - 1; r >= cell.Source.Row; r--)
                        if (table.RowSizing.IsDefaultOrEmpty || table.RowSizing[r].Mode != TableRowHeightMode.Exact)
                        { Rows[r] += cell.Height - height; break; }
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
    private DocumentStyleResolver? _resolver;
    private DocumentIndex? _documentIndex;
    private bool _contextual;
    public void Synchronize(DocumentTree tree, double width, DocumentStyleResolver? resolver = null, DocumentIndex? index = null)
    {
        if (_contextual && _documentIndex?.Tree != tree) { _nodes = new(); _branches = new(); }
        _resolver = resolver; _documentIndex = index;
        // An undo may revisit a former root whose mutable geometry parent links
        // have since been reassigned. Reconstruct metadata for that reset.
        if (_nodes.TryGetValue(tree.Root, out var old) && !ReferenceEquals(old, Root))
        { _nodes = new(); _branches = new(); }
        Root = GetNode(tree.Root, width);
    }
    private Node GetNode(DocumentNode source, double available, Table? table = null)
    {
        var inherited = source.Source is TableCell ? table is null ? new TableStyle() :
            _resolver?.ResolveTableStyle(table) ?? new TableStyle() : null;
        if (_nodes.TryGetValue(source, out var existing))
        {
            if (Math.Abs(existing.Available - available) < .01 && existing.InheritedTableStyle == inherited) return existing;
            _nodes.Remove(source);
        }
        CreatedNodes++;
        var node = new Node { Source = source, Available = available, InheritedTableStyle = inherited };
        if (source.Source is Paragraph paragraph)
        {
            node.Paragraph = _resolver?.ResolveParagraph(paragraph) ?? paragraph;
            node.SpaceBefore = node.Paragraph.Style.SpaceBefore; node.SpaceAfter = node.Paragraph.Style.SpaceAfter;
            if (node.Paragraph.Style.ContextualSpacing && _documentIndex is { } index)
            {
                _contextual = true;
                var at = index.ById(paragraph.Id);
                bool Same(ParagraphPosition other) => other.ContainerId == at.ContainerId && other.Paragraph.Style.StyleId == paragraph.Style.StyleId;
                if (at.Start > 0 && Same(index.At(at.Start - 1))) node.SpaceBefore = 0;
                if (at.End < index.Length && Same(index.At(at.End + 1))) node.SpaceAfter = 0;
            }
            paragraph = node.Paragraph;
            var fontSize = paragraph.DefaultStyle.FontSize;
            node.ContentHeight = Math.Max(1, Math.Ceiling(paragraph.Length * fontSize * .52 / node.TextWidth)) * (paragraph.Style.LineHeight ?? fontSize * 1.25);
        }
        else
        {
            if (source.Source is TableCell cellModel)
            {
                node.Cell = cellModel with { Background = cellModel.Background ?? inherited!.Background,
                    Padding = cellModel.Padding ?? inherited!.Padding, Borders = cellModel.Borders ?? inherited!.Borders };
            }
            var childWidth = source.Source switch { Section s => Math.Max(16, available - SectionPadding(s).Left - SectionPadding(s).Right), TableCell cell => Math.Max(16, available - CellPadding(node.Cell ?? cell).Left - CellPadding(node.Cell ?? cell).Right), _ => available };
            node.Children = GetBranch(source.Children, childWidth, source.Source as Table);
            if (node.Children is not null) { node.Children.Parent = null; node.Children.Owner = null; }
        }
        node.Update();
        if (node.Children is not null) node.Children.Owner = node;
        _nodes.Add(source, node); return node;
    }
    internal static EdgeInsets SectionPadding(Section section) => section.PaddingEdges ?? new(section.Padding, section.Padding, section.Padding, section.Padding);
    internal static EdgeInsets CellPadding(TableCell cell) => cell.Padding ?? new(8, 8, 8, 4);
    internal static double ColumnWidth(Table table, double available, int column, int span = 1)
    {
        if (table.ColumnWidths.IsDefaultOrEmpty) return available * span / table.ColumnCount;
        var total = table.ColumnWidths.Sum();
        var weight = 0d;
        for (var i = column; i < column + span; i++) weight += table.ColumnWidths[i];
        return available * weight / total;
    }
    internal static double ColumnOffset(Table table, double available, int column) => column == 0 ? 0 : ColumnWidth(table, available, 0, column);

    private static double Estimate(int length, int paragraphs, double width) =>
        paragraphs * 28d + length * (16 * .52 * 20 / Math.Max(16, width));

    private Branch? GetBranch(StorageTree<OrderKey, DocumentNode>? source, double width, Table? table)
    {
        if (source is null) return null;
        if (_branches.TryGetValue(source, out var existing))
        {
            if (Math.Abs(existing.Width - width) < .01 && ReferenceEquals(existing.Table, table)) return existing;
            _branches.Remove(source);
        }
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
