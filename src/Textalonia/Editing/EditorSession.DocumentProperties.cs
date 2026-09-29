using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    /// <summary>Changes the built-in and custom document properties in one undoable transaction.</summary>
    /// <returns>Whether the properties changed. Editing restrictions can reject the transaction.</returns>
    public bool SetDocumentProperties(DocumentCoreProperties coreProperties,
        IEnumerable<DocumentCustomProperty> customProperties)
    {
        ArgumentNullException.ThrowIfNull(coreProperties);
        ArgumentNullException.ThrowIfNull(customProperties);
        var custom = customProperties.ToImmutableArray();
        var updated = Document with { CoreProperties = coreProperties, CustomProperties = custom };
        updated.Validate();
        if (Document.CoreProperties == coreProperties && Document.CustomProperties.SequenceEqual(custom)) return false;
        if (GetCapability(EditOperation.Metadata) != CommandCapability.Enabled) return false;
        return Commit(updated, Selection, wholeDocument: true);
    }
}
