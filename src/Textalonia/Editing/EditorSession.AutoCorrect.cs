using System.Collections.Immutable;
using Textalonia.Model;
using Textalonia.Proofing;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    /// <summary>Applies a correction to one token while leaving the committed boundary after it.
    /// The correction is a separate undo unit from the preceding typing.</summary>
    public bool TryApplyAutoCorrect(int start, int end, AutoCorrectReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (start < 0 || end <= start || end > Index.Length || !Selection.IsEmpty || Selection.Active < end)
            return false;
        var entry = Index.At(start);
        if (end > entry.End) return false;
        var runs = entry.Paragraph.Slice(start - entry.Start, end - start);
        if (runs.IsEmpty) return false;
        var resolver = new DocumentStyleResolver(ActiveDocument);
        var sourceStyle = resolver.ResolveText(entry.Paragraph, runs[0].Style);
        if (sourceStyle.NoProof || runs.Any(run => run.Inline is not null ||
            resolver.ResolveText(entry.Paragraph, run.Style) != sourceStyle)) return false;
        var operation = replacement switch
        {
            AutoCorrectLink => EditOperation.Formatting,
            AutoCorrectInline => EditOperation.InlineObjects,
            AutoCorrectFragment => EditOperation.Clipboard,
            AutoCorrectText => EditOperation.Text,
            _ => throw new ArgumentOutOfRangeException(nameof(replacement))
        };
        if (IsReadOnly || !RangeAllowed(Document, ActiveStoryId, start, end, operation)) return false;
        var range = new TextSelection(start, end);
        var originalCaret = Selection.Active;
        FlowDocument result;
        int delta;
        switch (replacement)
        {
            case AutoCorrectText text:
            {
                var value = FlowDocument.NormalizeNewlines(text.Value ?? throw new ArgumentException("Missing correction text.", nameof(replacement)))
                    .Replace("\0", "");
                if (value == Index.ReadPlainText(start, end - start)) return false;
                var style = entry.Paragraph.StyleAt(start - entry.Start);
                var paragraphs = value.Split('\n').Select(part => new Paragraph(part, style) { Style = entry.Paragraph.Style }).ToImmutableArray();
                (result, _) = ReplaceRange(ActiveDocument, range, paragraphs);
                delta = value.Length - (end - start);
                break;
            }
            case AutoCorrectFragment fragment:
            {
                ArgumentNullException.ThrowIfNull(fragment.Value);
                (result, _) = BuildFragmentInsertion(ActiveDocument, range, fragment.Value,
                    Document.Bookmarks.Select(bookmark => bookmark.Name));
                delta = new DocumentIndex(result).Length - Index.Length;
                break;
            }
            case AutoCorrectInline inline:
            {
                inline.Descriptor.Validate();
                var resources = ImmutableDictionary<string, DocumentResource>.Empty;
                if (inline.Descriptor.Payload is ImageInlinePayload image)
                {
                    var resource = inline.Resource;
                    if (resource is null && !ActiveDocument.Resources.TryGetValue(image.ResourceId, out resource))
                        throw new ArgumentException("Image correction requires an encoded resource.", nameof(replacement));
                    resource.Validate();
                    resources = resources.Add(image.ResourceId, resource);
                }
                else if (inline.Resource is not null)
                    throw new ArgumentException("Only image corrections accept an encoded resource.", nameof(replacement));
                var rich = new Paragraph([new RichRun(inline.Descriptor, entry.Paragraph.StyleAt(start - entry.Start))])
                { Style = entry.Paragraph.Style };
                var document = new FlowDocument([rich]) { Resources = resources };
                (result, _) = BuildFragmentInsertion(ActiveDocument, range,
                    new DocumentFragment { Document = document, StartsInsideParagraph = true, EndsInsideParagraph = true },
                    Document.Bookmarks.Select(bookmark => bookmark.Name));
                delta = new DocumentIndex(result).Length - Index.Length;
                break;
            }
            case AutoCorrectLink link:
            {
                if (!FlowDocument.IsSafeHyperlink(link.Uri)) throw new ArgumentException("Unsafe correction link.", nameof(replacement));
                if (sourceStyle.Hyperlink == link.Uri && sourceStyle.InternalLink is null) return false;
                var paragraph = entry.Paragraph.Format(start - entry.Start, end - start,
                    style => style with { Hyperlink = link.Uri, InternalLink = null });
                result = Index.Tree.Rewrite(ActiveDocument,
                    new Dictionary<Guid, ImmutableArray<Paragraph>> { [paragraph.Id] = [paragraph] });
                delta = 0;
                break;
            }
            default: throw new ArgumentOutOfRangeException(nameof(replacement));
        }
        BreakUndoGroup();
        return Commit(result, new(originalCaret + delta, originalCaret + delta), editedRange: range);
    }
}
