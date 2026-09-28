using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class TableFormattingTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static void Build(DocumentLayout layout, FlowDocument document) => layout.Build(document, 500,
        FontFamily.Default, Brushes.Black, Brushes.Gray, new Thickness(0), new Rect(0, 0, 500, 10000));

    [Fact]
    public void Conditional_regions_follow_structural_edits_and_direct_explicit_null_wins()
    {
        var style = new TableStyleDefinition
        {
            Id = "bands",
            Formatting = new() { Background = "#FFFFFF", InsideHorizontal = new BorderSide(1, "#222222"),
                OutsideBorders = new BlockBorders(Left: new(3, "#FF0000")) },
            Conditions = ImmutableDictionary<TableStyleRegion, TableStyleOverrides>.Empty
                .Add(TableStyleRegion.OddRowBand, new() { Background = "#AAAAAA" })
                .Add(TableStyleRegion.FirstColumn, new() { Padding = new EdgeInsets(10, 2, 2, 2) })
                .Add(TableStyleRegion.HeaderRow, new() { Background = "#0000FF" })
        };
        var table = Table.Create(4, 2) with { StyleId = style.Id, RepeatHeaderRows = 1 };
        var doc = new FlowDocument([table]) { Styles = new() { Tables = ImmutableDictionary<string, TableStyleDefinition>.Empty.Add(style.Id, style) } };
        var resolver = new DocumentStyleResolver(doc);
        Assert.Equal("#0000FF", resolver.ResolveTableCell(table, 0, 0).Background);
        Assert.Equal("#AAAAAA", resolver.ResolveTableCell(table, 1, 0).Background);
        Assert.Equal("#FFFFFF", resolver.ResolveTableCell(table, 2, 0).Background);
        Assert.Equal(3, resolver.ResolveTableCell(table, 1, 0).Borders!.Left!.Width);
        Assert.Equal(1, resolver.ResolveTableCell(table, 1, 1).Borders!.Top!.Width);
        var changed = table.InsertRow(1);
        Assert.Equal(1, changed.RepeatHeaderRows);
        Assert.Equal("#AAAAAA", resolver.ResolveTableCell(changed, 3, 1).Background);
        changed = changed.SetCell(0, 0, changed.Rows[0][0] with { Background = "#00FF00", StyleOverrides = new() { Background = new(null) } });
        Assert.Null(resolver.ResolveTableCell(changed, 0, 0).Background);
        Assert.Equal(2, table.InsertRow(0).RepeatHeaderRows);
        Assert.Equal(0, table.RemoveRow(0).RepeatHeaderRows);
        (doc with { Blocks = [changed] }).Validate();
    }

    [Fact]
    public void Conditional_and_direct_borders_override_grid_edges_with_physical_rtl_sides()
    {
        var style = new TableStyleDefinition
        {
            Id = "edges", Formatting = new() { OutsideBorders = new BlockBorders(Left: new(3), Right: new(4)), InsideVertical = new BorderSide(1) },
            Conditions = ImmutableDictionary<TableStyleRegion, TableStyleOverrides>.Empty.Add(TableStyleRegion.HeaderRow,
                new() { Borders = new BlockBorders(Left: new(7), Right: new(7)) })
        };
        var table = Table.Create(2, 2) with { StyleId = style.Id, RightToLeft = true, RepeatHeaderRows = 1 };
        var doc = new FlowDocument([table]) { Styles = new() { Tables = ImmutableDictionary<string, TableStyleDefinition>.Empty.Add(style.Id, style) } };
        var resolver = new DocumentStyleResolver(doc);
        Assert.Equal(7, resolver.ResolveTableCell(table, 0, 0).Borders!.Left!.Width);
        Assert.Equal(4, resolver.ResolveTableCell(table, 1, 0).Borders!.Right!.Width);
        Assert.Equal(3, resolver.ResolveTableCell(table, 1, 1).Borders!.Left!.Width);
        table = table with { StyleOverrides = new() { Borders = new BlockBorders(Left: new(9)) } };
        Assert.Equal(9, resolver.ResolveTableCell(table, 0, 0).Borders!.Left!.Width);
        Assert.Null(resolver.ResolveTableCell(table, 0, 0).Borders!.Right);
    }

    [Fact]
    public Task Cell_direction_reaches_nested_paragraph_shaping_and_invalidates_on_edit() => Run(() =>
    {
        var paragraph = new Paragraph("abc \u05D0\u05D1\u05D2");
        var table = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [new Section { Blocks = [paragraph] }], TextDirection = TableCellTextDirection.RightToLeft });
        using var layout = new DocumentLayout(); Build(layout, new([table]));
        Assert.True(Assert.Single(layout.Paragraphs).Page.Owner.Paragraph.Style.RightToLeft);
        table = table.SetCell(0, 0, table.Rows[0][0] with { TextDirection = TableCellTextDirection.LeftToRight });
        Build(layout, new([table]));
        Assert.False(Assert.Single(layout.Paragraphs).Page.Owner.Paragraph.Style.RightToLeft);
        Assert.False(paragraph.Style.RightToLeft);
    });

    [Fact]
    public void Invalid_table_policies_are_rejected()
    {
        var table = Table.Create(1, 1);
        Assert.Throws<FormatException>(() => new FlowDocument([table with { Rows = default }]).Validate());
        Assert.Throws<FormatException>(() => new FlowDocument([table with { PreferredWidth = new(TableWidthUnit.Percentage, 101) }]).Validate());
        Assert.Throws<FormatException>(() => new FlowDocument([table with { RepeatHeaderRows = 2 }]).Validate());
        Assert.Throws<FormatException>(() => new FlowDocument([table with { Indent = double.NaN }]).Validate());
        Assert.Throws<FormatException>(() => new FlowDocument([table with { Position = new(double.PositiveInfinity) }]).Validate());
        Assert.Throws<FormatException>(() => new FlowDocument([table.SetCell(0, 0, table.Rows[0][0] with { Borders = new(Left: new(1) { Kind = (BorderKind)999 }) })]).Validate());
    }

    [Fact]
    public void Table_width_cache_does_not_retain_unrelated_document_content()
    {
        var (table, reference) = CacheSnapshot();
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(reference.IsAlive);
        GC.KeepAlive(table); // The immutable table stays alive in a later document snapshot.
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Table Table, WeakReference Snapshot) CacheSnapshot()
    {
        var table = Table.Create(1, 1);
        var document = new FlowDocument([table, new Paragraph(new string('x', 10000))]);
        var reference = new WeakReference(document);
        TableColumnLayout.Resolve(table, 500, new DocumentStyleResolver(document));
        return (table, reference);
    }

    [Fact]
    public Task Preferred_width_alignment_and_rtl_merged_cells_share_hit_testing() => Run(() =>
    {
        var table = Table.Create(1, 3).MergeCells(0, 0, 1, 2) with
        { PreferredWidth = new(TableWidthUnit.Absolute, 300), Alignment = TableAlignment.Right, Indent = 20, RightToLeft = true, ColumnWidths = [1, 2, 3] };
        using var layout = new DocumentLayout(); Build(layout, new([table]));
        var cells = layout.TableCells();
        var merged = Assert.Single(cells, c => c.Column == 0);
        var left = Assert.Single(cells, c => c.Column == 2);
        Assert.Equal(200, left.Bounds.X, 4);
        Assert.Equal(350, merged.Bounds.X, 4);
        Assert.Equal(150, merged.Bounds.Width, 4);
        Assert.Equal(500, merged.Bounds.Right, 4);
        Assert.Equal(0, layout.HitTestTableCell(merged.Bounds.Center)!.Column);
        Assert.Equal(2, layout.HitTestTableCell(left.Bounds.Center)!.Column);
    });

    [Fact]
    public Task Content_autofit_measures_rich_text_and_cell_preferences() => Run(() =>
    {
        var table = Table.Create(1, 2) with { AutoFit = TableAutoFit.Content };
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Paragraph("i")] });
        table = table.SetCell(0, 1, table.Rows[0][1] with { Blocks = [new Paragraph("wideword", new() { FontSize = 30 })] });
        var geometry = TableColumnLayout.Resolve(table, 500);
        Assert.True(geometry.Width < 500);
        Assert.True(geometry.Widths[1] > geometry.Widths[0] * 3);
        var fixedTable = table with { AutoFit = TableAutoFit.Fixed, PreferredWidth = new(TableWidthUnit.Percentage, 50) };
        fixedTable = fixedTable.SetCell(0, 0, fixedTable.Rows[0][0] with { PreferredWidth = new(TableWidthUnit.Absolute, 75) });
        var fixedGeometry = TableColumnLayout.Resolve(fixedTable, 500);
        Assert.Equal(250, fixedGeometry.Width, 4); Assert.Equal(75, fixedGeometry.Widths[0], 4);
        Assert.Equal(175, fixedGeometry.Widths[1], 4);
        Assert.Equal(500, TableColumnLayout.Resolve(fixedTable with { AutoFit = TableAutoFit.Window }, 500).Width, 4);
    });

    [Fact]
    public Task Vertical_alignment_moves_caret_and_nested_cell_geometry_without_model_edits() => Run(() =>
    {
        var inner = Table.Create(1, 1);
        var cell = new TableCell { Blocks = [inner], VerticalAlignment = TableCellVerticalAlignment.Bottom };
        var table = Table.Create(1, 1).SetCell(0, 0, cell) with { RowSizing = [new() { Mode = TableRowHeightMode.Exact, Height = 180 }] };
        var doc = new FlowDocument([table]);
        using var layout = new DocumentLayout(); Build(layout, doc);
        var nested = Assert.Single(layout.TableCells(), c => c.Table.Id == inner.Id);
        Assert.True(nested.Bounds.Y > 100);
        var caret = layout.Caret(0);
        Assert.True(nested.Bounds.Contains(caret.TopLeft));
        Assert.Equal(TableCellVerticalAlignment.Bottom, table.Rows[0][0].VerticalAlignment);
    });

    [Theory]
    [InlineData(BorderKind.Solid, 1)]
    [InlineData(BorderKind.Double, 2)]
    [InlineData(BorderKind.Dashed, 10)]
    [InlineData(BorderKind.Dotted, 25)]
    [InlineData(BorderKind.None, 0)]
    public Task Border_kinds_have_distinct_rendered_geometry(BorderKind kind, int expected) => Run(() =>
    {
        var decoration = new BlockDecoration(new Rect(0, 0, 100, 100), null, Brushes.Black, false,
            new(Top: new(2, "#000000") { Kind = kind }));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) decoration.Draw(context);
        int Shapes(Drawing item) => item is DrawingGroup group ? group.Children!.Sum(Shapes) : 1;
        Assert.Equal(expected, Shapes(drawing));
    });
}
