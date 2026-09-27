using Avalonia;
using Avalonia.Media;
using Textalonia.Layout;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class PageTableLayoutTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static FlowDocument Paged(Table table, PageSettings? settings = null) => new([table])
    {
        Sections = [new DocumentSection { PageSettings = settings ?? new() { Width = 400, Height = 500, Margins = new(20, 20, 20, 20) } }]
    };

    [Fact]
    public Task Row_spans_keep_distinct_cell_bounds_and_last_row_resize_metrics() => Run(() =>
    {
        var table = Table.Create(2, 2).MergeCells(0, 0, 2, 1) with
        { RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 60 }, new() { Mode = TableRowHeightMode.Exact, Height = 80 }] };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(table));
        var merged = Assert.Single(layout.TableCells(), cell => cell.Table.Id == table.Id && cell.Row == 0 && cell.Column == 0);
        var upper = Assert.Single(layout.TableCells(), cell => cell.Table.Id == table.Id && cell.Row == 0 && cell.Column == 1);
        var lower = Assert.Single(layout.TableCells(), cell => cell.Table.Id == table.Id && cell.Row == 1 && cell.Column == 1);
        Assert.Equal(140, merged.Bounds.Height); Assert.Equal(80, merged.RowHeight);
        Assert.Equal(upper.Bounds.Bottom, lower.Bounds.Top); Assert.Equal(60, upper.Bounds.Height); Assert.Equal(80, lower.Bounds.Height);
        Assert.Equal(1, layout.HitTestTableCell(lower.Bounds.Center)!.Row);
    });

    [Fact]
    public Task Nested_table_cells_have_hit_testing_and_geometry_inside_their_parent() => Run(() =>
    {
        var inner = Table.Create(2, 2);
        var outer = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [inner], Padding = new(10, 10, 10, 10) });
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(outer));
        var parent = Assert.Single(layout.TableCells(), cell => cell.Table.Id == outer.Id);
        var children = layout.TableCells().Where(cell => cell.Table.Id == inner.Id).ToArray(); Assert.Equal(4, children.Length);
        foreach (var child in children)
        {
            Assert.True(parent.Bounds.Contains(child.Bounds));
            Assert.Equal(inner.Id, layout.HitTestTableCell(child.Bounds.Center)!.Table.Id);
        }
    });

    [Fact]
    public Task Nested_exact_cell_clips_an_oversize_line_to_its_own_bounds() => Run(() =>
    {
        var paragraph = new Paragraph("oversize", new TextStyle { FontSize = 80 });
        var inner = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [paragraph] }) with
        { RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 30 }] };
        var outer = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [inner], Padding = new(10, 10, 10, 10) });
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(outer));
        var cell = Assert.Single(layout.TableCells(), cell => cell.Table.Id == inner.Id);
        var fragment = Assert.Single(layout.Fragments, fragment => fragment.ParagraphId == paragraph.Id);
        Assert.True(cell.Bounds.Contains(fragment.Clip));
        Assert.True(fragment.Clip.Height <= 30);
    });
    [Fact]
    public Task Unequal_column_table_continuations_reshape_remaining_text_at_actual_width() => Run(() =>
    {
        var paragraph = new Paragraph(string.Join(" ", Enumerable.Repeat("pagination", 110)))
        { Style = new() { SpaceAfter = 0 } };
        var table = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [paragraph], Padding = new(4, 4, 4, 4) });
        var settings = new PageSettings { Width = 520, Height = 210, Margins = new(20, 20, 20, 20),
            Columns = [new(2), new(1)], ColumnSpacing = 20, BalanceColumns = false };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(table, settings), new FontFamily("Inter"));
        Assert.Contains(layout.Fragments, fragment => fragment.ColumnIndex == 1);
        var ordered = layout.Fragments.Where(fragment => fragment.ParagraphId == paragraph.Id).OrderBy(fragment => fragment.TextStart).ToArray();
        Assert.Equal(0, ordered[0].TextStart); Assert.Equal(paragraph.Length, ordered[^1].TextEnd);
        for (var i = 1; i < ordered.Length; i++) Assert.Equal(ordered[i - 1].TextEnd, ordered[i].TextStart);
        foreach (var fragment in ordered)
        {
            var column = layout.Pages[fragment.PageIndex].Columns[fragment.ColumnIndex];
            Assert.True(fragment.Bounds.Width <= column.Width - 8 + .01);
            Assert.True(fragment.Bounds.Right <= column.Right + .01);
            Assert.True(fragment.Clip.Contains(fragment.Bounds));
        }
    });

    [Fact]
    public Task Oversize_atomic_table_content_is_consumed_once() => Run(() =>
    {
        var inline = new InlineDescriptor { Width = 40, Height = 600, Payload = new ControlInlinePayload("preview") };
        var paragraph = new Paragraph([new RichRun(inline), new RichRun(" following")]);
        var table = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [paragraph] });
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Paged(table,
            new() { Width = 300, Height = 180, Margins = new(20, 20, 20, 20) }));
        Assert.Single(layout.Fragments, fragment => fragment.TextStart == 0);
        Assert.InRange(layout.Pages.Length, 1, 3);
    });
}
