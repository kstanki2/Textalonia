using System.Collections.Immutable;

namespace Textalonia.Model;

public enum UnderlineKind { None, Single, Double, Dotted, Dashed, Thick, Wave }
public enum StrikeKind { None, Single, Double }
public enum TabAlignment { Left, Center, Right, Decimal }
public enum TabLeader { None, Dots, Dashes, Line }
public enum LineSpacingMode { Natural, Multiple, Exact, AtLeast }

/// <summary>A tab measured from the paragraph's leading content edge, in device-independent pixels.</summary>
public sealed record TabStop(double Position, TabAlignment Alignment = TabAlignment.Left,
    TabLeader Leader = TabLeader.None, char DecimalCharacter = '.');

/// <summary>Document grid pitches in device-independent pixels. Zero leaves that dimension unconstrained.</summary>
public sealed record EastAsianGrid(double CharacterSpacing = 0, double LineSpacing = 0);

/// <summary>Legacy paragraph placement relative to the current column content origin, in DIP. A null height grows with content.</summary>
public sealed record ParagraphFrame(double X = 0, double Y = 0, double Width = 240, double? Height = null);

public sealed partial record TextStyle
{
    public UnderlineKind UnderlineKind { get; init; }
    public string? UnderlineColor { get; init; }
    public bool UnderlineWordsOnly { get; init; }
    public StrikeKind StrikeKind { get; init; }
    public bool AllCaps { get; init; }
    public bool SmallCaps { get; init; }
    /// <summary>BCP-47 language name used for culture-sensitive shaping and future proofing.</summary>
    public string? Language { get; init; }
    public bool NoProof { get; init; }
    /// <summary>Additional advance between glyphs, in device-independent pixels.</summary>
    public double Tracking { get; init; }
    /// <summary>Horizontal glyph scale, where one means 100 percent.</summary>
    public double HorizontalScale { get; init; } = 1;
    /// <summary>Positive values raise glyphs above the normal baseline, in device-independent pixels.</summary>
    public double BaselineOffset { get; init; }
    /// <summary>Enable kerning at or above this font size; null uses the font's default.</summary>
    public double? KerningThreshold { get; init; }
}

public sealed partial record ParagraphStyle
{
    public ImmutableArray<TabStop> TabStops { get; init; } = [];
    /// <summary>Default tab interval in device-independent pixels; zero preserves automatic font-based tabs.</summary>
    public double DefaultTabWidth { get; init; }
    /// <summary>Natural preserves legacy LineHeight behavior. Other modes use LineSpacing.</summary>
    public LineSpacingMode LineSpacingMode { get; init; }
    public double LineSpacing { get; init; } = 1;
    public bool ContextualSpacing { get; init; }
    public BlockBorders? Borders { get; init; }
    public string? Shading { get; init; }
    /// <summary>Zero denotes body text; 1-9 are independent of the heading shortcut level.</summary>
    public int OutlineLevel { get; init; }
    /// <summary>Page-layout metadata, honored by a paginated layout provider.</summary>
    public bool PageBreakBefore { get; init; }
    public bool ColumnBreakBefore { get; init; }
    public ParagraphFrame? Frame { get; init; }
    public bool KeepWithNext { get; init; }
    public bool KeepTogether { get; init; }
    public bool WidowControl { get; init; } = true;
    public EastAsianGrid? EastAsianGrid { get; init; }
    public bool SnapToGrid { get; init; }
}
