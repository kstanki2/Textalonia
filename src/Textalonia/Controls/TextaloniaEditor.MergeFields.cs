using Textalonia.Model;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    /// <summary>Inserts a named field with optional .NET value format and missing-value fallback.</summary>
    public void InsertMergeField(string name, string? format = null, string? fallbackText = null) =>
        Session.InsertMergeField(name, format, fallbackText);

    /// <summary>Updates a merge field as one undoable edit, retaining its identity and character style.</summary>
    public void UpdateMergeField(Guid id, string name, string? format = null, string? fallbackText = null) =>
        Session.UpdateMergeField(id, name, format, fallbackText);

    /// <summary>The merge field selected or adjacent to the caret, if any.</summary>
    public InlineDescriptor? CurrentMergeField => Session.CurrentMergeField;
}
