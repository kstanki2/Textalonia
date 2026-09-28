using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class TableExtensionInterchangeTests
{
    private static FlowDocument Sample(bool styles = false)
    {
        var table = Table.Create(3, 2) with
        {
            PreferredWidth = new(TableWidthUnit.Percentage, 75), AutoFit = TableAutoFit.Fixed,
            Alignment = TableAlignment.Right, Indent = 12, RightToLeft = true, RepeatHeaderRows = 1,
            RowSizing = [new() { AllowSplit = false }, new(), new() { Mode = TableRowHeightMode.AtLeast, Height = 40 }]
        };
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Paragraph("heading")], PreferredWidth = new(TableWidthUnit.Absolute, 120), VerticalAlignment = TableCellVerticalAlignment.Bottom });
        var document = new FlowDocument([table]);
        if (!styles) return document;
        return document with
        {
            Blocks = [table with { StyleId = "Banded" }],
            Styles = new() { Tables = ImmutableDictionary<string, TableStyleDefinition>.Empty.Add("Banded", new()
            {
                Id = "Banded", Formatting = new() { InsideHorizontal = new(new BorderSide(2, "#123456") { Kind = BorderKind.Dashed }) },
                Conditions = ImmutableDictionary<TableStyleRegion, TableStyleOverrides>.Empty
                    .Add(TableStyleRegion.OddRowBand, new() { Background = new("#ABCDEF") })
                    .Add(TableStyleRegion.LastRow, new() { VerticalAlignment = TableCellVerticalAlignment.Center })
            }) }
        };
    }

    private static Table OnlyTable(FlowDocument document) => Find(document.Blocks);
    private static Table Find(IEnumerable<Block> blocks) => blocks.OfType<Table>().FirstOrDefault() ?? blocks.OfType<Section>().Select(section => Find(section.Blocks)).First();
    private static void AssertSettings(Table expected, Table actual)
    {
        Assert.Equal(expected.PreferredWidth, actual.PreferredWidth); Assert.Equal(expected.AutoFit, actual.AutoFit);
        Assert.Equal(expected.Alignment, actual.Alignment); Assert.Equal(expected.Indent, actual.Indent); Assert.Equal(expected.RightToLeft, actual.RightToLeft);
        Assert.Equal(expected.RepeatHeaderRows, actual.RepeatHeaderRows); Assert.Equal(expected.RowSizing.ToArray(), actual.RowSizing.ToArray());
        Assert.Equal(expected.Rows[0][0].PreferredWidth, actual.Rows[0][0].PreferredWidth);
        Assert.Equal(expected.Rows[0][0].VerticalAlignment, actual.Rows[0][0].VerticalAlignment);
    }

    [Fact]
    public void Native_xaml_and_clipboard_preserve_extended_tables_without_loss()
    {
        var document = Sample(true); var table = OnlyTable(document);
        table = table with { Position = new(20, 30, 4) };
        table = table.SetCell(0, 0, table.Rows[0][0] with { TextDirection = TableCellTextDirection.RightToLeft, Borders = new(Top: new BorderSide(2, "#112233") { Kind = BorderKind.Double }) });
        document = document with { Blocks = [table] };
        foreach (var format in new TextDocumentFormat[] { DocumentFormats.Json, DocumentFormats.Xaml })
            Assert.Equal(DocumentFormats.Json.Serialize(document), DocumentFormats.Json.Serialize(format.Parse(format.Serialize(document))));
        var payload = ClipboardInterchange.Serialize(new() { Document = document });
        Assert.Equal(DocumentFormats.Json.Serialize(document), DocumentFormats.Json.Serialize(ClipboardInterchange.Parse(payload).Document));
        Assert.Contains("\"version\": 9", DocumentFormats.Json.Serialize(document));
        Assert.Contains("Version=\"4\"", DocumentFormats.Xaml.Serialize(document));
        Assert.Contains("\"fragmentVersion\":5", payload);
    }

    [Fact]
    public async Task Docx_preserves_preferred_widths_layout_headers_row_policies_and_conditional_borders()
    {
        var document = Sample(true);
        using var stream = new MemoryStream(); await DocumentFormats.Docx.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        stream.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        AssertSettings(OnlyTable(document), OnlyTable(loaded.Document));
        var style = loaded.Document.Styles.Tables["Banded"];
        Assert.Equal(BorderKind.Dashed, style.Formatting.InsideHorizontal.Value!.Kind);
        Assert.Equal("#ABCDEF", style.Conditions[TableStyleRegion.OddRowBand].Background.Value);
        Assert.Equal(TableCellVerticalAlignment.Center, style.Conditions[TableStyleRegion.LastRow].VerticalAlignment.Value);
    }

    [Fact]
    public async Task Docx_preserves_explicit_top_alignment_over_named_center_alignment()
    {
        var table = Table.Create(1, 1) with { StyleId = "Centered", PreferredWidth = new(TableWidthUnit.Absolute, 240) };
        table = table.SetCell(0, 0, table.Rows[0][0] with { StyleOverrides = new() { VerticalAlignment = TableCellVerticalAlignment.Top } });
        var document = new FlowDocument([table]) { Styles = new() { Tables = ImmutableDictionary<string, TableStyleDefinition>.Empty.Add("Centered",
            new() { Id = "Centered", Formatting = new() { VerticalAlignment = TableCellVerticalAlignment.Center } }) } };
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Docx.SaveWithReportAsync(document, stream);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == "docx.cell-style-overrides");
        stream.Position = 0; var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        var actual = OnlyTable(loaded);
        Assert.Equal(240, actual.ColumnWidths.Sum());
        Assert.Equal(TableCellVerticalAlignment.Top, new DocumentStyleResolver(loaded).ResolveTableCell(actual, 0, 0).VerticalAlignment);
    }

    [Fact]
    public async Task Rtf_preserves_supported_table_settings_without_loss()
    {
        var document = Sample(); using var stream = new MemoryStream();
        await DocumentFormats.Rtf.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict }); stream.Position = 0;
        var loaded = await DocumentFormats.Rtf.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        AssertSettings(OnlyTable(document), OnlyTable(loaded.Document));
    }

    [Fact]
    public async Task Html_preserves_widths_direction_headers_and_row_policy()
    {
        var document = Sample(); using var stream = new MemoryStream();
        await DocumentFormats.Html.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict }); stream.Position = 0;
        var loaded = await DocumentFormats.Html.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        AssertSettings(OnlyTable(document), OnlyTable(loaded.Document));
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("rtf")]
    [InlineData("html")]
    public async Task Positioned_table_loss_is_precise_and_strict_save_leaves_destination_untouched(string name)
    {
        IDocumentFormat format = name switch { "docx" => DocumentFormats.Docx, "rtf" => DocumentFormats.Rtf, _ => DocumentFormats.Html };
        var document = Sample(); document = document with { Blocks = [OnlyTable(document) with { Position = new(20, 30) }] };
        using var stream = new MemoryStream(); stream.WriteByte(42);
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() => format.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict }));
        Assert.Contains(error.Report.Diagnostics, d => d.Code == name + ".positioned-table"); Assert.Equal(new byte[] { 42 }, stream.ToArray());
    }

    [Fact]
    public async Task Docx_import_reports_vertical_text_and_noncontiguous_repeating_headers()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false)))
            writer.Write("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:tbl>
                <w:tr><w:tc><w:tcPr><w:textDirection w:val="tbRl"/></w:tcPr><w:p/></w:tc></w:tr>
                <w:tr><w:trPr><w:tblHeader/></w:trPr><w:tc><w:p/></w:tc></w:tr>
                </w:tbl></w:body></w:document>
                """);
        stream.Position = 0; var loaded = await DocumentFormats.Docx.LoadWithReportAsync(stream);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.cell-text-direction");
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.noncontiguous-header-rows");
        Assert.Equal(0, OnlyTable(loaded.Document).RepeatHeaderRows);
    }
}
