using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

/// <summary>A versioned, self-contained clipboard fragment. Partial containers retain their formatting.</summary>
public sealed record DocumentFragment
{
    public const int CurrentVersion = 4;
    public int Version { get; init; } = CurrentVersion;
    public FlowDocument Document { get; init; } = new();
    /// <summary>Whether the first paragraph edge merges when pasted inside destination text.</summary>
    public bool StartsInsideParagraph { get; init; }
    /// <summary>Whether the last paragraph edge merges when pasted inside destination text.</summary>
    public bool EndsInsideParagraph { get; init; }

    public void Validate()
    {
        if (Version is not (1 or 2 or 3 or CurrentVersion)) throw new NotSupportedException($"Clipboard fragment version {Version} is not supported.");
        if (Document is null) throw new FormatException("Missing clipboard document.");
        Document.Validate();
    }
}

internal static class DocumentFragments
{
    internal static DocumentFragment Extract(FlowDocument document, DocumentIndex index, TextSelection selection)
    {
        if (selection.IsEmpty) return new();
        var positions = index.Paragraphs.ToDictionary(p => p.Paragraph.Id);
        (int Start, int End) Bounds(IEnumerable<Block> blocks)
        {
            var paragraphs = VisibleParagraphs(blocks).Select(p => positions[p.Id]).ToArray();
            return (paragraphs[0].Start, paragraphs[^1].End);
        }
        bool Included(int start, int end) => start < selection.End && end >= selection.Start;
        bool Complete(IEnumerable<Block> blocks)
        {
            var (start, end) = Bounds(blocks);
            return selection.Start <= start && selection.End >= end;
        }
        ImmutableArray<Block> Clip(IEnumerable<Block> blocks)
        {
            var result = ImmutableArray.CreateBuilder<Block>();
            foreach (var block in blocks)
            {
                var (start, end) = Bounds([block]);
                if (!Included(start, end)) continue;
                if (selection.Start <= start && selection.End >= end) { result.Add(block); continue; }
                switch (block)
                {
                    case Paragraph p:
                        var from = Math.Max(0, selection.Start - start);
                        var to = Math.Min(p.Length, selection.End - start);
                        result.Add(p with { Runs = p.Slice(from, Math.Max(0, to - from)) });
                        break;
                    case Section s:
                        result.Add(s with { Blocks = FlowDocument.EnsureBlocks(Clip(s.Blocks)) });
                        break;
                    case Table t:
                        var selected = new List<(int Row, int Column)>();
                        for (var r = 0; r < t.Rows.Length; r++)
                            for (var c = 0; c < t.ColumnCount; c++)
                            {
                                if (t.IsCovered(r, c)) continue;
                                var bounds = Bounds(t.Rows[r][c].Blocks);
                                if (Included(bounds.Start, bounds.End)) selected.Add((r, c));
                            }
                        if (selected.Count == 0) break;
                        var top = selected.Min(x => x.Row); var left = selected.Min(x => x.Column);
                        var bottom = selected.Max(x => x.Row); var right = selected.Max(x => x.Column);
                        ExpandRectangle(t, ref top, ref left, ref bottom, ref right);
                        result.Add(Crop(t, top, left, bottom, right, (r, c) =>
                        {
                            var owner = t.OwnerOf(r, c);
                            var cell = t.Rows[r][c];
                            if (Complete(t.Rows[owner.Row][owner.Column].Blocks)) return cell;
                            // A clipped merged cell must not retain unselected text in its split backups.
                            return cell with
                            {
                                Blocks = t.IsCovered(r, c) ? [new Paragraph()] : FlowDocument.EnsureBlocks(Clip(cell.Blocks)),
                                MergeOriginalBlocks = []
                            };
                        }));
                        break;
                }
            }
            return result.ToImmutable();
        }
        var blocks = Clip(document.Blocks);
        if (selection.End > 0 && index.CharAt(selection.End - 1) == '\n' &&
            !new FlowDocument(blocks).Text.EndsWith('\n')) blocks = blocks.Add(new Paragraph());
        var first = index.At(selection.Start); var last = index.At(selection.End);
        var result = document with { Blocks = blocks.Select(BlockOperations.CloneWithNewIds).ToImmutableArray() };
        result = result with { Sections = CopySections(document, index, blocks, result.Blocks, selection.Start) };
        result = CopyRanges(document, PruneStories(result), blocks, selection.Start, selection.End).PruneUnusedResources();
        result.Validate();
        return new() { Document = result, StartsInsideParagraph = selection.Start > first.Start,
            EndsInsideParagraph = selection.End < last.End && selection.End > last.Start };
    }

    internal static DocumentFragment ExtractCells(FlowDocument document, Guid tableId, int row, int column, int rowCount, int columnCount)
    {
        var table = FindTable(document.Blocks, tableId) ?? throw new ArgumentException("The table does not exist.", nameof(tableId));
        if (row < 0 || column < 0 || rowCount <= 0 || columnCount <= 0 ||
            (long)row + rowCount > table.Rows.Length || (long)column + columnCount > table.ColumnCount)
            throw new ArgumentOutOfRangeException(nameof(rowCount));
        var bottom = row + rowCount - 1; var right = column + columnCount - 1;
        ExpandRectangle(table, ref row, ref column, ref bottom, ref right);
        var cropped = Crop(table, row, column, bottom, right, (r, c) => table.Rows[r][c]);
        var result = PruneStories(document with { Blocks = [BlockOperations.CloneWithNewIds(cropped)], Sections = [] });
        result = CopyRanges(document, result, [cropped], 0).PruneUnusedResources();
        result.Validate();
        return new() { Document = result };
    }

    // Clipboard ranges are retained only when their complete content is copied. In
    // particular, clipping a bookmark does not create a new target with a misleading
    // meaning, and clipping a field copies its visible cached result as ordinary text.
    // Collapsed ranges on either selected boundary are included.
    private static FlowDocument CopyRanges(FlowDocument source, FlowDocument fragment,
        ImmutableArray<Block> original, int selectionStart, int? selectionEnd = null)
    {
        var sourceIndex = new DocumentIndex(source);
        var sourceParagraphs = sourceIndex.Paragraphs;
        var sourcePositions = sourceParagraphs.Select((entry, ordinal) => (Entry: entry, Ordinal: ordinal))
            .ToDictionary(item => item.Entry.Paragraph.Id);
        var paragraphs = new Dictionary<Guid, (Paragraph Paragraph, int Removed)>();
        var pairs = VisibleParagraphs(original).Zip(VisibleParagraphs(fragment.Blocks)).ToArray();
        foreach (var pair in pairs)
        {
            if (sourcePositions.TryGetValue(pair.First.Id, out var position))
                paragraphs.Add(pair.First.Id, (pair.Second, Math.Max(0, selectionStart - position.Entry.Start)));
            else if (selectionEnd is { } end && pair.Second.Id == pairs[^1].Second.Id &&
                sourceIndex.At(end) is { } boundary && boundary.Start == end && pair.Second.Length == 0)
                paragraphs.TryAdd(boundary.Paragraph.Id, (pair.Second, 0));
        }
        var omitted = new int[sourceParagraphs.Length + 1];
        for (var i = 0; i < sourceParagraphs.Length; i++)
            omitted[i + 1] = omitted[i] + (paragraphs.ContainsKey(sourceParagraphs[i].Paragraph.Id) ? 0 : 1);
        bool Included(DocumentAnchor start, DocumentAnchor end)
        {
            if (start.StoryId != Guid.Empty) return fragment.Stories.ContainsKey(start.StoryId);
            if (!paragraphs.TryGetValue(start.ParagraphId, out var first) ||
                !paragraphs.TryGetValue(end.ParagraphId, out var last) ||
                start.Offset < first.Removed || start.Offset > first.Removed + first.Paragraph.Length ||
                end.Offset < last.Removed || end.Offset > last.Removed + last.Paragraph.Length) return false;
            var from = sourcePositions[start.ParagraphId].Ordinal;
            var to = sourcePositions[end.ParagraphId].Ordinal;
            // A rectangular table selection can omit cells between its endpoints.
            return omitted[to + 1] == omitted[from];
        }
        DocumentAnchor Remap(DocumentAnchor anchor) => anchor.StoryId != Guid.Empty ? anchor : anchor with
        { ParagraphId = paragraphs[anchor.ParagraphId].Paragraph.Id, Offset = anchor.Offset - paragraphs[anchor.ParagraphId].Removed };
        return fragment with
        {
            Bookmarks = source.Bookmarks.Where(bookmark => Included(bookmark.Start, bookmark.End)).Select(bookmark => bookmark with
            { Id = Guid.NewGuid(), Start = Remap(bookmark.Start), End = Remap(bookmark.End) }).ToImmutableArray(),
            Fields = source.Fields.Where(field => Included(field.Start, field.End)).Select(field => field with
            { Id = Guid.NewGuid(), Start = Remap(field.Start), End = Remap(field.End) }).ToImmutableArray()
        };
    }

    // Clipboard fragments carry only note bodies and header/footer stories owned by their copied content.
    private static FlowDocument PruneStories(FlowDocument document)
    {
        var notes = new HashSet<Guid>();
        void Visit(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks) switch (block)
            {
                case Paragraph p: foreach (var run in p.Runs) if (run.Inline?.Payload is NoteInlinePayload reference) notes.Add(reference.NoteId); break;
                case Section s: Visit(s.Blocks); break;
                case Table t: foreach (var row in t.Rows) foreach (var cell in row) { Visit(cell.Blocks); Visit(cell.MergeOriginalBlocks); } break;
            }
        }
        Visit(document.Blocks);
        var retainedNotes = document.Notes.Where(note => notes.Contains(note.Id)).ToImmutableArray();
        var stories = retainedNotes.Select(note => note.StoryId).ToHashSet();
        foreach (var section in document.Sections) foreach (var footer in new[] { false, true })
            foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
                if (section.HeaderFooter.GetReference(footer, variant).StoryId is { } id) stories.Add(id);
        return document with { Stories = document.Stories.RemoveRange(document.Stories.Keys.Where(id => !stories.Contains(id))), Notes = retainedNotes };
    }

    private static ImmutableArray<DocumentSection> CopySections(FlowDocument document, DocumentIndex index,
        ImmutableArray<Block> original, ImmutableArray<Block> cloned, int offset)
    {
        if (document.Sections.IsEmpty) return [];
        var before = VisibleParagraphs(original).ToArray();
        var after = VisibleParagraphs(cloned).ToArray();
        var ids = before.Zip(after).ToDictionary(pair => pair.First.Id, pair => pair.Second.Id);
        var current = document.Sections[0];
        foreach (var section in document.Sections.Skip(1))
        {
            if (index.ById(section.StartParagraphId).Start > offset) break;
            current = section;
        }
        var result = ImmutableArray.CreateBuilder<DocumentSection>();
        var headerFooter = current.HeaderFooter;
        var currentIndex = document.Sections.IndexOf(current);
        foreach (var footer in new[] { false, true }) foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
            headerFooter = headerFooter.WithReference(footer, variant, new StoryReference
            { LinkToPrevious = false, StoryId = document.ResolveHeaderFooter(currentIndex, footer, variant)?.Id });
        result.Add(current with { Id = Guid.NewGuid(), StartParagraphId = Guid.Empty, HeaderFooter = headerFooter });
        foreach (var section in document.Sections.Skip(1))
            if (index.ById(section.StartParagraphId).Start > offset && ids.TryGetValue(section.StartParagraphId, out var id))
                result.Add(section with { Id = Guid.NewGuid(), StartParagraphId = id });
        return result.ToImmutable();
    }

    private static FlowDocument MergeSections(FlowDocument destination, FlowDocument fragment, FlowDocument result, Guid replacedId)
    {
        result = DocumentSection.Reconcile(result);
        // Initial source page settings are adopted on whole-document replacement by the caller.
        // Partial paste retains destination settings and carries copied interior boundaries.
        if (fragment.Sections.Length <= 1) return result;
        var sections = result.Sections.IsEmpty ? ImmutableArray.Create(new DocumentSection()) : result.Sections;
        var index = new DocumentIndex(result);
        var positions = index.Paragraphs.ToDictionary(entry => entry.Paragraph.Id, entry => entry.Start);
        var first = VisibleParagraphs(fragment.Blocks).First().Id;
        foreach (var section in fragment.Sections.Skip(1))
        {
            var id = section.StartParagraphId == first ? replacedId : section.StartParagraphId;
            if (!positions.ContainsKey(id) || !DocumentSection.IsOutsideTable(result.Blocks, id)) continue;
            sections = sections.Add(section with { StartParagraphId = id });
        }
        return DocumentSection.Reconcile(result with { Sections = sections.OrderBy(section =>
            section.StartParagraphId == Guid.Empty ? 0 : positions[section.StartParagraphId]).ToImmutableArray() });
    }
    private static void ExpandRectangle(Table table, ref int top, ref int left, ref int bottom, ref int right)
    {
        bool changed;
        do
        {
            var old = (top, left, bottom, right);
            for (var r = top; r <= bottom; r++)
                for (var c = left; c <= right; c++)
                {
                    var owner = table.OwnerOf(r, c); var cell = table.Rows[owner.Row][owner.Column];
                    top = Math.Min(top, owner.Row); left = Math.Min(left, owner.Column);
                    bottom = Math.Max(bottom, owner.Row + cell.RowSpan - 1);
                    right = Math.Max(right, owner.Column + cell.ColumnSpan - 1);
                }
            changed = old != (top, left, bottom, right);
        } while (changed);
    }

    private static Table Crop(Table table, int top, int left, int bottom, int right, Func<int, int, TableCell> cell) => table with
    {
        Rows = Enumerable.Range(top, bottom - top + 1).Select(r => Enumerable.Range(left, right - left + 1)
            .Select(c => cell(r, c)).ToImmutableArray()).ToImmutableArray(),
        ColumnWidths = table.ColumnWidths.IsEmpty ? [] : table.ColumnWidths.Skip(left).Take(right - left + 1).ToImmutableArray(),
        RowSizing = table.RowSizing.IsEmpty ? [] : table.RowSizing.Skip(top).Take(bottom - top + 1).ToImmutableArray()
    };

    private static Table? FindTable(IEnumerable<Block> blocks, Guid id)
    {
        foreach (var block in blocks)
        {
            if (block is Table t)
            {
                if (t.Id == id) return t;
                foreach (var row in t.Rows) foreach (var cell in row)
                    if (FindTable(cell.Blocks, id) is { } found) return found;
            }
            if (block is Section s && FindTable(s.Blocks, id) is { } sectionTable) return sectionTable;
        }
        return null;
    }

    internal static IEnumerable<Paragraph> VisibleParagraphs(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
            switch (block)
            {
                case Paragraph p: yield return p; break;
                case Section s:
                    foreach (var p in VisibleParagraphs(s.Blocks)) yield return p;
                    break;
                case Table t:
                    for (var r = 0; r < t.Rows.Length; r++) for (var c = 0; c < t.ColumnCount; c++)
                        if (!t.IsCovered(r, c)) foreach (var p in VisibleParagraphs(t.Rows[r][c].Blocks)) yield return p;
                    break;
            }
    }

    internal static FlowDocument Prepare(FlowDocument fragment, FlowDocument destination, IEnumerable<string>? bookmarkNamesInScope = null)
    {
        fragment = DocumentStyleImport.Prepare(fragment, destination);
        var resources = destination.Resources;
        var resourceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var listIds = new Dictionary<Guid, Guid>();
        var storyIds = fragment.Stories.Keys.ToDictionary(id => id, _ => Guid.NewGuid());
        var noteIds = fragment.Notes.ToDictionary(note => note.Id, _ => Guid.NewGuid());
        var paragraphIds = new Dictionary<Guid, Guid>();
        var bookmarkNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var destinationNames = (bookmarkNamesInScope ?? destination.Bookmarks.Select(bookmark => bookmark.Name)).ToHashSet(StringComparer.Ordinal);
        var occupiedNames = destinationNames.Concat(fragment.Bookmarks.Select(bookmark => bookmark.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (var bookmark in fragment.Bookmarks)
        {
            var name = bookmark.Name;
            if (destinationNames.Contains(name))
            {
                var suffix = 2;
                do
                {
                    var ending = "_" + suffix++;
                    name = bookmark.Name[..Math.Min(bookmark.Name.Length, 256 - ending.Length)] + ending;
                } while (!occupiedNames.Add(name));
            }
            bookmarkNames.Add(bookmark.Name, name);
        }
        var resolver = new DocumentStyleResolver(fragment);
        TextStyle RemapLinks(TextStyle style, Paragraph paragraph)
        {
            var link = resolver.ResolveText(paragraph, style).InternalLink;
            if (link is null || !bookmarkNames.TryGetValue(link.BookmarkName, out var name) || name == link.BookmarkName) return style;
            var remapped = link with { BookmarkName = name };
            return style with
            {
                InternalLink = remapped,
                Overrides = style.Overrides is { } overrides ? overrides with { InternalLink = new(remapped) } : null
            };
        }
        string Resource(string id)
        {
            if (resourceIds.TryGetValue(id, out var mapped)) return mapped;
            fragment.Resources.TryGetValue(id, out var incoming);
            mapped = id;
            if (resources.TryGetValue(mapped, out var existing) && existing != incoming)
                mapped = "resource-" + Guid.NewGuid().ToString("N");
            resourceIds.Add(id, mapped);
            if (incoming is not null) resources = resources.SetItem(mapped, incoming);
            return mapped;
        }
        var fonts = destination.Fonts.ToBuilder();
        foreach (var font in fragment.Fonts)
        {
            var copy = font with { ResourceId = Resource(font.ResourceId) };
            if (!fonts.Contains(copy)) fonts.Add(copy);
        }
        ImmutableArray<Block> Clone(IEnumerable<Block> blocks) => blocks.Select(CloneBlock).ToImmutableArray();
        Block CloneBlock(Block block)
        {
            switch (block)
            {
                case Paragraph p:
                    var paragraphId = Guid.NewGuid();
                    paragraphIds.Add(p.Id, paragraphId);
                    var style = p.Style;
                    if (resolver.ResolveParagraphStyle(style).ListId is { } id)
                    {
                        if (!listIds.TryGetValue(id, out var replacement)) listIds.Add(id, replacement = Guid.NewGuid());
                        style = style with { ListId = replacement,
                            Overrides = style.Overrides is { } overrides ? overrides with { ListId = replacement } : null };
                    }
                    return p with { Id = paragraphId, Style = style, DefaultStyle = RemapLinks(p.DefaultStyle, p), Runs = p.Runs.Select(run =>
                    {
                        run = run with { Style = RemapLinks(run.Style, p) };
                        if (run.Inline is not { } inline) return run;
                        var payload = inline.Payload;
                        if (payload is ImageInlinePayload image)
                        {
                            var target = Resource(image.ResourceId);
                            payload = image with { ResourceId = target };
                        }
                        if (payload is NoteInlinePayload note) payload = note with { NoteId = noteIds[note.NoteId] };
                        return run with { Inline = inline with { Id = Guid.NewGuid(), Payload = payload } };
                    }).ToImmutableArray() };
                case Section s: return s with { Id = Guid.NewGuid(), Blocks = Clone(s.Blocks) };
                case Table t: return t with { Id = Guid.NewGuid(), Rows = t.Rows.Select(row => row.Select(cell => cell with
                { Id = Guid.NewGuid(), Blocks = Clone(cell.Blocks), MergeOriginalBlocks = Clone(cell.MergeOriginalBlocks) }).ToImmutableArray()).ToImmutableArray() };
                default: throw new FormatException("Unknown clipboard block.");
            }
        }
        var copied = Clone(fragment.Blocks);
        var stories = fragment.Stories.Values.ToImmutableDictionary(story => storyIds[story.Id], story =>
            story with { Id = storyIds[story.Id], Blocks = Clone(story.Blocks) });
        var sections = CopySections(fragment, new DocumentIndex(fragment), fragment.Blocks, copied, 0).Select(section =>
        {
            var settings = section.HeaderFooter;
            foreach (var footer in new[] { false, true }) foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
            {
                var reference = settings.GetReference(footer, variant);
                if (reference.StoryId is { } id) settings = settings.WithReference(footer, variant, reference with { StoryId = storyIds[id] });
            }
            return section with { HeaderFooter = settings };
        }).ToImmutableArray();
        string RemapInstruction(string instruction)
        {
            try { return Textalonia.Model.Fields.FieldInstructionParser.RewriteBookmarkReferences(instruction, bookmarkNames); }
            catch (FormatException) { return instruction; }
        }
        DocumentAnchor RemapAnchor(DocumentAnchor anchor) => anchor with
        { StoryId = anchor.StoryId == Guid.Empty ? Guid.Empty : storyIds[anchor.StoryId], ParagraphId = paragraphIds[anchor.ParagraphId] };
        return fragment with { Blocks = copied, Resources = resources, Fonts = fonts.ToImmutable(), Stories = stories,
            Notes = fragment.Notes.Select(note => note with { Id = noteIds[note.Id], StoryId = storyIds[note.StoryId] }).ToImmutableArray(), Sections = sections,
            Bookmarks = fragment.Bookmarks.Select(bookmark => bookmark with { Id = Guid.NewGuid(), Name = bookmarkNames[bookmark.Name],
                Start = RemapAnchor(bookmark.Start), End = RemapAnchor(bookmark.End) }).ToImmutableArray(),
            Fields = fragment.Fields.Select(field => field with { Id = Guid.NewGuid(), Instruction = RemapInstruction(field.Instruction),
                Start = RemapAnchor(field.Start), End = RemapAnchor(field.End) }).ToImmutableArray() };
    }

    internal static (FlowDocument Document, int Caret) Insert(FlowDocument destination, int offset, FlowDocument fragment, bool startsInsideParagraph, bool endsInsideParagraph)
    {
        var at = new DocumentIndex(destination).At(offset);
        var resolver = new DocumentStyleResolver(fragment);
        TextStyle Rebase(TextStyle style, Paragraph before, Paragraph after)
        {
            if (style.Overrides is null || before.Style == after.Style) return style;
            var original = resolver.ResolveText(before, style);
            var moved = resolver.ResolveText(after, style);
            return style with { Overrides = TextStyleOverrides.Difference(moved, original, style.Overrides) };
        }
        var prefix = at.Paragraph.Slice(0, offset - at.Start);
        var suffix = at.Paragraph.Slice(offset - at.Start, at.End - offset);
        var blocks = fragment.Blocks;
        // At paragraph boundaries preserve the existing inline paste behavior. Inside text,
        // clipped edges merge while complete paragraph edges retain their separation.
        var interior = !prefix.IsEmpty && !suffix.IsEmpty;
        var mergeStart = !interior || startsInsideParagraph;
        var mergeEnd = !interior || endsInsideParagraph;
        var firstParagraph = mergeStart ? blocks[0] as Paragraph : null;
        if (mergeStart && blocks[0] is Paragraph first)
        {
            var merged = first with { Id = at.Paragraph.Id,
                Style = prefix.IsEmpty && at.Paragraph.Length == 0 ? first.Style : at.Paragraph.Style };
            blocks = blocks.SetItem(0, merged with
            {
                DefaultStyle = Rebase(first.DefaultStyle, first, merged),
                Runs = Paragraph.Normalize(prefix.Concat(first.Runs.Select(run =>
                    run with { Style = Rebase(run.Style, first, merged) })))
            });
        }
        else if (!prefix.IsEmpty) blocks = blocks.Insert(0, at.Paragraph with { Runs = prefix });
        Guid caretParagraph; int caretLocal;
        if (mergeEnd && blocks[^1] is Paragraph last)
        {
            caretParagraph = last.Id; caretLocal = last.Length;
            blocks = blocks.SetItem(blocks.Length - 1, last with { Runs = Paragraph.Normalize(last.Runs.Concat(
                suffix.Select(run => run with { Style = Rebase(run.Style, at.Paragraph, last) }))) });
        }
        else
        {
            // A paragraph after a structural paste gives the caret a stable, editable destination.
            var tail = at.Paragraph with { Id = Guid.NewGuid(), Runs = suffix };
            blocks = blocks.Add(tail); caretParagraph = tail.Id; caretLocal = 0;
        }
        ImmutableArray<Block> Replace(ImmutableArray<Block> source) => source.SelectMany<Block, Block>(block =>
            block.Id == at.Paragraph.Id ? blocks : block switch
            {
                Section s => [s with { Blocks = Replace(s.Blocks) }],
                Table t => [t with { Rows = t.Rows.Select((row, r) => row.Select((cell, c) => t.IsCovered(r, c) ? cell :
                    cell with { Blocks = Replace(cell.Blocks) }).ToImmutableArray()).ToImmutableArray() }],
                _ => [block]
            }).ToImmutableArray();
        var result = destination with { Blocks = Replace(destination.Blocks), Resources = fragment.Resources,
            Styles = fragment.Styles, Fonts = fragment.Fonts, Stories = destination.Stories.SetItems(fragment.Stories), Notes = destination.Notes.AddRange(fragment.Notes) };
        result = MergeSections(destination, fragment, result, at.Paragraph.Id);
        result = DocumentAnchors.Transform(destination, result, Guid.Empty, offset, 0,
            new DocumentIndex(result).Length - new DocumentIndex(destination).Length);
        DocumentAnchor Place(DocumentAnchor anchor) => anchor.StoryId == Guid.Empty && anchor.ParagraphId == firstParagraph?.Id
            ? anchor with { ParagraphId = at.Paragraph.Id, Offset = anchor.Offset + prefix.Sum(run => run.Text.Length) } : anchor;
        result = result with
        {
            Bookmarks = result.Bookmarks.AddRange(fragment.Bookmarks.Select(bookmark => bookmark with
            { Start = Place(bookmark.Start), End = Place(bookmark.End) })),
            Fields = result.Fields.AddRange(fragment.Fields.Select(field => field with
            { Start = Place(field.Start), End = Place(field.End) }))
        };
        result.Validate();
        return (result, new DocumentIndex(result).ById(caretParagraph).Start + caretLocal);
    }
}
