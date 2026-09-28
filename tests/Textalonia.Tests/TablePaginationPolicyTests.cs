using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Layout;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class TablePaginationPolicyTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static FlowDocument Paged(params Block[] blocks) => new(blocks)
    {
        Sections = [new DocumentSection { PageSettings = new() { Width = 320, Height = 220, Margins = new(20, 20, 20, 20) } }]
    };
    private static Paragraph Text(string text) => new(text.Replace("\n", "\u2028", StringComparison.Ordinal)) { Style = new() { SpaceBefore = 0, SpaceAfter = 0 } };

    [Fact]
    public Task Repeated_headers_share_offsets_without_changing_canonical_caret_page() => Run(() =>
    {
        var table = Table.Create(8, 2) with { RepeatHeaderRows = 1,
            RowSizing = Enumerable.Repeat(new TableRowSizing { Mode = TableRowHeightMode.Exact, Height = 45 }, 8).ToImmutableArray() };
        var header = Text("Header"); table = table.SetCell(0, 0, new() { Blocks = [header] });
        var document = Paged(table); var text = document.Text;
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(document, new FontFamily("Inter"));
        var copies = layout.Fragments.Where(f => f.ParagraphId == header.Id).ToArray();
        Assert.True(layout.Pages.Length >= 3);
        Assert.Equal(layout.Pages.Length, copies.Length);
        Assert.Single(copies, f => !f.IsRepeatedTableHeader);
        Assert.All(copies, f => Assert.Equal(copies[0].TextStart, f.TextStart));
        Assert.Equal(0, layout.GetPageIndex(copies[0].TextStart));
        Assert.Equal(text, document.Text);
        foreach (var copy in copies)
            Assert.InRange(layout.HitTest(copy.Bounds.TopLeft + new Vector(1, 1)), copy.TextStart, copy.TextEnd);
    });

    [Fact]
    public Task Split_policy_moves_intact_rows_but_allowed_rows_use_remaining_space() => Run(() =>
    {
        var before = Text("before") with { Style = new() { SpaceAfter = 70 } };
        var body = Text("one\ntwo\nthree\nfour\nfive\nsix");
        var table = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [body] });
        using var engine = new PaginationEngine();
        using var split = engine.Paginate(Paged(before, table), new FontFamily("Inter"));
        using var intact = engine.Paginate(Paged(before, table with { RowSizing = [new() { AllowSplit = false }] }), new FontFamily("Inter"));
        Assert.Equal(0, split.Fragments.First(f => f.ParagraphId == body.Id).PageIndex);
        Assert.Equal(1, intact.Fragments.First(f => f.ParagraphId == body.Id).PageIndex);
        Assert.Single(intact.Fragments.Where(f => f.ParagraphId == body.Id).Select(f => f.PageIndex).Distinct());
    });

    [Fact]
    public Task Oversized_unsplittable_row_with_header_makes_bounded_progress() => Run(() =>
    {
        var body = Text(string.Join("\n", Enumerable.Range(0, 35).Select(i => $"line {i}")));
        var table = Table.Create(2, 1).SetCell(1, 0, new() { Blocks = [body] }) with
        { RepeatHeaderRows = 1, RowSizing = [new(), new() { AllowSplit = false }] };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(table), new FontFamily("Inter"));
        Assert.InRange(layout.Pages.Length, 2, 12);
        Assert.Contains(layout.LayoutDiagnostics, d => d.Contains("split to guarantee progress", StringComparison.Ordinal));
        var lines = layout.Fragments.Where(f => f.ParagraphId == body.Id).OrderBy(f => f.TextStart).ToArray();
        for (var i = 1; i < lines.Length; i++) Assert.Equal(lines[i - 1].TextEnd, lines[i].TextStart);
        Assert.Equal(body.Length, lines[^1].TextEnd - lines[0].TextStart);
    });

    [Fact]
    public Task Header_rowspan_repeats_complete_merged_group() => Run(() =>
    {
        var table = Table.Create(7, 2).MergeCells(0, 0, 2, 1) with { RepeatHeaderRows = 1,
            RowSizing = Enumerable.Repeat(new TableRowSizing { Mode = TableRowHeightMode.Exact, Height = 35 }, 7).ToImmutableArray() };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(table));
        var copies = layout.TableCells().Where(c => c.Table.Id == table.Id && c.Row == 0 && c.Column == 0).ToArray();
        Assert.Equal(layout.Pages.Length, copies.Length);
        Assert.All(copies, cell => Assert.Equal(70, cell.Bounds.Height));
    });

    [Fact]
    public Task Preferred_width_alignment_rtl_and_cell_vertical_direction_share_page_geometry() => Run(() =>
    {
        var paragraph = Text("abc ???");
        var table = Table.Create(1, 3).MergeCells(0, 0, 1, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [paragraph],
            VerticalAlignment = TableCellVerticalAlignment.Bottom, TextDirection = TableCellTextDirection.RightToLeft }) with
        { PreferredWidth = new(TableWidthUnit.Absolute, 180), Alignment = TableAlignment.Center, RightToLeft = true,
            RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 100 }] };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(table), new FontFamily("Inter"));
        var merged = Assert.Single(layout.TableCells(), c => c.Column == 0);
        var last = Assert.Single(layout.TableCells(), c => c.Column == 2);
        Assert.Equal(120, merged.Bounds.Width, 5);
        Assert.Equal(70, last.Bounds.Left, 5);
        Assert.Equal(last.Bounds.Right, merged.Bounds.Left, 5);
        var line = Assert.Single(layout.Fragments, f => f.ParagraphId == paragraph.Id);
        Assert.True(line.Bounds.Top > merged.Bounds.Top + 50);
        Assert.True(line.Measurement.Paragraph.Style.RightToLeft);
    });

    [Fact]
    public Task Positioned_table_wraps_following_lines_and_restores_full_width_below_it() => Run(() =>
    {
        var table = Table.Create(1, 1) with { Position = new(0, 0, 6),
            PreferredWidth = new(TableWidthUnit.Absolute, 110),
            RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 70 }] };
        var paragraph = Text(string.Join(" ", Enumerable.Repeat("wrapped words", 45)));
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(table, paragraph), new FontFamily("Inter"));
        var cell = Assert.Single(layout.TableCells());
        var lines = layout.Fragments.Where(f => f.ParagraphId == paragraph.Id && f.PageIndex == 0).ToArray();
        Assert.True(lines[0].Bounds.Left >= cell.Bounds.Right + 6 - .01);
        Assert.Contains(lines, line => line.Bounds.Top >= cell.Bounds.Bottom + 6 && Math.Abs(line.Bounds.Left - 20) < .01);
        Assert.All(lines, line => Assert.False(line.Bounds.Intersects(cell.Bounds)));
        var all = layout.Fragments.Where(f => f.ParagraphId == paragraph.Id).ToArray();
        for (var i = 1; i < all.Length; i++) Assert.Equal(all[i - 1].TextEnd, all[i].TextStart);
    });

    [Fact]
    public Task Positioned_table_clips_oversized_content_once_with_diagnostic() => Run(() =>
    {
        var table = Table.Create(1, 1) with { Position = new(10, 10),
            PreferredWidth = new(TableWidthUnit.Absolute, 100),
            RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 600 }] };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(table));
        Assert.Single(layout.Pages);
        Assert.Contains(layout.LayoutDiagnostics, d => d.Contains("clipped without adding overflow pages", StringComparison.Ordinal));
        Assert.All(layout.TableCells(), cell => Assert.True(layout.Pages[0].Columns[0].Contains(cell.VisibleBounds)));
    });

    [Fact]
    public Task Merged_cell_continues_under_repeated_headers_at_unequal_column_widths() => Run(() =>
    {
        var body = Text(string.Join(" ", Enumerable.Repeat("merged pagination content", 70)));
        var table = Table.Create(3, 2).MergeCells(1, 0, 2, 1) with { RepeatHeaderRows = 1 };
        table = table.SetCell(1, 0, table.Rows[1][0] with { Blocks = [body] });
        var document = Paged(table) with { Sections = [new DocumentSection { PageSettings = new()
        { Width = 560, Height = 240, Margins = new(20, 20, 20, 20), Columns = [new(2), new(1)], ColumnSpacing = 20, BalanceColumns = false } }] };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(document, new FontFamily("Inter"));
        Assert.Contains(layout.Fragments, f => f.IsRepeatedTableHeader && f.ColumnIndex == 1);
        var lines = layout.Fragments.Where(f => f.ParagraphId == body.Id).ToArray();
        for (var i = 1; i < lines.Length; i++) Assert.Equal(lines[i - 1].TextEnd, lines[i].TextStart);
        Assert.Equal(body.Length, lines[^1].TextEnd - lines[0].TextStart);
        Assert.All(lines, line => Assert.True(layout.Pages[line.PageIndex].Columns[line.ColumnIndex].Contains(line.Clip)));
    });

    [Fact]
    public Task Oversized_header_is_suppressed_when_a_complete_body_line_cannot_fit() => Run(() =>
    {
        var body = Text(string.Join("\n", Enumerable.Repeat("line", 12)));
        var table = Table.Create(2, 1).SetCell(1, 0, new() { Blocks = [body] }) with
        { RepeatHeaderRows = 1, RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 165 }, new()] };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(table), new FontFamily("Inter"));
        Assert.InRange(layout.Pages.Length, 2, 5);
        Assert.Contains(layout.LayoutDiagnostics, d => d.Contains("omits repeated headers", StringComparison.Ordinal));
        var lines = layout.Fragments.Where(f => f.ParagraphId == body.Id).ToArray();
        Assert.Equal(body.Length, lines[^1].TextEnd - lines[0].TextStart);
    });

    [Fact]
    public Task Tall_inline_and_formatted_list_marker_wrap_clear_of_staggered_tables() => Run(() =>
    {
        var left = Table.Create(1, 1) with { Position = new(0, 0, 4), PreferredWidth = new(TableWidthUnit.Absolute, 110),
            RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 40 }] };
        var right = Table.Create(1, 1) with { Position = new(170, 40, 4), PreferredWidth = new(TableWidthUnit.Absolute, 110),
            RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 80 }] };
        var paragraph = new Paragraph([new RichRun(new InlineDescriptor { Width = 90, Height = 90,
            Payload = new ControlInlinePayload("preview") }), new RichRun(" following words")])
        { Style = new() { SpaceAfter = 0 } };
        var list = Text("listed text") with { Style = new() { List = ListKind.Numbered,
            ListDefinition = new() { Levels = [new() { Start = 123, MarkerFormatting = new() { FontSize = new(32) } }] } } };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(left, right, list, paragraph), new FontFamily("Inter"));
        var exclusions = layout.TableCells().Select(cell => cell.Bounds).ToArray();
        var lines = layout.Fragments.Where(f => f.ParagraphId == paragraph.Id || f.ParagraphId == list.Id).ToArray();
        Assert.All(lines.Where(line => line.PageIndex == 0), line => Assert.All(exclusions, rect => Assert.False(rect.Intersects(line.Bounds.Intersect(line.Clip)))));
        var inline = lines.First(line => line.ParagraphId == paragraph.Id);
        Assert.True(inline.PageIndex > 0 || inline.Bounds.Top >= exclusions[0].Bottom + 4);
        var markerLine = Assert.Single(lines, line => line.ParagraphId == list.Id);
        Assert.Equal(0, markerLine.PageIndex);
        using var marker = ListMarkerDrawing.Shape(markerLine.Marker!, markerLine.MarkerStyle!, new FontFamily("Inter"), Brushes.Black);
        var markerBounds = new Rect(markerLine.Origin.X - marker.Width - 10, markerLine.Origin.Y, marker.Width, marker.Height);
        Assert.All(exclusions, rect => Assert.False(rect.Intersects(markerBounds)));
        Assert.True(markerLine.Clip.Contains(markerLine.Bounds));
    });
}
