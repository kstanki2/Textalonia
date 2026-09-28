using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class DocxDocumentFormat
{
    private static XElement WriteParagraphProperties(ParagraphStyle source, Guid id, IEnumerable<TabStop>? inheritedTabs = null, double documentTabWidth = 48)
    {
        var o = source.Overrides;
        var style = o?.Apply(ParagraphStyle.Default) ?? source;
        bool Has(bool specified) => o is null || specified;
        if (style.EastAsianGrid is not null) Loss("paragraph-grid", "Paragraph-local East Asian grid", "Snap behavior retained; section grid requires page layout support.", id: id);
        if (style.DefaultTabWidth > 0 && style.DefaultTabWidth != documentTabWidth) Loss("paragraph-default-tabs", "Paragraph-specific default tab width", "Explicit tab stops retained; document default tab width is used.", id: id);
        if (style.TabStops.Any(tab => tab.DecimalCharacter != '.')) Loss("tab-decimal-character", "Custom decimal tab character", "Decimal alignment retained using the consumer's locale.", id: id);
        var spacing = new XElement(W + "spacing",
            Has(o?.SpaceBefore.IsSet == true) ? new XAttribute(W + "before", Twips(style.SpaceBefore)) : null,
            Has(o?.SpaceAfter.IsSet == true) ? new XAttribute(W + "after", Twips(style.SpaceAfter)) : null);
        if (Has(o?.LineHeight.IsSet == true || o?.LineSpacing.IsSet == true || o?.LineSpacingMode.IsSet == true))
        {
            var mode = style.LineSpacingMode;
            var line = mode == LineSpacingMode.Natural ? style.LineHeight : style.LineSpacing;
            if (line is null && o?.LineHeight.IsSet == true) { mode = LineSpacingMode.Multiple; line = 1; }
            if (line is not null)
            {
                spacing.Add(new XAttribute(W + "line", mode == LineSpacingMode.Multiple ? (int)Math.Round(line.Value * 240) : Twips(line.Value)));
                spacing.Add(new XAttribute(W + "lineRule", mode == LineSpacingMode.Multiple ? "auto" : mode == LineSpacingMode.AtLeast ? "atLeast" : "exact"));
            }
        }
        var indent = new XElement(W + "ind",
            Has(o?.Indent.IsSet == true) ? new XAttribute(W + "left", Twips(style.Indent)) : null,
            Has(o?.RightIndent.IsSet == true) ? new XAttribute(W + "right", Twips(style.RightIndent)) : null,
            Has(o?.FirstLineIndent.IsSet == true) ? new XAttribute(W + (style.FirstLineIndent < 0 ? "hanging" : "firstLine"), Twips(Math.Abs(style.FirstLineIndent))) : null);
        return new XElement(W + "pPr",
            Has(o?.HyphenateCaps.IsSet == true) && (style.HyphenateCaps || o is not null) ? new XAttribute(Tx + "hyphenateCaps", style.HyphenateCaps ? 1 : 0) : null,
            source.StyleId is { } styleId ? Val("pStyle", styleId) : null,
            Has(o?.RightToLeft.IsSet == true) ? Val("bidi", style.RightToLeft ? 1 : 0) : null,
            Has(o?.Alignment.IsSet == true) ? Val("jc", style.Alignment switch { ParagraphAlignment.Center => "center", ParagraphAlignment.Right => "right", ParagraphAlignment.Justify => "both", _ => "left" }) : null,
            spacing.HasAttributes ? spacing : null, indent.HasAttributes ? indent : null,
            Has(o?.OutlineLevel.IsSet == true || o?.HeadingLevel.IsSet == true) && (style.OutlineLevel > 0 || style.HeadingLevel > 0 || o is not null) ? Val("outlineLvl", style.OutlineLevel > 0 ? style.OutlineLevel - 1 : style.HeadingLevel > 0 ? style.HeadingLevel - 1 : 9) : null,
            Has(o?.TabStops.IsSet == true) && (!style.TabStops.IsEmpty || o is not null) ? new XElement(W + "tabs", (inheritedTabs ?? []).Where(tab => !style.TabStops.Any(current => current.Position == tab.Position)).Select(tab => new XElement(W + "tab", new XAttribute(W + "val", "clear"), new XAttribute(W + "pos", Twips(tab.Position)))), style.TabStops.Select(tab => new XElement(W + "tab",
                new XAttribute(W + "pos", Twips(tab.Position)), new XAttribute(W + "val", tab.Alignment switch { TabAlignment.Center => "center", TabAlignment.Right => "right", TabAlignment.Decimal => "decimal", _ => "left" }),
                new XAttribute(W + "leader", tab.Leader switch { TabLeader.Dots => "dot", TabLeader.Dashes => "hyphen", TabLeader.Line => "underscore", _ => "none" })))) : null,
            Has(o?.ContextualSpacing.IsSet == true) && (style.ContextualSpacing || o is not null) ? Val("contextualSpacing", style.ContextualSpacing ? 1 : 0) : null,
            Has(o?.SuppressHyphenation.IsSet == true) && (style.SuppressHyphenation || o is not null) ? Val("suppressAutoHyphens", style.SuppressHyphenation ? 1 : 0) : null,
            Has(o?.PageBreakBefore.IsSet == true) && (style.PageBreakBefore || o is not null) ? Val("pageBreakBefore", style.PageBreakBefore ? 1 : 0) : null,
            Has(o?.KeepWithNext.IsSet == true) && (style.KeepWithNext || o is not null) ? Val("keepNext", style.KeepWithNext ? 1 : 0) : null,
            Has(o?.KeepTogether.IsSet == true) && (style.KeepTogether || o is not null) ? Val("keepLines", style.KeepTogether ? 1 : 0) : null,
            Has(o?.WidowControl.IsSet == true) ? Val("widowControl", style.WidowControl ? 1 : 0) : null,
            Has(o?.SnapToGrid.IsSet == true) && (style.SnapToGrid || o is not null) ? Val("snapToGrid", style.SnapToGrid ? 1 : 0) : null,
            style.Shading is { } shading ? new XElement(W + "shd", new XAttribute(W + "fill", Color(shading, id)), new XAttribute(W + "val", "clear")) : o?.Shading.IsSet == true ? new XElement(W + "shd", new XAttribute(W + "fill", "auto"), new XAttribute(W + "val", "clear")) : null,
            style.Borders is { } borders ? new XElement(W + "pBdr", WriteBorders(borders, id).Elements()) : o?.Borders.IsSet == true ? new XElement(W + "pBdr", WriteBorders(new BlockBorders(), id).Elements()) : null);
    }
    private static ThemeFontReference? ThemeFont(XElement source, string attribute) =>
        source.Attribute(W + attribute) is { } value ? new ThemeFontReference(value.Value) : null;
    private static ThemeColorReference? ThemeColor(XElement source, string name, string tint, string shade)
    {
        if (source.Attribute(W + name) is not { } value) return null;
        var amount = 0d;
        if (source.Attribute(W + tint) is { } light && byte.TryParse(light.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var l)) amount = 1 - l / 255d;
        else if (source.Attribute(W + shade) is { } dark && byte.TryParse(dark.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var d)) amount = d / 255d - 1;
        return new ThemeColorReference(value.Value, amount);
    }
    private static TextStyleOverrides ReadTextOverrides(XElement? properties)
    {
        var value = ReadTextStyle(properties, TextStyle.Default);
        bool Has(string name, string? attribute = null) => properties?.Element(W + name) is { } element && (attribute is null || element.Attribute(W + attribute) is not null);
        return new TextStyleOverrides
        {
            Bold = Has("b") ? new(value.Bold) : default,
            FontWeight = Has("b") ? new(value.FontWeight) : default,
            Italic = Has("i") ? new(value.Italic) : default,
            Underline = Has("u") ? new(value.Underline) : default,
            UnderlineKind = Has("u") ? new(value.UnderlineKind) : default,
            UnderlineWordsOnly = Has("u") ? new(value.UnderlineWordsOnly) : default,
            UnderlineColor = Has("u") ? new(value.UnderlineColor) : default,
            Strikethrough = Has("strike") || Has("dstrike") ? new(value.Strikethrough) : default,
            StrikeKind = Has("strike") || Has("dstrike") ? new(value.StrikeKind) : default,
            FontSize = Has("sz") ? new(value.FontSize) : default,
            Foreground = Has("color") ? new(value.Foreground) : default,
            ThemeForeground = Has("color") ? new(value.ThemeForeground) : default,
            Background = Has("shd") || Has("highlight") ? new(value.Background) : default,
            ThemeBackground = Has("shd") ? new(value.ThemeBackground) : default,
            Baseline = Has("vertAlign") ? new(value.Baseline) : default,
            AllCaps = Has("caps") ? new(value.AllCaps) : default,
            SmallCaps = Has("smallCaps") ? new(value.SmallCaps) : default,
            Language = Has("lang") ? new(value.Language) : default,
            NoProof = Has("noProof") ? new(value.NoProof) : default,
            Tracking = Has("spacing") ? new(value.Tracking) : default,
            HorizontalScale = Has("w") ? new(value.HorizontalScale) : default,
            BaselineOffset = Has("position") ? new(value.BaselineOffset) : default,
            KerningThreshold = Has("kern") ? new(value.KerningThreshold) : default,
            FontFamily = Has("rFonts", "ascii") || Has("rFonts", "hAnsi") ? new(value.FontFamily) : default,
            ThemeFont = Has("rFonts", "ascii") || Has("rFonts", "hAnsi") || Has("rFonts", "asciiTheme") || Has("rFonts", "hAnsiTheme") ? new(value.ThemeFont) : default,
            EastAsianFontFamily = Has("rFonts", "eastAsia") ? new(value.EastAsianFontFamily) : default,
            ComplexScriptFontFamily = Has("rFonts", "cs") ? new(value.ComplexScriptFontFamily) : default,
            EastAsianThemeFont = Has("rFonts", "eastAsia") || Has("rFonts", "eastAsiaTheme") ? new(value.EastAsianThemeFont) : default,
            ComplexScriptThemeFont = Has("rFonts", "cs") || Has("rFonts", "cstheme") ? new(value.ComplexScriptThemeFont) : default,
        };
    }
    private static ParagraphStyleOverrides ReadParagraphOverrides(XElement? properties, Dictionary<string, NumberingInfo> numbering)
    {
        var value = ReadParagraphStyle(properties, ParagraphStyle.Default, numbering);
        bool Has(string name, string? attribute = null) => properties?.Element(W + name) is { } element && (attribute is null || element.Attribute(W + attribute) is not null);
        return new ParagraphStyleOverrides
        {
            Alignment = Has("jc") ? new(value.Alignment) : default,
            RightToLeft = Has("bidi") ? new(value.RightToLeft) : default,
            Indent = Has("ind", "left") ? new(value.Indent) : default,
            RightIndent = Has("ind", "right") ? new(value.RightIndent) : default,
            FirstLineIndent = Has("ind", "firstLine") || Has("ind", "hanging") ? new(value.FirstLineIndent) : default,
            SpaceBefore = Has("spacing", "before") ? new(value.SpaceBefore) : default,
            SpaceAfter = Has("spacing", "after") ? new(value.SpaceAfter) : default,
            LineHeight = Has("spacing", "line") ? new(value.LineHeight) : default,
            LineSpacing = Has("spacing", "line") ? new(value.LineSpacing) : default,
            LineSpacingMode = Has("spacing", "line") ? new(value.LineSpacingMode) : default,
            List = Has("numPr") ? new(value.List) : default,
            ListLevel = Has("numPr") ? new(value.ListLevel) : default,
            ListId = Has("numPr") ? new(value.ListId) : default,
            ListDefinition = Has("numPr") ? new(value.ListDefinition) : default,
            ListStart = Has("numPr") ? new(value.ListStart) : default,
            ListRestart = Has("numPr") ? new(value.ListRestart) : default,
            HeadingLevel = Has("outlineLvl") ? new(value.HeadingLevel) : default,
            OutlineLevel = Has("outlineLvl") ? new(value.OutlineLevel) : default,
            TabStops = Has("tabs") ? new(value.TabStops) : default,
            ContextualSpacing = Has("contextualSpacing") ? new(value.ContextualSpacing) : default,
            SuppressHyphenation = Has("suppressAutoHyphens") ? new(value.SuppressHyphenation) : default,
            HyphenateCaps = properties?.Attribute(Tx + "hyphenateCaps") is not null ? new(value.HyphenateCaps) : default,
            Borders = Has("pBdr") ? new(value.Borders) : default,
            Shading = Has("shd") ? new(value.Shading) : default,
            PageBreakBefore = Has("pageBreakBefore") ? new(value.PageBreakBefore) : default,
            KeepWithNext = Has("keepNext") ? new(value.KeepWithNext) : default,
            KeepTogether = Has("keepLines") ? new(value.KeepTogether) : default,
            WidowControl = Has("widowControl") ? new(value.WidowControl) : default,
            SnapToGrid = Has("snapToGrid") ? new(value.SnapToGrid) : default,
        };
    }
}
