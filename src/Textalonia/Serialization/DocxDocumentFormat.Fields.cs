using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class DocxDocumentFormat
{
    private static readonly XNamespace Tx = "urn:textalonia:fields:1";
    private sealed class RangeField
    {
        internal DocumentField Field = new();
        internal XElement Begin = null!;
        internal XElement? End;
        internal XElement? Separator;
        internal StringBuilder Instruction = new();
        internal List<XElement> InstructionNodes = [];
        internal RangeField? Parent;
        internal bool Nested;
        internal bool InInstruction;
        internal bool General;
        internal bool Started;
    }
    private sealed record FieldBoundary(RangeField Field, bool Start);

    // Replace general field markers with inert events before the existing atomic-field reader runs.
    // Their rich results then flow through the ordinary paragraph/table/story importer unchanged.
    private static void PrepareGeneralFields(XDocument xml)
    {
        if (xml.Root?.Name.Namespace != W || xml.Root.Name.LocalName is not ("document" or "hdr" or "ftr" or "footnotes" or "endnotes")) return;
        foreach (var simple in xml.Descendants(W + "fldSimple").OrderByDescending(e => e.Ancestors().Count()).ToArray())
        {
            if (simple.Ancestors(W + "fldSimple").Take(32).Count() >= 32) throw new FormatException("DOCX field nesting exceeds the limit.");
            var instruction = (string?)simple.Attribute(W + "instr") ?? "";
            if (string.IsNullOrWhiteSpace(instruction))
            {
                Loss("unsupported-field", "Empty field instruction", "Retained the rich cached result without field metadata.", simple);
                simple.ReplaceWith(simple.Nodes().ToArray()); continue;
            }
            if (simple.Attribute(Tx + "field") is null && !simple.Ancestors(W + "fldSimple").Any() && !simple.Descendants().Any(e => e.Name == W + "bookmarkStart" || e.Name == W + "bookmarkEnd" || e.Annotation<FieldBoundary>() is not null) && (PageFieldInstructions.TryParse(instruction, out _) || MergeFieldInstructions.Parse(instruction) is not null)) continue;
            var field = new RangeField { Field = ReadFieldMetadata(simple, instruction), Begin = simple };
            var start = new XElement(Tx + "boundary"); start.AddAnnotation(new FieldBoundary(field, true));
            var end = new XElement(Tx + "boundary"); end.AddAnnotation(new FieldBoundary(field, false));
            simple.ReplaceWith(new object[] { start, simple.Nodes().ToArray(), end });
        }
        var stack = new Stack<RangeField>();
        var fields = new List<RangeField>();
        var suppressed = new HashSet<XElement>();
        foreach (var node in xml.Descendants().Where(e => e.Name == W + "fldChar" || e.Name == W + "instrText" || e.Name == W + "t" || e.Name == W + "drawing" || e.Name == W + "tab" || e.Name == W + "br" || e.Name == W + "bookmarkStart" || e.Name == W + "bookmarkEnd").ToArray())
        {
            if (node.Name == W + "fldChar")
            {
                var kind = (string?)node.Attribute(W + "fldCharType");
                if (kind == "begin")
                {
                    if (stack.Count >= 32) throw new FormatException("DOCX field nesting exceeds the limit.");
                    var parent = stack.TryPeek(out var parentField) ? parentField : null;
                    if (parent is not null) parent.Nested = true;
                    var field = new RangeField { Begin = node, Parent = parent, InInstruction = parent is not null && parent.Separator is null };
                    fields.Add(field); stack.Push(field);
                }
                else if (kind == "separate" && stack.TryPeek(out var separating)) separating.Separator = node;
                else if (kind == "end" && stack.TryPop(out var ending))
                {
                    ending.End = node;
                    if (ending.InInstruction)
                    {
                        ending.Parent!.Instruction.Append(" { ").Append(ending.Instruction).Append(" } ");
                        if (ending.Parent.Instruction.Length > 16384) throw new FormatException("DOCX field instruction exceeds its size limit.");
                    }
                }
            }
            else if (node.Name == W + "bookmarkStart" || node.Name == W + "bookmarkEnd")
            { foreach (var enclosing in stack) enclosing.General = true; }
            else if (node.Name == W + "instrText" && stack.TryPeek(out var current) && current.Separator is null)
            {
                current.Instruction.Append(node.Value); current.InstructionNodes.Add(node);
                if (current.Instruction.Length > 16384) throw new FormatException("DOCX field instruction exceeds its size limit.");
            }
            else if (stack.Any(f => f.InInstruction)) suppressed.Add(node);
        }
        foreach (var field in fields)
        {
            var instruction = field.Instruction.ToString().Trim();
            field.General = field.End is not null && !string.IsNullOrWhiteSpace(instruction) && (field.General || field.Begin.Attribute(Tx + "field") is not null || field.Nested || field.Parent is not null ||
                field.Begin.Ancestors(W + "p").FirstOrDefault() != field.End.Ancestors(W + "p").FirstOrDefault() ||
                !PageFieldInstructions.TryParse(instruction, out _) && MergeFieldInstructions.Parse(instruction) is null);
            if (field.General) field.Field = ReadFieldMetadata(field.Begin, instruction);
        }
        foreach (var field in fields.Where(f => f.General))
        {
            foreach (var instruction in field.InstructionNodes) instruction.Remove();
            field.Separator?.Remove();
            if (field.InInstruction || AncestorInstruction(field.Parent)) { field.Begin.Remove(); field.End!.Remove(); continue; }
            var start = new XElement(Tx + "boundary"); start.AddAnnotation(new FieldBoundary(field, true));
            var end = new XElement(Tx + "boundary"); end.AddAnnotation(new FieldBoundary(field, false));
            field.Begin.ReplaceWith(start); field.End!.ReplaceWith(end);
        }
        foreach (var node in suppressed) if (node.Parent is not null) node.Remove();
        static bool AncestorInstruction(RangeField? field) => field is not null && (field.InInstruction || AncestorInstruction(field.Parent));
    }

    private static DocumentField ReadFieldMetadata(XElement element, string instruction)
    {
        var field = element.Attribute(Tx + "field") is { } data ? RangeInterchange.Decode<DocumentField>(data.Value) : new DocumentField();
        return field with { Instruction = element.Attribute(Tx + "field") is null ? instruction : RangeInterchange.PreferOriginalInstruction(field.Instruction, instruction), IsLocked = OnAttribute(element, "fldLock"), IsDirty = OnAttribute(element, "dirty") };
    }

    private static IEnumerable<XElement> WriteRangeBoundary(RangeInterchange.Item item, IReadOnlyDictionary<Guid, int> bookmarkIds)
    {
        if (item.Bookmark is { } bookmark)
        {
            yield return item.Start
                ? new XElement(W + "bookmarkStart", new XAttribute(W + "id", bookmarkIds[bookmark.Id]), new XAttribute(W + "name", bookmark.Name), new XAttribute(Tx + "bookmark", RangeInterchange.Encode(bookmark)))
                : new XElement(W + "bookmarkEnd", new XAttribute(W + "id", bookmarkIds[bookmark.Id]));
        }
        if (item.Field is not { } field) yield break;
        if (!item.Start) { yield return new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "end"))); yield break; }
        RangeInterchange.DiagnoseField(field.Instruction, "docx", field.Id);
        yield return new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "begin"), new XAttribute(W + "fldLock", field.IsLocked ? "1" : "0"), new XAttribute(W + "dirty", field.IsDirty ? "1" : "0"), new XAttribute(Tx + "field", RangeInterchange.Encode(field))));
        foreach (var run in WriteInstruction(field.Instruction)) yield return run;
        yield return new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "separate")));
    }
    private static IEnumerable<XElement> WriteInstruction(string instruction)
    {
        foreach (var part in RangeInterchange.InstructionParts(instruction))
        {
            if (!part.Nested)
            { yield return new XElement(W + "r", new XElement(W + "instrText", new XAttribute(XNamespace.Xml + "space", "preserve"), part.Text)); continue; }
            yield return new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "begin")));
            foreach (var nested in WriteInstruction(part.Text)) yield return nested;
            yield return new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "separate")));
            yield return new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "end")));
        }
    }
    private static ImmutableDictionary<string, string> ReadDocumentProperties(XDocument? xml)
    {
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        if (xml?.Root is null) return result.ToImmutable();
        foreach (var property in xml.Root.Elements())
        {
            var name = (string?)property.Attribute("name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (result.ContainsKey(name)) Loss("property-name", "Duplicate document property", "Kept the last property value.", property);
            var value = property.Elements().FirstOrDefault();
            if (value is not null && value.Name.LocalName is not ("lpwstr" or "lpstr" or "bstr"))
                Loss("property-type", "Typed custom document property", "Retained its text value in the string property catalog.", property);
            result[name] = value?.Value ?? "";
        }
        return result.ToImmutable();
    }
    private static XElement WriteDocumentProperties(ImmutableDictionary<string, string> properties)
    {
        XNamespace custom = "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";
        XNamespace types = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";
        return new XElement(custom + "Properties", new XAttribute(XNamespace.Xmlns + "vt", types),
            properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select((p, index) => new XElement(custom + "property",
                new XAttribute("fmtid", "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}"), new XAttribute("pid", index + 2),
                new XAttribute("name", p.Key), new XElement(types + "lpwstr", p.Value))));
    }

}
