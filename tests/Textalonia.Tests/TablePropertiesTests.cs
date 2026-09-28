using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Layout;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class TablePropertiesTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);

    [Fact]
    public Task Properties_dialog_applies_selected_cells_and_rows_as_one_undo() => Run(() =>
    {
        var table = Table.Create(3, 2);
        var original = new FlowDocument([table]);
        var editor = new TextaloniaEditor { Document = original };
        editor.SelectTableCells(table.Id, 1, 0, 2, 0);
        var selected = editor.CellSelection;
        var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout();
        try
        {
            var pending = editor.ShowTablePropertiesDialogAsync(); var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<ComboBox>(dialog, "Table sizing").SelectedItem = TableAutoFit.Fixed;
            Find<ComboBox>(dialog, "Table width unit").SelectedItem = TableWidthUnit.Absolute;
            Find<NumericUpDown>(dialog, "Table preferred width").Value = 320;
            Find<ComboBox>(dialog, "Table alignment").SelectedItem = TableAlignment.Center;
            Find<CheckBox>(dialog, "Right-to-left column order").IsChecked = true;
            Find<NumericUpDown>(dialog, "Repeating header rows").Value = 1;
            Find<ComboBox>(dialog, "Cell vertical alignment").SelectedItem = TableCellVerticalAlignment.Bottom;
            Find<ComboBox>(dialog, "Cell text direction").SelectedItem = TableCellTextDirection.RightToLeft;
            Find<CheckBox>(dialog, "Allow selected rows to split across pages").IsChecked = false;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.True(pending.GetAwaiter().GetResult()); Assert.Equal(selected, editor.CellSelection);
            var updated = Assert.IsType<Table>(editor.Document.Blocks[0]);
            Assert.Equal(new TablePreferredWidth(TableWidthUnit.Absolute, 320), updated.PreferredWidth);
            Assert.Equal(TableAutoFit.Fixed, updated.AutoFit); Assert.Equal(TableAlignment.Center, updated.Alignment);
            Assert.True(updated.RightToLeft); Assert.Equal(1, updated.RepeatHeaderRows);
            Assert.True(updated.RowSizing[0].AllowSplit); Assert.False(updated.RowSizing[1].AllowSplit); Assert.False(updated.RowSizing[2].AllowSplit);
            Assert.Equal(TableCellVerticalAlignment.Bottom, updated.Rows[1][0].VerticalAlignment);
            Assert.Equal(TableCellTextDirection.RightToLeft, updated.Rows[2][0].TextDirection);
            Assert.Equal(TableCellVerticalAlignment.Top, updated.Rows[1][1].VerticalAlignment);
            editor.Undo(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    });

    [Fact]
    public Task Invalid_cancelled_stale_and_readonly_dialogs_do_not_edit() => Run(() =>
    {
        var table = Table.Create(2, 2); var original = new FlowDocument([table]);
        var editor = new TextaloniaEditor { Document = original };
        var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout();
        try
        {
            var pending = editor.ShowTablePropertiesDialogAsync(); var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<ComboBox>(dialog, "Table width unit").SelectedItem = TableWidthUnit.Percentage;
            Find<NumericUpDown>(dialog, "Table preferred width").Value = 101;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.False(pending.IsCompleted); Assert.Same(original, editor.Document);
            Find<NumericUpDown>(dialog, "Table preferred width").Value = 80;
            editor.InsertText("changed"); var changed = editor.Document;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.False(pending.IsCompleted); Assert.Same(changed, editor.Document);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("Close and reopen") == true);
            dialog.Close(false); Dispatcher.UIThread.RunJobs(); Assert.False(pending.GetAwaiter().GetResult());
            editor.IsReadOnly = true;
            Assert.False(editor.ShowTablePropertiesDialogAsync().GetAwaiter().GetResult()); Assert.Empty(owner.OwnedWindows);
            editor.SetTableAutoFit(TableAutoFit.Content); editor.SetTableRepeatHeaderRows(1); editor.SetTableRowsAllowSplit(false);
            Assert.Same(changed, editor.Document);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    });

    [Fact]
    public Task Row_height_changes_and_resize_preserve_split_policy() => Run(() =>
    {
        var table = Table.Create(2, 2) with { RowSizing = [new() { AllowSplit = false }, new()] };
        var editor = new TextaloniaEditor { Document = new([table]) };
        editor.SetTableRowHeight(70);
        Assert.False(editor.FindTable(table.Id)!.RowSizing[0].AllowSplit);
        Assert.True(editor.BeginTableResize(table.Id, TableResizeAxis.Row, 0, 70));
        editor.PreviewTableResize(90); Assert.True(editor.CommitTableResize());
        Assert.False(editor.FindTable(table.Id)!.RowSizing[0].AllowSplit);
    });

    [Fact]
    public Task Styles_dialog_authors_bands_and_preserves_other_conditional_properties() => Run(() =>
    {
        var style = new TableStyleDefinition { Id = "Bands", Conditions = ImmutableDictionary<TableStyleRegion, TableStyleOverrides>.Empty
            .Add(TableStyleRegion.HeaderRow, new() { Padding = new EdgeInsets(7, 7, 7, 7) }) };
        var original = new FlowDocument([Table.Create(2, 2)]) { Styles = new() { Tables = ImmutableDictionary<string, TableStyleDefinition>.Empty.Add(style.Id, style) } };
        var editor = new TextaloniaEditor { Document = original };
        var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout();
        try
        {
            var pending = editor.ShowStylesDialogAsync(); var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            var choices = Find<ComboBox>(dialog, "Named style"); choices.SelectedItem = choices.Items.Cast<object>().Single(item => item.ToString() == "Bands (Table)");
            Find<TextBox>(dialog, "Header row shading (blank inherits)").Text = "#112233";
            Find<TextBox>(dialog, "Odd row band shading (blank inherits)").Text = "#eeeeee";
            Click(dialog, "Save style"); Dispatcher.UIThread.RunJobs(); Assert.True(pending.GetAwaiter().GetResult());
            var updated = editor.Document.Styles.Tables["Bands"];
            Assert.Equal("#112233", updated.Conditions[TableStyleRegion.HeaderRow].Background.Value);
            Assert.Equal(new EdgeInsets(7, 7, 7, 7), updated.Conditions[TableStyleRegion.HeaderRow].Padding.Value);
            Assert.Equal("#eeeeee", updated.Conditions[TableStyleRegion.OddRowBand].Background.Value);
            editor.Undo(); Assert.Same(original, editor.Document);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    });

    [Fact]
    public void Wrap_geometry_resolves_overlapping_blockers_and_advances_when_full()
    {
        var column = new Rect(20, 20, 300, 400);
        var free = WrapExclusionGeometry.Resolve(column, 30, 20, [new(20, 20, 100, 100), new(80, 20, 80, 100)]);
        Assert.Equal(new Rect(160, 30, 160, 20), free);
        var below = WrapExclusionGeometry.Resolve(column, 30, 20, [new(20, 20, 300, 100), new(20, 80, 300, 100)]);
        Assert.Equal(new Rect(20, 180, 300, 20), below);
        Assert.Equal(new Rect(20, 30, 300, 20), WrapExclusionGeometry.Resolve(column, 30, 20, [new(500, 20, 20, 20)]));
    }

    [Fact]
    public void Cropped_clipboard_tables_only_retain_headers_inside_the_copied_rows()
    {
        var table = Table.Create(4, 2) with { RepeatHeaderRows = 2, AutoFit = TableAutoFit.Fixed, PreferredWidth = new(TableWidthUnit.Absolute, 280) };
        var source = new FlowDocument([table]);
        var headerSlice = DocumentFragments.ExtractCells(source, table.Id, 1, 0, 2, 2);
        var first = Assert.IsType<Table>(Assert.Single(headerSlice.Document.Blocks));
        Assert.Equal(1, first.RepeatHeaderRows); Assert.Equal(table.PreferredWidth, first.PreferredWidth);
        Assert.NotEqual(table.Id, first.Id); headerSlice.Validate();
        var bodySlice = DocumentFragments.ExtractCells(source, table.Id, 3, 0, 1, 1);
        Assert.Equal(0, Assert.IsType<Table>(Assert.Single(bodySlice.Document.Blocks)).RepeatHeaderRows);
        bodySlice.Validate();
    }

    [Fact]
    public Task Cell_commands_update_sparse_overrides_and_explicit_top_beats_table_alignment() => Run(() =>
    {
        var table = Table.Create(1, 1) with { StyleOverrides = new() { VerticalAlignment = TableCellVerticalAlignment.Bottom } };
        table = table.SetCell(0, 0, table.Rows[0][0] with { StyleOverrides = new()
        { Background = "#112233", Padding = new EdgeInsets(4, 4, 4, 4), Borders = new BlockBorders(Left: new(2, "#112233")) } });
        var editor = new TextaloniaEditor { Document = new([table]) };
        editor.SetTableCellBackground("#ffffff"); editor.SetTableCellPadding(null); editor.SetTableCellBorders(new());
        editor.SetTableCellVerticalAlignment(TableCellVerticalAlignment.Top);
        var cell = new DocumentStyleResolver(editor.Document).ResolveTableCell(editor.FindTable(table.Id)!, 0, 0);
        Assert.Equal("#ffffff", cell.Background); Assert.Null(cell.Padding); Assert.Equal(new BlockBorders(), cell.Borders);
        Assert.Equal(TableCellVerticalAlignment.Top, cell.VerticalAlignment);
    });

    private static T Find<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
    private static void Click(Window dialog, string content) => dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
