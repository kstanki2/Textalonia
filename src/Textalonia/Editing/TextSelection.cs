namespace Textalonia.Editing;

/// <summary>UTF-16 offsets into FlowDocument.Text. Active is the caret; Anchor preserves selection direction.</summary>
public readonly record struct TextSelection(int Anchor, int Active)
{
    public int Start => Math.Min(Anchor, Active);
    public int End => Math.Max(Anchor, Active);
    public int Length => End - Start;
    public bool IsEmpty => Anchor == Active;
}
