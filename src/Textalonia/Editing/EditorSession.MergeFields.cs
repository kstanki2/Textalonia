using Textalonia.Model;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    /// <summary>Inserts a named, atomic merge field using the current typing style, as one undoable edit.</summary>
    public void InsertMergeField(string name, string? format = null, string? fallbackText = null)
    {
        if (IsReadOnly) return;
        InsertInline(MergeFields.Create(name, format, fallbackText));
    }

    /// <summary>Changes a field definition, preserving the inline identity and character style.</summary>
    public void UpdateMergeField(Guid id, string name, string? format = null, string? fallbackText = null)
    {
        if (IsReadOnly) return;
        var replacement = MergeFields.Create(name, format, fallbackText);
        UpdateInline(id, inline => inline.Payload is MergeFieldInlinePayload
            ? inline with { Payload = replacement.Payload, AltText = replacement.AltText, Width = replacement.Width }
            : throw new ArgumentException("The inline descriptor is not a merge field.", nameof(id)));
    }

    /// <summary>The field selected as a single token, or immediately before or after a collapsed caret.</summary>
    public InlineDescriptor? CurrentMergeField
    {
        get
        {
            if (Selection.Length > 1) return null;
            var entry = Index.At(Selection.Start);
            var position = entry.Start;
            foreach (var run in entry.Paragraph.Runs)
            {
                if (run.Inline is { Payload: MergeFieldInlinePayload } field &&
                    (Selection.IsEmpty ? Selection.Start >= position && Selection.Start <= position + 1 : Selection.Start == position))
                    return field;
                position += run.Storage.Length;
            }
            return null;
        }
    }
}
