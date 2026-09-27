using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

/// <summary>A literal match in one story and one session revision. Search coordinates use document UTF-16 storage.</summary>
public sealed record DocumentSearchResult
{
    public Guid StoryId { get; }
    public int Revision { get; }
    public int Start { get; }
    public int Length { get; }
    public string Text { get; }
    public TextSelection Selection => new(Start, Start + Length);
    internal Guid Scope { get; }
    internal DocumentSearchResult(Guid scope, Guid storyId, int revision, int start, int length, string text)
    { Scope = scope; StoryId = storyId; Revision = revision; Start = start; Length = length; Text = text; }
}

/// <summary>A stable paragraph destination, with effective outline level and current display text.</summary>
public sealed record DocumentOutlineEntry(Guid StoryId, Guid ParagraphId, int Level, string Text, string? StyleId);

public static class DocumentOutline
{
    /// <summary>Lists effective outline levels (1-9), falling back to heading levels, in reading order.</summary>
    public static ImmutableArray<DocumentOutlineEntry> Create(FlowDocument document, bool allStories = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entries = ImmutableArray.CreateBuilder<DocumentOutlineEntry>();
        var resolver = new DocumentStyleResolver(document);
        foreach (var storyId in StoryIds(document, allStories))
        {
            var index = document.GetStoryIndex(storyId);
            foreach (var position in index.Enumerate(0, index.Length))
            {
                var paragraph = position.Paragraph;
                var style = resolver.ResolveParagraphStyle(paragraph.Style);
                var level = style.OutlineLevel > 0 ? style.OutlineLevel : style.HeadingLevel;
                if (level is >= 1 and <= 9)
                    entries.Add(new(storyId, paragraph.Id, level, index.ReadPlainText(position.Start, paragraph.Length), paragraph.Style.StyleId));
            }
        }
        return entries.ToImmutable();
    }

    internal static IEnumerable<Guid> StoryIds(FlowDocument document, bool allStories)
    {
        yield return Guid.Empty;
        if (allStories)
            foreach (var story in document.Stories.Values.OrderBy(s => s.Kind).ThenBy(s => s.Id)) yield return story.Id;
    }
}
