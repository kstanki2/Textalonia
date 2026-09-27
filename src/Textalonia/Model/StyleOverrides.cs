using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Textalonia.Model;

/// <summary>Sparse direct formatting. An unset property inherits; a set null clears a value.</summary>
public sealed record TextStyleOverrides
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<string?> FontFamily { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> FontSize { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> Bold { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<int?> FontWeight { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<int> FontStretch { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> Italic { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> IsCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> Underline { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> Strikethrough { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<string?> Foreground { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<string?> Background { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<string?> Hyperlink { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<InternalLinkDestination?> InternalLink { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<Baseline> Baseline { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<UnderlineKind> UnderlineKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<string?> UnderlineColor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> UnderlineWordsOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<StrikeKind> StrikeKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> AllCaps { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> SmallCaps { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<string?> Language { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> NoProof { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> Tracking { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> HorizontalScale { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> BaselineOffset { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double?> KerningThreshold { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<ThemeFontReference?> ThemeFont { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<string?> EastAsianFontFamily { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<string?> ComplexScriptFontFamily { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<ThemeFontReference?> EastAsianThemeFont { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<ThemeFontReference?> ComplexScriptThemeFont { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<ThemeColorReference?> ThemeForeground { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<ThemeColorReference?> ThemeBackground { get; init; }

    public TextStyle Apply(TextStyle value) => value with
    {
        FontFamily = FontFamily.IsSet ? FontFamily.Value : value.FontFamily,
        FontSize = FontSize.IsSet ? FontSize.Value : value.FontSize,
        Bold = Bold.IsSet ? Bold.Value : value.Bold,
        FontWeight = FontWeight.IsSet ? FontWeight.Value : Bold.IsSet ? null : value.FontWeight,
        FontStretch = FontStretch.IsSet ? FontStretch.Value : value.FontStretch,
        Italic = Italic.IsSet ? Italic.Value : value.Italic,
        IsCode = IsCode.IsSet ? IsCode.Value : value.IsCode,
        Underline = Underline.IsSet ? Underline.Value : UnderlineKind.IsSet ? UnderlineKind.Value != global::Textalonia.Model.UnderlineKind.None : value.Underline,
        Strikethrough = Strikethrough.IsSet ? Strikethrough.Value : StrikeKind.IsSet ? StrikeKind.Value != global::Textalonia.Model.StrikeKind.None : value.Strikethrough,
        Foreground = Foreground.IsSet ? Foreground.Value : value.Foreground,
        Background = Background.IsSet ? Background.Value : value.Background,
        Hyperlink = Hyperlink.IsSet ? Hyperlink.Value : value.Hyperlink,
        InternalLink = InternalLink.IsSet ? InternalLink.Value : value.InternalLink,
        Baseline = Baseline.IsSet ? Baseline.Value : value.Baseline,
        UnderlineKind = UnderlineKind.IsSet ? UnderlineKind.Value : Underline.IsSet && !Underline.Value ? global::Textalonia.Model.UnderlineKind.None : value.UnderlineKind,
        UnderlineColor = UnderlineColor.IsSet ? UnderlineColor.Value : value.UnderlineColor,
        UnderlineWordsOnly = UnderlineWordsOnly.IsSet ? UnderlineWordsOnly.Value : value.UnderlineWordsOnly,
        StrikeKind = StrikeKind.IsSet ? StrikeKind.Value : Strikethrough.IsSet && !Strikethrough.Value ? global::Textalonia.Model.StrikeKind.None : value.StrikeKind,
        AllCaps = AllCaps.IsSet ? AllCaps.Value : value.AllCaps,
        SmallCaps = SmallCaps.IsSet ? SmallCaps.Value : value.SmallCaps,
        Language = Language.IsSet ? Language.Value : value.Language,
        NoProof = NoProof.IsSet ? NoProof.Value : value.NoProof,
        Tracking = Tracking.IsSet ? Tracking.Value : value.Tracking,
        HorizontalScale = HorizontalScale.IsSet ? HorizontalScale.Value : value.HorizontalScale,
        BaselineOffset = BaselineOffset.IsSet ? BaselineOffset.Value : value.BaselineOffset,
        KerningThreshold = KerningThreshold.IsSet ? KerningThreshold.Value : value.KerningThreshold,
        ThemeFont = ThemeFont.IsSet ? ThemeFont.Value : FontFamily.IsSet ? null : value.ThemeFont,
        EastAsianFontFamily = EastAsianFontFamily.IsSet ? EastAsianFontFamily.Value : value.EastAsianFontFamily,
        ComplexScriptFontFamily = ComplexScriptFontFamily.IsSet ? ComplexScriptFontFamily.Value : value.ComplexScriptFontFamily,
        EastAsianThemeFont = EastAsianThemeFont.IsSet ? EastAsianThemeFont.Value : EastAsianFontFamily.IsSet ? null : value.EastAsianThemeFont,
        ComplexScriptThemeFont = ComplexScriptThemeFont.IsSet ? ComplexScriptThemeFont.Value : ComplexScriptFontFamily.IsSet ? null : value.ComplexScriptThemeFont,
        ThemeForeground = ThemeForeground.IsSet ? ThemeForeground.Value : Foreground.IsSet ? null : value.ThemeForeground,
        ThemeBackground = ThemeBackground.IsSet ? ThemeBackground.Value : Background.IsSet ? null : value.ThemeBackground
    };

    public static TextStyleOverrides FromStyle(TextStyle style) => new()
    {
        FontFamily = new(style.FontFamily),
        FontSize = new(style.FontSize),
        Bold = new(style.Bold),
        FontWeight = new(style.FontWeight),
        FontStretch = new(style.FontStretch),
        Italic = new(style.Italic),
        IsCode = new(style.IsCode),
        Underline = new(style.Underline),
        Strikethrough = new(style.Strikethrough),
        Foreground = new(style.Foreground),
        Background = new(style.Background),
        Hyperlink = new(style.Hyperlink),
        InternalLink = new(style.InternalLink),
        Baseline = new(style.Baseline),
        UnderlineKind = new(style.UnderlineKind),
        UnderlineColor = new(style.UnderlineColor),
        UnderlineWordsOnly = new(style.UnderlineWordsOnly),
        StrikeKind = new(style.StrikeKind),
        AllCaps = new(style.AllCaps),
        SmallCaps = new(style.SmallCaps),
        Language = new(style.Language),
        NoProof = new(style.NoProof),
        Tracking = new(style.Tracking),
        HorizontalScale = new(style.HorizontalScale),
        BaselineOffset = new(style.BaselineOffset),
        KerningThreshold = new(style.KerningThreshold),
        ThemeFont = new(style.ThemeFont),
        EastAsianFontFamily = new(style.EastAsianFontFamily),
        ComplexScriptFontFamily = new(style.ComplexScriptFontFamily),
        EastAsianThemeFont = new(style.EastAsianThemeFont),
        ComplexScriptThemeFont = new(style.ComplexScriptThemeFont),
        ThemeForeground = new(style.ThemeForeground),
        ThemeBackground = new(style.ThemeBackground)
    };

    /// <summary>Retains existing direct values and records only properties changed by a formatting command.</summary>
    public static TextStyleOverrides Difference(TextStyle before, TextStyle after, TextStyleOverrides? basis = null)
    {
        basis ??= new();
        return basis with
        {
            FontFamily = EqualityComparer<string?>.Default.Equals(before.FontFamily, after.FontFamily) ? basis.FontFamily : new(after.FontFamily),
            FontSize = EqualityComparer<double>.Default.Equals(before.FontSize, after.FontSize) ? basis.FontSize : new(after.FontSize),
            Bold = EqualityComparer<bool>.Default.Equals(before.Bold, after.Bold) ? basis.Bold : new(after.Bold),
            FontWeight = EqualityComparer<int?>.Default.Equals(before.FontWeight, after.FontWeight) ? basis.FontWeight : new(after.FontWeight),
            FontStretch = EqualityComparer<int>.Default.Equals(before.FontStretch, after.FontStretch) ? basis.FontStretch : new(after.FontStretch),
            Italic = EqualityComparer<bool>.Default.Equals(before.Italic, after.Italic) ? basis.Italic : new(after.Italic),
            IsCode = EqualityComparer<bool>.Default.Equals(before.IsCode, after.IsCode) ? basis.IsCode : new(after.IsCode),
            Underline = EqualityComparer<bool>.Default.Equals(before.Underline, after.Underline) ? basis.Underline : new(after.Underline),
            Strikethrough = EqualityComparer<bool>.Default.Equals(before.Strikethrough, after.Strikethrough) ? basis.Strikethrough : new(after.Strikethrough),
            Foreground = EqualityComparer<string?>.Default.Equals(before.Foreground, after.Foreground) ? basis.Foreground : new(after.Foreground),
            Background = EqualityComparer<string?>.Default.Equals(before.Background, after.Background) ? basis.Background : new(after.Background),
            Hyperlink = EqualityComparer<string?>.Default.Equals(before.Hyperlink, after.Hyperlink) ? basis.Hyperlink : new(after.Hyperlink),
            InternalLink = before.InternalLink == after.InternalLink ? basis.InternalLink : new(after.InternalLink),
            Baseline = EqualityComparer<Baseline>.Default.Equals(before.Baseline, after.Baseline) ? basis.Baseline : new(after.Baseline),
            UnderlineKind = EqualityComparer<UnderlineKind>.Default.Equals(before.UnderlineKind, after.UnderlineKind) ? basis.UnderlineKind : new(after.UnderlineKind),
            UnderlineColor = EqualityComparer<string?>.Default.Equals(before.UnderlineColor, after.UnderlineColor) ? basis.UnderlineColor : new(after.UnderlineColor),
            UnderlineWordsOnly = EqualityComparer<bool>.Default.Equals(before.UnderlineWordsOnly, after.UnderlineWordsOnly) ? basis.UnderlineWordsOnly : new(after.UnderlineWordsOnly),
            StrikeKind = EqualityComparer<StrikeKind>.Default.Equals(before.StrikeKind, after.StrikeKind) ? basis.StrikeKind : new(after.StrikeKind),
            AllCaps = EqualityComparer<bool>.Default.Equals(before.AllCaps, after.AllCaps) ? basis.AllCaps : new(after.AllCaps),
            SmallCaps = EqualityComparer<bool>.Default.Equals(before.SmallCaps, after.SmallCaps) ? basis.SmallCaps : new(after.SmallCaps),
            Language = EqualityComparer<string?>.Default.Equals(before.Language, after.Language) ? basis.Language : new(after.Language),
            NoProof = EqualityComparer<bool>.Default.Equals(before.NoProof, after.NoProof) ? basis.NoProof : new(after.NoProof),
            Tracking = EqualityComparer<double>.Default.Equals(before.Tracking, after.Tracking) ? basis.Tracking : new(after.Tracking),
            HorizontalScale = EqualityComparer<double>.Default.Equals(before.HorizontalScale, after.HorizontalScale) ? basis.HorizontalScale : new(after.HorizontalScale),
            BaselineOffset = EqualityComparer<double>.Default.Equals(before.BaselineOffset, after.BaselineOffset) ? basis.BaselineOffset : new(after.BaselineOffset),
            KerningThreshold = EqualityComparer<double?>.Default.Equals(before.KerningThreshold, after.KerningThreshold) ? basis.KerningThreshold : new(after.KerningThreshold),
            ThemeFont = EqualityComparer<ThemeFontReference?>.Default.Equals(before.ThemeFont, after.ThemeFont) ? basis.ThemeFont : new(after.ThemeFont),
            EastAsianFontFamily = EqualityComparer<string?>.Default.Equals(before.EastAsianFontFamily, after.EastAsianFontFamily) ? basis.EastAsianFontFamily : new(after.EastAsianFontFamily),
            ComplexScriptFontFamily = EqualityComparer<string?>.Default.Equals(before.ComplexScriptFontFamily, after.ComplexScriptFontFamily) ? basis.ComplexScriptFontFamily : new(after.ComplexScriptFontFamily),
            EastAsianThemeFont = EqualityComparer<ThemeFontReference?>.Default.Equals(before.EastAsianThemeFont, after.EastAsianThemeFont) ? basis.EastAsianThemeFont : new(after.EastAsianThemeFont),
            ComplexScriptThemeFont = EqualityComparer<ThemeFontReference?>.Default.Equals(before.ComplexScriptThemeFont, after.ComplexScriptThemeFont) ? basis.ComplexScriptThemeFont : new(after.ComplexScriptThemeFont),
            ThemeForeground = EqualityComparer<ThemeColorReference?>.Default.Equals(before.ThemeForeground, after.ThemeForeground) ? basis.ThemeForeground : new(after.ThemeForeground),
            ThemeBackground = EqualityComparer<ThemeColorReference?>.Default.Equals(before.ThemeBackground, after.ThemeBackground) ? basis.ThemeBackground : new(after.ThemeBackground)
        };
    }

    /// <summary>Clears one direct property using its TextStyle/ParagraphStyle property name.</summary>
    public TextStyleOverrides Clear(string propertyName) => propertyName switch
    {
        nameof(FontFamily) => this with { FontFamily = default },
        nameof(FontSize) => this with { FontSize = default },
        nameof(Bold) => this with { Bold = default },
        nameof(FontWeight) => this with { FontWeight = default },
        nameof(FontStretch) => this with { FontStretch = default },
        nameof(Italic) => this with { Italic = default },
        nameof(IsCode) => this with { IsCode = default },
        nameof(Underline) => this with { Underline = default },
        nameof(Strikethrough) => this with { Strikethrough = default },
        nameof(Foreground) => this with { Foreground = default },
        nameof(Background) => this with { Background = default },
        nameof(Hyperlink) => this with { Hyperlink = default },
        nameof(InternalLink) => this with { InternalLink = default },
        nameof(Baseline) => this with { Baseline = default },
        nameof(UnderlineKind) => this with { UnderlineKind = default },
        nameof(UnderlineColor) => this with { UnderlineColor = default },
        nameof(UnderlineWordsOnly) => this with { UnderlineWordsOnly = default },
        nameof(StrikeKind) => this with { StrikeKind = default },
        nameof(AllCaps) => this with { AllCaps = default },
        nameof(SmallCaps) => this with { SmallCaps = default },
        nameof(Language) => this with { Language = default },
        nameof(NoProof) => this with { NoProof = default },
        nameof(Tracking) => this with { Tracking = default },
        nameof(HorizontalScale) => this with { HorizontalScale = default },
        nameof(BaselineOffset) => this with { BaselineOffset = default },
        nameof(KerningThreshold) => this with { KerningThreshold = default },
        nameof(ThemeFont) => this with { ThemeFont = default },
        nameof(EastAsianFontFamily) => this with { EastAsianFontFamily = default },
        nameof(ComplexScriptFontFamily) => this with { ComplexScriptFontFamily = default },
        nameof(EastAsianThemeFont) => this with { EastAsianThemeFont = default },
        nameof(ComplexScriptThemeFont) => this with { ComplexScriptThemeFont = default },
        nameof(ThemeForeground) => this with { ThemeForeground = default },
        nameof(ThemeBackground) => this with { ThemeBackground = default },
        _ => throw new ArgumentException("Unknown formatting property.", nameof(propertyName))
    };
}

/// <summary>Sparse direct formatting. An unset property inherits; a set null clears a value.</summary>
public sealed record ParagraphStyleOverrides
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<ParagraphAlignment> Alignment { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<ListKind> List { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<int> ListLevel { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<Guid?> ListId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<ListDefinition?> ListDefinition { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<int?> ListStart { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> ListRestart { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<int> HeadingLevel { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> SpaceBefore { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> SpaceAfter { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> Indent { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> RightIndent { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> FirstLineIndent { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double?> LineHeight { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> LetterSpacing { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> RightToLeft { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<ImmutableArray<TabStop>> TabStops { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> DefaultTabWidth { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<LineSpacingMode> LineSpacingMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<double> LineSpacing { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> ContextualSpacing { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<BlockBorders?> Borders { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<string?> Shading { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<int> OutlineLevel { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> PageBreakBefore { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> ColumnBreakBefore { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> KeepWithNext { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> KeepTogether { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> WidowControl { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<EastAsianGrid?> EastAsianGrid { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<ParagraphFrame?> Frame { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public StyleValue<bool> SnapToGrid { get; init; }

    public ParagraphStyle Apply(ParagraphStyle value) => value with
    {
        Alignment = Alignment.IsSet ? Alignment.Value : value.Alignment,
        List = List.IsSet ? List.Value : value.List,
        ListLevel = ListLevel.IsSet ? ListLevel.Value : value.ListLevel,
        ListId = ListId.IsSet ? ListId.Value : value.ListId,
        ListDefinition = ListDefinition.IsSet ? ListDefinition.Value : value.ListDefinition,
        ListStart = ListStart.IsSet ? ListStart.Value : value.ListStart,
        ListRestart = ListRestart.IsSet ? ListRestart.Value : value.ListRestart,
        HeadingLevel = HeadingLevel.IsSet ? HeadingLevel.Value : value.HeadingLevel,
        SpaceBefore = SpaceBefore.IsSet ? SpaceBefore.Value : value.SpaceBefore,
        SpaceAfter = SpaceAfter.IsSet ? SpaceAfter.Value : value.SpaceAfter,
        Indent = Indent.IsSet ? Indent.Value : value.Indent,
        RightIndent = RightIndent.IsSet ? RightIndent.Value : value.RightIndent,
        FirstLineIndent = FirstLineIndent.IsSet ? FirstLineIndent.Value : value.FirstLineIndent,
        LineHeight = LineHeight.IsSet ? LineHeight.Value : value.LineHeight,
        LetterSpacing = LetterSpacing.IsSet ? LetterSpacing.Value : value.LetterSpacing,
        RightToLeft = RightToLeft.IsSet ? RightToLeft.Value : value.RightToLeft,
        TabStops = TabStops.IsSet ? TabStops.Value : value.TabStops,
        DefaultTabWidth = DefaultTabWidth.IsSet ? DefaultTabWidth.Value : value.DefaultTabWidth,
        LineSpacingMode = LineSpacingMode.IsSet ? LineSpacingMode.Value : value.LineSpacingMode,
        LineSpacing = LineSpacing.IsSet ? LineSpacing.Value : value.LineSpacing,
        ContextualSpacing = ContextualSpacing.IsSet ? ContextualSpacing.Value : value.ContextualSpacing,
        Borders = Borders.IsSet ? Borders.Value : value.Borders,
        Shading = Shading.IsSet ? Shading.Value : value.Shading,
        OutlineLevel = OutlineLevel.IsSet ? OutlineLevel.Value : value.OutlineLevel,
        PageBreakBefore = PageBreakBefore.IsSet ? PageBreakBefore.Value : value.PageBreakBefore,
        ColumnBreakBefore = ColumnBreakBefore.IsSet ? ColumnBreakBefore.Value : value.ColumnBreakBefore,
        KeepWithNext = KeepWithNext.IsSet ? KeepWithNext.Value : value.KeepWithNext,
        KeepTogether = KeepTogether.IsSet ? KeepTogether.Value : value.KeepTogether,
        WidowControl = WidowControl.IsSet ? WidowControl.Value : value.WidowControl,
        EastAsianGrid = EastAsianGrid.IsSet ? EastAsianGrid.Value : value.EastAsianGrid,
        Frame = Frame.IsSet ? Frame.Value : value.Frame,
        SnapToGrid = SnapToGrid.IsSet ? SnapToGrid.Value : value.SnapToGrid
    };

    public static ParagraphStyleOverrides FromStyle(ParagraphStyle style) => new()
    {
        Alignment = new(style.Alignment),
        List = new(style.List),
        ListLevel = new(style.ListLevel),
        ListId = new(style.ListId),
        ListDefinition = new(style.ListDefinition),
        ListStart = new(style.ListStart),
        ListRestart = new(style.ListRestart),
        HeadingLevel = new(style.HeadingLevel),
        SpaceBefore = new(style.SpaceBefore),
        SpaceAfter = new(style.SpaceAfter),
        Indent = new(style.Indent),
        RightIndent = new(style.RightIndent),
        FirstLineIndent = new(style.FirstLineIndent),
        LineHeight = new(style.LineHeight),
        LetterSpacing = new(style.LetterSpacing),
        RightToLeft = new(style.RightToLeft),
        TabStops = new(style.TabStops),
        DefaultTabWidth = new(style.DefaultTabWidth),
        LineSpacingMode = new(style.LineSpacingMode),
        LineSpacing = new(style.LineSpacing),
        ContextualSpacing = new(style.ContextualSpacing),
        Borders = new(style.Borders),
        Shading = new(style.Shading),
        OutlineLevel = new(style.OutlineLevel),
        PageBreakBefore = new(style.PageBreakBefore),
        ColumnBreakBefore = new(style.ColumnBreakBefore),
        KeepWithNext = new(style.KeepWithNext),
        KeepTogether = new(style.KeepTogether),
        WidowControl = new(style.WidowControl),
        EastAsianGrid = new(style.EastAsianGrid),
        Frame = new(style.Frame),
        SnapToGrid = new(style.SnapToGrid)
    };

    /// <summary>Retains existing direct values and records only properties changed by a formatting command.</summary>
    public static ParagraphStyleOverrides Difference(ParagraphStyle before, ParagraphStyle after, ParagraphStyleOverrides? basis = null)
    {
        basis ??= new();
        return basis with
        {
            Alignment = EqualityComparer<ParagraphAlignment>.Default.Equals(before.Alignment, after.Alignment) ? basis.Alignment : new(after.Alignment),
            List = EqualityComparer<ListKind>.Default.Equals(before.List, after.List) ? basis.List : new(after.List),
            ListLevel = EqualityComparer<int>.Default.Equals(before.ListLevel, after.ListLevel) ? basis.ListLevel : new(after.ListLevel),
            ListId = EqualityComparer<Guid?>.Default.Equals(before.ListId, after.ListId) ? basis.ListId : new(after.ListId),
            ListDefinition = EqualityComparer<ListDefinition?>.Default.Equals(before.ListDefinition, after.ListDefinition) ? basis.ListDefinition : new(after.ListDefinition),
            ListStart = EqualityComparer<int?>.Default.Equals(before.ListStart, after.ListStart) ? basis.ListStart : new(after.ListStart),
            ListRestart = EqualityComparer<bool>.Default.Equals(before.ListRestart, after.ListRestart) ? basis.ListRestart : new(after.ListRestart),
            HeadingLevel = EqualityComparer<int>.Default.Equals(before.HeadingLevel, after.HeadingLevel) ? basis.HeadingLevel : new(after.HeadingLevel),
            SpaceBefore = EqualityComparer<double>.Default.Equals(before.SpaceBefore, after.SpaceBefore) ? basis.SpaceBefore : new(after.SpaceBefore),
            SpaceAfter = EqualityComparer<double>.Default.Equals(before.SpaceAfter, after.SpaceAfter) ? basis.SpaceAfter : new(after.SpaceAfter),
            Indent = EqualityComparer<double>.Default.Equals(before.Indent, after.Indent) ? basis.Indent : new(after.Indent),
            RightIndent = EqualityComparer<double>.Default.Equals(before.RightIndent, after.RightIndent) ? basis.RightIndent : new(after.RightIndent),
            FirstLineIndent = EqualityComparer<double>.Default.Equals(before.FirstLineIndent, after.FirstLineIndent) ? basis.FirstLineIndent : new(after.FirstLineIndent),
            LineHeight = EqualityComparer<double?>.Default.Equals(before.LineHeight, after.LineHeight) ? basis.LineHeight : new(after.LineHeight),
            LetterSpacing = EqualityComparer<double>.Default.Equals(before.LetterSpacing, after.LetterSpacing) ? basis.LetterSpacing : new(after.LetterSpacing),
            RightToLeft = EqualityComparer<bool>.Default.Equals(before.RightToLeft, after.RightToLeft) ? basis.RightToLeft : new(after.RightToLeft),
            TabStops = EqualityComparer<ImmutableArray<TabStop>>.Default.Equals(before.TabStops, after.TabStops) ? basis.TabStops : new(after.TabStops),
            DefaultTabWidth = EqualityComparer<double>.Default.Equals(before.DefaultTabWidth, after.DefaultTabWidth) ? basis.DefaultTabWidth : new(after.DefaultTabWidth),
            LineSpacingMode = EqualityComparer<LineSpacingMode>.Default.Equals(before.LineSpacingMode, after.LineSpacingMode) ? basis.LineSpacingMode : new(after.LineSpacingMode),
            LineSpacing = EqualityComparer<double>.Default.Equals(before.LineSpacing, after.LineSpacing) ? basis.LineSpacing : new(after.LineSpacing),
            ContextualSpacing = EqualityComparer<bool>.Default.Equals(before.ContextualSpacing, after.ContextualSpacing) ? basis.ContextualSpacing : new(after.ContextualSpacing),
            Borders = EqualityComparer<BlockBorders?>.Default.Equals(before.Borders, after.Borders) ? basis.Borders : new(after.Borders),
            Shading = EqualityComparer<string?>.Default.Equals(before.Shading, after.Shading) ? basis.Shading : new(after.Shading),
            OutlineLevel = EqualityComparer<int>.Default.Equals(before.OutlineLevel, after.OutlineLevel) ? basis.OutlineLevel : new(after.OutlineLevel),
            PageBreakBefore = EqualityComparer<bool>.Default.Equals(before.PageBreakBefore, after.PageBreakBefore) ? basis.PageBreakBefore : new(after.PageBreakBefore),
            ColumnBreakBefore = EqualityComparer<bool>.Default.Equals(before.ColumnBreakBefore, after.ColumnBreakBefore) ? basis.ColumnBreakBefore : new(after.ColumnBreakBefore),
            KeepWithNext = EqualityComparer<bool>.Default.Equals(before.KeepWithNext, after.KeepWithNext) ? basis.KeepWithNext : new(after.KeepWithNext),
            KeepTogether = EqualityComparer<bool>.Default.Equals(before.KeepTogether, after.KeepTogether) ? basis.KeepTogether : new(after.KeepTogether),
            WidowControl = EqualityComparer<bool>.Default.Equals(before.WidowControl, after.WidowControl) ? basis.WidowControl : new(after.WidowControl),
            EastAsianGrid = EqualityComparer<EastAsianGrid?>.Default.Equals(before.EastAsianGrid, after.EastAsianGrid) ? basis.EastAsianGrid : new(after.EastAsianGrid),
            Frame = EqualityComparer<ParagraphFrame?>.Default.Equals(before.Frame, after.Frame) ? basis.Frame : new(after.Frame),
            SnapToGrid = EqualityComparer<bool>.Default.Equals(before.SnapToGrid, after.SnapToGrid) ? basis.SnapToGrid : new(after.SnapToGrid)
        };
    }

    /// <summary>Clears one direct property using its TextStyle/ParagraphStyle property name.</summary>
    public ParagraphStyleOverrides Clear(string propertyName) => propertyName switch
    {
        nameof(Alignment) => this with { Alignment = default },
        nameof(List) => this with { List = default },
        nameof(ListLevel) => this with { ListLevel = default },
        nameof(ListId) => this with { ListId = default },
        nameof(ListDefinition) => this with { ListDefinition = default },
        nameof(ListStart) => this with { ListStart = default },
        nameof(ListRestart) => this with { ListRestart = default },
        nameof(HeadingLevel) => this with { HeadingLevel = default },
        nameof(SpaceBefore) => this with { SpaceBefore = default },
        nameof(SpaceAfter) => this with { SpaceAfter = default },
        nameof(Indent) => this with { Indent = default },
        nameof(RightIndent) => this with { RightIndent = default },
        nameof(FirstLineIndent) => this with { FirstLineIndent = default },
        nameof(LineHeight) => this with { LineHeight = default },
        nameof(LetterSpacing) => this with { LetterSpacing = default },
        nameof(RightToLeft) => this with { RightToLeft = default },
        nameof(TabStops) => this with { TabStops = default },
        nameof(DefaultTabWidth) => this with { DefaultTabWidth = default },
        nameof(LineSpacingMode) => this with { LineSpacingMode = default },
        nameof(LineSpacing) => this with { LineSpacing = default },
        nameof(ContextualSpacing) => this with { ContextualSpacing = default },
        nameof(Borders) => this with { Borders = default },
        nameof(Shading) => this with { Shading = default },
        nameof(OutlineLevel) => this with { OutlineLevel = default },
        nameof(PageBreakBefore) => this with { PageBreakBefore = default },
        nameof(ColumnBreakBefore) => this with { ColumnBreakBefore = default },
        nameof(KeepWithNext) => this with { KeepWithNext = default },
        nameof(KeepTogether) => this with { KeepTogether = default },
        nameof(WidowControl) => this with { WidowControl = default },
        nameof(EastAsianGrid) => this with { EastAsianGrid = default },
        nameof(Frame) => this with { Frame = default },
        nameof(SnapToGrid) => this with { SnapToGrid = default },
        _ => throw new ArgumentException("Unknown formatting property.", nameof(propertyName))
    };
}

