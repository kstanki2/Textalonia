using System.Globalization;

namespace Textalonia.Model;

internal static class DocumentStyleValidation
{
    internal static void Validate(FlowDocument document)
    {
        if (document.Styles is null || document.Defaults is null || document.Theme is null ||
            document.Defaults.Text is null || document.Defaults.Paragraph is null ||
            document.Theme.Colors is null || document.Theme.Fonts is null ||
            document.Theme.Colors.Count > 256 || document.Theme.Fonts.Count > 256)
            throw new FormatException("Invalid document defaults or theme.");
        document.Styles.Validate();
        if (document.Defaults.Text.StyleId is not null || document.Defaults.Paragraph.StyleId is not null)
            throw new FormatException("Document defaults cannot reference named styles.");
        foreach (var color in document.Theme.Colors)
        {
            if (!InlineDescriptor.ValidKey(color.Key) || color.Value is null) throw new FormatException("Invalid theme color.");
            FlowDocument.ValidateColor(color.Value);
        }
        foreach (var font in document.Theme.Fonts)
            if (!InlineDescriptor.ValidKey(font.Key) || !InlineDescriptor.ValidKey(font.Value))
                throw new FormatException("Invalid theme font.");
        Text(document.Defaults.Text.Overrides?.Apply(TextStyle.Default) ?? document.Defaults.Text, document);
        Paragraph(document.Defaults.Paragraph.Overrides?.Apply(ParagraphStyle.Default) ?? document.Defaults.Paragraph, document);
        foreach (var style in document.Styles.Characters.Values) Text(style.Formatting.Apply(TextStyle.Default), document);
        foreach (var style in document.Styles.Paragraphs.Values)
        {
            Text(style.TextFormatting.Apply(TextStyle.Default), document);
            Paragraph(style.Formatting.Apply(ParagraphStyle.Default), document);
        }
        foreach (var style in document.Styles.Tables.Values) Table(style.Formatting.Apply(new TableStyle()));
    }

    internal static void Text(TextStyle style, FlowDocument document)
    {
        DocumentStyleCatalog.Reference(document.Styles.Characters, style.StyleId);
        if (!double.IsFinite(style.FontSize) || style.FontSize is < 1 or > 512 ||
            style.FontWeight is < 1 or > 1000 || style.FontStretch is < 1 or > 9 || !Enum.IsDefined(style.Baseline))
            throw new FormatException("Invalid text size, weight, stretch or baseline.");
        FlowDocument.ValidateColor(style.Foreground); FlowDocument.ValidateColor(style.Background);
        FlowDocument.ValidateColor(style.UnderlineColor);
        if (style.Hyperlink is not null && !FlowDocument.IsSafeHyperlink(style.Hyperlink))
            throw new FormatException("Links must use http, https, or mailto.");
        if (style.InternalLink is { } destination && (!InlineDescriptor.ValidKey(destination.BookmarkName) ||
            destination.Tooltip is { Length: > 4096 } || !Enum.IsDefined(destination.Activation) || style.Hyperlink is not null))
            throw new FormatException("Invalid internal link destination or conflicting external URI.");
        if (!Enum.IsDefined(style.UnderlineKind) || !Enum.IsDefined(style.StrikeKind) ||
            !double.IsFinite(style.Tracking) || style.Tracking is < -1000 or > 1000 ||
            !double.IsFinite(style.HorizontalScale) || style.HorizontalScale is < 0.01 or > 10 ||
            !double.IsFinite(style.BaselineOffset) || style.BaselineOffset is < -1000 or > 1000 ||
            style.KerningThreshold is { } kerning && (!double.IsFinite(kerning) || kerning is < 0 or > 512))
            throw new FormatException("Invalid character typography.");
        if (style.Language is { } language)
        {
            if (language.Length is 0 or > 128 || language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new FormatException("Invalid text language.");
            try { _ = CultureInfo.GetCultureInfo(language); }
            catch (CultureNotFoundException error) { throw new FormatException("Invalid text language.", error); }
        }
        foreach (var reference in new[] { style.ThemeFont, style.EastAsianThemeFont, style.ComplexScriptThemeFont })
            if (reference is not null && !InlineDescriptor.ValidKey(reference.Name)) throw new FormatException("Invalid theme font reference.");
        foreach (var reference in new[] { style.ThemeForeground, style.ThemeBackground })
            if (reference is not null && (!InlineDescriptor.ValidKey(reference.Name) ||
                !double.IsFinite(reference.Tint) || reference.Tint is < -1 or > 1))
                throw new FormatException("Invalid theme color reference.");
    }

    internal static void Paragraph(ParagraphStyle style, FlowDocument document)
    {
        DocumentStyleCatalog.Reference(document.Styles.Paragraphs, style.StyleId);
        ListNumbering.ValidateStyle(style);
        if (!Enum.IsDefined(style.Alignment) || !Enum.IsDefined(style.List) ||
            style.HeadingLevel is < 0 or > 6 || style.ListLevel is < 0 or > 8 ||
            !double.IsFinite(style.Indent) || style.Indent is < 0 or > 1000 ||
            !double.IsFinite(style.SpaceBefore) || style.SpaceBefore is < 0 or > 1000 ||
            !double.IsFinite(style.SpaceAfter) || style.SpaceAfter is < 0 or > 1000 ||
            !double.IsFinite(style.RightIndent) || style.RightIndent is < 0 or > 100000 ||
            !double.IsFinite(style.FirstLineIndent) || style.FirstLineIndent is < -100000 or > 100000 ||
            !double.IsFinite(style.LetterSpacing) || style.LetterSpacing is < -1000 or > 1000 ||
            style.LineHeight is { } lineHeight && (!double.IsFinite(lineHeight) || lineHeight <= 0 || lineHeight > 10000))
            throw new FormatException("Invalid paragraph formatting.");
        if (!Enum.IsDefined(style.LineSpacingMode) || !double.IsFinite(style.LineSpacing) ||
            style.LineSpacing is <= 0 or > 10000 || style.OutlineLevel is < 0 or > 9 ||
            !double.IsFinite(style.DefaultTabWidth) || style.DefaultTabWidth is < 0 or > 10000 ||
            style.TabStops.IsDefault || style.TabStops.Length > 256)
            throw new FormatException("Invalid paragraph typography.");
        var position = -1d;
        foreach (var tab in style.TabStops)
        {
            if (tab is null || !double.IsFinite(tab.Position) || tab.Position < 0 || tab.Position > 100000 ||
                tab.Position <= position || !Enum.IsDefined(tab.Alignment) || !Enum.IsDefined(tab.Leader) ||
                char.IsControl(tab.DecimalCharacter) || char.IsSurrogate(tab.DecimalCharacter))
                throw new FormatException("Tab stops must be valid, unique and sorted by position.");
            position = tab.Position;
        }
        if (style.Frame is { } frame && (!double.IsFinite(frame.X) || !double.IsFinite(frame.Y) ||
            frame.X < 0 || frame.Y < 0 || frame.X > 100000 || frame.Y > 100000 ||
            !double.IsFinite(frame.Width) || frame.Width <= 0 || frame.Width > 100000 ||
            frame.Height is { } height && (!double.IsFinite(height) || height <= 0 || height > 100000)))
            throw new FormatException("Invalid paragraph frame.");
        if (style.EastAsianGrid is { } grid &&
            (!double.IsFinite(grid.CharacterSpacing) || grid.CharacterSpacing is < 0 or > 1000 ||
             !double.IsFinite(grid.LineSpacing) || grid.LineSpacing is < 0 or > 1000))
            throw new FormatException("Invalid East Asian document grid.");
        FlowDocument.ValidateColor(style.Shading); Borders(style.Borders);
    }

    internal static void Table(TableStyle style)
    {
        FlowDocument.ValidateColor(style.Background); Borders(style.Borders);
        if (style.Padding is { } padding)
            foreach (var value in new[] { padding.Left, padding.Top, padding.Right, padding.Bottom })
                if (!double.IsFinite(value) || value is < 0 or > 1000) throw new FormatException("Invalid table style padding.");
    }

    private static void Borders(BlockBorders? borders)
    {
        if (borders is null) return;
        foreach (var side in new[] { borders.Left, borders.Top, borders.Right, borders.Bottom })
        {
            if (side is null) continue;
            if (!double.IsFinite(side.Width) || side.Width is < 0 or > 1000) throw new FormatException("Invalid border width.");
            FlowDocument.ValidateColor(side.Color);
        }
    }
}


