using System.Collections.Immutable;
using Textalonia.Model;
using Textalonia.Model.Fields;

namespace Textalonia.Editing;

/// <summary>Pure field authoring operations; EditorSession wraps them in undoable transactions.</summary>
public static class FieldOperations
{
    public static FlowDocument Insert(FlowDocument document, Guid storyId, int start, int length, string instruction,
        FlowDocument? cachedResult = null)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(instruction);
        _ = FieldInstructionParser.Parse(instruction);
        cachedResult ??= FlowDocument.FromText("");
        var result = DocumentRangeEditing.Replace(document, storyId, start, length, cachedResult);
        var field = new DocumentField { Instruction = instruction,
            Start = DocumentAnchor.Create(result, storyId, start, AnchorAffinity.Before),
            End = DocumentAnchor.Create(result, storyId, start + new DocumentIndex(cachedResult).Length, AnchorAffinity.After) };
        result = result with { Fields = result.Fields.Add(field) }; result.Validate(); return result;
    }
    public static FlowDocument Remove(FlowDocument document, Guid fieldId, bool keepResult = true)
    {
        var field = Find(document, fieldId);
        var start = field.Start.Resolve(document); var end = field.End.Resolve(document);
        document = document with { Fields = document.Fields.Where(f => f.Id != fieldId &&
            (keepResult || f.Start.StoryId != field.Start.StoryId || f.Start.Resolve(document) < start || f.End.Resolve(document) > end)).ToImmutableArray() };
        return keepResult ? document : DocumentRangeEditing.Replace(document, field.Start.StoryId, start, end - start, FlowDocument.FromText(""));
    }
    public static FlowDocument SetLocked(FlowDocument document, Guid fieldId, bool locked) => Change(document, fieldId, f => f with { IsLocked = locked });
    public static FlowDocument SetShowCode(FlowDocument document, Guid fieldId, bool showCode) => Change(document, fieldId, f => f with { ShowCode = showCode });
    public static FlowDocument SetInstruction(FlowDocument document, Guid fieldId, string instruction)
    {
        _ = FieldInstructionParser.Parse(instruction);
        return Change(document, fieldId, f => f with { Instruction = instruction, IsDirty = true, LegacyMergeField = null });
    }
    /// <summary>Converts an atomic merge field into a general field while retaining its cached display, format and fallback.</summary>
    public static FlowDocument AdaptMergeField(FlowDocument document, Guid storyId, Guid inlineId)
    {
        var index = document.GetStoryIndex(storyId);
        foreach (var paragraph in index.Paragraphs)
        {
            var offset = paragraph.Start;
            foreach (var run in paragraph.Paragraph.Runs)
            {
                if (run.Inline?.Id == inlineId && run.Inline.Payload is MergeFieldInlinePayload merge)
                {
                    var result = Insert(document, storyId, offset, 1, "MERGEFIELD " + Quote(merge.Name), FlowDocument.FromText(run.Inline.AltText, run.Style));
                    var field = result.Fields[^1];
                    return Change(result, field.Id, f => f with { LegacyMergeField = merge });
                }
                offset += run.Text.Length;
            }
        }
        throw new ArgumentException("The atomic merge field does not exist.", nameof(inlineId));
    }
    public static FlowDocument InsertCaption(FlowDocument document, Guid storyId, int offset, string label, string caption)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 256 || label.Any(char.IsControl)) throw new ArgumentException("Invalid caption label.", nameof(label));
        var text = label + " 0" + (caption.Length == 0 ? "" : ": " + caption);
        document = DocumentRangeEditing.Replace(document, storyId, offset, 0, FlowDocument.FromText(text));
        return Insert(document, storyId, offset + label.Length + 1, 1, "SEQ " + Quote(label), FlowDocument.FromText("0"));
    }
    private static DocumentField Find(FlowDocument document, Guid id) => document.Fields.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException("The field does not exist.", nameof(id));
    private static FlowDocument Change(FlowDocument document, Guid id, Func<DocumentField, DocumentField> change)
    {
        var field = Find(document, id); return document with { Fields = document.Fields.Replace(field, change(field)) };
    }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
