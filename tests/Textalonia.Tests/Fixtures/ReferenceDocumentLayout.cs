// Frozen P1 full-measure geometry oracle. Keep independent of the incremental index.
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Textalonia.Model;

namespace Textalonia.Tests;

internal sealed record ReferenceParagraphVisual(ParagraphPosition Position, TextLayout Layout, Point Origin, double AvailableWidth, string? Marker)
{
    public Rect Bounds => new(Origin, new Size(AvailableWidth, Math.Max(Layout.Height, Position.Paragraph.DefaultStyle.FontSize * 1.25)));
}
internal sealed record ReferenceBlockDecoration(Rect Bounds, IBrush? Fill, IBrush? Border, bool LeftBorderOnly);

/// <summary>Retains shaped layouts for unchanged paragraphs. Document geometry is rebuilt after edits.</summary>
internal sealed class ReferenceDocumentLayout : IDisposable
{
    private sealed record Cached(Paragraph Paragraph, double Width, TextLayout Layout);
    private readonly Dictionary<Guid, Cached> _cache = [];
    public List<ReferenceParagraphVisual> Paragraphs { get; } = [];
    public List<ReferenceBlockDecoration> Decorations { get; } = [];
    public double Height { get; private set; }
    public double Width { get; private set; }
    private FontFamily _font = FontFamily.Default;
    private IBrush _foreground = Brushes.Black;
    private IBrush _border = Brushes.Gray;

    public void Build(FlowDocument document, double width, FontFamily font, IBrush foreground, IBrush border, Thickness padding)
    {
        if (!Equals(_font, font) || !Equals(_foreground, foreground)) Clear();
        _font = font; _foreground = foreground; _border = border;
        Paragraphs.Clear(); Decorations.Clear();
        Width = Math.Max(48, width);
        var positions = new DocumentIndex(document).Paragraphs.ToDictionary(p => p.Paragraph.Id);
        var used = new HashSet<Guid>();
        Height = LayoutBlocks(document.Blocks, padding.Left, padding.Top, Math.Max(24, Width - padding.Left - padding.Right), Paragraphs, Decorations) + padding.Bottom;
        foreach (var key in _cache.Keys.Where(key => !used.Contains(key)).ToArray())
        { _cache[key].Layout.Dispose(); _cache.Remove(key); }

        double LayoutBlocks(IEnumerable<Block> blocks, double x, double y, double available,
            List<ReferenceParagraphVisual> paragraphs, List<ReferenceBlockDecoration> decorations)
        {
            var numbers = new int[9];
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph p:
                        var level = p.Style.ListLevel;
                        string? marker = null;
                        if (p.Style.List == ListKind.Numbered)
                        {
                            numbers[level]++;
                            for (var i = level + 1; i < numbers.Length; i++) numbers[i] = 0;
                            marker = numbers[level] + ".";
                        }
                        else if (p.Style.List == ListKind.Bullet) marker = "\u2022";
                        else Array.Clear(numbers);
                        var indent = p.Style.Indent + (p.Style.List == ListKind.None ? 0 : 28 + level * 24);
                        var actualWidth = Math.Max(16, available - indent);
                        used.Add(p.Id);
                        if (!_cache.TryGetValue(p.Id, out var cached) || !ReferenceEquals(cached.Paragraph, p) || Math.Abs(cached.Width - actualWidth) > .1)
                        {
                            cached?.Layout.Dispose();
                            cached = new(p, actualWidth, CreateTextLayout(p, actualWidth, font, foreground));
                            _cache[p.Id] = cached;
                        }
                        y += p.Style.SpaceBefore;
                        var visual = new ReferenceParagraphVisual(positions[p.Id], cached.Layout, new(x + indent, y), actualWidth, marker);
                        paragraphs.Add(visual);
                        y += visual.Bounds.Height + p.Style.SpaceAfter;
                        break;
                    case Section section:
                        var sectionTop = y;
                        var childDecorations = new List<ReferenceBlockDecoration>();
                        y = LayoutBlocks(section.Blocks, x + section.Padding, y + section.Padding,
                            Math.Max(16, available - section.Padding * 2), paragraphs, childDecorations) + section.Padding;
                        decorations.Add(new(new Rect(x, sectionTop, available, y - sectionTop), Brush(section.Background), Brush(section.BorderColor), true));
                        decorations.AddRange(childDecorations);
                        y += 10;
                        break;
                    case Table table:
                        var tableTop = y;
                        var cellWidth = available / table.ColumnCount;
                        var heights = Enumerable.Repeat(36d, table.Rows.Length).ToArray();
                        var cells = new List<(int Row, int Column, TableCell Cell, double Height, List<ReferenceParagraphVisual> Visuals)>();
                        for (var r = 0; r < table.Rows.Length; r++)
                            for (var c = 0; c < table.ColumnCount; c++)
                            {
                                if (table.IsCovered(r, c)) continue;
                                var cell = table.Rows[r][c];
                                var visuals = new List<ReferenceParagraphVisual>();
                                var h = LayoutBlocks(cell.Paragraphs, 8, 8, Math.Max(16, cellWidth * cell.ColumnSpan - 16), visuals, []) + 4;
                                cells.Add((r, c, cell, h, visuals));
                                if (cell.RowSpan == 1) heights[r] = Math.Max(heights[r], h);
                            }
                        foreach (var cell in cells.Where(c => c.Cell.RowSpan > 1))
                        {
                            var allocated = heights.Skip(cell.Row).Take(cell.Cell.RowSpan).Sum();
                            if (allocated < cell.Height) heights[cell.Row + cell.Cell.RowSpan - 1] += cell.Height - allocated;
                        }
                        var rowOffsets = new double[table.Rows.Length + 1];
                        for (var r = 0; r < heights.Length; r++) rowOffsets[r + 1] = rowOffsets[r] + heights[r];
                        foreach (var cell in cells)
                        {
                            var origin = new Point(x + cell.Column * cellWidth, tableTop + rowOffsets[cell.Row]);
                            var h = rowOffsets[cell.Row + cell.Cell.RowSpan] - rowOffsets[cell.Row];
                            decorations.Add(new(new Rect(origin, new Size(cellWidth * cell.Cell.ColumnSpan, h)), Brush(cell.Cell.Background), _border, false));
                            paragraphs.AddRange(cell.Visuals.Select(v => v with { Origin = v.Origin + new Vector(origin.X, origin.Y) }));
                        }
                        y += rowOffsets[^1] + 12;
                        break;
                }
            }
            return y;
        }
    }

    internal static TextLayout CreateTextLayout(Paragraph paragraph, double width, FontFamily font, IBrush foreground)
    {
        var overrides = new List<ValueSpan<TextRunProperties>>();
        var offset = 0;
        foreach (var run in paragraph.Runs)
        {
            var style = run.Style;
            var decorations = new TextDecorationCollection();
            if (style.Underline || style.Hyperlink is not null) decorations.AddRange(TextDecorations.Underline);
            if (style.Strikethrough) decorations.AddRange(TextDecorations.Strikethrough);
            var properties = new GenericTextRunProperties(
                Typeface(style, font), style.FontSize * (style.Baseline == Baseline.Normal ? 1 : .75),
                decorations, Brush(style.Foreground) ?? (style.Hyperlink is not null ? Brush("#3478CE") : foreground),
                Brush(style.Background), style.Baseline switch
                { Baseline.Subscript => BaselineAlignment.Subscript, Baseline.Superscript => BaselineAlignment.Superscript, _ => BaselineAlignment.Baseline });
            overrides.Add(new(offset, run.Text.Length, properties));
            offset += run.Text.Length;
        }
        return new TextLayout(paragraph.Text, Typeface(paragraph.DefaultStyle, font), paragraph.DefaultStyle.FontSize, foreground,
            textAlignment: paragraph.Style.Alignment switch
            {
                ParagraphAlignment.Center => TextAlignment.Center, ParagraphAlignment.Right => TextAlignment.Right,
                ParagraphAlignment.Justify => TextAlignment.Justify, _ => TextAlignment.Left
            },
            textWrapping: TextWrapping.Wrap, maxWidth: width,
            flowDirection: paragraph.Style.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            textStyleOverrides: overrides);
    }

    private static Typeface Typeface(TextStyle style, FontFamily fallback) =>
        new(style.FontFamily is null ? fallback : new FontFamily(style.FontFamily), style.Italic ? FontStyle.Italic : FontStyle.Normal, style.Bold ? FontWeight.Bold : FontWeight.Normal);
    internal static IBrush? Brush(string? color) => color is null ? null : new SolidColorBrush(Color.Parse(color));

    public ReferenceParagraphVisual? At(int position) => Paragraphs.FirstOrDefault(p => position >= p.Position.Start && position <= p.Position.End) ?? Paragraphs.LastOrDefault();
    public Rect Caret(int position)
    {
        var visual = At(position);
        if (visual is null) return new Rect(0, 0, 1.5, 20);
        var rect = visual.Layout.HitTestTextPosition(Math.Clamp(position - visual.Position.Start, 0, visual.Position.Paragraph.Length));
        return new Rect(visual.Origin.X + rect.X, visual.Origin.Y + rect.Y, 1.5, Math.Max(rect.Height, visual.Position.Paragraph.DefaultStyle.FontSize * 1.1));
    }

    public int HitTest(Point point)
    {
        if (Paragraphs.Count == 0) return 0;
        var visual = Paragraphs.MinBy(p =>
        {
            var r = p.Bounds;
            var dx = Math.Max(Math.Max(r.Left - point.X, 0), point.X - r.Right);
            var dy = Math.Max(Math.Max(r.Top - point.Y, 0), point.Y - r.Bottom);
            return dy * dy * 16 + dx * dx;
        })!;
        var local = point - visual.Origin;
        var hit = visual.Layout.HitTestPoint(new Point(local.X, local.Y));
        return visual.Position.Start + Math.Clamp(hit.TextPosition, 0, visual.Position.Paragraph.Length);
    }

    public IEnumerable<Rect> SelectionRects(int start, int length)
    {
        foreach (var p in Paragraphs)
        {
            var from = Math.Max(start, p.Position.Start);
            var to = Math.Min(start + length, p.Position.End);
            if (to > from)
                foreach (var rect in p.Layout.HitTestTextRange(from - p.Position.Start, to - from))
                    yield return rect.Translate(new Vector(p.Origin.X, p.Origin.Y));
            if (start <= p.Position.End && start + length > p.Position.End)
            {
                var caret = Caret(p.Position.End);
                yield return caret.WithWidth(5);
            }
        }
    }

    public void Clear()
    {
        foreach (var cached in _cache.Values) cached.Layout.Dispose();
        _cache.Clear(); Paragraphs.Clear(); Decorations.Clear();
    }
    public void Dispose() => Clear();
}
