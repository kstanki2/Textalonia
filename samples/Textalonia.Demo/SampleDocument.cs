using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Demo;

public static class SampleDocument
{
    public static FlowDocument Create()
    {
        var blue = TextStyle.Default with { Foreground = "#357AC4", Bold = true };
        var title = new Paragraph("Make room for a better idea.", TextStyle.Default with { FontSize = 34, Bold = true })
            { Style = new() { HeadingLevel = 1, SpaceAfter = 14 } };
        var intro = new Paragraph([
            new RichRun("A document is more than a string. ", TextStyle.Default with { Bold = true }),
            new RichRun("Give your words structure, emphasis, and a little personality. This page is yours to edit.")
        ]);
        var features = new Paragraph([
            new RichRun("Try "), new RichRun("bold", TextStyle.Default with { Bold = true }), new RichRun(", "),
            new RichRun("italic", TextStyle.Default with { Italic = true }), new RichRun(", "),
            new RichRun("highlighted text", TextStyle.Default with { Background = "#F5D76E", Foreground = "#253248" }),
            new RichRun(", or a "), new RichRun("link", blue with { Hyperlink = "https://avaloniaui.net/", Underline = true }),
            new RichRun(". Undo brings every edit back.")
        ]);
        Paragraph Bullet(string text) => new(text) { Style = new() { List = ListKind.Bullet, SpaceAfter = 6 } };
        var note = new Section
        {
            Background = "#EDF4FD", BorderColor = "#4B88CD", Padding = 15,
            Blocks = [
                new Paragraph("A SMALL TIP", blue with { FontSize = 12 }) { Style = new() { SpaceAfter = 6 } },
                new Paragraph("Use Ctrl/Cmd+B, I, or U to format a selection. Shift+Enter adds a soft line break.",
                    TextStyle.Default with { Foreground = "#304E70" }) { Style = new() { SpaceAfter = 0 } }
            ]
        };
        var table = Table.Create(3, 3) with { AutoFit = TableAutoFit.Window, RepeatHeaderRows = 1, StyleId = "SampleTable" };
        var data = new[] { new[] { "Your next document", "Owner", "Status" }, new[] { "Design notes", "Product", "In progress" }, new[] { "Release checklist", "Engineering", "Ready" } };
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                table = table.SetCell(r, c, table.Rows[r][c] with
                {
                    Paragraphs = [new Paragraph(data[r][c], TextStyle.Default with { FontSize = 14, Bold = r == 0, Foreground = r == 0 ? "#344860" : null })
                    { Style = new() { SpaceAfter = 2 } }]
                });
        return new FlowDocument([
            title, intro, features,
            new Paragraph([new RichRun("An interactive inline: "), new RichRun(SampleInlineControls.Counter())]),
            Bullet("Style a paragraph, make a list, or add a table."),
            Bullet("Open and export documents with the buttons above."),
            note,
            new Paragraph("Keep everyone on the same page", TextStyle.Default with { FontSize = 22, Bold = true })
            { Style = new() { HeadingLevel = 2, SpaceBefore = 8, SpaceAfter = 12 } },
            table,
            new Paragraph("Built for Avalonia 12. Packaged for your next application.", TextStyle.Default with { FontSize = 13, Italic = true })
        ]) { Styles = new() { Tables = ImmutableDictionary<string, TableStyleDefinition>.Empty.Add("SampleTable", new()
        {
            Id = "SampleTable", Name = "Sample table bands",
            Conditions = ImmutableDictionary<TableStyleRegion, TableStyleOverrides>.Empty
                .Add(TableStyleRegion.HeaderRow, new() { Background = "#EDF2F8" })
                .Add(TableStyleRegion.EvenRowBand, new() { Background = "#F5F8FC" })
        }) } };
    }
}
