using Textalonia.Model;

namespace Textalonia.Serialization;

internal static class StyleConversion
{
    internal static void ReportLosses(IDocumentFormat format, FlowDocument document)
    {
        ListInterchange.ReportLosses(format, document);
        if (document.Styles.Characters.Count + document.Styles.Paragraphs.Count + document.Styles.Tables.Count != 0 ||
            document.Theme.Colors.Count + document.Theme.Fonts.Count != 0 || document.Theme.Name is not null ||
            document.Defaults != new DocumentDefaults())
            ConversionDiagnostics.Report("conversion.named-styles", "Named styles and document themes",
                "Effective appearance is exported; style identity, inheritance and theme references are flattened.");
        if (!document.Fonts.IsEmpty)
            ConversionDiagnostics.Report("conversion.document-fonts", "Document font declarations",
                "Embedded font data and document font substitution metadata are not retained by this format.");
        var resolver = new DocumentStyleResolver(document);
        foreach (var entry in new DocumentIndex(document).Paragraphs)
        {
            var original = entry.Paragraph;
            if (original.Style.Overrides is not null || original.DefaultStyle.Overrides is not null || original.Runs.Any(run => run.Style.Overrides is not null))
                ConversionDiagnostics.Report("conversion.direct-overrides", "Sparse formatting overrides",
                    "Effective appearance is exported without direct-formatting presence information.", original.Id);
            var p = resolver.ResolveParagraph(original);
            if (p.Style.DefaultTabWidth != ParagraphStyle.Default.DefaultTabWidth || !p.Style.TabStops.IsEmpty || p.Style.OutlineLevel != 0 || p.Style.ContextualSpacing ||
                p.Style.LineSpacingMode != LineSpacingMode.Natural || p.Style.Borders is not null || p.Style.Shading is not null ||
                p.Style.PageBreakBefore || p.Style.ColumnBreakBefore || p.Style.KeepTogether || p.Style.KeepWithNext || !p.Style.WidowControl ||
                p.Style.EastAsianGrid is not null || p.Style.SnapToGrid)
                ConversionDiagnostics.Report("conversion.paragraph-typography", "Extended paragraph formatting",
                    "The format retains only its documented paragraph subset.", p.Id);
            if (p.Runs.Select(r => r.Style).Append(p.DefaultStyle).Any(s => s.UnderlineKind != UnderlineKind.None ||
                s.UnderlineColor is not null || s.UnderlineWordsOnly || s.StrikeKind != StrikeKind.None ||
                s.AllCaps || s.SmallCaps || s.Language is not null || s.NoProof || s.Tracking != 0 ||
                s.HorizontalScale != 1 || s.BaselineOffset != 0 || s.KerningThreshold is not null ||
                s.EastAsianFontFamily is not null || s.ComplexScriptFontFamily is not null))
                ConversionDiagnostics.Report("conversion.run-typography", "Extended character formatting",
                    "The format retains only its documented character subset.", p.Id);
        }
    }
}
