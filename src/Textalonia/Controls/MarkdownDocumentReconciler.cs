using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Controls;

// Preserve unchanged immutable blocks (and stable paragraph anchors) across full Markdown parses.
internal static class MarkdownDocumentReconciler
{
    public static FlowDocument Reuse(FlowDocument previous, FlowDocument next, CancellationToken token)
    {
        // A parser assigns fresh identities to whole lists. Normalize those groups before
        // reusing individual paragraphs, or appended items would silently start a new list.
        var listIds = new Dictionary<Guid, Guid>();
        var claimedIds = new HashSet<Guid>();
        void Pair(Block before, Block after)
        {
            if (before is Paragraph a && after is Paragraph b && a.Style.List == b.Style.List &&
                a.Style.ListLevel == b.Style.ListLevel && a.Style.ListId is { } oldId && b.Style.ListId is { } newId &&
                !listIds.ContainsKey(newId) && claimedIds.Add(oldId)) listIds.Add(newId, oldId);
            else if (before is Section oldSection && after is Section section) Collect(oldSection.Blocks, section.Blocks);
        }
        void Collect(ImmutableArray<Block> before, ImmutableArray<Block> after)
        {
            var prefix = 0;
            while (prefix < before.Length && prefix < after.Length && Same(before[prefix], after[prefix], ignoreListIds: true))
            { token.ThrowIfCancellationRequested(); Pair(before[prefix], after[prefix]); prefix++; }
            var suffix = 0;
            while (suffix < before.Length - prefix && suffix < after.Length - prefix && Same(before[^(suffix + 1)], after[^(suffix + 1)], ignoreListIds: true))
            { token.ThrowIfCancellationRequested(); Pair(before[^(suffix + 1)], after[^(suffix + 1)]); suffix++; }
            for (var i = prefix; i < Math.Min(before.Length - suffix, after.Length - suffix); i++)
            { token.ThrowIfCancellationRequested(); Pair(before[i], after[i]); }
        }
        Collect(previous.Blocks, next.Blocks);
        if (listIds.Count > 0)
            next = next.RewriteParagraphs(p => p.Style.ListId is { } id && listIds.TryGetValue(id, out var previousId)
                ? [p with { Style = p.Style with { ListId = previousId } }] : [p]);
        var blocks = ReuseBlocks(previous.Blocks, next.Blocks, token);
        return DocumentTree.ReuseSnapshot(previous, next with { Blocks = blocks }, token);
    }

    private static ImmutableArray<Block> ReuseBlocks(ImmutableArray<Block> before, ImmutableArray<Block> after, CancellationToken token)
    {
        var result = after.ToBuilder();
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && Same(before[prefix], after[prefix]))
        { token.ThrowIfCancellationRequested(); result[prefix] = before[prefix]; prefix++; }
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && Same(before[^(suffix + 1)], after[^(suffix + 1)]))
        { token.ThrowIfCancellationRequested(); result[after.Length - suffix - 1] = before[^(suffix + 1)]; suffix++; }
        for (var i = prefix; i < Math.Min(before.Length - suffix, after.Length - suffix); i++)
        {
            token.ThrowIfCancellationRequested();
            if (before[i] is Paragraph oldParagraph && after[i] is Paragraph paragraph)
                result[i] = paragraph with { Id = oldParagraph.Id };
            else if (before[i] is Section oldSection && after[i] is Section section)
                result[i] = section with { Id = oldSection.Id, Blocks = ReuseBlocks(oldSection.Blocks, section.Blocks, token) };
        }
        return result.ToImmutable();
    }

    private static bool Same(Block before, Block after, bool ignoreListIds = false) => (before, after) switch
    {
        (Paragraph a, Paragraph b) => a.DefaultStyle == b.DefaultStyle &&
            a.Style == (ignoreListIds ? b.Style with { ListId = a.Style.ListId } : b.Style) && a.Runs.SequenceEqual(b.Runs),
        (Section a, Section b) => a with { Id = b.Id, Blocks = b.Blocks } == b && a.Blocks.Length == b.Blocks.Length &&
            a.Blocks.Zip(b.Blocks).All(pair => Same(pair.First, pair.Second, ignoreListIds)),
        _ => false
    };

    internal static int MapPosition(string before, string after, int position)
    {
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        if (position < prefix) return position;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        if (suffix > 0 && position >= before.Length - suffix) return Math.Clamp(position + after.Length - before.Length, 0, after.Length);
        if (position <= prefix) return position;
        return Math.Min(position, after.Length - suffix);
    }
}
