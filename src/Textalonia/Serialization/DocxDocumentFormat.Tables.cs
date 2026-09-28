using System.Collections.Immutable;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class DocxDocumentFormat
{
    private static TablePreferredWidth ReadPreferredWidth(XElement? element)
    {
        if (element is null) return new();
        var type = (string?)element.Attribute(W + "type");
        var value = Dimension(element, W + "w", 0);
        if (type is "auto" or "nil" || value == 0) return new();
        if (type is null or "dxa") return new(TableWidthUnit.Absolute, Bounded(value / 15, 0, 100000, element));
        if (type == "pct") return new(TableWidthUnit.Percentage, Bounded(value / 50, 0, 100, element));
        Loss("preferred-width-unit", "Unsupported preferred width unit: " + type, "Automatic width used.", element);
        return new();
    }

    private static XElement WritePreferredWidth(string name, TablePreferredWidth width, Guid id)
    {
        var value = width.Unit switch { TableWidthUnit.Absolute => width.Value * 15, TableWidthUnit.Percentage => width.Value * 50, _ => 0 };
        if (Math.Abs(value - Math.Round(value)) > .000001)
            Loss("dimension-precision", "Preferred table or cell width precision", "Rounded width to the DOCX unit.", id: id);
        return new(W + name, new XAttribute(W + "w", (int)Math.Round(value)), new XAttribute(W + "type", width.Unit switch
        { TableWidthUnit.Absolute => "dxa", TableWidthUnit.Percentage => "pct", _ => "auto" }));
    }

    private static Table ReadTableProperties(Table table, XElement? properties, XElement[] rows)
    {
        var width = ReadPreferredWidth(properties?.Element(W + "tblW"));
        var layout = (string?)properties?.Element(W + "tblLayout")?.Attribute(W + "type");
        if (layout is not (null or "fixed" or "autofit")) Loss("table-layout", "Unsupported table layout: " + layout, "Legacy relative layout used.", properties);
        var headers = rows.TakeWhile(row => On(row.Element(W + "trPr")?.Element(W + "tblHeader"))).Count();
        foreach (var row in rows.Skip(headers).Where(row => On(row.Element(W + "trPr")?.Element(W + "tblHeader"))))
            Loss("noncontiguous-header-rows", "Repeating header after a body row", "Only the contiguous leading header rows repeat.", row);
        var indent = properties?.Element(W + "tblInd");
        if (indent is not null && (string?)indent.Attribute(W + "type") is not (null or "dxa"))
            Loss("table-indent-unit", "Non-absolute table indentation", "Indentation omitted.", indent);
        if (properties?.Element(W + "tblpPr") is { } position)
            Loss("positioned-table", "Word positioned table anchors and wrapping", "Table imported in normal flow; DOCX anchor geometry is not mapped.", position);
        if (properties?.Element(W + "tblLook") is { } look && look.Attributes().Any(a => a.Name.LocalName is "firstRow" or "lastRow" or "firstColumn" or "lastColumn" && a.Value is "0" or "false" || a.Name.LocalName is "noHBand" or "noVBand" && a.Value is "1" or "true"))
            Loss("table-style-region-flags", "Disabled conditional table style regions", "All modeled named-style regions are enabled.", look);
        return table with
        {
            PreferredWidth = width, AutoFit = layout switch { "fixed" => TableAutoFit.Fixed, "autofit" => TableAutoFit.Content, _ => TableAutoFit.Legacy },
            Alignment = Value(properties?.Element(W + "jc")) switch { "center" => TableAlignment.Center, "right" or "end" => TableAlignment.Right, _ => TableAlignment.Left },
            Indent = indent is not null && (string?)indent.Attribute(W + "type") is null or "dxa" ? Bounded(Dimension(indent, W + "w", 0) / 15, 0, 100000, indent) : 0,
            RightToLeft = On(properties?.Element(W + "bidiVisual")), RepeatHeaderRows = headers
        };
    }

    private static void WriteTableProperties(XElement properties, Table table)
    {
        if (table.AutoFit != TableAutoFit.Legacy) properties.Add(new XElement(W + "tblLayout", new XAttribute(W + "type", table.AutoFit == TableAutoFit.Fixed ? "fixed" : "autofit")));
        if (table.AutoFit == TableAutoFit.Window)
            Loss("table-autofit-window", "AutoFit window mode", "Preferred width is retained; DOCX uses its automatic content-fitting layout.", id: table.Id);
        properties.Add(Val("jc", table.Alignment.ToString().ToLowerInvariant()));
        if (table.Indent != 0) properties.Add(new XElement(W + "tblInd", new XAttribute(W + "w", Twips(table.Indent)), new XAttribute(W + "type", "dxa")));
        if (table.RightToLeft) properties.Add(new XElement(W + "bidiVisual"));
        if (table.StyleId is not null) properties.Add(new XElement(W + "tblLook", new XAttribute(W + "firstRow", 1), new XAttribute(W + "lastRow", 1),
            new XAttribute(W + "firstColumn", 1), new XAttribute(W + "lastColumn", 1), new XAttribute(W + "noHBand", 0), new XAttribute(W + "noVBand", 0)));
        if (table.StyleOverrides is { TextDirection.IsSet: true } formatting && formatting.TextDirection.Value != TableCellTextDirection.Inherit)
            Loss("cell-text-direction", "Table-wide cell direction override", "Paragraph direction retained; cell-wide direction override omitted.", id: table.Id);
        if (table.Position is not null) Loss("positioned-table", "Positioned table geometry", "Table exported in normal flow; column-relative wrap geometry omitted.", id: table.Id);
    }

    private static TableCellVerticalAlignment ReadCellVerticalAlignment(XElement? properties)
    {
        var value = Value(properties?.Element(W + "vAlign"));
        if (value is not (null or "top" or "center" or "bottom")) Loss("cell-vertical-alignment", "Unsupported cell vertical alignment: " + value, "Top alignment used.", properties);
        return value switch { "center" => TableCellVerticalAlignment.Center, "bottom" => TableCellVerticalAlignment.Bottom, _ => TableCellVerticalAlignment.Top };
    }

    private static TableCellTextDirection ReadCellTextDirection(XElement? properties)
    {
        if (properties?.Element(W + "textDirection") is { } direction && Value(direction) is not (null or "lrTb"))
            Loss("cell-text-direction", "Rotated or vertical cell text direction", "Horizontal paragraph direction retained.", direction);
        return TableCellTextDirection.Inherit;
    }

    private static void ReportCellFormattingLosses(TableCell cell)
    {
        if (cell.TextDirection != TableCellTextDirection.Inherit || cell.StyleOverrides?.TextDirection.IsSet == true)
            Loss("cell-text-direction", "Cell direction override", "Paragraph direction retained; cell-wide direction override omitted.", id: cell.Id);
        if (cell.StyleOverrides is not null)
            Loss("cell-style-overrides", "Sparse cell formatting overrides", "Cell formatting is exported as effective direct properties.", id: cell.Id);
    }

    private static XElement WriteBorder(string name, BorderSide? side, Guid id) => new(W + name,
        new XAttribute(W + "val", side is { Width: > 0, Kind: not BorderKind.None } ? side.Kind switch
        { BorderKind.Dashed => "dashed", BorderKind.Dotted => "dotted", BorderKind.Double => "double", _ => "single" } : "nil"),
        side is { Width: > 0, Kind: not BorderKind.None } ? new XAttribute(W + "sz", BorderUnits(side.Width, id)) : null,
        side?.Color is { } color ? new XAttribute(W + "color", Color(color, id)) : null);

    private static string? TableRegionName(TableStyleRegion region) => region switch
    {
        TableStyleRegion.OddColumnBand => "band1Vert", TableStyleRegion.EvenColumnBand => "band2Vert",
        TableStyleRegion.OddRowBand => "band1Horz", TableStyleRegion.EvenRowBand => "band2Horz",
        TableStyleRegion.FirstColumn => "firstCol", TableStyleRegion.LastColumn => "lastCol",
        TableStyleRegion.FirstRow => "firstRow", TableStyleRegion.LastRow => "lastRow", _ => null
    };
    private static ImmutableDictionary<TableStyleRegion, TableStyleOverrides> ReadTableConditions(XElement element)
    {
        var result = ImmutableDictionary.CreateBuilder<TableStyleRegion, TableStyleOverrides>();
        foreach (var condition in element.Elements(W + "tblStylePr"))
        {
            var name = (string?)condition.Attribute(W + "type");
            var region = Enum.GetValues<TableStyleRegion>().FirstOrDefault(region => TableRegionName(region) == name);
            if (name is null || TableRegionName(region) != name) { Loss("table-conditional-style", "Unsupported conditional table region: " + name, "Supported table style regions retained.", condition); continue; }
            result[region] = ReadTableFormatting(condition.Element(W + "tblPr"), condition.Element(W + "tcPr"));
            if (condition.Element(W + "rPr") is not null || condition.Element(W + "pPr") is not null)
                Loss("table-text-style", "Conditional table character or paragraph formatting", "Cell decoration retained.", condition);
        }
        return result.ToImmutable();
    }
    private static IEnumerable<XElement> WriteTableConditions(TableStyleDefinition style)
    {
        foreach (var condition in style.Conditions.OrderBy(c => c.Key))
        {
            if (TableRegionName(condition.Key) is not { } name)
            { Loss("table-header-style", "Repeating-header conditional style", "Other conditional regions retained; header-only region omitted."); continue; }
            yield return new XElement(W + "tblStylePr", new XAttribute(W + "type", name), WriteTableStyle(condition.Value), WriteTableCellStyle(condition.Value));
        }
    }
    private static XElement WriteTableCellStyle(TableStyleOverrides formatting)
    {
        if (formatting.TextDirection.IsSet && formatting.TextDirection.Value != TableCellTextDirection.Inherit)
            Loss("cell-text-direction", "Named table style cell direction", "Paragraph direction retained; cell-wide override omitted.");
        return new(W + "tcPr", formatting.VerticalAlignment.IsSet ? Val("vAlign", formatting.VerticalAlignment.Value.ToString().ToLowerInvariant()) : null,
            formatting.Borders.IsSet ? WriteBorders(formatting.Borders.Value ?? new BlockBorders(), Guid.Empty) : null);
    }
}
