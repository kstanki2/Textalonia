using Textalonia.Model;

namespace Textalonia.Serialization;

internal static class TableConversion
{
    internal static void ReportLosses(string format, FlowDocument document)
    {
        void Visit(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                if (block is Section section) { Visit(section.Blocks); continue; }
                if (block is not Table table) continue;
                if (table.Position is not null)
                    ConversionDiagnostics.Report(format + ".positioned-table", "Positioned table geometry", "Table retained in normal flow; anchor coordinates and wrapping omitted.", table.Id);
                if (format == "rtf" && table.AutoFit == TableAutoFit.Window)
                    ConversionDiagnostics.Report("rtf.table-autofit-window", "AutoFit window mode", "Preferred width retained; automatic content-fitting layout used.", table.Id);
                if (table.StyleOverrides is not null || table.Rows.SelectMany(row => row).Any(cell => cell.StyleOverrides is not null))
                    ConversionDiagnostics.Report(format + ".table-style-overrides", "Sparse table or cell formatting overrides", "Effective cell appearance retained without override-presence information.", table.Id);
                var resolver = new DocumentStyleResolver(document);
                for (var row = 0; row < table.Rows.Length; row++) for (var col = 0; col < table.ColumnCount; col++)
                {
                    if (table.IsCovered(row, col)) continue;
                    var cell = resolver.ResolveTableCell(table, row, col);
                    if (format == "rtf" && cell.TextDirection != TableCellTextDirection.Inherit)
                        ConversionDiagnostics.Report("rtf.cell-text-direction", "Cell direction override", "Paragraph direction retained; cell-wide direction override omitted.", cell.Id);
                    Visit(cell.Blocks);
                }
            }
        }
        Visit(document.Blocks);
        foreach (var story in document.Stories.Values) Visit(story.Blocks);
    }
}
