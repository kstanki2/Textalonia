using System.Collections.Immutable;
using System.Text;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class RtfDocumentFormat
{
    private sealed partial class Reader
    {
        private Guid _paragraphId = Guid.NewGuid();
        private readonly HashSet<Guid> _anchorParagraphs = [];
        private readonly List<DocumentBookmark> _bookmarks = [];
        private readonly List<DocumentField> _fields = [];
        private readonly Dictionary<string, DocumentBookmark> _bookmarkStarts = new(StringComparer.Ordinal);
        private ImmutableDictionary<string, string> _properties = ImmutableDictionary<string, string>.Empty;
        private int _fieldDepth;

        private DocumentAnchor RangeAnchor(AnchorAffinity affinity = AnchorAffinity.After)
        {
            Flush(); _anchorParagraphs.Add(_paragraphId);
            return new() { ParagraphId = _paragraphId, Offset = _runs.Sum(r => r.Storage.Length), Affinity = affinity };
        }
        private bool RangeDestination(Group group, State state)
        {
            if (group.Destination == "textaloniaproperties")
            { _properties = RangeInterchange.Decode<ImmutableDictionary<string, string>>(Plain(group).Trim(), 32 * 1024 * 1024); return true; }
            if (group.Destination == "textaloniafield" || group.Destination == "textaloniabookmark") return true;
            if (group.Destination == "bkmkstart")
            {
                var metadata = group.Groups("textaloniabookmark").FirstOrDefault();
                var names = group with { Nodes = group.Nodes.Where(n => n is not Group { Destination: "textaloniabookmark" }).ToList() };
                var name = Plain(names, state.UnicodeFallback).Trim();
                if (name.Length == 0) { Loss("rtf.bookmark", "Unnamed bookmark", "Omitted the unnamed marker.", group.Offset); return true; }
                var bookmark = metadata is null ? new DocumentBookmark() : RangeInterchange.Decode<DocumentBookmark>(Plain(metadata).Trim());
                if (_bookmarkStarts.ContainsKey(name)) Loss("rtf.bookmark", "Duplicate bookmark start", "Kept the most recent start marker.", group.Offset);
                _bookmarkStarts[name] = bookmark with { Name = name, Start = RangeAnchor(bookmark.Start.Affinity) };
                return true;
            }
            if (group.Destination == "bkmkend")
            {
                var name = Plain(group, state.UnicodeFallback).Trim();
                if (_bookmarkStarts.Remove(name, out var bookmark))
                {
                    var original = bookmark.Name; var suffix = 2;
                    while (_bookmarks.Any(b => b.Name.Equals(bookmark.Name, StringComparison.OrdinalIgnoreCase))) bookmark = bookmark with { Name = original + "_" + suffix++ };
                    if (original != bookmark.Name) Loss("rtf.bookmark-name", "Duplicate bookmark name", "Renamed the imported bookmark to " + bookmark.Name + ".", group.Offset);
                    _bookmarks.Add(bookmark with { End = RangeAnchor(bookmark.End.Affinity) });
                }
                else Loss("rtf.bookmark", "Unmatched bookmark end", "Omitted the detached marker.", group.Offset);
                return true;
            }
            return false;
        }
        private static Group InstructionGroup(Group group)
        {
            var nodes = new List<Node>();
            foreach (var node in group.Nodes)
            {
                if (node is not Group child) { nodes.Add(node); continue; }
                if (child.Destination == "field")
                {
                    var instructions = child.Groups("fldinst").ToArray();
                    if (instructions.Length != 1) throw new FormatException("Malformed nested RTF field instruction.");
                    var nested = InstructionGroup(instructions[0]);
                    nodes.Add(new Group([new Literal(" { ", false, child.Offset), .. nested.Nodes, new Literal(" } ", false, child.Offset)], child.Offset));
                }
                else nodes.Add(InstructionGroup(child));
            }
            return group with { Nodes = nodes };
        }
        private void ReadGeneralField(Group group, State state, string instruction, Group[] results, Group? metadata)
        {
            if (string.IsNullOrWhiteSpace(instruction))
            {
                Loss("rtf.unsupported-field", "Empty field instruction", "Retained the rich cached result without field metadata.", group.Offset);
                foreach (var result in results) Walk(result, state);
                return;
            }
            if (++_fieldDepth > 32) throw new FormatException("RTF field nesting exceeds the limit.");
            try
            {
                var field = metadata is null ? new DocumentField() : RangeInterchange.Decode<DocumentField>(Plain(metadata).Trim());
                field = field with { Instruction = metadata is null ? instruction : RangeInterchange.PreferOriginalInstruction(field.Instruction, instruction), Start = RangeAnchor(field.Start.Affinity),
                    IsLocked = group.Nodes.OfType<Control>().Any(c => c.Word == "fldlock"),
                    IsDirty = group.Nodes.OfType<Control>().Any(c => c.Word == "flddirty") };
                foreach (var result in results) Walk(result, state);
                field = field with { End = RangeAnchor(field.End.Affinity) };
                _fields.Add(field); RangeInterchange.DiagnoseField(instruction, "rtf", field.Id, "rtf:" + group.Offset);
            }
            finally { _fieldDepth--; }
        }
    }

    private static void WriteRangeBoundary(StringBuilder output, RangeInterchange.Item item)
    {
        if (item.Bookmark is { } bookmark)
        {
            output.Append(item.Start ? "{\\*\\bkmkstart " : "{\\*\\bkmkend ").Append(Escape(bookmark.Name));
            if (item.Start) output.Append("{\\*\\textaloniabookmark ").Append(RangeInterchange.Encode(bookmark)).Append('}');
            output.Append('}');
        }
        if (item.Field is not { } field) return;
        if (!item.Start) { output.Append("}}"); return; }
        RangeInterchange.DiagnoseField(field.Instruction, "rtf", field.Id);
        output.Append("{\\field");
        if (field.IsLocked) output.Append("\\fldlock");
        if (field.IsDirty) output.Append("\\flddirty");
        output.Append("{\\*\\textaloniafield ").Append(RangeInterchange.Encode(field)).Append('}').Append("{\\*\\fldinst ");
        WriteInstruction(output, field.Instruction);
        output.Append("}{\\fldrslt ");
    }
    private static void WriteInstruction(StringBuilder output, string instruction)
    {
        foreach (var part in RangeInterchange.InstructionParts(instruction))
            if (!part.Nested) output.Append(Escape(part.Text));
            else
            {
                output.Append("{\\field{\\*\\fldinst "); WriteInstruction(output, part.Text); output.Append("}{\\fldrslt }}");
            }
    }

}
