using Avalonia;
using Avalonia.Media;
using Textalonia.Model;

namespace Textalonia.Controls;

internal static class ListMarkerDrawing
{
    internal static double Indent(Paragraph paragraph) => paragraph.Style.Indent +
        (paragraph.Style.List == ListKind.None ? 0 : paragraph.Style.ListDefinition?.Level(paragraph.Style.ListLevel, paragraph.Style.List).TextIndent ?? 28 + paragraph.Style.ListLevel * 24);

    internal static Paragraph Prepare(FlowDocument document, Paragraph source, Paragraph resolved, FontFamily font, IBrush foreground, DocumentFontService? fonts = null)
    {
        if (resolved.Style.List == ListKind.None) return resolved;
        var marker = ListNumbering.GetMarker(document, source.Id);
        if (marker is null) return resolved;
        var level = marker.LevelDefinition;
        var style = resolved.Style with { ListDefinition = marker.Definition };
        if (level.MarkerFormatting != new TextStyleOverrides() || level.CharacterStyleId is not null)
        {
            var markerStyle = new DocumentStyleResolver(document).ResolveListMarkerStyle(source, level);
            using var measured = Shape(marker.Text, markerStyle, font, foreground, fonts);
            if (style.LineSpacingMode == LineSpacingMode.Natural)
                style = style with { LineHeight = Math.Max(style.LineHeight ?? resolved.DefaultStyle.FontSize * 1.25, measured.Height) };
            else if (style.LineSpacingMode == LineSpacingMode.AtLeast)
                style = style with { LineSpacing = Math.Max(style.LineSpacing, measured.Height) };
        }
        if (level.MarkerIndent is not null || level.TextIndent is not null || level.TabPosition is not null || level.FollowCharacter != ListFollowCharacter.Tab)
        {
            var textIndent = level.TextIndent ?? 28 + style.ListLevel * 24;
            var markerIndent = level.MarkerIndent ?? Math.Max(0, textIndent - 24);
            var markerStyle = new DocumentStyleResolver(document).ResolveListMarkerStyle(source, level);
            using var layout = Shape(marker.Text, markerStyle, font, foreground, fonts);
            using var space = Shape(" ", markerStyle, font, foreground, fonts);
            var end = markerIndent + layout.Width;
            var first = level.FollowCharacter switch
            {
                ListFollowCharacter.Space => end + space.WidthIncludingTrailingWhitespace,
                ListFollowCharacter.Nothing => end,
                _ => Math.Max(level.TabPosition ?? textIndent, end + 4)
            };
            style = style with { FirstLineIndent = style.FirstLineIndent + first - textIndent };
        }
        return resolved with { Style = style };
    }

    internal static ShapingTextLayout Shape(string text, TextStyle style, FontFamily font, IBrush foreground, DocumentFontService? fonts = null)
    {
        // The native fast path has no document-font resolver. Resolve its single marker
        // run here; embedded collections continue through the shared font-aware shaper.
        if (fonts is not null && !fonts.HasEmbeddedFonts)
        {
            font = fonts.Resolve(style);
            style = style with { FontFamily = null };
            fonts = null;
        }
        return DocumentLayout.CreateTextLayout(new Paragraph(text, style) { Style = new() { SpaceAfter = 0 } }, 100000, font, foreground, fonts: fonts);
    }

    internal static void Draw(DrawingContext context, string text, TextStyle style, ListLevelDefinition level,
        Paragraph paragraph, Point textOrigin, FontFamily font, IBrush? foreground, Rect? clip = null,
        Rendering.IPageTextRenderer? textRenderer = null, DocumentFontService? fonts = null)
    {
        using var layout = Shape(text, style, font, foreground ?? Brushes.Black, fonts);
        var custom = level.MarkerIndent is not null || level.TextIndent is not null || level.TabPosition is not null || level.FollowCharacter != ListFollowCharacter.Tab;
        var textIndent = level.TextIndent ?? 28 + paragraph.Style.ListLevel * 24;
        var x = custom ? textOrigin.X - paragraph.Style.FirstLineIndent - textIndent + (level.MarkerIndent ?? Math.Max(0, textIndent - 24)) : textOrigin.X - layout.Width - 10;
        using var scope = context.PushClip(clip ?? new Rect(x, textOrigin.Y, Math.Max(100000, layout.Width), Math.Max(100000, layout.Height)));
        var origin = new Point(x, textOrigin.Y);
        foreach (var line in layout.TextLines)
        {
            if (textRenderer is null) line.Draw(context, origin); else textRenderer.DrawLine(context, line, origin);
            origin = new Point(origin.X, origin.Y + line.Height);
        }
    }
}
