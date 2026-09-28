using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    /// <summary>Finds non-overlapping literal matches in the active story or all stories. Matches never split graphemes.</summary>
    public ImmutableArray<DocumentSearchResult> Search(string query, bool matchCase = false, bool allStories = true)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0) return [];
        var results = ImmutableArray.CreateBuilder<DocumentSearchResult>();
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var storyIds = allStories ? DocumentOutline.StoryIds(Document, true) : [ActiveStoryId];
        foreach (var storyId in storyIds)
        {
            var index = storyId == ActiveStoryId ? Index : Document.GetStoryIndex(storyId);
            foreach (var offset in index.Find(query, comparison))
                if (index.Snap(offset) == offset && index.Snap(offset + query.Length) == offset + query.Length)
                    results.Add(new(_positionScope, storyId, Revision, offset, query.Length, index.ReadText(offset, query.Length)));
        }
        return results.ToImmutable();
    }

    /// <summary>Activates a current match. Story switches advance the revision; search again before a subsequent replacement.</summary>
    public bool SelectSearchResult(DocumentSearchResult result)
    {
        if (!IsCurrentSearchResult(result)) return false;
        SwitchStory(result.StoryId);
        Select(result.Start, result.Start + result.Length);
        return true;
    }

    public bool IsCurrentSearchResult(DocumentSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Scope != _positionScope || result.Revision != Revision ||
            result.StoryId != Guid.Empty && !Document.Stories.ContainsKey(result.StoryId)) return false;
        var index = result.StoryId == ActiveStoryId ? Index : Document.GetStoryIndex(result.StoryId);
        return result.Start >= 0 && result.Length > 0 && result.Start <= index.Length - result.Length &&
            index.ReadText(result.Start, result.Length) == result.Text;
    }

    /// <summary>Replaces validated current matches across stories in one undoable transaction. Stale or overlapping input is rejected.</summary>
    public int ReplaceSearchResults(IEnumerable<DocumentSearchResult> results, string replacement)
    {
        ArgumentNullException.ThrowIfNull(results); ArgumentNullException.ThrowIfNull(replacement);
        if (IsReadOnly) return 0;
        var matches = results.ToArray();
        if (matches.Length == 0) return 0;
        foreach (var match in matches)
            if (match is null || !IsCurrentSearchResult(match))
                throw new InvalidOperationException("Search results are stale or belong to another session. Search again before replacing.");
        var groups = matches.GroupBy(m => m.StoryId).Select(g => (StoryId: g.Key, Matches: g.OrderBy(m => m.Start).ToArray())).ToArray();
        foreach (var group in groups)
            for (var i = 1; i < group.Matches.Length; i++)
                if (group.Matches[i - 1].Start + group.Matches[i - 1].Length > group.Matches[i].Start)
                    throw new ArgumentException("Replacement matches must not overlap or repeat.", nameof(results));
        replacement = FlowDocument.NormalizeNewlines(replacement).Replace("\0", "");
        var document = Document;
        foreach (var group in groups)
        {
            var projection = document.GetStoryDocument(group.StoryId);
            foreach (var match in group.Matches.Reverse())
            {
                var current = new DocumentIndex(projection).At(match.Start);
                var fragment = FlowDocument.FromText(replacement, current.Paragraph.StyleAt(match.Start - current.Start))
                    .Blocks.Cast<Paragraph>().Select(p => p with { Style = current.Paragraph.Style }).ToImmutableArray();
                (projection, _) = ReplaceRange(projection, match.Selection, fragment);
            }
            document = DocumentAnchors.WithStory(document, group.StoryId, projection);
        }
        document.Validate();
        return Commit(document, new(0, 0), wholeDocument: true) ? matches.Length : 0;
    }

    public ImmutableArray<DocumentOutlineEntry> GetOutline(bool allStories = false) => DocumentOutline.Create(Document, allStories);

    /// <summary>Resolves the stable paragraph identity again so outline entries survive edits before the heading.</summary>
    public bool NavigateToOutline(DocumentOutlineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.StoryId != Guid.Empty && !Document.Stories.ContainsKey(entry.StoryId)) return false;
        var index = Document.GetStoryIndex(entry.StoryId);
        if (index.Tree.Paths?.Find(entry.ParagraphId) is null) return false;
        var position = index.ById(entry.ParagraphId);
        SwitchStory(entry.StoryId); Select(position.Start, position.Start);
        return true;
    }
}

