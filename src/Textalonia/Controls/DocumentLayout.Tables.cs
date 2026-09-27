using Avalonia;
using Textalonia.Model;

namespace Textalonia.Controls;

internal sealed record TableCellVisual(Table Table, int Row, int Column, Rect Bounds, double ColumnWidth, double RowHeight, Rect? Clip)
{
    public TableCell Cell => Table.Rows[Row][Column];
    public Rect VisibleBounds => Clip?.Intersect(Bounds) ?? Bounds;
}

internal sealed partial class DocumentLayout
{
    /// <summary>Cell geometry from the same row offsets and relative widths used to paint text and borders.</summary>
    internal IReadOnlyList<TableCellVisual> TableCells()
    {
        var result = new List<TableCellVisual>();
        if (_document is null) return result;
        Visit(_heights.Root, _padding.Left, _padding.Top, null);
        return result;

        void Visit(LayoutHeightIndex.Node node, double x, double y, Rect? clip)
        {
            if (y > _viewport.Bottom + 160 || y + node.Height < _viewport.Top - 160) return;
            switch (node.Source.Source)
            {
                case Paragraph: return;
                case Table table:
                    foreach (var cellNode in node.IntersectingCells(_viewport.Top - 160 - y, _viewport.Bottom + 160 - y))
                    {
                        var cell = (TableCell)cellNode.Source.Source!;
                        var row = cellNode.Source.Row; var column = cellNode.Source.Column;
                        var bounds = new Rect(x + LayoutHeightIndex.ColumnOffset(table, node.Available, column), y + node.RowOffsets[row],
                            LayoutHeightIndex.ColumnWidth(table, node.Available, column, cell.ColumnSpan), node.RowOffsets[row + cell.RowSpan] - node.RowOffsets[row]);
                        result.Add(new(table, row, column, bounds, LayoutHeightIndex.ColumnWidth(table, node.Available, column + cell.ColumnSpan - 1),
                            node.Rows[row + cell.RowSpan - 1], clip));
                        var exact = !table.RowSizing.IsEmpty && Enumerable.Range(row, cell.RowSpan).All(r => table.RowSizing[r].Mode == TableRowHeightMode.Exact);
                        Visit(cellNode, bounds.X, bounds.Y, exact ? clip?.Intersect(bounds) ?? bounds : clip);
                    }
                    return;
                case Section section:
                    var sectionInset = LayoutHeightIndex.SectionPadding(section); x += sectionInset.Left; y += sectionInset.Top;
                    break;
                case TableCell cell:
                    var cellInset = LayoutHeightIndex.CellPadding(cell); x += cellInset.Left; y += cellInset.Top;
                    break;
            }
            Branch(node.Children, x, y, clip);
        }
        void Branch(LayoutHeightIndex.Branch? branch, double x, double y, Rect? clip)
        {
            if (branch is null || y > _viewport.Bottom + 160 || y + branch.Height < _viewport.Top - 160) return;
            Branch(branch.Left, x, y, clip);
            y += branch.Left?.Height ?? 0;
            Visit(branch.Value, x, y, clip);
            Branch(branch.Right, x, y + branch.Value.Height, clip);
        }
    }

    internal TableCellVisual? HitTestTableCell(Point point, Guid? tableId = null) => TableCells()
        .LastOrDefault(cell => (tableId is null || cell.Table.Id == tableId) && cell.VisibleBounds.Contains(point));
}
