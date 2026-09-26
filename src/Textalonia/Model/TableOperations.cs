using System.Collections.Immutable;

namespace Textalonia.Model;

public static class TableOperations
{
    private static void RequireUnmerged(Table table)
    {
        if (table.Rows.SelectMany(r => r).Any(c => c.RowSpan != 1 || c.ColumnSpan != 1))
            throw new InvalidOperationException("Split merged cells before adding or removing rows or columns.");
    }

    public static Table InsertRow(this Table table, int index)
    {
        RequireUnmerged(table);
        if (table.Rows.Length >= 1000) throw new InvalidOperationException("Row limit reached.");
        return table with { Rows = table.Rows.Insert(index,
            Enumerable.Range(0, table.ColumnCount).Select(_ => new TableCell()).ToImmutableArray()) };
    }

    public static Table RemoveRow(this Table table, int index)
    {
        RequireUnmerged(table);
        if (table.Rows.Length == 1) throw new InvalidOperationException("A table must have at least one row.");
        return table with { Rows = table.Rows.RemoveAt(index) };
    }

    public static Table InsertColumn(this Table table, int index)
    {
        RequireUnmerged(table);
        if (table.ColumnCount >= 100) throw new InvalidOperationException("Column limit reached.");
        return table with { Rows = table.Rows.Select(row => row.Insert(index, new TableCell())).ToImmutableArray() };
    }

    public static Table RemoveColumn(this Table table, int index)
    {
        RequireUnmerged(table);
        if (table.ColumnCount == 1) throw new InvalidOperationException("A table must have at least one column.");
        return table with { Rows = table.Rows.Select(row => row.RemoveAt(index)).ToImmutableArray() };
    }
}
