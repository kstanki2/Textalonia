using System.Collections.Immutable;
using Textalonia.Model;
using Textalonia.Model.Fields;

namespace Textalonia.MailMerge;

/// <summary>Validates and expands structural TableStart/TableEnd merge-field regions.</summary>
internal static class MailMergeRegions
{
    internal sealed record Expansion(FlowDocument Document,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> ParagraphValues,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> FieldValues);

    private enum BoundaryKind { Start, End }
    private sealed record Boundary(BoundaryKind Kind, string Name);
    private abstract record BlockNode;
    private sealed record OrdinaryBlock(Block Block) : BlockNode;
    private sealed record BlockRegion(string Name, ImmutableArray<BlockNode> Children) : BlockNode;
    private abstract record RowNode;
    private sealed record OrdinaryRow(ImmutableArray<TableCell> Cells, TableRowSizing? Sizing) : RowNode;
    private sealed record RowRegion(string Name, ImmutableArray<RowNode> Children) : RowNode;
    private sealed record Occurrence(Guid StoryId, Guid SourceId, Guid ResultId, string Path);

    internal static void Validate(FlowDocument template)
    {
        var paths = new Dictionary<(Guid StoryId, Guid ParagraphId), string>();
        var regionId = 0;
        VisitBlocks(ParseBlocks(template.Blocks), Guid.Empty, "");
        foreach (var story in template.Stories.Values) VisitBlocks(ParseBlocks(story.Blocks), story.Id, "");
        foreach (var range in template.Bookmarks.Select(b => (b.Start, b.End))
                     .Concat(template.Fields.Select(f => (f.Start, f.End)))
                     .Concat(template.ContentControls.Select(c => (c.Start, c.End)))
                     .Concat(template.PermissionRanges.Select(p => (p.Start, p.End))))
        {
            if (!paths.TryGetValue((range.Start.StoryId, range.Start.ParagraphId), out var startPath) ||
                !paths.TryGetValue((range.End.StoryId, range.End.ParagraphId), out var endPath) ||
                startPath != endPath)
                throw new FormatException("An anchored range crosses a mail-merge region boundary.");
        }
        foreach (var section in template.Sections.Skip(1))
            if (!paths.ContainsKey((Guid.Empty, section.StartParagraphId)))
                throw new FormatException("A physical section cannot start on a mail-merge region marker.");

        void VisitBlocks(ImmutableArray<BlockNode> nodes, Guid storyId, string path)
        {
            foreach (var node in nodes)
            {
                if (node is BlockRegion region)
                    VisitBlocks(region.Children, storyId, path + "/" + ++regionId);
                else if (node is OrdinaryBlock ordinary)
                    VisitBlock(ordinary.Block, storyId, path);
            }
        }
        void VisitBlock(Block block, Guid storyId, string path)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    paths.TryAdd((storyId, paragraph.Id), path);
                    if (path.Length != 0 && paragraph.Runs.Any(run => run.Inline?.Payload is NoteInlinePayload))
                        throw new NotSupportedException("Repeating a note reference inside a mail-merge region is not supported.");
                    break;
                case Section section: VisitBlocks(ParseBlocks(section.Blocks), storyId, path); break;
                case Table table:
                    foreach (var node in ParseRows(table)) VisitRow(node, storyId, path);
                    break;
            }
        }
        void VisitRow(RowNode node, Guid storyId, string path)
        {
            if (node is RowRegion region)
            {
                var childPath = path + "/" + ++regionId;
                foreach (var child in region.Children) VisitRow(child, storyId, childPath);
            }
            else if (node is OrdinaryRow row)
                foreach (var cell in row.Cells) VisitBlocks(ParseBlocks(cell.Blocks), storyId, path);
        }
    }

    internal static Expansion Expand(FlowDocument template, IReadOnlyDictionary<string, object?> values,
        MailMergeOptions options, CancellationToken token)
    {
        if (!HasMarkers(template))
            return new(template, new Dictionary<Guid, IReadOnlyDictionary<string, object?>>(),
                new Dictionary<Guid, IReadOnlyDictionary<string, object?>>());
        var worker = new Worker(template, options, token);
        return worker.Expand(values);
    }

    private static bool HasMarkers(FlowDocument template)
    {
        bool Visit(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
                switch (block)
                {
                    case Paragraph paragraph when paragraph.Runs.Any(run =>
                        run.Inline?.Payload is MergeFieldInlinePayload field && IsRegionName(field.Name)):
                        return true;
                    case Section section when Visit(section.Blocks): return true;
                    case Table table:
                        foreach (var row in table.Rows)
                            foreach (var cell in row)
                                if (Visit(cell.Blocks) || Visit(cell.MergeOriginalBlocks)) return true;
                        break;
                }
            return false;
        }
        return Visit(template.Blocks) || template.Stories.Values.Any(story => Visit(story.Blocks));
    }

    private static ImmutableArray<BlockNode> ParseBlocks(ImmutableArray<Block> blocks)
    {
        var offset = 0;
        var result = Parse(null);
        if (offset != blocks.Length) throw new FormatException("Invalid mail-merge region structure.");
        return result;

        ImmutableArray<BlockNode> Parse(string? expected)
        {
            var nodes = ImmutableArray.CreateBuilder<BlockNode>();
            while (offset < blocks.Length)
            {
                var block = blocks[offset++];
                if (block is Paragraph paragraph && Marker(paragraph) is { } marker)
                {
                    if (marker.Kind == BoundaryKind.End)
                    {
                        if (expected is null || marker.Name != expected)
                            throw new FormatException($"Unmatched TableEnd:{marker.Name} mail-merge region.");
                        return nodes.ToImmutable();
                    }
                    nodes.Add(new BlockRegion(marker.Name, Parse(marker.Name)));
                    continue;
                }
                CheckBlock(block);
                nodes.Add(new OrdinaryBlock(block));
            }
            if (expected is not null) throw new FormatException($"Unmatched TableStart:{expected} mail-merge region.");
            return nodes.ToImmutable();
        }
    }

    private static ImmutableArray<RowNode> ParseRows(Table table)
    {
        var offset = 0;
        var result = Parse(null);
        if (offset != table.Rows.Length) throw new FormatException("Invalid table mail-merge region structure.");
        return result;

        ImmutableArray<RowNode> Parse(string? expected)
        {
            var nodes = ImmutableArray.CreateBuilder<RowNode>();
            while (offset < table.Rows.Length)
            {
                var rowIndex = offset++;
                var row = table.Rows[rowIndex];
                if (RowMarker(row) is { } marker)
                {
                    if (rowIndex < table.RepeatHeaderRows)
                        throw new FormatException("Table-row region markers cannot be placed in repeating header rows.");
                    if (marker.Kind == BoundaryKind.End)
                    {
                        if (expected is null || marker.Name != expected)
                            throw new FormatException($"Unmatched TableEnd:{marker.Name} table-row region.");
                        return nodes.ToImmutable();
                    }
                    nodes.Add(new RowRegion(marker.Name, Parse(marker.Name)));
                    continue;
                }
                foreach (var cell in row)
                {
                    ParseBlocks(cell.Blocks);
                    if (!cell.MergeOriginalBlocks.IsEmpty) ParseBlocks(cell.MergeOriginalBlocks);
                }
                nodes.Add(new OrdinaryRow(row, table.RowSizing.IsEmpty ? null : table.RowSizing[rowIndex]));
            }
            if (expected is not null) throw new FormatException($"Unmatched TableStart:{expected} table-row region.");
            return nodes.ToImmutable();
        }
    }

    private static void CheckBlock(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                if (paragraph.Runs.Any(run => run.Inline?.Payload is MergeFieldInlinePayload field &&
                    IsRegionName(field.Name)))
                    throw new FormatException("A TableStart or TableEnd marker must occupy its own paragraph or table row.");
                break;
            case Section section: ParseBlocks(section.Blocks); break;
            case Table table:
                var rows = ParseRows(table);
                if (rows.Any(node => node is RowRegion) && table.Rows.Any(row => row.Any(cell => cell.RowSpan != 1)))
                    throw new FormatException("Table-row regions cannot cross vertically merged cells.");
                break;
        }
    }

    private static Boundary? Marker(Paragraph paragraph)
    {
        if (paragraph.Runs.Length != 1 || paragraph.Runs[0].Inline?.Payload is not MergeFieldInlinePayload field)
            return null;
        if (field.Name.StartsWith("TableStart:", StringComparison.OrdinalIgnoreCase))
            return Named(BoundaryKind.Start, field.Name[11..]);
        if (field.Name.StartsWith("TableEnd:", StringComparison.OrdinalIgnoreCase))
            return Named(BoundaryKind.End, field.Name[9..]);
        return null;
        static Boundary Named(BoundaryKind kind, string name) => !string.IsNullOrWhiteSpace(name) && name == name.Trim()
            ? new(kind, name) : throw new FormatException("Mail-merge region names must be nonempty and cannot have surrounding spaces.");
    }

    private static bool IsRegionName(string name) =>
        name.StartsWith("TableStart:", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("TableEnd:", StringComparison.OrdinalIgnoreCase);

    private static Boundary? RowMarker(ImmutableArray<TableCell> row)
    {
        Boundary? found = null;
        foreach (var cell in row)
        {
            if (cell.RowSpan != 1 || cell.ColumnSpan != 1 || !cell.MergeOriginalBlocks.IsEmpty) return null;
            if (cell.Blocks.Length != 1 || cell.Blocks[0] is not Paragraph paragraph) return null;
            if (Marker(paragraph) is { } marker)
            {
                if (found is not null) throw new FormatException("A table-row region boundary must contain one marker.");
                found = marker;
            }
            else if (paragraph.Runs.Length != 0) return null;
        }
        return found;
    }

    private sealed class Worker(FlowDocument template, MailMergeOptions options, CancellationToken token)
    {
        private readonly List<Occurrence> _paragraphs = [];
        private readonly Dictionary<Guid, IReadOnlyDictionary<string, object?>> _paragraphValues = [];
        private readonly Dictionary<Guid, IReadOnlyDictionary<string, object?>> _fieldValues = [];
        private readonly Dictionary<string, Dictionary<Guid, Guid>> _listIds = new(StringComparer.Ordinal);
        private int _instance;
        private int _expandedItems;

        internal Expansion Expand(IReadOnlyDictionary<string, object?> values)
        {
            var blocks = ExpandBlocks(ParseBlocks(template.Blocks), values, "", Guid.Empty);
            if (blocks.IsEmpty) blocks = [new Paragraph()];
            var stories = template.Stories;
            foreach (var pair in template.Stories)
            {
                token.ThrowIfCancellationRequested();
                var content = ExpandBlocks(ParseBlocks(pair.Value.Blocks), values, "", pair.Key);
                stories = stories.SetItem(pair.Key, pair.Value with
                { Blocks = content.IsEmpty ? [new Paragraph()] : content });
            }
            var result = template with { Blocks = blocks, Stories = stories };
            result = CopyRanges(result);
            result.Validate();
            return new(result, _paragraphValues, _fieldValues);
        }

        private ImmutableArray<Block> ExpandBlocks(ImmutableArray<BlockNode> nodes,
            IReadOnlyDictionary<string, object?> values, string path, Guid storyId)
        {
            var output = ImmutableArray.CreateBuilder<Block>();
            foreach (var node in nodes)
            {
                token.ThrowIfCancellationRequested();
                if (node is OrdinaryBlock ordinary)
                {
                    if (CopyBlock(ordinary.Block, values, path, storyId) is { } block) output.Add(block);
                }
                else if (node is BlockRegion region)
                {
                    var itemIndex = 0;
                    foreach (var child in Children(values, region.Name))
                    {
                        options.RegionProgress?.Invoke(new(region.Name, itemIndex, path.Count(c => c == '/') + 1, true));
                        output.AddRange(ExpandBlocks(region.Children, child, NextPath(path), storyId));
                        options.RegionProgress?.Invoke(new(region.Name, itemIndex++, path.Count(c => c == '/') + 1, false));
                    }
                }
            }
            return output.ToImmutable();
        }

        private Block? CopyBlock(Block block, IReadOnlyDictionary<string, object?> values, string path, Guid storyId)
        {
            var repeated = path.Length != 0;
            switch (block)
            {
                case Paragraph paragraph:
                {
                    if (repeated && paragraph.Runs.Any(run => run.Inline?.Payload is NoteInlinePayload))
                        throw new NotSupportedException("Repeating a note reference inside a mail-merge region is not supported.");
                    var copy = paragraph with
                    {
                        Id = repeated ? Guid.NewGuid() : paragraph.Id,
                        Style = repeated ? CopyListStyle(paragraph.Style, path) : paragraph.Style,
                        Runs = repeated ? paragraph.Runs.Select(run => run.Inline is null ? run :
                            run with { Inline = run.Inline with { Id = Guid.NewGuid() } }).ToImmutableArray() : paragraph.Runs
                    };
                    _paragraphs.Add(new(storyId, paragraph.Id, copy.Id, path));
                    // Historical merged-cell backups may reuse a visible paragraph ID.
                    _paragraphValues.TryAdd(copy.Id, values);
                    return copy;
                }
                case Section section:
                {
                    var content = ExpandBlocks(ParseBlocks(section.Blocks), values, path, storyId);
                    return section with { Id = repeated ? Guid.NewGuid() : section.Id,
                        Blocks = content.IsEmpty ? [new Paragraph()] : content };
                }
                case Table table:
                {
                    var rows = ImmutableArray.CreateBuilder<ImmutableArray<TableCell>>();
                    var sizing = ImmutableArray.CreateBuilder<TableRowSizing>();
                    foreach (var node in ParseRows(table)) AddRow(node, values, path);
                    if (rows.Count == 0) return null;
                    return table with { Id = repeated ? Guid.NewGuid() : table.Id, Rows = rows.ToImmutable(),
                        RowSizing = table.RowSizing.IsEmpty ? [] : sizing.ToImmutable(),
                        RepeatHeaderRows = table.RepeatHeaderRows };

                    void AddRow(RowNode node, IReadOnlyDictionary<string, object?> scope, string rowPath)
                    {
                        token.ThrowIfCancellationRequested();
                        if (node is RowRegion region)
                        {
                            var itemIndex = 0;
                            foreach (var child in Children(scope, region.Name))
                            {
                                options.RegionProgress?.Invoke(new(region.Name, itemIndex, rowPath.Count(c => c == '/') + 1, true));
                                var childPath = NextPath(rowPath);
                                foreach (var nested in region.Children) AddRow(nested, child, childPath);
                                options.RegionProgress?.Invoke(new(region.Name, itemIndex++, rowPath.Count(c => c == '/') + 1, false));
                            }
                            return;
                        }
                        var ordinary = (OrdinaryRow)node;
                        var copied = ordinary.Cells.Select(cell => cell with
                        {
                            Id = rowPath.Length == 0 ? cell.Id : Guid.NewGuid(),
                            Blocks = Ensure(ExpandBlocks(ParseBlocks(cell.Blocks), scope, rowPath, storyId)),
                            MergeOriginalBlocks = cell.MergeOriginalBlocks.IsEmpty ? [] :
                                ExpandBlocks(ParseBlocks(cell.MergeOriginalBlocks), scope, rowPath, storyId)
                        }).ToImmutableArray();
                        rows.Add(copied);
                        if (ordinary.Sizing is not null) sizing.Add(ordinary.Sizing);
                    }
                }
                default: throw new FormatException("Unknown mail-merge template block.");
            }
        }

        private static ImmutableArray<Block> Ensure(ImmutableArray<Block> blocks) =>
            blocks.IsEmpty ? [new Paragraph()] : blocks;

        private ParagraphStyle CopyListStyle(ParagraphStyle style, string path)
        {
            var effective = new DocumentStyleResolver(template).ResolveParagraphStyle(style);
            if (effective.ListId is not { } sourceId) return style;
            if (!_listIds.TryGetValue(path, out var map)) _listIds.Add(path, map = []);
            if (!map.TryGetValue(sourceId, out var id)) map.Add(sourceId, id = Guid.NewGuid());
            return style with { ListId = id,
                Overrides = style.Overrides is { } overrides ? overrides with { ListId = id } : null };
        }

        private string NextPath(string parent)
        {
            if (++_expandedItems > 100_000) throw new FormatException("Mail-merge regions exceed the expansion limit.");
            return parent + "/" + ++_instance;
        }

        private IEnumerable<IReadOnlyDictionary<string, object?>> Children(
            IReadOnlyDictionary<string, object?> values, string name)
        {
            if (!values.TryGetValue(name, out var raw) || raw is null) yield break;
            if (raw is not IEnumerable<IReadOnlyDictionary<string, object?>> records)
                throw new FormatException($"Mail-merge region '{name}' requires a collection of records.");
            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                if (record is null) throw new FormatException($"Mail-merge region '{name}' contains a null record.");
                // Collections belong to their immediate parent. Only scalar ancestor values
                // flow into a detail row, so a missing child collection stays empty.
                var scope = values.Where(pair => pair.Value is not IEnumerable<IReadOnlyDictionary<string, object?>>)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                foreach (var pair in record) scope[pair.Key] = pair.Value;
                yield return scope;
            }
        }

        private FlowDocument CopyRanges(FlowDocument result)
        {
            var positions = _paragraphs.GroupBy(p => (p.StoryId, p.SourceId)).ToDictionary(g => g.Key,
                g => g.GroupBy(p => p.Path).ToDictionary(p => p.Key, p => p.First().ResultId, StringComparer.Ordinal));
            IEnumerable<(DocumentAnchor Start, DocumentAnchor End, string Path)> Copies(DocumentAnchor start, DocumentAnchor end)
            {
                if (!positions.TryGetValue((start.StoryId, start.ParagraphId), out var starts) ||
                    !positions.TryGetValue((end.StoryId, end.ParagraphId), out var ends)) yield break;
                foreach (var pair in starts)
                    if (ends.TryGetValue(pair.Key, out var endId))
                        yield return (start with { ParagraphId = pair.Value }, end with { ParagraphId = endId }, pair.Key);
            }

            var bookmarks = ImmutableArray.CreateBuilder<DocumentBookmark>();
            var bookmarkNames = new Dictionary<(string Name, string Path), string>();
            var usedNames = template.Bookmarks.Select(bookmark => bookmark.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var bookmark in template.Bookmarks)
                foreach (var (start, end, path) in Copies(bookmark.Start, bookmark.End))
                {
                    var name = path.Length == 0 ? bookmark.Name : UniqueName(bookmark.Name, usedNames);
                    bookmarkNames.Add((bookmark.Name, path), name);
                    bookmarks.Add(bookmark with { Id = path.Length == 0 ? bookmark.Id : Guid.NewGuid(),
                        Name = name, Start = start, End = end });
                }
            var fields = ImmutableArray.CreateBuilder<DocumentField>();
            foreach (var field in template.Fields)
                foreach (var (start, end, path) in Copies(field.Start, field.End))
                {
                    var copy = field with { Id = path.Length == 0 ? field.Id : Guid.NewGuid(),
                        Start = start, End = end, IsDirty = true,
                        Instruction = RemapInstruction(field.Instruction, path, bookmarkNames) };
                    fields.Add(copy);
                    if (path.Length != 0) _fieldValues.Add(copy.Id, _paragraphValues[start.ParagraphId]);
                }
            var controls = ImmutableArray.CreateBuilder<DocumentContentControl>();
            var controlIds = new Dictionary<(Guid Id, string Path), Guid>();
            foreach (var control in template.ContentControls)
                foreach (var (start, end, path) in Copies(control.Start, control.End))
                {
                    var id = path.Length == 0 ? control.Id : Guid.NewGuid();
                    controlIds.Add((control.Id, path), id);
                    controls.Add(control with { Id = id, Start = start, End = end });
                }
            var permissions = ImmutableArray.CreateBuilder<DocumentPermissionRange>();
            foreach (var permission in template.PermissionRanges)
                foreach (var (start, end, path) in Copies(permission.Start, permission.End))
                    permissions.Add(permission with { Id = path.Length == 0 ? permission.Id : Guid.NewGuid(),
                        Start = start, End = end });
            // A range crossing a repeated-region boundary cannot be copied meaningfully.
            foreach (var range in template.Bookmarks.Select(b => (b.Start, b.End))
                         .Concat(template.Fields.Select(f => (f.Start, f.End)))
                         .Concat(template.ContentControls.Select(c => (c.Start, c.End)))
                         .Concat(template.PermissionRanges.Select(p => (p.Start, p.End))))
            {
                var starts = positions.GetValueOrDefault((range.Start.StoryId, range.Start.ParagraphId));
                var ends = positions.GetValueOrDefault((range.End.StoryId, range.End.ParagraphId));
                if (starts is null && ends is null) continue; // An empty region removed the entire range.
                if (starts is null || ends is null || !starts.Keys.Any(ends.ContainsKey))
                    throw new FormatException("An anchored range crosses a mail-merge region boundary.");
            }
            var sections = ImmutableArray.CreateBuilder<DocumentSection>();
            var protectedIds = ImmutableArray.CreateBuilder<Guid>();
            if (!template.Sections.IsEmpty)
            {
                sections.Add(template.Sections[0]);
                if (template.Protection.ProtectedSectionIds.Contains(template.Sections[0].Id))
                    protectedIds.Add(template.Sections[0].Id);
                var index = new DocumentIndex(result);
                var candidates = new List<(int Position, DocumentSection Section, Guid SourceId)>();
                foreach (var section in template.Sections.Skip(1))
                    if (positions.TryGetValue((Guid.Empty, section.StartParagraphId), out var copies))
                        foreach (var pair in copies)
                        {
                            var copy = section with { Id = pair.Key.Length == 0 ? section.Id : Guid.NewGuid(),
                                StartParagraphId = pair.Value };
                            candidates.Add((index.ById(pair.Value).Start, copy, section.Id));
                        }
                // Removing an empty region can bring several section boundaries to the
                // same paragraph. The last boundary supplies the surviving settings.
                foreach (var entry in candidates.GroupBy(candidate => candidate.Position)
                             .OrderBy(group => group.Key).Select(group => group.Last()))
                {
                    if (entry.Position == 0)
                    {
                        sections[0] = entry.Section with { StartParagraphId = Guid.Empty };
                        protectedIds.Clear();
                        if (template.Protection.ProtectedSectionIds.Contains(entry.SourceId))
                            protectedIds.Add(entry.Section.Id);
                        continue;
                    }
                    sections.Add(entry.Section);
                    if (template.Protection.ProtectedSectionIds.Contains(entry.SourceId)) protectedIds.Add(entry.Section.Id);
                }
            }
            var resultWithLinks = result with
            {
                Bookmarks = bookmarks.ToImmutable(), Fields = fields.ToImmutable(),
                ContentControls = controls.ToImmutable(), PermissionRanges = permissions.ToImmutable(),
                Sections = sections.ToImmutable(),
                Protection = template.Protection with { ProtectedSectionIds = protectedIds.ToImmutable() }
            };
            return RemapLinks(resultWithLinks, bookmarkNames, controlIds);
        }

        private static string UniqueName(string original, HashSet<string> used)
        {
            var suffix = 1;
            string name;
            do
            {
                var ending = "_merge" + suffix++;
                name = original[..Math.Min(original.Length, 256 - ending.Length)] + ending;
            } while (!used.Add(name));
            return name;
        }

        private static string RemapInstruction(string instruction, string path,
            Dictionary<(string Name, string Path), string> names)
        {
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in names.OrderBy(pair => pair.Key.Path.Length))
                if (pair.Key.Path == path || path.StartsWith(pair.Key.Path + "/", StringComparison.Ordinal))
                    replacements[pair.Key.Name] = pair.Value;
            if (replacements.Count == 0) return instruction;
            try { return FieldInstructionParser.RewriteBookmarkReferences(instruction, replacements); }
            catch (FormatException) { return instruction; }
        }

        private FlowDocument RemapLinks(FlowDocument document,
            Dictionary<(string Name, string Path), string> names,
            Dictionary<(Guid Id, string Path), Guid> controlIds)
        {
            var paths = _paragraphs.GroupBy(p => p.ResultId).ToDictionary(g => g.Key, g => g.First().Path);
            string? MappedName(string name, string path)
            {
                while (true)
                {
                    if (names.TryGetValue((name, path), out var mapped)) return mapped;
                    var slash = path.LastIndexOf('/');
                    if (slash < 0) return null;
                    path = path[..slash];
                }
            }
            InternalLinkDestination? Link(InternalLinkDestination? link, string path) =>
                link is not null && MappedName(link.BookmarkName, path) is { } mapped && mapped != link.BookmarkName
                    ? link with { BookmarkName = mapped } : link;
            TextStyle Style(TextStyle style, string path) => style with
            {
                InternalLink = Link(style.InternalLink, path),
                Overrides = style.Overrides is { } overrides && overrides.InternalLink.IsSet
                    ? overrides with { InternalLink = new(Link(overrides.InternalLink.Value, path)) } : style.Overrides
            };
            ImmutableArray<Block> Rewrite(ImmutableArray<Block> blocks) => blocks.Select<Block, Block>(block => block switch
            {
                Paragraph paragraph => paragraph with
                {
                    DefaultStyle = Style(paragraph.DefaultStyle, paths.GetValueOrDefault(paragraph.Id, "")),
                    Runs = paragraph.Runs.Select(run =>
                    {
                        var path = paths.GetValueOrDefault(paragraph.Id, "");
                        if (run.Inline?.Payload is FormControlInlinePayload form &&
                            controlIds.TryGetValue((form.ControlId, path), out var id))
                            run = run with { Inline = run.Inline with { Payload = form with { ControlId = id } } };
                        return run with { Style = Style(run.Style, path) };
                    }).ToImmutableArray()
                },
                Section section => section with { Blocks = Rewrite(section.Blocks) },
                Table table => table with { Rows = table.Rows.Select(row => row.Select(cell => cell with
                { Blocks = Rewrite(cell.Blocks), MergeOriginalBlocks = Rewrite(cell.MergeOriginalBlocks) }).ToImmutableArray()).ToImmutableArray() },
                _ => block
            }).ToImmutableArray();
            return document with { Blocks = Rewrite(document.Blocks), Stories = document.Stories.ToImmutableDictionary(pair => pair.Key,
                pair => pair.Value with { Blocks = Rewrite(pair.Value.Blocks) }) };
        }
    }
}
