namespace Textalonia.Model;

/// <summary>Immutable character formatting. Null colors inherit the editor's theme.</summary>
public sealed record TextStyle
{
    public static TextStyle Default { get; } = new();
    public string? FontFamily { get; init; }
    public double FontSize { get; init; } = 16;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strikethrough { get; init; }
    public string? Foreground { get; init; }
    public string? Background { get; init; }
    public string? Hyperlink { get; init; }
    public Baseline Baseline { get; init; }
}

public enum Baseline { Normal, Subscript, Superscript }
public enum ParagraphAlignment { Left, Center, Right, Justify }
public enum ListKind { None, Bullet, Numbered }

public sealed record ParagraphStyle
{
    public static ParagraphStyle Default { get; } = new();
    public ParagraphAlignment Alignment { get; init; }
    public ListKind List { get; init; }
    public int ListLevel { get; init; }
    public int HeadingLevel { get; init; }
    public double SpaceBefore { get; init; }
    public double SpaceAfter { get; init; } = 8;
    public double Indent { get; init; }
    public bool RightToLeft { get; init; }
}

[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record RichRun(string Text, TextStyle Style)
{
    public RichRun(string text) : this(text, TextStyle.Default) { }
}
