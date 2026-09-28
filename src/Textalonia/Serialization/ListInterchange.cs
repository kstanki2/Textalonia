using Textalonia.Model;

namespace Textalonia.Serialization;

internal static class ListInterchange
{
    internal static void ReportLosses(IDocumentFormat format, FlowDocument document)
    {
        var resolver = new DocumentStyleResolver(document);
        foreach (var owner in document.Stories.Keys.Select(document.GetStoryDocument).Prepend(document))
            foreach (var position in new DocumentIndex(owner).Paragraphs)
            {
                var paragraph = position.Paragraph;
                var style = resolver.ResolveParagraphStyle(paragraph.Style);
                if (style.ListDefinition is not { } definition) continue;
                foreach (var level in definition.Levels)
                {
                    var extended = level.MarkerFormatting != new TextStyleOverrides() || level.CharacterStyleId is not null ||
                        level.TextIndent is not null || level.MarkerIndent is not null || level.TabPosition is not null || level.FollowCharacter != ListFollowCharacter.Tab;
                    if (level.CharacterStyleId is not null || level.ParagraphStyleId is not null)
                        ConversionDiagnostics.Report("conversion.list-style-links", "Named paragraph/character links on a list level",
                            "Exported resolved marker formatting without editable list style links.", paragraph.Id);
                    if (format is HtmlDocumentFormat && extended)
                        ConversionDiagnostics.Report("html.list-marker-appearance", "Custom marker typography, indentation or following character",
                            "Retained list metadata for Textalonia; browsers display their native list marker layout.", paragraph.Id);
                    if (format is RtfDocumentFormat)
                    {
                        var marker = resolver.ResolveListMarkerStyle(paragraph, level);
                        if (marker.UnderlineKind != UnderlineKind.None || marker.UnderlineColor is not null || marker.UnderlineWordsOnly ||
                            marker.StrikeKind != StrikeKind.None || marker.AllCaps || marker.SmallCaps || marker.Tracking != 0 || marker.HorizontalScale != 1 ||
                            marker.BaselineOffset != 0 || marker.KerningThreshold is not null || marker.Language is not null || marker.NoProof ||
                            marker.EastAsianFontFamily is not null || marker.ComplexScriptFontFamily is not null || marker.Hyperlink is not null || marker.InternalLink is not null)
                            ConversionDiagnostics.Report("rtf.list-marker-typography", "Extended typography or navigation on a list marker",
                                "Retained marker font, size, weight, italic, basic underline/strike, width class, baseline and colors; other marker properties are omitted.", paragraph.Id);
                        if (new[] { level.TextIndent, level.MarkerIndent, level.TabPosition }.Any(value => value is { } number && Math.Abs(number * 15 - Math.Round(number * 15)) > .00001))
                            ConversionDiagnostics.Report("rtf.list-position-precision", "Fractional list marker/text/tab positions", "Rounded list positions to RTF twips (1/15 DIP).", paragraph.Id);
                    }
                }
            }
    }
}
