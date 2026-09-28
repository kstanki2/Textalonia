using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    /// <summary>Changes image geometry in one undo step. With aspect locking, a single dimension
    /// scales the other dimension; supplying both dimensions is an explicit size override.</summary>
    public void UpdateImage(Guid id, ImagePlacement placement, double? width = null, double? height = null)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (IsReadOnly) return;
        placement.Validate();
        UpdateInline(id, inline =>
        {
            if (inline.Payload is not (ImageInlinePayload or OleInlinePayload))
                throw new ArgumentException("The inline object is not an image or OLE preview.", nameof(id));
            var targetWidth = width ?? inline.Width;
            var targetHeight = height ?? inline.Height;
            if (placement.LockAspectRatio)
            {
                if (width.HasValue && !height.HasValue) targetHeight = inline.Height * targetWidth / inline.Width;
                if (height.HasValue && !width.HasValue) targetWidth = inline.Width * targetHeight / inline.Height;
            }
            return inline with { Placement = placement, Width = targetWidth, Height = targetHeight };
        });
    }

    /// <summary>Inserts an image and optional supplied preview without discarding its original bytes.</summary>
    public void InsertImage(InlineDescriptor descriptor, DocumentResource original, DocumentResource? preview = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(original);
        if (IsReadOnly) return;
        if (descriptor.Payload is not ImageInlinePayload image)
            throw new ArgumentException("An image descriptor is required.", nameof(descriptor));
        if ((preview is null) != (image.PreviewResourceId is null))
            throw new ArgumentException("A preview resource and its identifier must be supplied together.", nameof(preview));
        var resources = AddImageResource(ActiveDocument.Resources, image.ResourceId, original);
        if (preview is not null) resources = AddImageResource(resources, image.PreviewResourceId!, preview);
        InsertResourceObject(descriptor, resources);
    }

    /// <summary>Inserts opaque package data and its supplied preview as one atomic, undoable object.</summary>
    public void InsertOle(InlineDescriptor descriptor, DocumentResource package, DocumentResource preview)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(preview);
        if (IsReadOnly) return;
        if (descriptor.Payload is not OleInlinePayload ole)
            throw new ArgumentException("An OLE descriptor is required.", nameof(descriptor));
        if (package.Kind != DocumentResourceKind.Embedded)
            throw new ArgumentException("OLE packages must contain embedded bytes.", nameof(package));
        var resources = AddImageResource(ActiveDocument.Resources, ole.ResourceId, package);
        resources = AddImageResource(resources, ole.PreviewResourceId, preview);
        InsertResourceObject(descriptor, resources);
    }

    private static ImmutableDictionary<string, DocumentResource> AddImageResource(
        ImmutableDictionary<string, DocumentResource> resources, string id, DocumentResource resource)
    {
        resource.Validate();
        if (!InlineDescriptor.ValidKey(id)) throw new ArgumentException("Invalid resource identifier.", nameof(id));
        if (resources.TryGetValue(id, out var existing) && existing != resource)
            throw new ArgumentException("The resource identifier already belongs to different data.", nameof(id));
        return resources.SetItem(id, resource);
    }

    private void InsertResourceObject(InlineDescriptor descriptor, ImmutableDictionary<string, DocumentResource> resources)
    {
        descriptor.Validate();
        var paragraph = new Paragraph([new RichRun(descriptor, TypingStyle)]) { Style = Index.At(Selection.Start).Paragraph.Style };
        var (document, caret) = ReplaceRange(ActiveDocument with { Resources = resources }, Selection, [paragraph]);
        document = document.PruneUnusedResources();
        document.Validate();
        Commit(document, new(caret, caret), editedRange: Selection);
    }

    /// <summary>Returns the immutable embedded package without activating it or opening a file.</summary>
    public DocumentResource? ExtractOle(Guid id)
    {
        var (inline, _) = FindResourceObject(id);
        if (inline.Payload is not OleInlinePayload ole)
            throw new ArgumentException("The inline object is not an OLE package.", nameof(id));
        return Document.Resources.GetValueOrDefault(ole.ResourceId);
    }

    /// <summary>Removes an atomic object in the active story and prunes unreferenced resources.</summary>
    public void RemoveInline(Guid id)
    {
        if (IsReadOnly) return;
        var (_, offset) = FindResourceObject(id);
        var range = new TextSelection(offset, offset + 1);
        var (document, caret) = ReplaceRange(ActiveDocument, range, [new Paragraph("", TypingStyle)]);
        Commit(document, new(caret, caret), editedRange: range);
    }

    private (InlineDescriptor Inline, int Offset) FindResourceObject(Guid id)
    {
        foreach (var entry in Index.Enumerate(0, Index.Length))
        {
            var offset = entry.Start;
            foreach (var run in entry.Paragraph.Runs)
            {
                if (run.Inline is { } inline && inline.Id == id) return (inline, offset);
                offset += run.Storage.Length;
            }
        }
        throw new ArgumentException("The inline descriptor does not exist in the active story.", nameof(id));
    }

    /// <summary>Sets or removes a section's header background in one undo step. Guid.Empty targets
    /// the current section, creating the implicit first section when necessary.</summary>
    public void SetWatermark(Guid sectionId, DocumentWatermark? watermark, DocumentResource? resource = null)
    {
        if (IsReadOnly) return;
        watermark?.Validate();
        var sections = Document.Sections;
        if (sections.IsEmpty) sections = [new DocumentSection()];
        var id = sectionId == Guid.Empty ? CurrentSection?.Id ?? sections[0].Id : sectionId;
        var position = sections.FindIndex(section => section.Id == id);
        if (position < 0) throw new ArgumentException("The physical section does not exist.", nameof(sectionId));
        var resources = Document.Resources;
        if (resource is not null)
        {
            if (watermark?.ResourceId is not { } resourceId)
                throw new ArgumentException("An image watermark must name the supplied resource.", nameof(resource));
            resources = AddImageResource(resources, resourceId, resource);
        }
        var document = Document with { Sections = sections.SetItem(position, sections[position] with { Watermark = watermark }), Resources = resources };
        document.Validate();
        Commit(document, Selection, wholeDocument: true);
    }
}
