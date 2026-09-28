using System.Runtime.CompilerServices;
using Avalonia.Media.TextFormatting.Unicode;
using Avalonia.Media;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>Shared physical table widths. Legacy tables keep proportional columns without content measurement.</summary>
internal static class TableColumnLayout
{
    internal sealed record Geometry(double[] Widths, double Width, double Offset)
    {
        internal double ColumnWidth(int column, int span) => Widths.Skip(column).Take(span).Sum();
        internal double ColumnOffset(Table table, int column, int span)
        {
            var prefix = Widths.Take(column).Sum();
            return Offset + (table.RightToLeft ? Width - prefix - ColumnWidth(column, span) : prefix);
        }
    }
    private sealed record Cached(double Available, DocumentFormattingKey? Formatting, FontFamily Font, Geometry Geometry);
    private static readonly ConditionalWeakTable<Table, List<Cached>> Cache = new();
    internal static Geometry Resolve(Table table, double available, DocumentStyleResolver? resolver = null, FontFamily? font = null, DocumentFontService? fonts = null)
    {
        font ??= FontFamily.Default;
        var formatting = resolver?.FormattingKey;
        var entries = Cache.GetOrCreateValue(table);
        lock (entries)
        {
            var existing = entries.FirstOrDefault(x => x.Available == available && Equals(x.Font, font) && x.Formatting == formatting);
            if (existing is not null) return existing.Geometry;
            var geometry = Measure(table, available, resolver, font, fonts);
            if (entries.Count == 4) entries.RemoveAt(0);
            entries.Add(new(available, formatting, font, geometry));
            return geometry;
        }
    }
    private static Geometry Measure(Table table, double available, DocumentStyleResolver? resolver, FontFamily font, DocumentFontService? fonts)
    {
        var count = table.ColumnCount;
        var room = Math.Max(16, available - table.Indent);
        var target = table.AutoFit == TableAutoFit.Window ? room : Preferred(table.PreferredWidth, room) ?? room;
        var weights = table.ColumnWidths.IsDefaultOrEmpty ? Enumerable.Repeat(1d, count).ToArray() : table.ColumnWidths.ToArray();
        var total = weights.Sum();
        var widths = weights.Select(x => target * x / total).ToArray();
        var minimum = new double[count]; var maximum = new double[count];
        var preferred = new double[count];
        var measured = table.AutoFit == TableAutoFit.Content;
        for (var r = 0; r < table.Rows.Length; r++)
            for (var c = 0; c < count; c++)
            {
                if (table.IsCovered(r, c)) continue;
                var cell = measured ? resolver?.ResolveTableCell(table, r, c) ?? table.Rows[r][c] : table.Rows[r][c];
                if (Preferred(cell.PreferredWidth, target) is { } wanted) Distribute(preferred, c, cell.ColumnSpan, wanted);
                if (!measured) continue;
                var inset = LayoutHeightIndex.CellPadding(cell);
                var metrics = Content(cell.Blocks, resolver, font, fonts);
                Distribute(minimum, c, cell.ColumnSpan, metrics.Minimum + inset.Left + inset.Right);
                Distribute(maximum, c, cell.ColumnSpan, metrics.Maximum + inset.Left + inset.Right);
            }
        if (measured)
        {
            for (var c = 0; c < count; c++)
            {
                minimum[c] = Math.Max(16, minimum[c]);
                maximum[c] = Math.Max(minimum[c], Math.Max(preferred[c], maximum[c]));
            }
            if (table.PreferredWidth.Unit == TableWidthUnit.Auto) target = Math.Min(target, maximum.Sum());
            target = Math.Max(target, minimum.Sum());
            var flexibility = maximum.Zip(minimum).Select(x => Math.Max(0, x.First - x.Second)).Sum();
            var extra = Math.Max(0, target - minimum.Sum());
            widths = minimum.Select((value, c) => value + (flexibility > 0
                ? extra * Math.Max(0, maximum[c] - value) / flexibility : extra * weights[c] / total)).ToArray();
        }
        else if (preferred.Any(value => value > 0))
        {
            var fixedTotal = preferred.Sum();
            var unfixedWeight = weights.Where((_, c) => preferred[c] == 0).Sum();
            if (fixedTotal >= target || unfixedWeight == 0)
                widths = preferred.Select((value, c) => (value > 0 ? value : weights[c]) * target /
                    preferred.Select((value2, c2) => value2 > 0 ? value2 : weights[c2]).Sum()).ToArray();
            else widths = preferred.Select((value, c) => value > 0 ? value : (target - fixedTotal) * weights[c] / unfixedWeight).ToArray();
        }
        var width = widths.Sum();
        var offset = table.Indent + (table.Alignment switch
        {
            TableAlignment.Center => Math.Max(0, room - width) / 2,
            TableAlignment.Right => Math.Max(0, room - width),
            _ => 0
        });
        return new(widths, width, offset);
    }
    private static double? Preferred(TablePreferredWidth value, double available) => value.Unit switch
    {
        TableWidthUnit.Absolute => value.Value,
        TableWidthUnit.Percentage => available * value.Value / 100,
        _ => null
    };
    private static void Distribute(double[] values, int column, int span, double required)
    {
        var current = values.Skip(column).Take(span).Sum();
        var extra = Math.Max(0, required - current) / span;
        for (var c = column; c < column + span; c++) values[c] += extra;
    }
    private static (double Minimum, double Maximum) Content(IEnumerable<Block> blocks, DocumentStyleResolver? resolver, FontFamily font, DocumentFontService? fonts)
    {
        var minimum = 0d; var maximum = 0d;
        foreach (var block in blocks)
        {
            if (block is Paragraph source)
            {
                var paragraph = resolver?.ResolveParagraph(source) ?? source;
                using var full = DocumentLayout.CreateTextLayout(paragraph, double.PositiveInfinity, font, Brushes.Black, fonts: fonts);
                var max = full.WidthIncludingTrailingWhitespace;
                var min = 0d;
                // Use the text backend's Unicode break opportunities (including CJK,
                // nonbreaking spaces and atomic objects), preserving rich run metrics.
                var breaks = new LineBreakEnumerator(paragraph.Text.AsSpan());
                var start = 0;
                while (breaks.MoveNext(out var next))
                {
                    if (next.PositionMeasure > start)
                    {
                        var part = paragraph with { Runs = paragraph.Slice(start, next.PositionMeasure - start), Style = paragraph.Style with { FirstLineIndent = 0 } };
                        using var shape = DocumentLayout.CreateTextLayout(part, double.PositiveInfinity, font, Brushes.Black, fonts: fonts);
                        min = Math.Max(min, shape.WidthIncludingTrailingWhitespace);
                    }
                    start = next.PositionWrap;
                }
                var indent = ListMarkerDrawing.Indent(paragraph) + paragraph.Style.RightIndent + Math.Max(0, paragraph.Style.FirstLineIndent);
                minimum = Math.Max(minimum, min + indent); maximum = Math.Max(maximum, max + indent);
            }
            else if (block is Section section)
            {
                var measured = Content(section.Blocks, resolver, font, fonts); var padding = LayoutHeightIndex.SectionPadding(section);
                minimum = Math.Max(minimum, measured.Minimum + padding.Left + padding.Right);
                maximum = Math.Max(maximum, measured.Maximum + padding.Left + padding.Right);
            }
            else if (block is Table nested)
            {
                var contentTable = nested with { AutoFit = TableAutoFit.Content };
                var min = Resolve(contentTable, 16, resolver, font, fonts);
                var max = Resolve(contentTable, 100000, resolver, font, fonts);
                minimum = Math.Max(minimum, min.Width); maximum = Math.Max(maximum, max.Width);
            }
        }
        return (minimum, maximum);
    }
}
