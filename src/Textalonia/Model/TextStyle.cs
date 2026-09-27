namespace Textalonia.Model;

/// <summary>Immutable character formatting. Null colors inherit the editor's theme.</summary>
public sealed partial record TextStyle
{
    public static TextStyle Default { get; } = new();
    /// <summary>A named character style. Null Overrides retains legacy fully explicit formatting.</summary>
    public string? StyleId { get; init; }
    public TextStyleOverrides? Overrides { get; init; }
    /// <summary>Creates a style reference whose formatting inherits until explicitly overridden.</summary>
    public static TextStyle ForStyle(string? id) => new() { StyleId = id, Overrides = new() };
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
    /// <summary>Inline code meaning, independent of the selected typeface.</summary>
    public bool IsCode { get; init; }
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

public sealed partial record ParagraphStyle
{
    public static ParagraphStyle Default { get; } = new();
    public string? StyleId { get; init; }
    public ParagraphStyleOverrides? Overrides { get; init; }
    /// <summary>Creates a paragraph style reference without direct formatting.</summary>
    public static ParagraphStyle ForStyle(string? id) => new() { StyleId = id, Overrides = new() };
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
    private static readonly PieceText InlineStorage = PieceText.From("\uFFFC");
    private PieceText _storage;
    public string Text { get => Storage.ToString(); init => _storage = Inline is null ? PieceText.From(value) : InlineStorage; }
    public TextStyle Style { get; init; }
    private InlineDescriptor? _inline;
    public InlineDescriptor? Inline
    {
        get => _inline;
        init { _inline = value; if (value is not null) _storage = InlineStorage; }
    }
    [System.Text.Json.Serialization.JsonIgnore] public string PlainText => Inline?.AltText ?? Text;
    [System.Text.Json.Serialization.JsonConstructor]
    public RichRun(string Text, TextStyle Style) { _storage = PieceText.From(Text); this.Style = Style; }
    public RichRun(string text) : this(text, TextStyle.Default) { }
    public RichRun(InlineDescriptor inline, TextStyle? style = null) : this("\uFFFC", style ?? TextStyle.Default) =>
        Inline = inline ?? throw new ArgumentNullException(nameof(inline));
    private RichRun(PieceText storage, TextStyle style) { _storage = storage; Style = style; }
    internal PieceText Storage => Inline is null ? _storage : InlineStorage;
    internal RichRun Slice(int start, int length) => Inline is not null && start == 0 && length == 1 ? this : new(Storage.Slice(start, length), Style);
    internal RichRun Append(RichRun other) => new(PieceText.Join(_storage, other._storage), Style);
    public void Deconstruct(out string Text, out TextStyle Style) { Text = this.Text; Style = this.Style; }
    public bool Equals(RichRun? other) => other is not null && Style == other.Style && Inline == other.Inline &&
        (ReferenceEquals(_storage, other._storage) || Text == other.Text);
    public override int GetHashCode() => HashCode.Combine(Text, Style, Inline);
}
