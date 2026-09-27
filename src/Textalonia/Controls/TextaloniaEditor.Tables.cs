using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

public enum TableResizeAxis { Column, Row }

/// <summary>A rectangular selection expanded to include every intersecting merged cell.</summary>
public sealed record TableCellSelection(Guid TableId, int AnchorRow, int AnchorColumn, int ActiveRow, int ActiveColumn,
    int Row, int Column, int RowCount, int ColumnCount)
{
    public bool Contains(int row, int column) => row >= Row && row < Row + RowCount && column >= Column && column < Column + ColumnCount;
}

public partial class TextaloniaEditor
{
    private sealed record TableResize(FlowDocument Document, int Revision, Table Table, TableResizeAxis Axis, int Index, double InitialSize);
    private TableResize? _tableResize;
    private FlowDocument? _tablePreviewDocument;
    private TableCellSelection? _cellSelection;
    private bool _tableEventsAttached;
    private TextSelection _tableTextSelection;
    private int _tableRevision;
    internal event EventHandler? TableCellSelectionChanged;

    /// <summary>Formatting shared by the text selection or by every paragraph in selected table cells.</summary>
    public SelectionFormattingState FormattingState
    {
        get
        {
            if (_cellSelection is not { } selected || FindTable(selected.TableId) is not { } table ||
                selected.Row + selected.RowCount > table.Rows.Length || selected.Column + selected.ColumnCount > table.ColumnCount) return Session.FormattingState;
            var text = new List<TextStyle>(); var paragraphs = new List<ParagraphStyle>();
            var resolver = new DocumentStyleResolver(Session.Document);
            for (var row = selected.Row; row < selected.Row + selected.RowCount; row++)
                for (var column = selected.Column; column < selected.Column + selected.ColumnCount; column++)
                {
                    if (table.IsCovered(row, column)) continue;
                    var location = Session.Index.Tree.Locate(table.Rows[row][column].Id);
                    var end = location.Start + location.Node.Length;
                    foreach (var entry in Session.Index.Enumerate(location.Start, Math.Min(end, Session.Index.Length)))
                    {
                        if (entry.Start >= end) break;
                        paragraphs.Add(resolver.ResolveParagraphStyle(entry.Paragraph.Style));
                        if (entry.Paragraph.Runs.IsEmpty) text.Add(resolver.ResolveText(entry.Paragraph, entry.Paragraph.DefaultStyle));
                        else text.AddRange(entry.Paragraph.Runs.Select(run => resolver.ResolveText(entry.Paragraph, run.Style)));
                    }
                }
            return new(text, paragraphs);
        }
    }

    private void FormatSelectedCellParagraphs(Func<Paragraph, Paragraph> change) => ApplySelectedCells((table, row, column) =>
    {
        var cell = table.Rows[row][column];
        var blocks = new FlowDocument(cell.Blocks).RewriteParagraphs(paragraph => [change(paragraph)]).Blocks;
        return table.SetCell(row, column, cell with { Blocks = blocks });
    });
    private void ApplySelectedTextStyle(Func<TextStyle, TextStyle> change) => FormatSelectedCellParagraphs(paragraph =>
        paragraph.Format(0, paragraph.Length, value => ChangeCellTextStyle(paragraph, value, change)) with
        { DefaultStyle = ChangeCellTextStyle(paragraph, paragraph.DefaultStyle, change) });
    public void ApplyParagraphStyle(Func<ParagraphStyle, ParagraphStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (_cellSelection is null) Session.ApplyParagraphStyle(change);
        else FormatSelectedCellParagraphs(paragraph =>
        {
            var effective = new DocumentStyleResolver(Session.Document).ResolveParagraphStyle(paragraph.Style);
            var changed = change(effective);
            return paragraph with { Style = paragraph.Style.Overrides is null ? changed : paragraph.Style with
            { Overrides = ParagraphStyleOverrides.Difference(effective, changed, paragraph.Style.Overrides) } };
        });
    }
    private TextStyle ChangeCellTextStyle(Paragraph paragraph, TextStyle stored, Func<TextStyle, TextStyle> change)
    {
        var effective = new DocumentStyleResolver(Session.Document).ResolveText(paragraph, stored);
        var changed = change(effective);
        return stored.Overrides is null ? changed : stored with
        { Overrides = TextStyleOverrides.Difference(effective, changed, stored.Overrides) };
    }
    private void ToggleSelectedBold()
    {
        var state = FormattingState.Bold; var enabled = state.IsMixed || !state.Value;
        ApplyStyle(style => style with { Bold = enabled, FontWeight = null });
    }
    private void ToggleSelectedItalic()
    {
        var state = FormattingState.Italic; var enabled = state.IsMixed || !state.Value;
        ApplyStyle(style => style with { Italic = enabled });
    }
    private void ToggleSelectedUnderline()
    {
        var state = FormattingState.Underline; var enabled = state.IsMixed || !state.Value;
        ApplyStyle(style => style with { Underline = enabled, UnderlineKind = UnderlineKind.None });
    }
    private void ToggleSelectedStrikethrough()
    {
        var state = FormattingState.Strikethrough; var enabled = state.IsMixed || !state.Value;
        ApplyStyle(style => style with { Strikethrough = enabled, StrikeKind = StrikeKind.None });
    }
    internal void SetSelectedHeading(int level)
    {
        if (_cellSelection is null) { Session.SetHeading(level); return; }
        if (level is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(level));
        var size = level switch { 1 => 32, 2 => 26, 3 => 22, 4 => 20, 5 => 18, _ => 16 };
        TextStyle Change(TextStyle style) => style with { FontSize = size, Bold = level > 0, FontWeight = null };
        FormatSelectedCellParagraphs(paragraph =>
        {
            var effective = new DocumentStyleResolver(Session.Document).ResolveParagraphStyle(paragraph.Style);
            var changed = effective with { HeadingLevel = level, SpaceBefore = level > 0 ? 12 : 0 };
            return paragraph.Format(0, paragraph.Length, value => ChangeCellTextStyle(paragraph, value, Change)) with
            {
                DefaultStyle = ChangeCellTextStyle(paragraph, paragraph.DefaultStyle, Change),
                Style = paragraph.Style.Overrides is null ? changed : paragraph.Style with
                { Overrides = ParagraphStyleOverrides.Difference(effective, changed, paragraph.Style.Overrides) }
            };
        });
    }
    internal void ToggleSelectedList(ListKind kind)
    {
        if (_cellSelection is null) { Session.ToggleList(kind); return; }
        var state = FormattingState.List;
        if (!state.IsMixed && state.Value == kind) kind = ListKind.None;
        var identity = kind == ListKind.None ? (Guid?)null : Guid.NewGuid();
        ApplyParagraphStyle(style => style with { List = kind, ListId = identity, ListLevel = 0, ListDefinition = null, ListStart = null, ListRestart = false });
    }
    internal void RestartSelectedList()
    {
        if (_cellSelection is null) { Session.RestartList(); return; }
        var first = true;
        ApplyParagraphStyle(style =>
        {
            if (style.List == ListKind.None || !first) return style;
            first = false; return style with { ListRestart = true, ListStart = 1 };
        });
    }

    public TableCellSelection? CellSelection => _cellSelection;
    public bool IsResizingTable => _tableResize is not null;
    internal FlowDocument? TablePreviewDocument => _tablePreviewDocument is not { } preview ? null :
        ActiveStoryId == Guid.Empty ? preview : Session.Document with
        { Stories = Session.Document.Stories.SetItem(ActiveStoryId, Session.Document.Stories[ActiveStoryId] with { Blocks = preview.Blocks }) };

    private void AttachTableEvents()
    {
        if (_tableEventsAttached) return;
        _tableEventsAttached = true;
        _tableTextSelection = Session.Selection; _tableRevision = Session.Revision;
        Session.Changed += (_, _) =>
        {
            if (_tableResize is { } resize && (Session.IsReadOnly || Session.Revision != resize.Revision)) CancelTableResize();
            if (_cellSelection is not null && (_tableTextSelection != Session.Selection || _tableRevision != Session.Revision)) ClearTableCellSelection();
            _tableTextSelection = Session.Selection; _tableRevision = Session.Revision;
        };
    }

    internal Table? FindTable(Guid id) => Session.Index.Tree.Paths?.Find(id) is null ? null : Session.Index.Tree.Locate(id).Node.Source as Table;

    /// <summary>Selects cells in an explicitly identified table, including nested tables, without editing the document.</summary>
    public void SelectTableCells(Guid tableId, int anchorRow, int anchorColumn, int activeRow, int activeColumn)
    {
        AttachTableEvents();
        var table = FindTable(tableId) ?? throw new ArgumentException("The table is not in this document.", nameof(tableId));
        if (anchorRow < 0 || anchorRow >= table.Rows.Length || activeRow < 0 || activeRow >= table.Rows.Length ||
            anchorColumn < 0 || anchorColumn >= table.ColumnCount || activeColumn < 0 || activeColumn >= table.ColumnCount)
            throw new ArgumentOutOfRangeException(nameof(activeRow));
        var top = Math.Min(anchorRow, activeRow); var bottom = Math.Max(anchorRow, activeRow);
        var left = Math.Min(anchorColumn, activeColumn); var right = Math.Max(anchorColumn, activeColumn);
        bool expanded;
        do
        {
            expanded = false;
            for (var r = top; r <= bottom; r++)
                for (var c = left; c <= right; c++)
                {
                    var owner = table.OwnerOf(r, c); var cell = table.Rows[owner.Row][owner.Column];
                    var nextTop = Math.Min(top, owner.Row); var nextLeft = Math.Min(left, owner.Column);
                    var nextBottom = Math.Max(bottom, owner.Row + cell.RowSpan - 1); var nextRight = Math.Max(right, owner.Column + cell.ColumnSpan - 1);
                    expanded |= nextTop != top || nextLeft != left || nextBottom != bottom || nextRight != right;
                    top = nextTop; left = nextLeft; bottom = nextBottom; right = nextRight;
                }
        } while (expanded);
        var activeOwner = table.OwnerOf(activeRow, activeColumn);
        var caret = Session.Index.Tree.Locate(table.Rows[activeOwner.Row][activeOwner.Column].Id).Start;
        Session.Select(caret, caret);
        _cellSelection = new(tableId, anchorRow, anchorColumn, activeRow, activeColumn, top, left, bottom - top + 1, right - left + 1);
        _tableTextSelection = Session.Selection; _tableRevision = Session.Revision;
        foreach (var command in _commands) command.RaiseCanExecuteChanged();
        TableCellSelectionChanged?.Invoke(this, EventArgs.Empty);
        _surface?.InvalidateVisual();
    }

    public void ClearTableCellSelection()
    {
        _cellSelection = null;
        foreach (var command in _commands) command.RaiseCanExecuteChanged();
        TableCellSelectionChanged?.Invoke(this, EventArgs.Empty);
        _surface?.InvalidateVisual();
    }
    public void SelectCurrentTableCell()
    {
        if (Session.CurrentCell() is { } cell) SelectTableCells(cell.Table.Id, cell.Row, cell.Column, cell.Row, cell.Column);
    }

    /// <summary>Keyboard equivalent of extending a rectangular pointer selection.</summary>
    public void ExtendTableCellSelection(int rows, int columns)
    {
        if (_cellSelection is null) SelectCurrentTableCell();
        if (_cellSelection is not { } selection || FindTable(selection.TableId) is not { } table) return;
        SelectTableCells(selection.TableId, selection.AnchorRow, selection.AnchorColumn,
            Math.Clamp(selection.ActiveRow + rows, 0, table.Rows.Length - 1), Math.Clamp(selection.ActiveColumn + columns, 0, table.ColumnCount - 1));
    }

    internal (Table Table, int Row, int Column)? TableTarget() => _cellSelection is { } selection && FindTable(selection.TableId) is { } table
        ? (table, selection.Row, selection.Column) : Session.CurrentCell();

    private void ChangeTargetTable(Func<Table, int, int, Table> update)
    {
        if (Session.IsReadOnly || TableTarget() is not { } target) return;
        CancelTableResize();
        var selected = _cellSelection;
        var table = update(target.Table, target.Row, target.Column);
        if (!ReferenceEquals(table, target.Table)) Session.Execute(d => d.ReplaceBlock(target.Table.Id, table));
        if (selected is not null) SelectTableCells(table.Id, Math.Min(selected.AnchorRow, table.Rows.Length - 1), Math.Min(selected.AnchorColumn, table.ColumnCount - 1),
            Math.Min(selected.ActiveRow, table.Rows.Length - 1), Math.Min(selected.ActiveColumn, table.ColumnCount - 1));
    }

    public void InsertTableRow(bool after = true) => ChangeTargetTable((table, row, column) =>
        table.InsertRow(after ? (_cellSelection is { } cells ? cells.Row + cells.RowCount : row + table.Rows[row][column].RowSpan) : row));
    public void InsertTableColumn(bool after = true) => ChangeTargetTable((table, row, column) =>
        table.InsertColumn(after ? (_cellSelection is { } cells ? cells.Column + cells.ColumnCount : column + table.Rows[row][column].ColumnSpan) : column));
    public void DeleteTableRows() => ChangeTargetTable((table, row, column) =>
    {
        var count = _cellSelection?.RowCount ?? table.Rows[row][column].RowSpan;
        if (count == table.Rows.Length) throw new InvalidOperationException("Use Delete table to remove every row.");
        for (var i = 0; i < count; i++) table = table.RemoveRow(row);
        return table;
    });
    public void DeleteTableColumns() => ChangeTargetTable((table, row, column) =>
    {
        var count = _cellSelection?.ColumnCount ?? table.Rows[row][column].ColumnSpan;
        if (count == table.ColumnCount) throw new InvalidOperationException("Use Delete table to remove every column.");
        for (var i = 0; i < count; i++) table = table.RemoveColumn(column);
        return table;
    });

    public void MergeSelectedTableCells() => ChangeTargetTable((table, row, column) =>
    {
        if (_cellSelection is not { } selection || selection.RowCount == 1 && selection.ColumnCount == 1) return table;
        for (var r = selection.Row; r < selection.Row + selection.RowCount; r++)
            for (var c = selection.Column; c < selection.Column + selection.ColumnCount; c++) table = table.SplitCell(r, c);
        return table.MergeCells(row, column, selection.RowCount, selection.ColumnCount);
    });
    public void SplitSelectedTableCells() => ApplySelectedCells((table, row, column) => table.SplitCell(row, column));

    private void ApplySelectedCells(Func<Table, int, int, Table> update) => ChangeTargetTable((table, row, column) =>
    {
        if (_cellSelection is not { } selection) return update(table, row, column);
        var owners = new HashSet<(int Row, int Column)>();
        for (var r = selection.Row; r < selection.Row + selection.RowCount; r++)
            for (var c = selection.Column; c < selection.Column + selection.ColumnCount; c++) owners.Add(table.OwnerOf(r, c));
        foreach (var owner in owners.OrderBy(owner => owner.Row).ThenBy(owner => owner.Column)) table = update(table, owner.Row, owner.Column);
        return table;
    });
    /// <summary>Clears selected cells and their merge backups as one edit, retaining the table grid.</summary>
    public void ClearSelectedTableCellContents()
    {
        if (_cellSelection is not { } selected) return;
        ChangeTargetTable((table, _, _) =>
        {
            for (var r = selected.Row; r < selected.Row + selected.RowCount; r++)
                for (var c = selected.Column; c < selected.Column + selected.ColumnCount; c++)
                    table = table.SetCell(r, c, table.Rows[r][c] with { Blocks = [new Paragraph()], MergeOriginalBlocks = [] });
            return table;
        });
    }

    public void DeleteSelectedTable()
    {
        if (Session.IsReadOnly || TableTarget() is not { } target) return;
        ImmutableArray<Block> Remove(ImmutableArray<Block> blocks) => FlowDocument.EnsureBlocks(blocks.Where(b => b.Id != target.Table.Id)
            .Select(b => b switch
            {
                Section section => section with { Blocks = Remove(section.Blocks) },
                Table table => table with { Rows = table.Rows.Select((row, r) => row.Select((cell, c) =>
                    table.IsCovered(r, c) ? cell : cell with { Blocks = Remove(cell.Blocks) }).ToImmutableArray()).ToImmutableArray() },
                _ => b
            }).ToImmutableArray());
        CancelTableResize(); Session.Execute(document => document with { Blocks = Remove(document.Blocks) });
    }

    public void SetTableCellPadding(EdgeInsets? padding) => ApplySelectedCells((table, row, column) => table.SetCell(row, column, table.Rows[row][column] with { Padding = padding }));
    public void SetTableCellBorders(BlockBorders? borders) => ApplySelectedCells((table, row, column) => table.SetCell(row, column, table.Rows[row][column] with { Borders = borders }));
    public void SetTableCellBackground(string? background) => ApplySelectedCells((table, row, column) => table.SetCell(row, column, table.Rows[row][column] with { Background = background }));

    /// <summary>Sets a positive relative column width.</summary>
    public void SetTableColumnWidth(double width) => ChangeTargetTable((table, _, column) =>
    {
        if (!double.IsFinite(width) || width <= 0 || width > 100000) throw new ArgumentOutOfRangeException(nameof(width));
        var widths = table.ColumnWidths.IsEmpty ? Enumerable.Repeat(1d, table.ColumnCount).ToImmutableArray() : table.ColumnWidths;
        return table with { ColumnWidths = widths.SetItem(column, width) };
    });
    public void SetTableRowHeight(double height, TableRowHeightMode mode = TableRowHeightMode.AtLeast) => ChangeTargetTable((table, row, _) =>
    {
        var sizing = table.RowSizing.IsEmpty ? Enumerable.Range(0, table.Rows.Length).Select(_ => new TableRowSizing()).ToImmutableArray() : table.RowSizing;
        return table with { RowSizing = sizing.SetItem(row, new() { Height = height, Mode = mode }) };
    });

    /// <summary>Keyboard/toolbar equivalent of dragging a current cell edge by a distance in device-independent pixels.</summary>
    public bool ResizeCurrentTableTrack(TableResizeAxis axis, double delta)
    {
        if (Session.IsReadOnly || _surface is null || TableTarget() is not { } target) return false;
        if (!double.IsFinite(delta)) throw new ArgumentOutOfRangeException(nameof(delta));
        _surface.EnsureLayout(_surface.Bounds.Width);
        var cell = _surface.GeometryTableCells().FirstOrDefault(cell => cell.Table.Id == target.Table.Id && cell.Row == target.Row && cell.Column == target.Column);
        if (cell is null) return false;
        var size = axis == TableResizeAxis.Column ? cell.ColumnWidth : cell.RowHeight;
        var index = axis == TableResizeAxis.Column ? cell.Column + cell.Cell.ColumnSpan - 1 : cell.Row + cell.Cell.RowSpan - 1;
        if (!BeginTableResize(target.Table.Id, axis, index, size)) return false;
        PreviewTableResize(size + delta); return CommitTableResize();
    }

    /// <summary>Starts a resize without editing the session. Initial size is the rendered track size in device-independent pixels.</summary>
    public bool BeginTableResize(Guid tableId, TableResizeAxis axis, int index, double initialSize)
    {
        if (Session.IsReadOnly) return false;
        if (!Enum.IsDefined(axis) || !double.IsFinite(initialSize) || initialSize <= 0) throw new ArgumentOutOfRangeException(nameof(initialSize));
        var table = FindTable(tableId) ?? throw new ArgumentException("The table is not in this document.", nameof(tableId));
        if (index < 0 || index >= (axis == TableResizeAxis.Column ? table.ColumnCount : table.Rows.Length)) throw new ArgumentOutOfRangeException(nameof(index));
        if (axis == TableResizeAxis.Column && table.ColumnCount == 1) return false;
        AttachTableEvents(); CancelTableResize();
        _tableResize = new(Session.ActiveDocument, Session.Revision, table, axis, index, initialSize);
        return true;
    }
    public void PreviewTableResize(double size)
    {
        if (_tableResize is not { } resize) return;
        if (Session.IsReadOnly || Session.Revision != resize.Revision) { CancelTableResize(); return; }
        if (!double.IsFinite(size)) throw new ArgumentOutOfRangeException(nameof(size));
        size = Math.Clamp(size, 1, 100000);
        if (Math.Abs(size - resize.InitialSize) < .001) _tablePreviewDocument = null;
        else
        {
            var table = resize.Table;
            if (resize.Axis == TableResizeAxis.Column)
            {
                var widths = table.ColumnWidths.IsEmpty ? Enumerable.Repeat(1d, table.ColumnCount).ToImmutableArray() : table.ColumnWidths;
                var weight = widths[resize.Index]; var requested = weight * size / resize.InitialSize;
                var adjacent = resize.Index + 1 < table.ColumnCount ? resize.Index + 1 : resize.Index - 1;
                if (adjacent >= 0)
                {
                    var total = weight + widths[adjacent];
                    requested = Math.Clamp(requested, Math.Max(total / 1000, total - 100000), Math.Min(total * .999, 100000));
                    widths = widths.SetItem(adjacent, total - requested).SetItem(resize.Index, requested);
                }
                table = table with { ColumnWidths = widths };
            }
            else
            {
                var rows = table.RowSizing.IsEmpty ? Enumerable.Range(0, table.Rows.Length).Select(_ => new TableRowSizing()).ToImmutableArray() : table.RowSizing;
                table = table with { RowSizing = rows.SetItem(resize.Index, new() { Mode = rows[resize.Index].Mode == TableRowHeightMode.Exact ? TableRowHeightMode.Exact : TableRowHeightMode.AtLeast, Height = size }) };
            }
            _tablePreviewDocument = resize.Document.ReplaceBlock(table.Id, table);
        }
        _surface?.Refresh(invalidateLayout: true);
    }
    public bool CommitTableResize()
    {
        var resize = _tableResize; var preview = _tablePreviewDocument;
        _tableResize = null; _tablePreviewDocument = null;
        if (resize is null || preview is null || Session.IsReadOnly || Session.Revision != resize.Revision)
        { _surface?.Refresh(); return false; }
        Session.Execute(_ => preview); return true;
    }
    public void CancelTableResize()
    {
        var hadPreview = _tablePreviewDocument is not null;
        _tableResize = null; _tablePreviewDocument = null;
        if (hadPreview) _surface?.Refresh();
    }
    public void ContinuePreviousList()
    {
        var selected = Session.Index.At(Session.Selection.Start);
        var resolver = new DocumentStyleResolver(Session.Document);
        var previous = Session.Index.Enumerate(0, selected.Start).Where(p => p.Start < selected.Start)
            .Select(p => resolver.ResolveParagraphStyle(p.Paragraph.Style)).LastOrDefault(s => s.List != ListKind.None && s.ListId is not null);
        if (previous?.ListId is not { } identity) return;
        if (_cellSelection is null) Session.ContinueList(identity);
        else ApplyParagraphStyle(style => style with
        { List = previous.List, ListDefinition = previous.ListDefinition, ListId = identity, ListRestart = false, ListStart = null });
    }
}
