namespace Textalonia.Model;

public enum AnchorAffinity { Before, After }

/// <summary>A persistent story-local UTF-16 boundary. Session edits transform these boundaries.</summary>
public sealed record DocumentAnchor
{
    public Guid StoryId { get; init; }
    public Guid ParagraphId { get; init; }
    public int Offset { get; init; }
    public AnchorAffinity Affinity { get; init; } = AnchorAffinity.After;

    public static DocumentAnchor Create(FlowDocument document, Guid storyId, int offset, AnchorAffinity affinity = AnchorAffinity.After)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(affinity)) throw new ArgumentOutOfRangeException(nameof(affinity));
        var index = document.GetStoryIndex(storyId);
        if (offset < 0 || offset > index.Length || index.Snap(offset) != offset)
            throw new ArgumentOutOfRangeException(nameof(offset), "Anchors must be at a grapheme boundary.");
        var entry = index.At(offset);
        return new() { StoryId = storyId, ParagraphId = entry.Paragraph.Id, Offset = offset - entry.Start, Affinity = affinity };
    }

    public int Resolve(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(Affinity) || ParagraphId == Guid.Empty || Offset < 0)
            throw new FormatException("Invalid document anchor.");
        var index = document.GetStoryIndex(StoryId);
        if (index.Tree.Paths?.Find(ParagraphId) is null) throw new FormatException("The document anchor is detached.");
        if (index.Tree.Locate(ParagraphId).Node.Source is not Paragraph) throw new FormatException("The document anchor must reference a paragraph.");
        var entry = index.ById(ParagraphId);
        if (Offset > entry.Paragraph.Length || index.Snap(entry.Start + Offset) != entry.Start + Offset)
            throw new FormatException("The document anchor is not at a valid text boundary.");
        return entry.Start + Offset;
    }
}
