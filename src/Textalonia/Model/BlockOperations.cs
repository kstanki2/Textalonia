using System.Collections.Immutable;

namespace Textalonia.Model;

/// <summary>Operations that preserve the complete block tree, including hidden merge content.</summary>
public static class BlockOperations
{
    /// <summary>Copies a block with fresh identifiers for every descendant and retained merge backup.</summary>
    public static Block CloneWithNewIds(Block block) => block switch
    {
        Paragraph paragraph => paragraph with { Id = Guid.NewGuid() },
        Section section => section with { Id = Guid.NewGuid(), Blocks = Clone(section.Blocks) },
        Table table => table with
        {
            Id = Guid.NewGuid(),
            Rows = table.Rows.Select(row => row.Select(cell => cell with
            {
                Id = Guid.NewGuid(), Blocks = Clone(cell.Blocks), MergeOriginalBlocks = Clone(cell.MergeOriginalBlocks)
            }).ToImmutableArray()).ToImmutableArray()
        },
        _ => throw new NotSupportedException("Unknown block type.")
    };

    internal static ImmutableArray<Block> Clone(IEnumerable<Block> blocks) => blocks.Select(CloneWithNewIds).ToImmutableArray();

    internal static bool ContentEquals(IEnumerable<Block> left, IEnumerable<Block> right) =>
        left.SequenceEqual(right, ContentComparer.Instance);

    private sealed class ContentComparer : IEqualityComparer<Block>
    {
        public static readonly ContentComparer Instance = new();
        public bool Equals(Block? left, Block? right)
        {
            if (ReferenceEquals(left, right)) return true;
            return (left, right) switch
            {
                (Paragraph a, Paragraph b) => a.Style == b.Style && a.DefaultStyle == b.DefaultStyle && a.Runs.SequenceEqual(b.Runs),
                (Section a, Section b) => a.Background == b.Background && a.BorderColor == b.BorderColor &&
                    a.Padding == b.Padding && a.PaddingEdges == b.PaddingEdges && a.Borders == b.Borders && ContentEquals(a.Blocks, b.Blocks),
                (Table a, Table b) => a.ColumnWidths.SequenceEqual(b.ColumnWidths) && a.RowSizing.SequenceEqual(b.RowSizing) &&
                    a.Rows.Length == b.Rows.Length && a.Rows.Zip(b.Rows).All(rows => rows.First.Length == rows.Second.Length &&
                        rows.First.Zip(rows.Second).All(cells => CellEquals(cells.First, cells.Second))),
                _ => false
            };
        }
        private static bool CellEquals(TableCell a, TableCell b) => a.RowSpan == b.RowSpan && a.ColumnSpan == b.ColumnSpan &&
            a.Background == b.Background && a.Padding == b.Padding && a.Borders == b.Borders &&
            ContentEquals(a.Blocks, b.Blocks) && ContentEquals(a.MergeOriginalBlocks, b.MergeOriginalBlocks);
        public int GetHashCode(Block obj) => 0;
    }
}
