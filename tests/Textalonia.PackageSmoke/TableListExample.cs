using Textalonia.Controls;
using Textalonia.Layout;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.PackageSmoke;

internal static class TableListExample
{
    internal static void Verify()
    {
        var table = Table.Create(12, 2) with { AutoFit = TableAutoFit.Fixed,
            PreferredWidth = new(TableWidthUnit.Percentage, 80), RepeatHeaderRows = 1, RightToLeft = true };
        for (var row = 0; row < table.Rows.Length; row++)
            for (var column = 0; column < table.ColumnCount; column++)
                table = table.SetCell(row, column, table.Rows[row][column] with
                { Blocks = [new Paragraph($"Cell {row}:{column}")], VerticalAlignment = TableCellVerticalAlignment.Center });
        var list = new ListDefinition { Levels = [new() { TextIndent = 48, MarkerIndent = 4,
            MarkerFormatting = new() { Bold = true, Foreground = "#334455" }, FollowCharacter = ListFollowCharacter.Space }] };
        var document = new FlowDocument([table, new Paragraph("Styled marker") { Style = list.CreateParagraphStyle(0, Guid.NewGuid()) }])
        { Sections = [new DocumentSection { PageSettings = new() { Width = 400, Height = 210, Margins = new(20, 20, 20, 20) } }] };
        document.Validate();
        foreach (var format in new TextDocumentFormat[] { DocumentFormats.Json, DocumentFormats.Xaml })
        {
            var restored = format.Parse(format.Serialize(document));
            if (DocumentFormats.Json.Serialize(restored) != DocumentFormats.Json.Serialize(document))
                throw new InvalidOperationException("Packaged table/list round trip failed.");
        }
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        if (pages.Pages.Length < 2 || !pages.Fragments.Any(fragment => fragment.IsRepeatedTableHeader))
            throw new InvalidOperationException("Packaged repeating table headers failed.");
        var editor = new TextaloniaEditor { Document = document };
        editor.SelectTableCells(table.Id, 1, 0, 2, 0);
        editor.SetTableRowsAllowSplit(false); editor.SetTableCellTextDirection(TableCellTextDirection.RightToLeft);
        editor.Undo(); editor.Undo();
        if (!ReferenceEquals(editor.Document, document)) throw new InvalidOperationException("Packaged table undo failed.");
    }
}
