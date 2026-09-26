namespace Textalonia.Model;

/// <summary>Immutable character formatting. Null colors inherit the editor's theme.</summary>
public sealed record TextStyle
{
    public static TextStyle Default { get; } = new();
    public string? FontFamily { get; init; }
    public double FontSize { get; init; } = 16;
    public bool Bold { get; init; }
    /// <summary>OpenType weight (1–1000). An explicit value takes precedence over Bold.</summary>
    public int? FontWeight { get; init; }
    /// <summary>OpenType width class (1–9), with 5 denoting normal stretch.</summary>
    public int FontStretch { get; init; } = 5;
    [System.Text.Json.Serialization.JsonIgnore] public int EffectiveFontWeight => FontWeight ?? (Bold ? 700 : 400);
    [System.Text.Json.Serialization.JsonIgnore] public bool EffectiveBold => EffectiveFontWeight >= 600;
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
    public Guid? ListId { get; init; }
    public ListDefinition? ListDefinition { get; init; }
    public int? ListStart { get; init; }
    public bool ListRestart { get; init; }
    public int HeadingLevel { get; init; }
    public double SpaceBefore { get; init; }
    public double SpaceAfter { get; init; } = 8;
    public double Indent { get; init; }
    public double RightIndent { get; init; }
    /// <summary>Additional first-line indentation; negative values create a hanging indent.</summary>
    public double FirstLineIndent { get; init; }
    /// <summary>Line height in device-independent pixels; null uses the font's natural height.</summary>
    public double? LineHeight { get; init; }
    /// <summary>Additional glyph spacing in device-independent pixels, applied across the paragraph.</summary>
    public double LetterSpacing { get; init; }
    public bool RightToLeft { get; init; }
}

public sealed record RichRun
{
    private PieceText _storage;
    public string Text { get => _storage.ToString(); init => _storage = PieceText.From(value); }
    public TextStyle Style { get; init; }
    [System.Text.Json.Serialization.JsonConstructor]
    public RichRun(string Text, TextStyle Style) { _storage = PieceText.From(Text); this.Style = Style; }
    public RichRun(string text) : this(text, TextStyle.Default) { }
    private RichRun(PieceText storage, TextStyle style) { _storage = storage; Style = style; }
    internal PieceText Storage => _storage;
    internal RichRun Slice(int start, int length) => new(_storage.Slice(start, length), Style);
    internal RichRun Append(RichRun other) => new(PieceText.Join(_storage, other._storage), Style);
    public void Deconstruct(out string Text, out TextStyle Style) { Text = this.Text; Style = this.Style; }
    public bool Equals(RichRun? other) => other is not null && Style == other.Style &&
        (ReferenceEquals(_storage, other._storage) || Text == other.Text);
    public override int GetHashCode() => HashCode.Combine(Text, Style);
}
