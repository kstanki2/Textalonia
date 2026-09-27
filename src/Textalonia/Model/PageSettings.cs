using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Textalonia.Model;

public enum PageOrientation { Portrait, Landscape }
public enum PageNumberFormat { Decimal, UpperRoman, LowerRoman, UpperLetter, LowerLetter }
public enum LineNumberRestart { Continuous, EachPage, EachSection }

/// <summary>A column's proportional share of the usable page width, excluding column gaps.</summary>
public sealed record PageColumn(double Width = 1);
public sealed record LineNumberingSettings(int Start = 1, int CountBy = 1,
    double Distance = 12, LineNumberRestart Restart = LineNumberRestart.EachPage);

/// <summary>Physical page metrics in device-independent pixels (96 per inch), independent of view zoom and monitor DPI.</summary>
public sealed record PageSettings
{
    public double Width { get; init; } = 816;
    public double Height { get; init; } = 1056;
    public PageOrientation Orientation { get; init; }
    public EdgeInsets Margins { get; init; } = new(96, 96, 96, 96);
    public double Gutter { get; init; }
    public bool MirrorMargins { get; init; }
    /// <summary>Empty means one column. Nonempty entries are relative widths, with ColumnSpacing between them.</summary>
    public ImmutableArray<PageColumn> Columns { get; init; } = [];
    public double ColumnSpacing { get; init; } = 24;
    public bool BalanceColumns { get; init; } = true;
    public string? Background { get; init; } = "#FFFFFF";
    public BlockBorders? Borders { get; init; }
    public LineNumberingSettings? LineNumbering { get; init; }
    public EastAsianGrid? Grid { get; init; }
    [JsonIgnore] public double EffectiveWidth => Orientation == PageOrientation.Landscape ? Height : Width;
    [JsonIgnore] public double EffectiveHeight => Orientation == PageOrientation.Landscape ? Width : Height;

    public void Validate()
    {
        static bool Metric(double value, double maximum = 100000) => double.IsFinite(value) && value >= 0 && value <= maximum;
        if (!Metric(Width) || !Metric(Height) || Width < 1 || Height < 1 || !Enum.IsDefined(Orientation) || Margins is null ||
            !Metric(Margins.Left) || !Metric(Margins.Right) || !Metric(Margins.Top) || !Metric(Margins.Bottom) ||
            !Metric(Gutter) || !Metric(ColumnSpacing) || Columns.IsDefault || Columns.Length > 32 ||
            Columns.Any(column => column is null || !Metric(column.Width) || column.Width == 0))
            throw new FormatException("Invalid page dimensions, margins or columns.");
        if (EffectiveWidth - Margins.Left - Margins.Right - Gutter - Math.Max(0, Columns.Length - 1) * ColumnSpacing < 1 ||
            EffectiveHeight - Margins.Top - Margins.Bottom < 1)
            throw new FormatException("Page margins, gutter and column gaps must leave a positive content area.");
        FlowDocument.ValidateColor(Background);
        if (Borders is { } borders)
            foreach (var side in new[] { borders.Left, borders.Top, borders.Right, borders.Bottom })
                if (side is not null)
                {
                    if (!Metric(side.Width, 1000)) throw new FormatException("Invalid page border width.");
                    FlowDocument.ValidateColor(side.Color);
                }
        if (LineNumbering is { } numbering && (numbering.Start < 1 || numbering.CountBy < 1 ||
            !Metric(numbering.Distance, 1000) || !Enum.IsDefined(numbering.Restart)))
            throw new FormatException("Invalid line numbering.");
        if (Grid is { } grid && (!Metric(grid.CharacterSpacing, 1000) || !Metric(grid.LineSpacing, 1000)))
            throw new FormatException("Invalid document grid.");
    }
}

/// <summary>Explicit conversions at interchange boundaries. The document model stores DIP throughout.</summary>
public static class DocumentUnits
{
    public static double FromInches(double value) => value * 96;
    public static double ToInches(double value) => value / 96;
    public static double FromPoints(double value) => value * 96 / 72;
    public static double ToPoints(double value) => value * 72 / 96;
    public static double FromMillimeters(double value) => value * 96 / 25.4;
    public static double ToMillimeters(double value) => value * 25.4 / 96;
    public static double FromTwips(double value) => value / 15;
    public static double ToTwips(double value) => value * 15;
}
