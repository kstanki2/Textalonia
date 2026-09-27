using Avalonia.Input;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class TableClipboardInteractionTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private sealed class Clipboard : IEditorClipboard
    {
        internal DataTransfer? Data;
        internal Action? BeforeSet;
        public Task SetDataAsync(DataTransfer data) { BeforeSet?.Invoke(); Data = data; return Task.CompletedTask; }
        public Task<IAsyncDataTransfer?> TryGetDataAsync() => Task.FromResult<IAsyncDataTransfer?>(Data);
    }

    [Fact]
    public Task Rectangle_copy_and_cut_preserve_table_structure_and_have_one_undo() => fixture.Session.Dispatch(async () =>
    {
        var table = Table.Create(2, 2);
        for (var row = 0; row < 2; row++) for (var column = 0; column < 2; column++)
            table = table.SetCell(row, column, table.Rows[row][column] with { Blocks = [new Paragraph($"cell{row}{column}")] });
        var original = new FlowDocument([new Paragraph("outside"), table]);
        var clipboard = new Clipboard();
        var editor = new TextaloniaEditor { Document = original, ClipboardProvider = () => clipboard };
        editor.SelectTableCells(table.Id, 0, 1, 1, 1);
        Assert.True(editor.CopyCommand.CanExecute(null));
        await editor.CopyAsync();
        var native = clipboard.Data!.TryGetValue(DataFormat.CreateStringApplicationFormat("org.textalonia.fragment"));
        var copied = Assert.IsType<Table>(Assert.Single(ClipboardInterchange.Parse(native!).Document.Blocks));
        Assert.Equal(1, copied.ColumnCount); Assert.Equal(2, copied.Rows.Length);
        Assert.Equal("cell01\ncell11", new FlowDocument([copied]).Text);
        Assert.Same(original, editor.Document);
        await editor.CutAsync();
        var cut = Assert.IsType<Table>(editor.Document.Blocks[1]);
        Assert.Equal(table.Id, cut.Id); Assert.Equal(2, cut.ColumnCount); Assert.Equal(2, cut.Rows.Length);
        Assert.Equal("cell00", new FlowDocument(cut.Rows[0][0].Blocks).Text);
        Assert.Equal("cell10", new FlowDocument(cut.Rows[1][0].Blocks).Text);
        Assert.Equal("", new FlowDocument(cut.Rows[0][1].Blocks).Text);
        Assert.Equal("", new FlowDocument(cut.Rows[1][1].Blocks).Text);
        editor.Undo(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Delayed_cut_and_readonly_do_not_clear_a_changed_rectangle() => fixture.Session.Dispatch(async () =>
    {
        var table = Table.Create(1, 2).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("one")] })
            .SetCell(0, 1, new TableCell { Blocks = [new Paragraph("two")] });
        var original = new FlowDocument([table]); var clipboard = new Clipboard();
        var editor = new TextaloniaEditor { Document = original, ClipboardProvider = () => clipboard };
        editor.SelectTableCells(table.Id, 0, 0, 0, 0);
        clipboard.BeforeSet = () => editor.SelectTableCells(table.Id, 0, 1, 0, 1);
        await editor.CutAsync(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
        clipboard.BeforeSet = null; editor.IsReadOnly = true;
        Assert.True(editor.CopyCommand.CanExecute(null)); Assert.False(editor.CutCommand.CanExecute(null));
        await editor.CutAsync(); Assert.Same(original, editor.Document);
        return true;
    }, CancellationToken.None);
}
