using System.Collections.Immutable;
using System.Text.Json;
using Textalonia.Model;
using Textalonia.Model.Fields;

namespace Textalonia.Serialization;

/// <summary>Splits rich runs only at annotation boundaries; no field instruction occupies visible text.</summary>
internal static class RangeInterchange
{
    internal sealed record Item(RichRun? Run = null, DocumentBookmark? Bookmark = null, DocumentField? Field = null, bool Start = false,
        DocumentContentControl? ContentControl = null, DocumentPermissionRange? Permission = null);

    internal static IEnumerable<Item> Items(FlowDocument document, Paragraph paragraph, bool includeForms = false)
    {
        var marks = new SortedDictionary<int, List<Item>>();
        void Add(DocumentAnchor anchor, Item item)
        {
            if (anchor.ParagraphId != paragraph.Id) return;
            if (!marks.TryGetValue(anchor.Offset, out var values)) marks.Add(anchor.Offset, values = []);
            values.Add(item);
        }
        foreach (var bookmark in document.Bookmarks)
        {
            Add(bookmark.Start, new(Bookmark: bookmark, Start: true));
            Add(bookmark.End, new(Bookmark: bookmark));
        }
        foreach (var field in document.Fields)
        {
            Add(field.Start, new(Field: field, Start: true));
            Add(field.End, new(Field: field));
        }
        if (includeForms)
        {
            foreach (var control in document.ContentControls.Where(c => !c.IsAtomic))
            {
                Add(control.Start, new(ContentControl: control, Start: true));
                Add(control.End, new(ContentControl: control));
            }
            foreach (var permission in document.PermissionRanges)
            {
                Add(permission.Start, new(Permission: permission, Start: true));
                Add(permission.End, new(Permission: permission));
            }
        }
        var emitted = new HashSet<int>();
        IEnumerable<Item> Boundary(int offset)
        {
            if (!emitted.Add(offset) || !marks.TryGetValue(offset, out var items)) return [];
            // Close inner ranges before opening adjacent ranges. Empty ranges open and close together.
            var empty = items.Where(i => i.ContentControl is { } c && c.Start.ParagraphId == c.End.ParagraphId && c.Start.Offset == c.End.Offset ||
                i.Permission is { } p && p.Start.ParagraphId == p.End.ParagraphId && p.Start.Offset == p.End.Offset ||
                i.Field is { } f && f.Start.ParagraphId == f.End.ParagraphId && f.Start.Offset == f.End.Offset || i.Bookmark is { } b &&
                b.Start.ParagraphId == b.End.ParagraphId && b.Start.Offset == b.End.Offset).ToArray();
            return items.Except(empty).Where(i => !i.Start).OrderByDescending(i => i.Field?.Start.Resolve(document) ?? i.ContentControl?.Start.Resolve(document) ?? int.MaxValue)
                .ThenByDescending(i => i.ContentControl is { } control ? document.ContentControls.IndexOf(control) : -1)
                .Concat(items.Except(empty).Where(i => i.Start).OrderByDescending(i => i.Field?.End.Resolve(document) ?? i.ContentControl?.End.Resolve(document) ?? int.MaxValue))
                .Concat(empty.OrderByDescending(i => i.Start));
        }
        var position = 0;
        foreach (var run in paragraph.Runs)
        {
            foreach (var item in Boundary(position)) yield return item;
            if (run.Inline is not null) { yield return new(Run: run); position += run.Storage.Length; continue; }
            var end = position + run.Storage.Length;
            var local = 0;
            foreach (var boundary in marks.Keys.Where(p => p > position && p < end))
            {
                var length = boundary - position - local;
                yield return new(Run: new RichRun(run.Text.Substring(local, length), run.Style));
                local += length;
                foreach (var item in Boundary(boundary)) yield return item;
            }
            if (local < run.Storage.Length) yield return new(Run: new RichRun(run.Text[local..], run.Style));
            position = end;
        }
        foreach (var item in Boundary(position)) yield return item;
    }

    internal sealed record InstructionPart(string Text, bool Nested = false);
    internal static IReadOnlyList<InstructionPart> InstructionParts(string source)
    {
        FieldInstruction parsed;
        try { parsed = FieldInstructionParser.Parse(source); }
        catch (FormatException) { return [new(source)]; }
        source = parsed.Source;
        var parts = new List<InstructionPart>(); var quoted = false; var start = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (quoted && source[i] == '\\' && i + 1 < source.Length && source[i + 1] is '\\' or '"' or '{' or '}') { i++; continue; }
            if (source[i] == '"') { quoted = !quoted; continue; }
            if (source[i] != '{') continue;
            if (i > start) parts.Add(new(source[start..i]));
            var begin = i + 1; i = End(begin);
            parts.Add(new(source[begin..i], true)); start = i + 1;
        }
        if (start < source.Length) parts.Add(new(source[start..]));
        return parts;
        int End(int position)
        {
            var inQuotes = false;
            for (; position < source.Length; position++)
            {
                var ch = source[position];
                if (inQuotes && ch == '\\' && position + 1 < source.Length && source[position + 1] is '\\' or '"' or '{' or '}') { position++; continue; }
                if (ch == '"') inQuotes = !inQuotes;
                else if (ch == '{') position = End(position + 1);
                else if (ch == '}' && !inQuotes) return position;
            }
            throw new FormatException("Unclosed nested field instruction.");
        }
    }

    internal static string PreferOriginalInstruction(string original, string imported)
    {
        try
        {
            if (FieldInstructionParser.Write(FieldInstructionParser.Parse(original)) == FieldInstructionParser.Write(FieldInstructionParser.Parse(imported))) return original;
        }
        catch (FormatException) { }
        return imported;
    }

    internal static string Encode<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, JsonDocumentFormat.Options));
    internal static T Decode<T>(string value, int maximumLength = 256 * 1024)
    {
        if (value.Length > maximumLength) throw new FormatException("Range metadata exceeds its size limit.");
        using var json = JsonDocument.Parse(Convert.FromBase64String(value), new JsonDocumentOptions { MaxDepth = 256 });
        JsonDocumentFormat.ValidateUniqueMembers(json.RootElement);
        return json.RootElement.Deserialize<T>(JsonDocumentFormat.Options) ?? throw new FormatException("Missing range metadata.");
    }

    internal static void DiagnoseField(string instruction, string format, Guid? id = null, string? sourceLocation = null)
    {
        try
        {
            var parsed = FieldInstructionParser.Parse(instruction);
            if (FieldEvaluator.IsSupported(parsed.Code) && (parsed.Code != "MERGEFIELD" || MergeFieldInstructions.Parse(instruction) is not null)) return;
        }
        catch (FormatException) { }
        ConversionDiagnostics.Report(format + ".unsupported-field", "Unknown or malformed field instruction",
            "Preserved the instruction and rich cached result; evaluation leaves the cached result unchanged.", id, sourceLocation);
    }

    internal static FlowDocument ResolveStories(FlowDocument document)
    {
        var owners = new Dictionary<Guid, Guid>();
        foreach (var story in document.Stories.Values)
            foreach (var entry in new DocumentIndex(new FlowDocument(story.Blocks)).Paragraphs) owners[entry.Paragraph.Id] = story.Id;
        DocumentAnchor Anchor(DocumentAnchor anchor) => anchor with { StoryId = owners.GetValueOrDefault(anchor.ParagraphId) };
        return document with
        {
            Bookmarks = document.Bookmarks.Select(b => b with { Start = Anchor(b.Start), End = Anchor(b.End) }).ToImmutableArray(),
            Fields = document.Fields.Select(f => f with { Start = Anchor(f.Start), End = Anchor(f.End) }).ToImmutableArray(),
            ContentControls = document.ContentControls.Select(c => c with { Start = Anchor(c.Start), End = Anchor(c.End) }).ToImmutableArray(),
            PermissionRanges = document.PermissionRanges.Select(p => p with { Start = Anchor(p.Start), End = Anchor(p.End) }).ToImmutableArray()
        };
    }
}
