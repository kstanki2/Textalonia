using Avalonia.Threading;
using Textalonia.Layout;
using Textalonia.Model.Fields;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    /// <summary>Optional host-owned field evaluation policy used when a call supplies no options. Delegate targets remain host-owned.</summary>
    public FieldEvaluationOptions? FieldOptions { get; set; }

    /// <summary>Updates ordinary and page-dependent fields using exact layout, as one undoable edit.</summary>
    public FieldUpdateResult UpdateFieldsWithLayout(FieldEvaluationOptions? options = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsReadOnly) return new(Document, [], 0);
        using var engine = new PaginationEngine();
        var result = engine.UpdateFields(Document, options ?? FieldOptions, FontFamily,
            new PaginationOptions { MaxShapingCharacters = MaxShapingCharacters });
        Session.ApplyFieldUpdate(result);
        return result;
    }
}
