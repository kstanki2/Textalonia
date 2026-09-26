using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Textalonia.Model;

namespace Textalonia.Baselines;

// Shared by tests and benchmarks; inputs are independent of machine, time and culture.
internal static class BaselineDocuments
{
    public static readonly string[] Names = ["mixed-scripts", "emoji", "long-paragraph", "structured"];
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
