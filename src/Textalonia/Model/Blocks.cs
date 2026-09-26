using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Textalonia.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Paragraph), "paragraph")]
[JsonDerivedType(typeof(Section), "section")]
[JsonDerivedType(typeof(Table), "table")]
public abstract record Block
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

public sealed record Paragraph : Block
{
    public ImmutableArray<RichRun> Runs { get; init; } = [];
    public ParagraphStyle Style { get; init; } = ParagraphStyle.Default;
    public TextStyle DefaultStyle { get; init; } = TextStyle.Default;

    public Paragraph() { }
    public Paragraph(string text, TextStyle? style = null)
    {
        DefaultStyle = style ?? TextStyle.Default;
        Runs = text.Length == 0 ? [] : [new RichRun(text, DefaultStyle)];
    }
    public Paragraph(IEnumerable<RichRun> runs) => Runs = Normalize(runs);

    [JsonIgnore] public string Text => string.Concat(Runs.Select(x => x.Text));
    [JsonIgnore] public string PlainText => string.Concat(Runs.Select(x => x.PlainText));
    [JsonIgnore] public int Length
    {
        get
        {
            var length = 0;
            foreach (var run in Runs) length = checked(length + run.Storage.Length);
            return length;
        }
    }

    public TextStyle StyleAt(int offset)
    {
        if (Runs.IsEmpty) return DefaultStyle;
        var position = 0;
        foreach (var run in Runs)
        {
            position += run.Storage.Length;
            if (offset < position) return run.Style;
        }
        return Runs[^1].Style;
    }

    public ImmutableArray<RichRun> Slice(int start, int length)
    {
        var result = ImmutableArray.CreateBuilder<RichRun>();
        var position = 0;
        foreach (var run in Runs)
        {
            var from = Math.Max(0, start - position);
            var to = Math.Min(run.Storage.Length, start + length - position);
            if (to > from) result.Add(run.Slice(from, to - from));
            position += run.Storage.Length;
            if (position >= start + length) break;
        }
        return result.ToImmutable();
    }

    public Paragraph Format(int start, int length, Func<TextStyle, TextStyle> change)
    {
        var middle = Slice(start, length).Select(r => r with { Style = change(r.Style) });
        return this with
        {
            Runs = Normalize(Slice(0, start).Concat(middle).Concat(Slice(start + length, Length - start - length))),
            DefaultStyle = Length == 0 ? change(DefaultStyle) : DefaultStyle
        };
    }

    public static ImmutableArray<RichRun> Normalize(IEnumerable<RichRun> runs)
    {
        var result = ImmutableArray.CreateBuilder<RichRun>();
        foreach (var run in runs)
        {
            if (run.Storage.Length == 0) continue;
            if (result.Count > 0 && result[^1].Inline is null && run.Inline is null && result[^1].Style == run.Style)
                result[^1] = result[^1].Append(run);
            else result.Add(run);
        }
        return result.ToImmutable();
    }
}

public sealed record Section : Block
{
    private SnapshotArray<Block> _blocks = SnapshotArray<Block>.From([new Paragraph()]);
    public ImmutableArray<Block> Blocks { get => _blocks.Read(); init => _blocks = SnapshotArray<Block>.From(value); }
    internal Section WithChildren(StorageTree<OrderKey, DocumentNode>? children) => this with
    { _blocks = new(() => children!.Items().Select(p => (Block)p.Value.Source!).ToImmutableArray()) };
    public string? Background { get; init; }
    public string? BorderColor { get; init; }
    public double Padding { get; init; } = 12;
    public EdgeInsets? PaddingEdges { get; init; }
    public BlockBorders? Borders { get; init; }
}

public sealed record TableCell
{
    public Guid Id { get; init; } = Guid.NewGuid();
    private SnapshotArray<Block> _blocks = SnapshotArray<Block>.From([new Paragraph()]);
    public ImmutableArray<Block> Blocks { get => _blocks.Read(); init => _blocks = SnapshotArray<Block>.From(value); }
    /// <summary>Legacy view of immediate paragraphs. Setting it replaces the cell's blocks.</summary>
    [JsonIgnore]
    public ImmutableArray<Paragraph> Paragraphs
    {
        get => Blocks.OfType<Paragraph>().ToImmutableArray();
        init => _blocks = SnapshotArray<Block>.From(value.IsDefault ? default : value.Cast<Block>().ToImmutableArray());
    }
    internal TableCell WithChildren(StorageTree<OrderKey, DocumentNode>? children) => this with
    { _blocks = new(() => children!.Items().Select(p => (Block)p.Value.Source!).ToImmutableArray()) };
    public int ColumnSpan { get; init; } = 1;
    public int RowSpan { get; init; } = 1;
    public string? Background { get; init; }
    public EdgeInsets? Padding { get; init; }
    public BlockBorders? Borders { get; init; }
    /// <summary>Original anchor blocks retained for a lossless split before editing.</summary>
    public ImmutableArray<Block> MergeOriginalBlocks { get; init; } = [];
    [JsonIgnore]
    public ImmutableArray<Paragraph> MergeOriginal
    {
        get => MergeOriginalBlocks.OfType<Paragraph>().ToImmutableArray();
        init => MergeOriginalBlocks = value.IsDefault ? default : value.Cast<Block>().ToImmutableArray();
    }
}

public enum TableRowHeightMode { Auto, AtLeast, Exact }
public sealed record TableRowSizing
{
    public TableRowHeightMode Mode { get; init; }
    public double Height { get; init; }
}

public sealed record Table : Block
{
    public ImmutableArray<ImmutableArray<TableCell>> Rows { get; init; } = [];
    /// <summary>Positive relative column widths; an empty array gives equal columns.</summary>
    public ImmutableArray<double> ColumnWidths { get; init; } = [];
    /// <summary>Row height policies; an empty array gives automatic sizing.</summary>
    public ImmutableArray<TableRowSizing> RowSizing { get; init; } = [];
    [JsonIgnore] public int ColumnCount => Rows.IsDefaultOrEmpty || Rows[0].IsDefault ? 0 : Rows[0].Length;

    public static Table Create(int rows, int columns)
    {
        if (rows is < 1 or > 1000 || columns is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(rows), "Use 1-1000 rows and 1-100 columns.");
        return new Table
        {
            Rows = Enumerable.Range(0, rows).Select(_ =>
                Enumerable.Range(0, columns).Select(_ => new TableCell()).ToImmutableArray()).ToImmutableArray()
        };
    }

    private sealed record CellMap(ImmutableArray<ImmutableArray<TableCell>> Rows, (int Row, int Column)[,] Owners);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Table, CellMap> CellMaps = new();

    public (int Row, int Column) OwnerOf(int row, int column)
    {
        var cache = CellMaps.GetValue(this, _ =>
        {
            var owners = new (int Row, int Column)[Rows.Length, ColumnCount];
            var assigned = new bool[Rows.Length, ColumnCount];
            for (var r = 0; r < Rows.Length; r++)
                for (var c = 0; c < ColumnCount; c++)
                {
                    if (assigned[r, c]) continue;
                    var cell = Rows[r][c];
                    for (var y = r; y < Math.Min(Rows.Length, r + cell.RowSpan); y++)
                        for (var x = c; x < Math.Min(ColumnCount, c + cell.ColumnSpan); x++)
                        { owners[y, x] = (r, c); assigned[y, x] = true; }
                }
            return new CellMap(Rows, owners);
        });
        return cache.Owners[row, column];
    }

    public bool IsCovered(int row, int column) => OwnerOf(row, column) != (row, column);

    public Table SetCell(int row, int column, TableCell cell) =>
        this with { Rows = Rows.SetItem(row, Rows[row].SetItem(column, cell)) };

    public Table MergeCells(int row, int column, int rowCount, int columnCount)
    {
        if (rowCount < 1 || columnCount < 1 || row < 0 || column < 0 ||
            (long)row + rowCount > Rows.Length || (long)column + columnCount > ColumnCount)
            throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (rowCount == 1 && columnCount == 1) return this;
        var blocks = ImmutableArray.CreateBuilder<Block>();
        for (var r = row; r < row + rowCount; r++)
            for (var c = column; c < column + columnCount; c++)
            {
                var cell = Rows[r][c];
                if (IsCovered(r, c) || cell.RowSpan != 1 || cell.ColumnSpan != 1)
                    throw new InvalidOperationException("Split existing merged cells before merging this range.");
                // Fresh IDs keep hidden source cells independent of the editable merged content.
                blocks.AddRange(cell.Blocks.Select(BlockOperations.CloneWithNewIds));
            }
        var anchor = Rows[row][column];
        return SetCell(row, column, anchor with
        {
            RowSpan = rowCount, ColumnSpan = columnCount,
            MergeOriginalBlocks = anchor.Blocks, Blocks = blocks.ToImmutable()
        });
    }

    /// <summary>Restores original cells. Edited merged text stays in the anchor, so edits are never discarded.</summary>
    public Table SplitCell(int row, int column)
    {
        (row, column) = OwnerOf(row, column);
        var cell = Rows[row][column];
        if (cell.RowSpan == 1 && cell.ColumnSpan == 1) return this;
        var original = new List<Block>();
        for (var r = row; r < row + cell.RowSpan; r++)
            for (var c = column; c < column + cell.ColumnSpan; c++)
                original.AddRange(r == row && c == column ? cell.MergeOriginalBlocks : Rows[r][c].Blocks);
        var unchanged = BlockOperations.ContentEquals(cell.Blocks, original);
        return SetCell(row, column, cell with
        {
            RowSpan = 1, ColumnSpan = 1,
            Blocks = unchanged && !cell.MergeOriginalBlocks.IsEmpty ? cell.MergeOriginalBlocks : cell.Blocks,
            MergeOriginalBlocks = []
        });
    }
}
