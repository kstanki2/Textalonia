using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class TableModelTests
{
    private static Table Grid(int rows = 4, int columns = 4)
    {
        var table = Table.Create(rows, columns) with
        {
            ColumnWidths = Enumerable.Range(1, columns).Select(i => (double)i).ToImmutableArray(),
            RowSizing = Enumerable.Range(1, rows).Select(i => new TableRowSizing { Mode = TableRowHeightMode.AtLeast, Height = 20 + i }).ToImmutableArray()
        };
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                table = table.SetCell(r, c, table.Rows[r][c] with
                {
                    Blocks = [new Paragraph($"{r}:{c}")], Background = "#123456",
                    Padding = new(1, 2, 3, 4), Borders = new(Left: new(2, "#ABCDEF"))
                });
        return table;
    }

    public static IEnumerable<object[]> StructuralCases()
    {
        foreach (var rows in new[] { false, true })
            foreach (var insert in new[] { false, true })
                foreach (var edited in new[] { false, true })
                    for (var index = 0; index < (insert ? 5 : 4); index++)
                        yield return [rows, insert, edited, index];
    }

    [Theory]
    [MemberData(nameof(StructuralCases))]
    public void Merge_boundary_matrix_preserves_surviving_sources_styles_and_exact_history(bool rows, bool insert, bool edited, int index)
    {
        var original = Grid();
        var table = original.MergeCells(1, 1, 2, 2);
        if (edited) table = table.SetCell(1, 1, table.Rows[1][1] with { Blocks = [new Paragraph("edited aggregate")] });
        var document = new FlowDocument([table]);
        var serialized = DocumentFormats.Json.Serialize(document);
        var session = new EditorSession(document);
        Table Apply(Table t) => rows ? insert ? t.InsertRow(index) : t.RemoveRow(index) : insert ? t.InsertColumn(index) : t.RemoveColumn(index);
        session.Execute(d => d.ReplaceBlock(table.Id, Apply(table)));
        var changed = Assert.IsType<Table>(session.Document.Blocks[0]);
        session.Document.Validate();
        Assert.Equal(rows ? insert ? 5 : 3 : 4, changed.Rows.Length);
        Assert.Equal(rows ? 4 : insert ? 5 : 3, changed.ColumnCount);
        Assert.Equal(changed.Rows.Length, changed.RowSizing.Length);
        Assert.Equal(changed.ColumnCount, changed.ColumnWidths.Length);
        var start = insert ? index <= 1 ? 2 : 1 : index < 1 ? 0 : 1;
        var span = insert ? index == 2 ? 3 : 2 : index is 1 or 2 ? 1 : 2;
        var ar = rows ? start : 1;
        var ac = rows ? 1 : start;
        var anchor = changed.Rows[ar][ac];
        Assert.Equal(rows ? span : 2, anchor.RowSpan);
        Assert.Equal(rows ? 2 : span, anchor.ColumnSpan);
        Assert.Equal(table.Rows[1][1].Background, anchor.Background);
        Assert.Equal(table.Rows[1][1].Padding, anchor.Padding);
        Assert.Equal(table.Rows[1][1].Borders, anchor.Borders);
        var sourceRow = rows && !insert && index == 1 ? 2 : 1;
        var sourceColumn = !rows && !insert && index == 1 ? 2 : 1;
        Assert.Equal(original.Rows[sourceRow][sourceColumn].Id, anchor.Id);
        if (edited) Assert.Equal("edited aggregate", Assert.IsType<Paragraph>(anchor.Blocks[0]).Text);
        var split = changed.SplitCell(ar, ac);
        new FlowDocument([split]).Validate();
        for (var r = 0; r < original.Rows.Length; r++)
            for (var c = 0; c < original.ColumnCount; c++)
            {
                var axis = rows ? r : c;
                if (!insert && axis == index) continue;
                var mapped = insert ? axis + (axis >= index ? 1 : 0) : axis - (axis > index ? 1 : 0);
                var nr = rows ? mapped : r;
                var nc = rows ? c : mapped;
                if (edited && nr == ar && nc == ac) continue;
                Assert.Same(original.Rows[r][c].Blocks[0], split.Rows[nr][nc].Blocks[0]);
            }
        var after = DocumentFormats.Json.Serialize(session.Document);
        Assert.Equal(after, DocumentFormats.Json.Serialize(DocumentFormats.Json.Parse(after)));
        session.Undo();
        Assert.Same(document, session.Document);
        Assert.Equal(serialized, DocumentFormats.Json.Serialize(session.Document));
        session.Redo();
        Assert.Equal(after, DocumentFormats.Json.Serialize(session.Document));
    }

    [Fact]
    public void Deletion_can_collapse_a_merge_or_remove_its_entire_extent()
    {
        var table = Grid(2, 2).MergeCells(0, 0, 1, 2);
        var collapsed = table.RemoveColumn(0);
        Assert.Equal("0:1", collapsed.Rows[0][0].Paragraphs[0].Text);
        Assert.Equal(1, collapsed.Rows[0][0].ColumnSpan);
        Assert.Empty(collapsed.Rows[0][0].MergeOriginalBlocks);
        var removed = table.RemoveRow(0);
        Assert.Equal("1:0\n1:1", new FlowDocument([removed]).Text);
        var edited = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Paragraph("edited")] }).RemoveColumn(0);
        Assert.Equal("edited", edited.Rows[0][0].Paragraphs[0].Text);
        new FlowDocument([edited]).Validate();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Anchor_deletion_promotes_surviving_cell_identity_and_its_distinct_formatting(bool rows)
    {
        var table = Grid(2, 2);
        var survivorRow = rows ? 1 : 0;
        var survivorColumn = rows ? 0 : 1;
        var survivor = table.Rows[survivorRow][survivorColumn] with
        {
            Background = "#FEDCBA", Padding = new(13, 14, 15, 16), Borders = new(Right: new(3, "#112233"))
        };
        table = table.SetCell(survivorRow, survivorColumn, survivor).MergeCells(0, 0, 2, 2);
        var changed = rows ? table.RemoveRow(0) : table.RemoveColumn(0);
        var anchor = changed.Rows[0][0];
        Assert.Equal(survivor.Id, anchor.Id);
        Assert.Equal(survivor.Background, anchor.Background);
        Assert.Equal(survivor.Padding, anchor.Padding);
        Assert.Equal(survivor.Borders, anchor.Borders);
        var restored = changed.SplitCell(0, 0).Rows[0][0];
        Assert.Equal(survivor.Id, restored.Id);
        Assert.Equal(survivor.Background, restored.Background);
        Assert.Equal(survivor.Padding, restored.Padding);
        Assert.Equal(survivor.Borders, restored.Borders);
        Assert.Same(survivor.Blocks[0], restored.Blocks[0]);
    }

    [Fact]
    public void Repeated_structural_edits_keep_backups_splittable()
    {
        var table = Grid().MergeCells(0, 0, 3, 3).InsertRow(1).InsertColumn(1).RemoveRow(0).RemoveColumn(0);
        var split = table.SplitCell(0, 0);
        new FlowDocument([split]).Validate();
        Assert.Equal("", split.Rows[0][0].Paragraphs[0].Text);
        Assert.Equal("1:1", split.Rows[1][1].Paragraphs[0].Text);
        Assert.Equal("2:2", split.Rows[2][2].Paragraphs[0].Text);
    }

    [Fact]
    public void Nested_blocks_merge_split_clone_roundtrip_and_rewrite_without_losing_hidden_content()
    {
        var inner = Grid(2, 2).MergeCells(0, 0, 1, 2);
        var outer = Grid(1, 2);
        outer = outer.SetCell(0, 0, outer.Rows[0][0] with { Blocks = [new Section { Blocks = [inner] }] });
        var merged = outer.MergeCells(0, 0, 1, 2);
        var document = new FlowDocument([merged]);
        document.Validate();
        var clone = new FlowDocument([BlockOperations.CloneWithNewIds(merged)]);
        new FlowDocument([merged, clone.Blocks[0]]).Validate();
        Assert.Equal(document.Text, clone.Text);
        var encoded = DocumentFormats.Json.Serialize(document);
        Assert.Equal(encoded, DocumentFormats.Json.Serialize(DocumentFormats.Json.Parse(encoded)));
        Assert.Same(outer.Rows[0][0].Blocks[0], merged.SplitCell(0, 0).Rows[0][0].Blocks[0]);
        var rewritten = document.RewriteParagraphs(p => [p with { Runs = [new RichRun(p.Text + "!")] }]);
        rewritten.Validate();
        var rewrittenTable = Assert.IsType<Table>(rewritten.Blocks[0]);
        Assert.Same(merged.Rows[0][0].MergeOriginalBlocks[0], rewrittenTable.Rows[0][0].MergeOriginalBlocks[0]);
        Assert.Contains("0:0!", rewritten.Text);
        Assert.Equal(document.Text, new FlowDocument([merged]).Text);
    }

    [Fact]
    public void Structural_edits_after_editing_nested_merged_content_retain_nested_backups()
    {
        var nested = Grid(2, 2).MergeCells(0, 0, 1, 2);
        var outer = Grid(1, 3).SetCell(0, 0, new TableCell { Blocks = [nested] }).MergeCells(0, 0, 1, 3);
        var session = new EditorSession(new FlowDocument([outer]));
        session.Select(0, 0); session.InsertText("changed ");
        var editedOuter = Assert.IsType<Table>(session.Document.Blocks[0]);
        session.Execute(document => document.ReplaceBlock(outer.Id, editedOuter.InsertColumn(1).RemoveColumn(2)));
        var changed = Assert.IsType<Table>(session.Document.Blocks[0]);
        Assert.Same(nested, changed.Rows[0][0].MergeOriginalBlocks[0]);
        Assert.Contains("changed 0:0", session.Index.Text);
        var split = changed.SplitCell(0, 0);
        new FlowDocument([split]).Validate();
        Assert.Contains("changed 0:0", new FlowDocument([split]).Text);
        Assert.Equal("0:2", split.Rows[0][2].Paragraphs[0].Text);
        var encoded = DocumentFormats.Json.Serialize(session.Document);
        Assert.Equal(encoded, DocumentFormats.Json.Serialize(DocumentFormats.Json.Parse(encoded)));
        session.Undo(); Assert.Same(editedOuter, session.Document.Blocks[0]);
        session.Undo(); Assert.Same(outer, session.Document.Blocks[0]);
    }

    [Fact]
    public void Nested_local_edits_copy_only_ancestor_paths_and_history_counts_hidden_nested_text()
    {
        var inner = Grid(1, 2);
        var outer = Grid(1, 2).SetCell(0, 0, new TableCell { Blocks = [new Section { Blocks = [inner] }] });
        var tail = new Paragraph("tail");
        var session = new EditorSession(new FlowDocument([outer, tail]));
        session.Select(1, 1);
        session.InsertText("x");
        session.Document.Validate();
        Assert.Equal("0x:0", session.Index.At(1).Paragraph.Text);
        Assert.Same(tail, session.Document.Blocks[1]);
        Assert.InRange(session.Index.Tree.UpdatedNodes, 1, 8);
        session.Undo();
        Assert.Equal("0:0", session.Index.At(1).Paragraph.Text);
        var hugeInner = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph(new string('x', 100000))] });
        var hidden = Table.Create(1, 2).SetCell(0, 1, new TableCell { Blocks = [hugeInner] }).MergeCells(0, 0, 1, 2);
        hidden = hidden.SetCell(0, 0, hidden.Rows[0][0] with { Blocks = [new Paragraph("visible")] });
        session.Load(new FlowDocument([hidden]));
        session.HistoryByteLimit = 10000;
        session.SelectAll(); session.InsertText("small");
        Assert.False(session.CanUndo);
        Assert.Equal(0, session.RetainedHistoryBytes);
    }

    [Fact]
    public void Nested_session_commands_target_innermost_cells_and_undo_exact_snapshots()
    {
        var section = new Section { Blocks = [new Paragraph("section text")] };
        var outer = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [section] });
        var session = new EditorSession(new FlowDocument([outer]));
        Assert.Equal(outer.Id, session.CurrentCell()!.Value.Table.Id);
        session.InsertTable(2, 2);
        var inserted = session.Document;
        inserted.Validate();
        var inner = session.CurrentCell()!.Value.Table;
        Assert.NotEqual(outer.Id, inner.Id);
        session.InsertText("nested edit");
        var edited = session.Document;
        session.UpdateCurrentTable((table, row, column) => table.MergeCells(row, column, 2, 2));
        var merged = session.Document;
        session.UpdateCurrentTable((table, row, column) => table.InsertRow(1));
        session.Document.Validate();
        session.Undo(); Assert.Same(merged, session.Document);
        session.UpdateCurrentTable((table, row, column) => table.SplitCell(row, column));
        session.Document.Validate();
        Assert.Contains("nested edit", session.Index.Text);
        var beforeDelete = session.Document;
        session.DeleteCurrentTable();
        Assert.Equal("section text\n", session.Index.Text);
        Assert.Equal(outer.Id, session.CurrentCell()!.Value.Table.Id);
        session.Document.Validate();
        session.Undo(); Assert.Same(beforeDelete, session.Document);
        session.Undo(); Assert.Same(merged, session.Document);
        session.Undo(); Assert.Same(edited, session.Document);
        session.Undo(); Assert.Same(inserted, session.Document);
        session.Undo(); Assert.Same(outer, session.Document.Blocks[0]);
    }

    [Fact]
    public void Validation_checks_nested_backups_depth_global_ids_and_table_geometry()
    {
        var paragraph = new Paragraph("duplicate");
        var cell = new TableCell { Blocks = [paragraph], MergeOriginalBlocks = [paragraph] };
        new FlowDocument([Table.Create(1, 1).SetCell(0, 0, cell)]).Validate();
        var duplicateBackup = cell with { MergeOriginalBlocks = [paragraph, paragraph] };
        Assert.Throws<FormatException>(() => new FlowDocument([Table.Create(1, 1).SetCell(0, 0, duplicateBackup)]).Validate());
        Block deep = new Paragraph("deep");
        for (var i = 0; i < 34; i++) deep = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [deep] });
        Assert.Throws<FormatException>(() => new FlowDocument([deep]).Validate());
        var invalid = new[]
        {
            Grid(1, 2) with { ColumnWidths = [double.NaN, 1] },
            Grid(1, 2) with { ColumnWidths = [1] },
            Grid(1, 2) with { RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 0 }] },
            Grid(1, 2).SetCell(0, 0, new TableCell { Padding = new(-1), Borders = new(Top: new(double.PositiveInfinity)) })
        };
        foreach (var table in invalid) Assert.Throws<FormatException>(() => new FlowDocument([table]).Validate());
    }
}
