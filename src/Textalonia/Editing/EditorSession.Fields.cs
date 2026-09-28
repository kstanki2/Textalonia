using Textalonia.Model;
using Textalonia.Model.Fields;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    public DocumentField? InsertField(string instruction, FlowDocument? cachedResult = null)
    {
        if (IsReadOnly) return null;
        var document = FieldOperations.Insert(Document, ActiveStoryId, Selection.Start, Selection.Length, instruction, cachedResult);
        var field = document.Fields[^1];
        Commit(document, new(field.End.Resolve(document), field.End.Resolve(document)), wholeDocument: true);
        return field;
    }

    public FieldUpdateResult UpdateFields(FieldEvaluationOptions? options = null)
    {
        if (IsReadOnly) return new(Document, [], 0);
        var result = FieldEvaluator.Update(Document, options);
        ApplyFieldUpdate(result);
        return result;
    }

    internal void ApplyFieldUpdate(FieldUpdateResult result)
    {
        if (IsReadOnly) return;
        if (result.UpdatedCount > 0 || !result.Document.Fields.SequenceEqual(Document.Fields) ||
            !result.Document.Bookmarks.SequenceEqual(Document.Bookmarks))
            Commit(result.Document, Selection, wholeDocument: true);
    }

    public void SetFieldLocked(Guid fieldId, bool locked)
    {
        if (IsReadOnly) return;
        Commit(FieldOperations.SetLocked(Document, fieldId, locked), Selection, wholeDocument: true);
    }

    public void SetFieldInstruction(Guid fieldId, string instruction)
    {
        if (IsReadOnly) return;
        Commit(FieldOperations.SetInstruction(Document, fieldId, instruction), Selection, wholeDocument: true);
    }

    public void SetFieldShowCode(Guid fieldId, bool showCode)
    {
        if (IsReadOnly) return;
        Commit(FieldOperations.SetShowCode(Document, fieldId, showCode), Selection, wholeDocument: true);
    }

    public void RemoveField(Guid fieldId, bool keepResult = true)
    {
        if (IsReadOnly) return;
        Commit(FieldOperations.Remove(Document, fieldId, keepResult), Selection, wholeDocument: true);
    }

    public void InsertCaption(string label, string caption)
    {
        if (IsReadOnly) return;
        var document = FieldOperations.InsertCaption(Document, ActiveStoryId, Selection.Start, label, caption);
        Commit(document, Selection, wholeDocument: true);
    }
}
