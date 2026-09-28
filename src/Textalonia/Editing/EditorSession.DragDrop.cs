using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

// An immutable fragment and source range are captured before entering the native drag loop.
// Selection-only changes are harmless; any document revision change rejects the snapshot.
internal sealed class ContentDragSnapshot(EditorSession source, int revision, TextSelection selection, DocumentFragment fragment)
{
    internal EditorSession Source { get; } = source;
    internal int Revision { get; } = revision;
    internal TextSelection Selection { get; } = selection;
    internal DocumentFragment Fragment { get; } = fragment;
    internal bool Completed { get; set; }
    internal bool IsCurrent => !Completed && Source.Revision == Revision;
}

internal enum ContentDropResult { None, Copy, Move }

public sealed partial class EditorSession
{
    internal ContentDragSnapshot? CaptureContentDrag() => Selection.IsEmpty ? null : new(this, Revision, Selection, CopyFragment());

    internal bool CanDropContent(ContentDragSnapshot? source, int offset, int targetRevision)
    {
        if (IsReadOnly || Revision != targetRevision || offset < 0 || offset > Index.Length) return false;
        if (source is null) return true;
        if (!source.IsCurrent) return false;
        // Both edges belong to the source range. Copying or moving here is an intentional no-op.
        return !ReferenceEquals(source.Source, this) || offset < source.Selection.Start || offset > source.Selection.End;
    }

    internal ContentDropResult DropContent(DocumentFragment fragment, int offset, int targetRevision,
        ContentDragSnapshot? source = null, bool move = false)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (!CanDropContent(source, offset, targetRevision)) return ContentDropResult.None;
        offset = Snap(offset);
        if (!CanDropContent(source, offset, targetRevision)) return ContentDropResult.None;
        move &= source is not null && !source.Source.IsReadOnly;
        var destination = ActiveDocument;
        FlowDocument? removed = null;
        DocumentBookmark[] movedBookmarks = [];
        DocumentField[] movedFields = [];
        if (move)
        {
            // Build and validate both sides without changing either session. Failed insertion
            // therefore cannot delete source content or add a source history entry.
            var original = source!.Source.ActiveDocument;
            var copiedNames = fragment.Document.Bookmarks.Select(bookmark => bookmark.Name).ToHashSet(StringComparer.Ordinal);
            movedBookmarks = original.Bookmarks.Where(bookmark => copiedNames.Contains(bookmark.Name)).ToArray();
            bool Copied(DocumentField field) => field.Start.StoryId == Guid.Empty
                ? source.Selection.Start <= field.Start.Resolve(original) && field.End.Resolve(original) <= source.Selection.End
                : fragment.Document.Stories.ContainsKey(field.Start.StoryId);
            movedFields = original.Fields.Where(Copied).ToArray();
            var bookmarkIds = movedBookmarks.Select(bookmark => bookmark.Id).ToHashSet();
            var fieldIds = movedFields.Select(field => field.Id).ToHashSet();
            removed = RemoveDraggedSelection(original with
            { Bookmarks = original.Bookmarks.Where(bookmark => !bookmarkIds.Contains(bookmark.Id)).ToImmutableArray(),
                Fields = original.Fields.Where(field => !fieldIds.Contains(field.Id)).ToImmutableArray() }, source.Selection);
            if (ReferenceEquals(source.Source, this))
            {
                destination = removed;
                if (offset > source.Selection.End) offset -= Index.Length - new DocumentIndex(removed).Length;
            }
        }
        var occupiedNames = Document.Bookmarks.Where(bookmark => !move || !ReferenceEquals(source!.Source, this) ||
            !movedBookmarks.Any(moved => moved.Id == bookmark.Id)).Select(bookmark => bookmark.Name);
        var (inserted, caret) = BuildFragmentInsertion(destination, new(offset, offset), fragment, occupiedNames);
        if (move && ReferenceEquals(source!.Source, this))
        {
            // Moving a complete range retains its public identity and target name. The
            // clipboard path still clones content IDs to keep hidden merge backups unique.
            var bookmarks = movedBookmarks.ToDictionary(bookmark => bookmark.Name, StringComparer.Ordinal);
            var previousFields = destination.Fields.Select(field => field.Id).ToHashSet();
            var fieldIds = inserted.Fields.Where(field => !previousFields.Contains(field.Id)).Zip(movedFields)
                .ToDictionary(pair => pair.First.Id, pair => pair.Second.Id);
            inserted = inserted with
            {
                Bookmarks = inserted.Bookmarks.Select(bookmark => bookmarks.TryGetValue(bookmark.Name, out var old)
                    ? bookmark with { Id = old.Id } : bookmark).ToImmutableArray(),
                Fields = inserted.Fields.Select(field => fieldIds.TryGetValue(field.Id, out var id) ? field with { Id = id } : field).ToImmutableArray()
            };
            inserted.Validate();
        }
        // Same-session moves are published exactly once, including their undo and selection state.
        // Consume before publishing: a host Changed handler must not re-enter with the
        // same drag and insert it twice while a cross-editor source is still unchanged.
        if (source is not null) source.Completed = true;
        Commit(inserted, new(caret, caret));
        if (source is null) return ContentDropResult.Copy;
        if (move && !ReferenceEquals(source.Source, this))
        {
            // Each editor owns its own undo history. Target Changed handlers may edit the
            // source: if so retain it and report a copy instead of deleting a stale range.
            if (source.Source.Revision == source.Revision && !source.Source.IsReadOnly)
            {
                var sourceCaret = Math.Min(source.Selection.Start, new DocumentIndex(removed!).Length);
                source.Source.Commit(removed!, new(sourceCaret, sourceCaret));
            }
            else move = false;
        }
        source.Completed = true;
        return move ? ContentDropResult.Move : ContentDropResult.Copy;
    }

    private static FlowDocument RemoveDraggedSelection(FlowDocument document, TextSelection selection)
    {
        var index = new DocumentIndex(document);
        var positions = index.Paragraphs.ToDictionary(p => p.Paragraph.Id);
        var completeContainers = new HashSet<Guid>();
        void Collect(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                if (block is Paragraph) continue;
                var visible = DocumentFragments.VisibleParagraphs([block]).ToArray();
                if (visible.Length > 0 && selection.Start <= positions[visible[0].Id].Start && selection.End >= positions[visible[^1].Id].End)
                { completeContainers.Add(block.Id); continue; }
                if (block is Section section) Collect(section.Blocks);
                else if (block is Table table)
                    for (var r = 0; r < table.Rows.Length; r++) for (var c = 0; c < table.ColumnCount; c++)
                        if (!table.IsCovered(r, c)) Collect(table.Rows[r][c].Blocks);
            }
        }
        Collect(document.Blocks);
        var (removed, _) = ReplaceRange(document, selection, [new Paragraph()]);
        ImmutableArray<Block> Prune(ImmutableArray<Block> blocks) => FlowDocument.EnsureBlocks(blocks
            .Where(block => !completeContainers.Contains(block.Id)).Select(block => block switch
            {
                Section section => (Block)(section with { Blocks = Prune(section.Blocks) }),
                Table table => table with { Rows = table.Rows.Select((row, r) => row.Select((cell, c) =>
                    table.IsCovered(r, c) ? cell : cell with { Blocks = Prune(cell.Blocks) }).ToImmutableArray()).ToImmutableArray() },
                _ => block
            }).ToImmutableArray());
        if (completeContainers.Count > 0) removed = DocumentAnchors.Reconcile(removed, removed with { Blocks = Prune(removed.Blocks) });
        removed = DocumentSection.Reconcile(removed).PruneUnusedResources();
        removed.Validate();
        return removed;
    }
}
