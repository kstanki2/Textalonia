using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class TableInteractionTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface) Create(FlowDocument document)
    {
        var editor = new TextaloniaEditor { Document = document, ShowToolbar = false };
        var window = new Window { Width = 700, Height = 500, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument(); Dispatcher.UIThread.RunJobs();
        var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
        surface.EnsureLayout(surface.Bounds.Width);
        return (window, editor, surface);
    }

    [Fact]
    public Task Many_resize_previews_commit_one_undo_and_cancel_restores_exact_snapshot() => Run(() =>
    {
        var table = Table.Create(2, 2); var original = new FlowDocument([table]);
        var editor = new TextaloniaEditor { Document = original };
        var revision = editor.Session.Revision;
        Assert.True(editor.BeginTableResize(table.Id, TableResizeAxis.Column, 0, 200));
        for (var size = 201; size <= 250; size++) editor.PreviewTableResize(size);
        Assert.Same(original, editor.Document); Assert.Equal(revision, editor.Session.Revision); Assert.False(editor.Session.CanUndo);
        var preview = Assert.IsType<Table>(editor.TablePreviewDocument!.Blocks[0]);
        Assert.Equal(1.25, preview.ColumnWidths[0]); Assert.Equal(.75, preview.ColumnWidths[1]);
        Assert.True(editor.CommitTableResize()); Assert.Equal(revision + 1, editor.Session.Revision);
        editor.Undo(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
        Assert.True(editor.BeginTableResize(table.Id, TableResizeAxis.Row, 0, 36));
        editor.PreviewTableResize(100); editor.CancelTableResize();
        Assert.Same(original, editor.Document); Assert.Null(editor.TablePreviewDocument); Assert.False(editor.IsResizingTable);
        Assert.True(editor.Session.CanRedo);
    });

    [Fact]
    public Task Resize_back_to_start_has_no_history_and_readonly_or_revision_change_cancels() => Run(() =>
    {
        var table = Table.Create(2, 2); var original = new FlowDocument([table]);
        var editor = new TextaloniaEditor { Document = original };
        editor.BeginTableResize(table.Id, TableResizeAxis.Column, 0, 200); editor.PreviewTableResize(250); editor.PreviewTableResize(200);
        Assert.False(editor.CommitTableResize()); Assert.False(editor.Session.CanUndo); Assert.Same(original, editor.Document);
        editor.BeginTableResize(table.Id, TableResizeAxis.Row, 0, 36); editor.PreviewTableResize(80);
        editor.IsReadOnly = true;
        Assert.False(editor.IsResizingTable); Assert.Null(editor.TablePreviewDocument); Assert.False(editor.CommitTableResize());
        Assert.False(editor.BeginTableResize(table.Id, TableResizeAxis.Row, 0, 36));
        editor.SetTableCellPadding(new(30, 30, 30, 30)); Assert.Same(original, editor.Document);
        editor.IsReadOnly = false;
        editor.BeginTableResize(table.Id, TableResizeAxis.Row, 0, 36); editor.PreviewTableResize(80); editor.InsertText("later");
        var edited = editor.Document;
        Assert.False(editor.CommitTableResize()); Assert.Same(edited, editor.Document); Assert.Contains("later", editor.Text);
    });

    [Fact]
    public Task Rectangular_selection_expands_spans_and_targets_nested_cells_without_touching_outer_table() => Run(() =>
    {
        var nested = Table.Create(3, 3).MergeCells(0, 0, 2, 2);
        var outer = Table.Create(1, 1);
        outer = outer.SetCell(0, 0, outer.Rows[0][0] with { Blocks = [new Paragraph("outer"), nested] });
        var original = new FlowDocument([outer]); var editor = new TextaloniaEditor { Document = original };
        editor.SelectTableCells(nested.Id, 2, 2, 1, 1);
        var selection = Assert.IsType<TableCellSelection>(editor.CellSelection);
        Assert.Equal((0, 0, 3, 3), (selection.Row, selection.Column, selection.RowCount, selection.ColumnCount));
        editor.SetTableCellPadding(new(1, 2, 3, 4));
        var updated = editor.FindTable(nested.Id)!;
        Assert.All(updated.Rows.SelectMany((row, r) => row.Where((_, c) => !updated.IsCovered(r, c))), cell => Assert.Equal(new EdgeInsets(1, 2, 3, 4), cell.Padding));
        Assert.Null(editor.FindTable(outer.Id)!.Rows[0][0].Padding); Assert.NotNull(editor.CellSelection);
        editor.Undo(); Assert.Same(original, editor.Document);
        editor.SelectTableCells(nested.Id, 0, 0, 0, 0); editor.DeleteSelectedTable();
        Assert.Null(editor.FindTable(nested.Id)); Assert.NotNull(editor.FindTable(outer.Id));
        editor.Undo(); Assert.Same(original, editor.Document);
    });

    [Fact]
    public Task Insert_after_merged_cell_uses_far_edge_and_rectangular_merge_is_one_undo() => Run(() =>
    {
        var table = Table.Create(3, 3).MergeCells(0, 0, 2, 2); var original = new FlowDocument([table]);
        var editor = new TextaloniaEditor { Document = original };
        editor.SelectTableCells(table.Id, 0, 0, 0, 0); editor.InsertTableColumn();
        var inserted = editor.FindTable(table.Id)!;
        Assert.Equal(4, inserted.ColumnCount); Assert.Equal(2, inserted.Rows[0][0].ColumnSpan);
        editor.Undo(); editor.SelectTableCells(table.Id, 0, 0, 2, 2); editor.MergeSelectedTableCells();
        Assert.Equal(3, editor.FindTable(table.Id)!.Rows[0][0].ColumnSpan);
        Assert.Equal(3, editor.FindTable(table.Id)!.Rows[0][0].RowSpan); editor.Document.Validate();
        editor.Undo(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
    });

    [Fact]
    public Task Pointer_resize_previews_then_commits_one_edit_and_escape_cancels_capture() => Run(() =>
    {
        var table = Table.Create(2, 2); var original = new FlowDocument([table]);
        var (window, editor, surface) = Create(original);
        try
        {
            var cell = surface.Layout.TableCells().First(c => c.Row == 0 && c.Column == 0);
            var start = surface.TranslatePoint(new Point(cell.Bounds.Right - 1, cell.Bounds.Center.Y), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            Assert.True(editor.IsResizingTable);
            window.MouseMove(start + new Vector(50, 0));
            Assert.NotNull(editor.TablePreviewDocument); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
            window.MouseUp(start + new Vector(50, 0), MouseButton.Left);
            Assert.False(editor.IsResizingTable); Assert.NotSame(original, editor.Document);
            editor.Undo(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
            window.UpdateLayout(); surface.EnsureLayout(surface.Bounds.Width);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(40, 0));
            Assert.True(editor.IsResizingTable);
            window.KeyPress(Key.Escape, RawInputModifiers.None);
            Assert.False(editor.IsResizingTable); Assert.Null(editor.TablePreviewDocument); Assert.Same(original, editor.Document);
            window.MouseUp(start + new Vector(40, 0), MouseButton.Left);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Alt_pointer_rectangle_matches_keyboard_extension_in_nested_table() => Run(() =>
    {
        var nested = Table.Create(2, 2); var outer = Table.Create(1, 1);
        outer = outer.SetCell(0, 0, outer.Rows[0][0] with { Blocks = [new Paragraph("outer"), nested] });
        var (window, editor, surface) = Create(new([outer]));
        try
        {
            var cells = surface.Layout.TableCells().Where(c => c.Table.Id == nested.Id).ToArray();
            var start = surface.TranslatePoint(cells[0].Bounds.Center, window)!.Value;
            var end = surface.TranslatePoint(cells[^1].Bounds.Center, window)!.Value;
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseMove(end, RawInputModifiers.Alt); window.MouseUp(end, MouseButton.Left, RawInputModifiers.Alt);
            var pointerSelection = editor.CellSelection;
            Assert.NotNull(pointerSelection); Assert.Equal(nested.Id, pointerSelection.TableId); Assert.Equal(2, pointerSelection.RowCount);
            editor.SelectTableCells(nested.Id, 0, 0, 0, 0);
            window.KeyPress(Key.Right, RawInputModifiers.Alt | RawInputModifiers.Shift);
            window.KeyPress(Key.Down, RawInputModifiers.Alt | RawInputModifiers.Shift);
            Assert.Equal(pointerSelection, editor.CellSelection); Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Row_resize_preserves_exact_policy_and_mixed_typography_is_explicit() => Run(() =>
    {
        var table = Table.Create(2, 2) with { RowSizing = [new() { Height = 40, Mode = TableRowHeightMode.Exact }, new()] };
        var editor = new TextaloniaEditor { Document = new([table]) };
        editor.BeginTableResize(table.Id, TableResizeAxis.Row, 0, 40); editor.PreviewTableResize(75); editor.CommitTableResize();
        Assert.Equal(TableRowHeightMode.Exact, editor.FindTable(table.Id)!.RowSizing[0].Mode);
        Assert.Equal(75, editor.FindTable(table.Id)!.RowSizing[0].Height);
        editor.Document = new([new Paragraph("a", new() { FontWeight = 300, FontStretch = 3 }), new Paragraph("b", new() { FontWeight = 700, FontStretch = 7 })]);
        var toolbar = new TextaloniaToolbar { Editor = editor }; editor.Session.SelectAll();
        var button = toolbar.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Typography and spacing");
        var content = Assert.IsType<ScrollViewer>(Assert.IsType<Flyout>(button.Flyout).Content);
        var panel = Assert.IsType<StackPanel>(content.Content);
        var weight = panel.Children.OfType<NumericUpDown>().Single(c => AutomationProperties.GetName(c) == "Font weight");
        Assert.Null(weight.Value); Assert.Equal("Mixed", weight.PlaceholderText);
        weight.Value = 500;
        Assert.Equal(500, editor.Session.FormattingState.FontWeight.Value); Assert.False(editor.Session.FormattingState.FontWeight.IsMixed);
        editor.IsReadOnly = true; Assert.False(weight.IsEnabled);
    });
    [Fact]
    public Task Column_rectangle_formats_only_selected_cells_and_displays_mixed_values_in_one_undo() => Run(() =>
    {
        var table = Table.Create(2, 2);
        for (var row = 0; row < 2; row++)
            for (var column = 0; column < 2; column++)
                table = table.SetCell(row, column, table.Rows[row][column] with
                { Blocks = [new Paragraph($"{row}:{column}", new() { Bold = row == 0 && column == 0 })] });
        var original = new FlowDocument([table]); var editor = new TextaloniaEditor { Document = original };
        var toolbar = new TextaloniaToolbar { Editor = editor };
        var bold = toolbar.Children.OfType<ToggleButton>().Single(button => AutomationProperties.GetName(button) == "Bold");
        editor.SelectTableCells(table.Id, 0, 0, 1, 0);
        Assert.True(editor.FormattingState.Bold.IsMixed); Assert.Null(bold.IsChecked);
        editor.BoldCommand.Execute(null);
        Assert.True(bold.IsChecked); Assert.False(editor.FormattingState.Bold.IsMixed);
        var changed = editor.FindTable(table.Id)!;
        for (var row = 0; row < 2; row++)
        {
            Assert.True(changed.Rows[row][0].Paragraphs[0].Runs[0].Style.Bold);
            Assert.False(changed.Rows[row][1].Paragraphs[0].Runs[0].Style.Bold);
        }
        editor.Undo(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
        editor.SelectTableCells(table.Id, 0, 0, 1, 0); editor.ApplyParagraphStyle(style => style with { LineHeight = 60 });
        Assert.All(editor.FindTable(table.Id)!.Rows, row => { Assert.Equal(60, row[0].Paragraphs[0].Style.LineHeight); Assert.Null(row[1].Paragraphs[0].Style.LineHeight); });
        var formatted = editor.Document; editor.IsReadOnly = true; editor.ApplyStyle(style => style with { Italic = true });
        Assert.Same(formatted, editor.Document);
    });

    [Fact]
    public Task Clearing_a_merged_selection_cannot_reveal_old_text_after_split() => Run(() =>
    {
        var table = Table.Create(2, 2);
        for (var row = 0; row < 2; row++)
            for (var column = 0; column < 2; column++)
                table = table.SetCell(row, column, table.Rows[row][column] with { Blocks = [new Paragraph("old text")] });
        table = table.MergeCells(0, 0, 2, 2);
        var original = new FlowDocument([table]); var editor = new TextaloniaEditor { Document = original };
        editor.SelectTableCells(table.Id, 0, 0, 0, 0); editor.ClearSelectedTableCellContents();
        Assert.DoesNotContain("old text", editor.Text);
        editor.SplitSelectedTableCells(); Assert.DoesNotContain("old text", editor.Text); editor.Document.Validate();
        editor.Undo(); editor.Undo(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
    });

    [Fact]
    public Task Keyboard_resize_uses_the_same_track_math_as_pointer_and_capture_loss_cancels() => Run(() =>
    {
        var table = Table.Create(2, 2); var original = new FlowDocument([table]);
        var (window, editor, surface) = Create(original);
        try
        {
            editor.SelectTableCells(table.Id, 0, 0, 0, 0);
            Assert.True(editor.ResizeCurrentTableTrack(TableResizeAxis.Column, 30));
            var keyboardWidths = editor.FindTable(table.Id)!.ColumnWidths;
            editor.Undo(); window.UpdateLayout(); surface.EnsureLayout(surface.Bounds.Width);
            var cell = surface.Layout.TableCells().First(c => c.Row == 0 && c.Column == 0);
            var start = surface.TranslatePoint(new Point(cell.Bounds.Right - 1, cell.Bounds.Center.Y), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(30, 0)); window.MouseUp(start + new Vector(30, 0), MouseButton.Left);
            Assert.Equal(keyboardWidths.ToArray(), editor.FindTable(table.Id)!.ColumnWidths.ToArray());
            editor.Undo(); window.UpdateLayout(); surface.EnsureLayout(surface.Bounds.Width);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(20, 0));
            Assert.True(editor.IsResizingTable);
            editor.PointerComponent = new DefaultPointerComponent();
            Assert.False(editor.IsResizingTable); Assert.Null(editor.TablePreviewDocument); Assert.Same(original, editor.Document);
            window.MouseUp(start, MouseButton.Left); Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Active_pointer_resize_releases_capture_when_document_becomes_readonly() => Run(() =>
    {
        var table = Table.Create(2, 2); var original = new FlowDocument([table]);
        var (window, editor, surface) = Create(original);
        try
        {
            var cell = surface.Layout.TableCells().First(c => c.Row == 0 && c.Column == 0);
            var start = surface.TranslatePoint(new Point(cell.Bounds.Right - 1, cell.Bounds.Center.Y), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(20, 0));
            Assert.True(editor.IsResizingTable); editor.IsReadOnly = true;
            Assert.False(editor.IsResizingTable); Assert.Null(editor.TablePreviewDocument);
            window.MouseUp(start + new Vector(20, 0), MouseButton.Left);
            Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Typography_popup_numeric_editing_preserves_textbox_focus_and_readonly_blocks_changes() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "document text" };
        var window = new Window { Width = 1000, Height = 650, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument(); Dispatcher.UIThread.RunJobs();
        try
        {
            var toolbar = editor.GetVisualDescendants().OfType<TextaloniaToolbar>().Single();
            var button = toolbar.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Typography and spacing");
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            var panel = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(flyout.Content).Content);
            var weight = panel.Children.OfType<NumericUpDown>().Single(c => AutomationProperties.GetName(c) == "Font weight");
            flyout.ShowAt(button); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var input = weight.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(input.Focus());
            weight.Value = 500;
            Assert.True(input.IsFocused);
            Assert.True(weight.IsKeyboardFocusWithin);
            Assert.Equal(500, editor.FormattingState.FontWeight.Value);
            input.SelectAll();
            window.KeyTextInput("6");
            window.KeyTextInput("00");
            Assert.True(input.IsFocused); Assert.Equal("600", input.Text);
            Assert.Equal("document text", editor.Text);
            window.KeyPress(Key.Tab, RawInputModifiers.None);
            Assert.Equal(600, editor.FormattingState.FontWeight.Value);
            var original = editor.Document; var revision = editor.Session.Revision;
            editor.IsReadOnly = true;
            Assert.False(weight.IsEnabled);
            weight.Value = 700;
            Assert.Same(original, editor.Document); Assert.Equal(revision, editor.Session.Revision);
            flyout.Hide();
        }
        finally { window.Close(); }
    });

}

