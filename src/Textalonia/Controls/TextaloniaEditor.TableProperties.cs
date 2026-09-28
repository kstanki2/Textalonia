using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    public void SetTablePreferredWidth(TablePreferredWidth width) => ChangeTargetTable((table, _, _) => table with { PreferredWidth = width });
    public void SetTableAutoFit(TableAutoFit mode) => ChangeTargetTable((table, _, _) => table with { AutoFit = mode });
    public void SetTableAlignment(TableAlignment alignment, double indent = 0) =>
        ChangeTargetTable((table, _, _) => table with { Alignment = alignment, Indent = indent });
    public void SetTableRightToLeft(bool rightToLeft) => ChangeTargetTable((table, _, _) => table with { RightToLeft = rightToLeft });
    public void SetTableRepeatHeaderRows(int count) => ChangeTargetTable((table, _, _) => table with { RepeatHeaderRows = count });
    public void ApplyNamedTableStyle(string? styleId) => ChangeTargetTable((table, _, _) => table with { StyleId = styleId });
    public void SetTableCellPreferredWidth(TablePreferredWidth width) =>
        ApplySelectedCells((table, row, column) => table.SetCell(row, column, table.Rows[row][column] with { PreferredWidth = width }));
    public void SetTableCellVerticalAlignment(TableCellVerticalAlignment alignment) =>
        ApplySelectedCells((table, row, column) => table.SetCell(row, column, table.Rows[row][column] with { VerticalAlignment = alignment, StyleOverrides = (table.Rows[row][column].StyleOverrides ?? new()) with { VerticalAlignment = alignment } }));
    public void SetTableCellTextDirection(TableCellTextDirection direction) =>
        ApplySelectedCells((table, row, column) => table.SetCell(row, column, table.Rows[row][column] with { TextDirection = direction, StyleOverrides = (table.Rows[row][column].StyleOverrides ?? new()) with { TextDirection = direction } }));

    /// <summary>Sets page splitting for each selected row, or every row spanned by the current cell.</summary>
    public void SetTableRowsAllowSplit(bool allowSplit) => ChangeTargetTable((table, row, column) =>
    {
        var sizes = table.RowSizing.IsEmpty ? Enumerable.Repeat(new TableRowSizing(), table.Rows.Length).ToImmutableArray() : table.RowSizing;
        var count = CellSelection?.RowCount ?? table.Rows[row][column].RowSpan;
        for (var r = row; r < row + count; r++) sizes = sizes.SetItem(r, sizes[r] with { AllowSplit = allowSplit });
        return table with { RowSizing = sizes };
    });

    /// <summary>Edits table layout and selected cell/row properties in one undoable operation.</summary>
    public Task<bool> ShowTablePropertiesDialogAsync()
    {
        if (Session.IsReadOnly || TableTarget() is not { } target) return Task.FromResult(false);
        var table = target.Table;
        var selection = CellSelection;
        var rowCount = selection?.RowCount ?? table.Rows[target.Row][target.Column].RowSpan;
        var columnCount = selection?.ColumnCount ?? table.Rows[target.Row][target.Column].ColumnSpan;
        var owners = new HashSet<(int Row, int Column)>();
        for (var r = target.Row; r < target.Row + rowCount; r++)
            for (var c = target.Column; c < target.Column + columnCount; c++) owners.Add(table.OwnerOf(r, c));
        void Cells(Func<TableCell, TableCell> change)
        {
            foreach (var (r, c) in owners) table = table.SetCell(r, c, change(table.Rows[r][c]));
        }
        var resolver = new DocumentStyleResolver(Session.ActiveDocument);
        FormattingValue<T> CellValue<T>(Func<TableCell, T> read)
        {
            var values = owners.Select(owner => read(resolver.ResolveTableCell(table, owner.Row, owner.Column))).Distinct().ToArray();
            return values.Length == 1 ? new(values[0]) : new(default!, true);
        }
        var dialog = new FormattingDialog("Table properties");
        dialog.Body.Children.Add(new TextBlock { Text = "Table layout applies to the whole table. Cell and row settings apply to the selected cells. Measurements use DIP (96 per inch).", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        dialog.EnumField("Table sizing", new FormattingValue<TableAutoFit>(table.AutoFit), value => table = table with { AutoFit = value });
        var preferredWidth = table.PreferredWidth;
        dialog.EnumField("Table width unit", new FormattingValue<TableWidthUnit>(preferredWidth.Unit), value => preferredWidth = preferredWidth with { Unit = value });
        dialog.NumberField("Table preferred width", new(preferredWidth.Value), 0, 100000, value => preferredWidth = preferredWidth with { Value = value });
        dialog.EnumField("Table alignment", new FormattingValue<TableAlignment>(table.Alignment), value => table = table with { Alignment = value });
        dialog.NumberField("Table indent (DIP)", new(table.Indent), 0, 100000, value => table = table with { Indent = value });
        dialog.Flag("Right-to-left column order", new(table.RightToLeft), value => table = table with { RightToLeft = value });
        dialog.NumberField("Repeating header rows", new(table.RepeatHeaderRows), 0, table.Rows.Length, value => table = table with { RepeatHeaderRows = (int)value });
        var positioned = table.Position is not null;
        var position = table.Position ?? new TablePosition();
        dialog.Flag("Position table on page", new(positioned), value => positioned = value);
        dialog.NumberField("Table left on column (DIP)", new(position.X), 0, 100000, value => position = position with { X = value });
        dialog.NumberField("Table top on column (DIP)", new(position.Y), 0, 100000, value => position = position with { Y = value });
        dialog.NumberField("Text wrap distance (DIP)", new(position.Distance), 0, 1000, value => position = position with { Distance = value });
        var style = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "(None)" }.Concat(Session.Document.Styles.Tables.Keys.OrderBy(id => id)).ToArray(), SelectedItem = table.StyleId ?? "(None)" };
        AutomationProperties.SetName(style, "Table style");
        dialog.Body.Children.Add(new TextBlock { Text = "Table style" }); dialog.Body.Children.Add(style);
        style.SelectionChanged += (_, _) => { if (style.SelectedItem is string id) table = table with { StyleId = id == "(None)" ? null : id }; };
        dialog.EnumField("Cell vertical alignment", CellValue(cell => cell.VerticalAlignment), value => Cells(cell => cell with { VerticalAlignment = value, StyleOverrides = (cell.StyleOverrides ?? new()) with { VerticalAlignment = value } }));
        dialog.EnumField("Cell text direction", CellValue(cell => cell.TextDirection), value => Cells(cell => cell with { TextDirection = value, StyleOverrides = (cell.StyleOverrides ?? new()) with { TextDirection = value } }));
        dialog.EnumField("Cell width unit", CellValue(cell => cell.PreferredWidth.Unit), value => Cells(cell => cell with { PreferredWidth = cell.PreferredWidth with { Unit = value } }));
        dialog.NumberField("Cell preferred width", CellValue(cell => cell.PreferredWidth.Value), 0, 100000,
            value => Cells(cell => cell with { PreferredWidth = cell.PreferredWidth with { Value = value } }));
        var rowPolicies = Enumerable.Range(target.Row, rowCount).Select(r => table.RowSizing.IsEmpty || table.RowSizing[r].AllowSplit).Distinct().ToArray();
        dialog.Flag("Allow selected rows to split across pages", rowPolicies.Length == 1 ? new(rowPolicies[0]) : new(default, true), value =>
        {
            var sizes = table.RowSizing.IsEmpty ? Enumerable.Repeat(new TableRowSizing(), table.Rows.Length).ToImmutableArray() : table.RowSizing;
            for (var r = target.Row; r < target.Row + rowCount; r++) sizes = sizes.SetItem(r, sizes[r] with { AllowSplit = value });
            table = table with { RowSizing = sizes };
        });
        return ShowFormattingDialog(dialog, () => ChangeTargetTable((_, _, _) => table with { PreferredWidth = preferredWidth, Position = positioned ? position : null }));
    }
}
