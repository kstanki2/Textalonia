using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

/// <summary>Pure story-range replacement, including persistent anchor transformations.</summary>
public static class DocumentRangeEditing
{
    public static FlowDocument Replace(FlowDocument document, Guid storyId, int start, int length, FlowDocument result)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(result);
        var projection = document.GetStoryDocument(storyId);
        var index = new DocumentIndex(projection);
        if (start < 0 || length < 0 || start > index.Length - length || index.Snap(start) != start || index.Snap(start + length) != start + length)
            throw new ArgumentOutOfRangeException(nameof(start));
        result.Validate();
        var prepared = DocumentFragments.Prepare(result, projection, document.Bookmarks.Select(bookmark => bookmark.Name));
        var preparedIndex = new DocumentIndex(prepared);
        FlowDocument changed;
        if (prepared.Blocks.All(b => b is Paragraph) && prepared.Fields.IsEmpty && prepared.Bookmarks.IsEmpty && prepared.Stories.IsEmpty)
        {
            var source = projection with { Resources = prepared.Resources, Styles = prepared.Styles, Fonts = prepared.Fonts };
            (changed, _) = EditorSession.ReplaceRange(source, new(start, start + length), prepared.Blocks.Cast<Paragraph>().ToImmutableArray());
        }
        else if (start == 0 && length == index.Length)
        {
            // A field result replaces story content, while the containing document keeps
            // its properties, page settings, existing secondary stories and enclosing ranges.
            changed = projection with
            {
                Blocks = prepared.Blocks, Resources = prepared.Resources, Styles = prepared.Styles, Fonts = prepared.Fonts,
                Stories = projection.Stories.SetItems(prepared.Stories), Notes = projection.Notes.AddRange(prepared.Notes),
                Bookmarks = projection.Bookmarks.AddRange(prepared.Bookmarks), Fields = projection.Fields.AddRange(prepared.Fields)
            };
            changed = DocumentSection.Reconcile(changed);
            changed = DocumentAnchors.Transform(projection, changed, Guid.Empty, start, length, preparedIndex.Length);
        }
        else
        {
            var insertion = projection;
            var offset = start;
            if (length > 0) (insertion, offset) = EditorSession.ReplaceRange(projection, new(start, start + length), [new Paragraph()]);
            (changed, _) = DocumentFragments.Insert(insertion, offset, prepared, true, true);
        }
        // A block result owns paragraph formatting at a complete paragraph boundary.
        // Preserve surrounding paragraph formatting for an embedded single-line result.
        var resultIndex = preparedIndex;
        var changedIndex = new DocumentIndex(changed);
        if (start == index.At(start).Start || resultIndex.ParagraphCount > 1)
        {
            var first = changedIndex.At(start);
            var replacementStyle = resultIndex.At(0).Paragraph.Style;
            if (first.Paragraph.Style != replacementStyle)
                changed = changed.ReplaceBlock(first.Paragraph.Id, first.Paragraph with { Style = replacementStyle });
        }
        var updated = DocumentAnchors.WithStory(document, storyId, changed);
        updated.Validate();
        return updated;
    }
}
