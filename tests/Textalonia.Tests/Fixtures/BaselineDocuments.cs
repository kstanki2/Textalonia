using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Textalonia.Model;

namespace Textalonia.Baselines;

// Shared by tests and benchmarks; inputs are independent of machine, time and culture.
internal static class BaselineDocuments
{
    public static readonly string[] Names = ["mixed-scripts", "emoji", "long-paragraph", "structured", "document-semantics"];
    public static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));
    private static Paragraph Paragraph(string name, string text) => new(text) { Id = Id(name) };

    public static FlowDocument Create(string name) => name switch
    {
        "mixed-scripts" => new([
            Paragraph("mixed-1", "Latin café e\u0301 Ελληνικά Русский 中文 日本語 한국어"),
            Paragraph("mixed-2", "abc العربية 123 עברית xyz") with { Style = new() { RightToLeft = true } },
            Paragraph("mixed-3", "soft\u2028break\ttab"), Paragraph("mixed-empty", "")]),
        "emoji" => new([Paragraph("emoji", "A👩‍💻e\u0301🇯🇵👍🏽👨‍👩‍👧‍👦Z")]),
        "long-paragraph" => new([Paragraph("long", string.Concat(Enumerable.Repeat("Long paragraph café 中文 👩‍💻. ", 3000)))]),
        "structured" => Structured(),
        "document-semantics" => DocumentSemantics(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown fixture.")
    };

    public static FlowDocument Structured()
    {
        var table = Grid(3, 3, "fixture");
        table = table.MergeCells(0, 0, 2, 2);
        // MergeCells creates fresh IDs in production; freeze those for this fixture.
        table = table.SetCell(0, 0, table.Rows[0][0] with
        {
            Paragraphs = table.Rows[0][0].Paragraphs.Select((p, i) => p with { Id = Id($"merged-{i}") }).ToImmutableArray()
        });
        return new([
            Paragraph("heading", "Baseline document") with { Style = new() { HeadingLevel = 1 } },
            new Section
            {
                Id = Id("section"), Background = "#EEF2FF", BorderColor = "#223344", Padding = 14,
                Blocks = [
                    new Paragraph([
                        new RichRun("bold ", TextStyle.Default with { Bold = true }),
                        new RichRun("italic link", TextStyle.Default with { Italic = true, Underline = true, Hyperlink = "https://example.com", Foreground = "#123ABC" }),
                        new RichRun(" 中文 👩‍💻\u2028soft", TextStyle.Default with { Background = "#FFEEDD", Baseline = Baseline.Superscript })
                    ]) { Id = Id("runs") },
                    Paragraph("bullet", "Bullet item") with { Style = new() { List = ListKind.Bullet, ListLevel = 1 } },
                    Paragraph("number", "Numbered item") with { Style = new() { List = ListKind.Numbered } },
                    table]
            },
            Paragraph("tail", "Tail")]);
    }

    public static Table Grid(int rows, int columns, string name) => new()
    {
        Id = Id(name + "-table"),
        Rows = Enumerable.Range(0, rows).Select(r => Enumerable.Range(0, columns).Select(c => new TableCell
        {
            Id = Id($"{name}-cell-{r}-{c}"),
            Background = (r + c) % 2 == 0 ? "#EEF2FF" : null,
            Paragraphs = [Paragraph($"{name}-p-{r}-{c}", $"Cell {r:D3}/{c:D2} café 中文")]
        }).ToImmutableArray()).ToImmutableArray()
    };

    public static FlowDocument DocumentSemantics()
    {
        var list = Id("semantics-list");
        var definition = new ListDefinition
        {
            Levels = [
                new() { Start = 3, Prefix = "[", Suffix = "]" },
                new() { Start = 2, Marker = ListMarkerStyle.LowerRoman, IncludeAncestors = true, Suffix = ")" },
                new() { Kind = ListKind.Bullet, Marker = ListMarkerStyle.Bullet, Text = "◆" }]
        };
        var style = new ParagraphStyle { List = ListKind.Numbered, ListId = list, ListDefinition = definition };
        var nested = Grid(2, 2, "semantics-nested");
        var outer = Grid(2, 2, "semantics-outer");
        outer = outer.SetCell(0, 0, outer.Rows[0][0] with
        {
            Blocks = [new Section
            {
                Id = Id("semantics-cell-section"), Blocks = [nested],
                PaddingEdges = new(3, 5, 7, 9),
                Borders = new(new(2, "#445566"), new(1, "#556677"), new(3, "#667788"), new(4, "#778899"))
            }],
            Padding = new(8, 4, 12, 6), Borders = new(new(1, "#112233"), new(2, "#223344"), new(4, "#334455"), new(3, "#445566"))
        });
        outer = outer with
        {
            ColumnWidths = [140, 220],
            RowSizing = [new() { Mode = TableRowHeightMode.AtLeast, Height = 60 }, new() { Mode = TableRowHeightMode.Exact, Height = 80 }]
        };
        outer = outer.SetCell(1, 0, outer.Rows[1][0] with
        { Blocks = [Grid(1, 1, "semantics-covered-nested")] });
        outer = outer.MergeCells(0, 0, 2, 1);
        // Freeze every cloned nested block/cell ID while preserving original backups.
        Block Freeze(Block block, string path) => block switch
        {
            Paragraph p => p with { Id = Id(path) },
            Section s => s with { Id = Id(path), Blocks = s.Blocks.Select((b, i) => Freeze(b, $"{path}/{i}")).ToImmutableArray() },
            Table t => t with
            {
                Id = Id(path), Rows = t.Rows.Select((row, r) => row.Select((cell, c) => cell with
                {
                    Id = Id($"{path}/{r}/{c}"),
                    Blocks = cell.Blocks.Select((b, i) => Freeze(b, $"{path}/{r}/{c}/{i}")).ToImmutableArray()
                }).ToImmutableArray()).ToImmutableArray()
            },
            _ => throw new InvalidOperationException()
        };
        outer = outer.SetCell(0, 0, outer.Rows[0][0] with
        { Blocks = outer.Rows[0][0].Blocks.Select((b, i) => Freeze(b, $"semantics-merged/{i}")).ToImmutableArray() });
        return new([
            Paragraph("semantics-rich", "Weighted and spaced") with
            {
                Runs = [new("Weighted and spaced", new() { Bold = true, FontWeight = 600, FontStretch = 6 })], DefaultStyle = new() { Bold = true, FontWeight = 350, FontStretch = 4 },
                Style = new() { LineHeight = 28, LetterSpacing = 1.25, Indent = 12, RightIndent = 15, FirstLineIndent = -8, SpaceBefore = 5, SpaceAfter = 13 }
            },
            Paragraph("semantics-first", "Start at three") with { Style = style },
            new Section
            {
                Id = Id("semantics-section"),
                Blocks = [Paragraph("semantics-intervening", "Intervening paragraph"),
                    Paragraph("semantics-child", "Nested level") with { Style = style with { ListLevel = 1 } }, Paragraph("semantics-bullet", "Custom bullet") with { Style = style with { ListLevel = 2 } }]
            },
            Paragraph("semantics-continue", "Continue") with { Style = style },
            Paragraph("semantics-restart", "Restart at seven") with { Style = style with { ListRestart = true, ListStart = 7 } },
            outer]);
    }

    public static FlowDocument Workload(string name) => name switch
    {
        "paragraphs-100" => Paragraphs(100),
        "paragraphs-1000" => Paragraphs(1000),
        "paragraphs-10000" => Paragraphs(10000),
        "long-paragraph" => Create(name),
        "table-heavy" => new([Grid(100, 10, "workload"), Paragraph("table-tail", "End")]),
        "run-heavy" => new(Enumerable.Range(0, 100).Select(p => new Paragraph(
            Enumerable.Range(0, 100).Select(r => new RichRun($"run{r:D2} café ",
                TextStyle.Default with { Bold = r % 2 == 0, Italic = r % 3 == 0, FontSize = 14 + r % 3 })))
            { Id = Id($"run-p-{p}") })),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static FlowDocument Paragraphs(int count) => new(Enumerable.Range(0, count).Select(i =>
        Paragraph($"workload-{i}", $"Paragraph {i:D5}: The quick brown fox edits café 中文. Stable baseline text for layout and history." +
            (i % 100 == 0 ? " NEEDLE" : ""))));
}
