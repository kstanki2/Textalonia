using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace Textalonia.Controls;

/// <summary>Transient mouse table gestures; detach, focus loss and capture loss cancel preview without touching history.</summary>
internal sealed class TablePointerController : IDisposable
{
    private readonly DocumentInputContext context;
    private int _gestureRevision;
    public TablePointerController(DocumentInputContext context)
    {
        this.context = context;
        context.Session.Changed += SessionChanged;
    }
    private void SessionChanged(object? sender, EventArgs e)
    {
        if (_anchor is not null && (context.Session.Revision != _gestureRevision || !_selecting && context.Session.IsReadOnly)) Cancel();
    }
    private IPointer? _pointer;
    private TableCellVisual? _anchor;
    private TableResizeAxis? _axis;
    private Point _start;
    private double _initialSize;
    private bool _selecting;
    private bool _releasing;
    private (Guid TableId, int Row, int Column)? _hover;
    private TableResizeAxis? _cursorAxis;

    public bool Pressed(PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse || !e.GetCurrentPoint(context.Surface).Properties.IsLeftButtonPressed) return false;
        context.Surface.EnsureLayout(context.Surface.Bounds.Width);
        var point = e.GetPosition(context.Surface);
        var selecting = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var cell = selecting ? context.Surface.GeometryHitTestTableCell(point) : ResizeCell(point) ?? context.Surface.GeometryHitTestTableCell(point);
        if (cell is null) { context.Editor.ClearTableCellSelection(); return false; }
        var axis = selecting ? null : ResizeAxis(cell, point);
        if (!selecting && (axis is null || context.Session.IsReadOnly)) { context.Editor.ClearTableCellSelection(); return false; }
        context.Surface.Focus(); context.CancelComposition();
        _gestureRevision = context.Session.Revision; _pointer = e.Pointer; _anchor = cell; _start = point; _axis = axis; _selecting = selecting;
        if (selecting) context.Editor.SelectTableCells(cell.Table.Id, cell.Row, cell.Column, cell.Row, cell.Column);
        else
        {
            _initialSize = axis == TableResizeAxis.Column ? cell.ColumnWidth : cell.RowHeight;
            var index = axis == TableResizeAxis.Column ? cell.Column + cell.Cell.ColumnSpan - 1 : cell.Row + cell.Cell.RowSpan - 1;
            if (!context.Editor.BeginTableResize(cell.Table.Id, axis!.Value, index, _initialSize)) { Cancel(); return false; }
        }
        e.Pointer.Capture(context.Surface); e.Handled = true; return true;
    }

    public bool Moved(PointerEventArgs e)
    {
        context.Surface.EnsureLayout(context.Surface.Bounds.Width);
        var point = e.GetPosition(context.Surface);
        if (_anchor is null)
        {
            var hovered = ResizeCell(point) ?? context.Surface.GeometryHitTestTableCell(point);
            _hover = hovered is null ? null : (hovered.Table.Id, hovered.Row, hovered.Column);
            var axis = hovered is null || context.Session.IsReadOnly ? null : ResizeAxis(hovered, point);
            if (_cursorAxis != axis)
            {
                _cursorAxis = axis;
                context.Surface.Cursor = new Cursor(axis == TableResizeAxis.Column ? StandardCursorType.SizeWestEast : axis == TableResizeAxis.Row ? StandardCursorType.SizeNorthSouth : StandardCursorType.Ibeam);
            }
            context.InvalidateVisual(); return false;
        }
        if (e.Pointer != _pointer || _pointer.Captured != context.Surface) return false;
        if (_selecting)
        {
            // Stay in the originating table when the pointer crosses a nested table.
            var cell = context.Surface.GeometryHitTestTableCell(point, _anchor.Table.Id);
            if (cell is not null) context.Editor.SelectTableCells(_anchor.Table.Id, _anchor.Row, _anchor.Column, cell.Row, cell.Column);
        }
        else context.Editor.PreviewTableResize(_initialSize + (_axis == TableResizeAxis.Column ? (point.X - _start.X) / context.Surface.ViewZoom : (point.Y - _start.Y) / context.Surface.ViewZoom));
        e.Handled = true; return true;
    }

    public bool Released(PointerReleasedEventArgs e)
    {
        if (_anchor is null || e.Pointer != _pointer) return false;
        if (!_selecting) context.Editor.CommitTableResize();
        Release(); e.Handled = true; return true;
    }
    public void CaptureLost(PointerCaptureLostEventArgs e) { if (!_releasing && _pointer == e.Pointer) Cancel(); }
    public void Cancel()
    {
        context.Editor.CancelTableResize(); Release();
    }
    private void Release()
    {
        _anchor = null; _axis = null; _selecting = false;
        if (_cursorAxis is not null) { _cursorAxis = null; context.Surface.Cursor = new Cursor(StandardCursorType.Ibeam); }
        var pointer = _pointer; _pointer = null; _releasing = true;
        try { if (pointer?.Captured == context.Surface) pointer.Capture(null); }
        finally { _releasing = false; }
        context.InvalidateVisual();
    }
    private TableCellVisual? ResizeCell(Point point) => context.Surface.GeometryTableCells().LastOrDefault(cell =>
        cell.VisibleBounds.Inflate(5).Contains(point) && ResizeAxis(cell, point) is not null);
    private static TableResizeAxis? ResizeAxis(TableCellVisual cell, Point point)
    {
        if (cell.Table.ColumnCount > 1 && cell.Column + cell.Cell.ColumnSpan < cell.Table.ColumnCount && Math.Abs(point.X - cell.Bounds.Right) <= 5) return TableResizeAxis.Column;
        if (Math.Abs(point.Y - cell.Bounds.Bottom) <= 5) return TableResizeAxis.Row;
        return null;
    }
    public void Render(DrawingContext drawing)
    {
        var selection = context.Editor.CellSelection;
        foreach (var cell in context.Surface.GeometryTableCells())
            if (selection is not null && selection.TableId == cell.Table.Id && selection.Contains(cell.Row, cell.Column))
                drawing.FillRectangle(context.Editor.SelectionBrush, cell.VisibleBounds);
        if (_hover is not { } hover || context.Session.IsReadOnly) return;
        var current = context.Surface.GeometryTableCells().LastOrDefault(c => c.Table.Id == hover.TableId && c.Row == hover.Row && c.Column == hover.Column);
        if (current is null) return;
        var bounds = current.VisibleBounds;
        var pen = new Pen(Brushes.DodgerBlue, 2);
        if (current.Column + current.Cell.ColumnSpan < current.Table.ColumnCount) drawing.DrawLine(pen, bounds.TopRight, bounds.BottomRight);
        drawing.DrawLine(pen, bounds.BottomLeft, bounds.BottomRight);
    }
    public void Dispose() { context.Session.Changed -= SessionChanged; Cancel(); _hover = null; }
}
