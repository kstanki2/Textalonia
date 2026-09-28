using System.Collections.Immutable;

namespace Textalonia.Model;

internal static class DocumentAnchors
{
    internal static void Validate(FlowDocument document)
    {
        if (document.Bookmarks.IsDefault || document.Fields.IsDefault || document.Bookmarks.Length > 10000 || document.Fields.Length > 10000 ||
            document.Properties is null || document.Properties.Count > 4096 || document.Stories is null)
            throw new FormatException("Invalid document ranges or properties.");
        foreach (var pair in document.Properties)
            if (!InlineDescriptor.ValidKey(pair.Key) || pair.Value is null || pair.Value.Length > 1048576)
                throw new FormatException("Invalid document property.");
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Range(Guid id, DocumentAnchor start, DocumentAnchor end)
        {
            if (id == Guid.Empty || !ids.Add(id) || start is null || end is null || start.StoryId != end.StoryId || start.Resolve(document) > end.Resolve(document))
                throw new FormatException("Invalid anchored range.");
        }
        foreach (var bookmark in document.Bookmarks)
        {
            if (bookmark is null || !InlineDescriptor.ValidKey(bookmark.Name) || !names.Add(bookmark.Name))
                throw new FormatException("Bookmark names must be unique, nonempty, and at most 256 characters.");
            Range(bookmark.Id, bookmark.Start, bookmark.End);
        }
        foreach (var field in document.Fields)
        {
            if (field is null || string.IsNullOrWhiteSpace(field.Instruction) || field.Instruction.Length > 16384 || field.Instruction.Contains('\0'))
                throw new FormatException("Invalid field instruction.");
            field.LegacyMergeField?.Validate();
            Range(field.Id, field.Start, field.End);
        }
        foreach (var story in document.Fields.GroupBy(f => f.Start.StoryId))
        {
            var stack = new Stack<int>();
            foreach (var range in story.Select(f => (Start: f.Start.Resolve(document), End: f.End.Resolve(document))).OrderBy(f => f.Start).ThenByDescending(f => f.End))
            {
                while (stack.Count > 0 && range.Start >= stack.Peek()) stack.Pop();
                if (stack.Count > 0 && range.End > stack.Peek()) throw new FormatException("Field boundaries cannot partially overlap.");
                stack.Push(range.End);
                if (stack.Count > 32) throw new FormatException("Field nesting exceeds 32 levels.");
            }
        }
    }

    internal static FlowDocument WithStory(FlowDocument document, Guid storyId, FlowDocument projection)
    {
        if (storyId == Guid.Empty) return projection;
        return document with
        {
            Stories = document.Stories.SetItem(storyId, document.Stories[storyId] with { Blocks = projection.Blocks }),
            Resources = document.Resources.SetItems(projection.Resources), Styles = projection.Styles,
            Defaults = projection.Defaults, Theme = projection.Theme, Fonts = projection.Fonts, Properties = projection.Properties,
            Bookmarks = document.Bookmarks.Where(b => b.Start.StoryId != storyId).Concat(projection.Bookmarks.Select(b => b with
                { Start = b.Start with { StoryId = storyId }, End = b.End with { StoryId = storyId } })).ToImmutableArray(),
            Fields = document.Fields.Where(f => f.Start.StoryId != storyId).Concat(projection.Fields.Select(f => f with
                { Start = f.Start with { StoryId = storyId }, End = f.End with { StoryId = storyId } })).ToImmutableArray(),
            ContentControls = document.ContentControls.Where(c => c.Start.StoryId != storyId).Concat(projection.ContentControls.Select(c => c with
                { Start = c.Start with { StoryId = storyId }, End = c.End with { StoryId = storyId } })).ToImmutableArray(),
            PermissionRanges = document.PermissionRanges.Where(r => r.Start.StoryId != storyId).Concat(projection.PermissionRanges.Select(r => r with
                { Start = r.Start with { StoryId = storyId }, End = r.End with { StoryId = storyId } })).ToImmutableArray()
        };
    }

    internal static FlowDocument Transform(FlowDocument before, FlowDocument after, Guid storyId, int start, int removedLength, int insertedLength)
    {
        if (before.Bookmarks.IsEmpty && before.Fields.IsEmpty && before.ContentControls.IsEmpty && before.PermissionRanges.IsEmpty) return after;
        var index = after.GetStoryIndex(storyId);
        DocumentAnchor Map(DocumentAnchor anchor)
        {
            if (anchor.StoryId != storyId) return anchor;
            var offset = anchor.Resolve(before);
            int position;
            if (offset < start) position = offset;
            else if (offset > start + removedLength) position = offset + insertedLength - removedLength;
            else if (removedLength > 0 && offset == start + removedLength) position = start + insertedLength;
            else position = start + (anchor.Affinity == AnchorAffinity.After ? insertedLength : 0);
            position = Snap(index, Math.Clamp(position, 0, index.Length), anchor.Affinity);
            return DocumentAnchor.Create(after, storyId, position, anchor.Affinity);
        }
        var oldBookmarks = before.Bookmarks.ToDictionary(b => b.Id);
        var oldFields = before.Fields.ToDictionary(f => f.Id);
        var oldControls = before.ContentControls.ToDictionary(c => c.Id);
        var oldPermissions = before.PermissionRanges.ToDictionary(r => r.Id);
        return ContentControlValidation.SynchronizeValues(ContentControlValidation.ReconcileAtomicRanges(after with
        {
            Bookmarks = after.Bookmarks.Select(b => oldBookmarks.TryGetValue(b.Id, out var old) && b == old
                ? b with { Start = Map(b.Start), End = Map(b.End) } : b).ToImmutableArray(),
            Fields = after.Fields.Select(f => oldFields.TryGetValue(f.Id, out var old) && f == old
                ? f with { Start = Map(f.Start), End = Map(f.End), IsDirty = f.IsDirty || removedLength != 0 || insertedLength != 0 } : f).ToImmutableArray(),
            ContentControls = after.ContentControls.Select(c => oldControls.TryGetValue(c.Id, out var old) && c == old
                ? c with { Start = Map(c.Start), End = Map(c.End) } : c).ToImmutableArray(),
            PermissionRanges = after.PermissionRanges.Select(r => oldPermissions.TryGetValue(r.Id, out var old) && r == old
                ? r with { Start = Map(r.Start), End = Map(r.End) } : r).ToImmutableArray()
        }));
    }

    // Unmapped application/structural edits preserve paragraph identities; a deleted boundary
    // collapses into the changed span. Explicit range operations use Transform instead.
    internal static FlowDocument Reconcile(FlowDocument before, FlowDocument after)
    {
        if (before.Bookmarks.IsEmpty && before.Fields.IsEmpty && before.ContentControls.IsEmpty && before.PermissionRanges.IsEmpty) return after;
        var oldBookmarks = before.Bookmarks.ToDictionary(b => b.Id);
        var oldFields = before.Fields.ToDictionary(f => f.Id);
        var oldControls = before.ContentControls.ToDictionary(c => c.Id);
        var oldPermissions = before.PermissionRanges.ToDictionary(r => r.Id);
        var cache = new Dictionary<Guid, (DocumentIndex Before, DocumentIndex After)>();
        var clones = new Dictionary<Guid, Dictionary<Guid, Guid>>();
        var contentChanged = before.Blocks != after.Blocks || before.Stories != after.Stories;
        DocumentAnchor Map(DocumentAnchor anchor)
        {
            if (anchor.StoryId != Guid.Empty && !after.Stories.ContainsKey(anchor.StoryId)) return anchor;
            if (!cache.TryGetValue(anchor.StoryId, out var pair))
            {
                cache[anchor.StoryId] = pair = (before.GetStoryIndex(anchor.StoryId), after.GetStoryIndex(anchor.StoryId));
                clones[anchor.StoryId] = MapTableClones(before.GetStoryDocument(anchor.StoryId).Blocks, after.GetStoryDocument(anchor.StoryId).Blocks);
            }
            var oldEntry = pair.Before.ById(anchor.ParagraphId);
            var targetId = clones[anchor.StoryId].GetValueOrDefault(anchor.ParagraphId, anchor.ParagraphId);
            if (pair.After.Tree.Paths?.Find(targetId) is not null)
            {
                var next = pair.After.ById(targetId);
                if (ReferenceEquals(oldEntry.Paragraph, next.Paragraph)) return anchor;
                // A split keeps the original identity on the first paragraph, while its
                // trailing anchors belong to a newly created paragraph. A local diff
                // would incorrectly collapse those anchors at the end of the first piece.
                if (targetId == anchor.ParagraphId && pair.Before.ParagraphCount != pair.After.ParagraphCount)
                {
                    var mapped = MapText(pair.Before.Text, pair.After.Text, oldEntry.Start + anchor.Offset, anchor.Affinity);
                    return DocumentAnchor.Create(after, anchor.StoryId, Snap(pair.After, mapped, anchor.Affinity), anchor.Affinity);
                }
                var offset = MapText(oldEntry.Paragraph.Text, next.Paragraph.Text, anchor.Offset, anchor.Affinity);
                var snapped = Snap(pair.After, next.Start + offset, anchor.Affinity);
                return DocumentAnchor.Create(after, anchor.StoryId, snapped, anchor.Affinity);
            }
            var absolute = MapText(pair.Before.Text, pair.After.Text, oldEntry.Start + anchor.Offset, anchor.Affinity);
            return DocumentAnchor.Create(after, anchor.StoryId, Snap(pair.After, absolute, anchor.Affinity), anchor.Affinity);
        }
        bool Exists(DocumentAnchor a) => a.StoryId == Guid.Empty || after.Stories.ContainsKey(a.StoryId);
        return ContentControlValidation.SynchronizeValues(ContentControlValidation.ReconcileAtomicRanges(after with
        {
            Bookmarks = after.Bookmarks.Where(b => Exists(b.Start)).Select(b => oldBookmarks.TryGetValue(b.Id, out var old) && b == old
                ? b with { Start = Map(b.Start), End = Map(b.End) } : b).ToImmutableArray(),
            Fields = after.Fields.Where(f => Exists(f.Start)).Select(f => oldFields.TryGetValue(f.Id, out var old) && f == old
                ? f with { Start = Map(f.Start), End = Map(f.End), IsDirty = f.IsDirty || contentChanged } : f).ToImmutableArray(),
            ContentControls = after.ContentControls.Where(c => Exists(c.Start)).Select(c => oldControls.TryGetValue(c.Id, out var old) && c == old
                ? c with { Start = Map(c.Start), End = Map(c.End) } : c).ToImmutableArray(),
            PermissionRanges = after.PermissionRanges.Where(r => Exists(r.Start)).Select(r => oldPermissions.TryGetValue(r.Id, out var old) && r == old
                ? r with { Start = Map(r.Start), End = Map(r.End) } : r).ToImmutableArray()
        }));
    }

    // Merged-cell content is cloned so hidden restoration backups retain distinct IDs.
    // Its source order can differ from document order (for example a vertical merge),
    // so a whole-document text diff cannot identify these replacement paragraphs.
    private static Dictionary<Guid, Guid> MapTableClones(ImmutableArray<Block> before, ImmutableArray<Block> after)
    {
        static IEnumerable<Table> Tables(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                if (block is Section section)
                    foreach (var nested in Tables(section.Blocks)) yield return nested;
                if (block is not Table table) continue;
                yield return table;
                for (var r = 0; r < table.Rows.Length; r++) for (var c = 0; c < table.ColumnCount; c++)
                    if (!table.IsCovered(r, c)) foreach (var nested in Tables(table.Rows[r][c].Blocks)) yield return nested;
            }
        }
        static IEnumerable<Block> Cells(Table table, int row, int column, int rows, int columns)
        {
            for (var r = row; r < row + rows; r++) for (var c = column; c < column + columns; c++)
                foreach (var block in table.Rows[r][c].Blocks) yield return block;
        }
        var originalTables = Tables(before).ToDictionary(table => table.Id);
        var result = new Dictionary<Guid, Guid>();
        foreach (var table in Tables(after))
        {
            if (!originalTables.TryGetValue(table.Id, out var original) || original.Rows.Length != table.Rows.Length || original.ColumnCount != table.ColumnCount) continue;
            for (var r = 0; r < table.Rows.Length; r++) for (var c = 0; c < table.ColumnCount; c++)
            {
                if (table.IsCovered(r, c) || original.IsCovered(r, c)) continue;
                var old = original.Rows[r][c]; var current = table.Rows[r][c];
                IEnumerable<Block>? from = null, to = null;
                if (old.RowSpan == 1 && old.ColumnSpan == 1 && (current.RowSpan > 1 || current.ColumnSpan > 1))
                { from = Cells(original, r, c, current.RowSpan, current.ColumnSpan); to = current.Blocks; }
                else if (current.RowSpan == 1 && current.ColumnSpan == 1 && (old.RowSpan > 1 || old.ColumnSpan > 1))
                { from = old.Blocks; to = Cells(table, r, c, old.RowSpan, old.ColumnSpan); }
                if (from is null || to is null || !BlockOperations.ContentEquals(from, to)) continue;
                foreach (var pair in Textalonia.Editing.DocumentFragments.VisibleParagraphs(from).Zip(Textalonia.Editing.DocumentFragments.VisibleParagraphs(to)))
                    result[pair.First.Id] = pair.Second.Id;
            }
        }
        return result;
    }

    private static int Snap(DocumentIndex index, int offset, AnchorAffinity affinity)
    {
        var snapped = index.Snap(offset);
        return snapped < offset && affinity == AnchorAffinity.After ? index.NextCaret(snapped) : snapped;
    }

    private static int MapText(string before, string after, int offset, AnchorAffinity affinity)
    {
        if (before == after) return offset;
        var start = 0;
        while (start < Math.Min(before.Length, after.Length) && before[start] == after[start]) start++;
        var end = before.Length; var nextEnd = after.Length;
        while (end > start && nextEnd > start && before[end - 1] == after[nextEnd - 1]) { end--; nextEnd--; }
        if (offset < start) return offset;
        if (offset > end || offset == end && end > start) return offset + after.Length - before.Length;
        return affinity == AnchorAffinity.After ? nextEnd : start;
    }
}
