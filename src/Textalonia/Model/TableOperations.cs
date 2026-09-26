using System.Collections.Immutable;

namespace Textalonia.Model;

public static class TableOperations
{
    /// <summary>Inserting strictly inside a merge expands it; insertion at either edge stays outside.</summary>
    public static Table InsertRow(this Table table, int index) => Transform(table, index, rows: true, insert: true);
    public static Table RemoveRow(this Table table, int index) => Transform(table, index, rows: true, insert: false);
    public static Table InsertColumn(this Table table, int index) => Transform(table, index, rows: false, insert: true);
    public static Table RemoveColumn(this Table table, int index) => Transform(table, index, rows: false, insert: false);

    // Hidden cells remain the authoritative originals. Unedited aggregates can be
    // rebuilt after a structural change; edited aggregates stay intact in their
    // surviving anchor, while explicitly deleted originals disappear from backups.
    private static Table Transform(Table table, int index, bool rows, bool insert)
    {
        var size = rows ? table.Rows.Length : table.ColumnCount;
        if (index < 0 || index >= size + (insert ? 1 : 0)) throw new ArgumentOutOfRangeException(nameof(index));
        if (insert && size >= (rows ? 1000 : 100)) throw new InvalidOperationException("Table size limit reached.");
        if (!insert && size == 1) throw new InvalidOperationException("A table must retain at least one row and column.");
        var result = table with
        {
            Rows = rows
                ? insert ? table.Rows.Insert(index, Enumerable.Range(0, table.ColumnCount).Select(_ => new TableCell()).ToImmutableArray())
                    : table.Rows.RemoveAt(index)
                : table.Rows.Select(row => insert ? row.Insert(index, new TableCell()) : row.RemoveAt(index)).ToImmutableArray(),
            ColumnWidths = rows || table.ColumnWidths.IsEmpty ? table.ColumnWidths : insert
                ? table.ColumnWidths.Insert(index, table.ColumnWidths[Math.Min(index, size - 1)]) : table.ColumnWidths.RemoveAt(index),
            RowSizing = !rows || table.RowSizing.IsEmpty ? table.RowSizing : insert
                ? table.RowSizing.Insert(index, new TableRowSizing()) : table.RowSizing.RemoveAt(index)
        };
        for (var r = 0; r < table.Rows.Length; r++)
            for (var c = 0; c < table.ColumnCount; c++)
            {
                if (table.IsCovered(r, c)) continue;
                var anchor = table.Rows[r][c];
                if (anchor.RowSpan == 1 && anchor.ColumnSpan == 1) continue;
                var start = rows ? r : c;
                var span = rows ? anchor.RowSpan : anchor.ColumnSpan;
                var intersects = insert ? index > start && index < start + span : index >= start && index < start + span;
                var nextSpan = span + (intersects ? insert ? 1 : -1 : 0);
                if (nextSpan == 0) continue;
                var nextStart = insert ? start + (index <= start ? 1 : 0) : start - (index < start ? 1 : 0);
                var nextRow = rows ? nextStart : r;
                var nextColumn = rows ? c : nextStart;
                if (!intersects) continue;

                var originals = new List<Block>();
                for (var y = r; y < r + anchor.RowSpan; y++)
                    for (var x = c; x < c + anchor.ColumnSpan; x++)
                        originals.AddRange(y == r && x == c ? anchor.MergeOriginalBlocks : table.Rows[y][x].Blocks);
                var unchanged = BlockOperations.ContentEquals(anchor.Blocks, originals);
                var promoted = result.Rows[nextRow][nextColumn];
                var originalAnchor = !insert && index == start ? promoted.Blocks : anchor.MergeOriginalBlocks;
                var nextRows = rows ? nextSpan : anchor.RowSpan;
                var nextColumns = rows ? anchor.ColumnSpan : nextSpan;
                var nextOriginals = new List<Block>();
                for (var y = nextRow; y < nextRow + nextRows; y++)
                    for (var x = nextColumn; x < nextColumn + nextColumns; x++)
                        nextOriginals.AddRange(y == nextRow && x == nextColumn ? originalAnchor : result.Rows[y][x].Blocks);
                var stillMerged = nextRows != 1 || nextColumns != 1;
                var content = unchanged
                    ? stillMerged ? BlockOperations.Clone(nextOriginals) : originalAnchor
                    : anchor.Blocks;
                result = result.SetCell(nextRow, nextColumn, promoted with
                {
                    RowSpan = nextRows, ColumnSpan = nextColumns,
                    Blocks = content,
                    MergeOriginalBlocks = stillMerged ? originalAnchor : []
                });
            }
        return result;
    }
}
