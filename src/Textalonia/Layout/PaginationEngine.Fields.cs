using Avalonia.Media;
using Textalonia.Model;
using Textalonia.Model.Fields;

namespace Textalonia.Layout;

public sealed partial class PaginationEngine
{
    /// <summary>Updates fields against exact physical pages with bounded reflow. Does not mutate a session.</summary>
    public FieldUpdateResult UpdateFields(FlowDocument document, FieldEvaluationOptions? fieldOptions = null,
        FontFamily? font = null, PaginationOptions? paginationOptions = null, int maximumIterations = 8)
    {
        return FieldEvaluator.UpdateUntilStable(document, snapshot =>
        {
            using var layout = Paginate(snapshot, font, options: paginationOptions);
            var pages = layout.Pages;
            var contexts = new Dictionary<Guid, FieldPageContext>();
            var anchorPages = new Dictionary<DocumentAnchor, int>();
            int Page(DocumentAnchor anchor)
            {
                var offset = anchor.Resolve(snapshot);
                var firstInstance = anchor.StoryId == Guid.Empty ? null : layout.StoryFragments
                    .Where(f => f.StoryKey == anchor.StoryId && offset >= f.TextStart && offset <= f.TextEnd)
                    .OrderBy(f => f.PageIndex).FirstOrDefault();
                var index = Math.Clamp(firstInstance?.PageIndex ?? layout.GetPageIndex(anchor.StoryId, offset), 0, pages.Length - 1);
                anchorPages[anchor] = pages[index].Number;
                return index;
            }
            foreach (var field in snapshot.Fields)
            {
                var at = Page(field.Start); var page = pages[at];
                contexts[field.Id] = new(page.Number, pages.Length, pages.Count(p => p.SectionId == page.SectionId));
            }
            foreach (var bookmark in snapshot.Bookmarks) Page(bookmark.Start);
            foreach (var paragraph in new DocumentIndex(snapshot).Paragraphs)
                Page(DocumentAnchor.Create(snapshot, Guid.Empty, paragraph.Start));
            var signature = string.Join(";", pages.Select(p => $"{p.SectionId}:{p.Number}")) + "|" +
                string.Join(";", layout.Fragments.Select(f => $"{f.ParagraphId}:{f.TextStart}:{f.TextEnd}:{f.PageIndex}"));
            return new(signature, field => contexts.GetValueOrDefault(field.Id), anchor => anchorPages.TryGetValue(anchor, out var page) ? page : null);
        }, fieldOptions, maximumIterations);
    }
}
