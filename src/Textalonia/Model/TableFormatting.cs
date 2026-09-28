namespace Textalonia.Model;

public enum TableWidthUnit { Auto, Absolute, Percentage }
/// <summary>Preferred width in device-independent pixels or percent (0-100).</summary>
public sealed record TablePreferredWidth(TableWidthUnit Unit = TableWidthUnit.Auto, double Value = 0);
public enum TableAutoFit { Legacy, Fixed, Content, Window }
public enum TableAlignment { Left, Center, Right }
public enum TableCellVerticalAlignment { Top, Center, Bottom }
public enum TableCellTextDirection { Inherit, LeftToRight, RightToLeft }
/// <summary>Conditional regions in increasing precedence; direct formatting wins.</summary>
public enum TableStyleRegion { OddColumnBand, EvenColumnBand, OddRowBand, EvenRowBand, FirstColumn, LastColumn, FirstRow, LastRow, HeaderRow }
/// <summary>Position relative to the anchor column, with square text-wrap distance. Simple view displays the table inline.</summary>
public sealed record TablePosition(double X = 0, double Y = 0, double Distance = 8);

internal static class TableFormatting
{
    internal static void ValidateWidth(TablePreferredWidth width)
    {
        if (width is null || !Enum.IsDefined(width.Unit) || !double.IsFinite(width.Value) ||
            width.Value < 0 || width.Value > (width.Unit == TableWidthUnit.Percentage ? 100 : 100000) ||
            width.Unit != TableWidthUnit.Auto && width.Value == 0)
            throw new FormatException("Invalid preferred table or cell width.");
    }
}
