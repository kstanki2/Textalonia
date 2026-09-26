using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Textalonia.Model;

namespace Textalonia.Controls;

internal sealed record ParagraphVisual(ParagraphPosition Position, TextLayout Layout, Point Origin, double AvailableWidth, string? Marker)
{
    public Rect Bounds => new(Origin, new Size(AvailableWidth, Math.Max(Layout.Height, Position.Paragraph.DefaultStyle.FontSize * 1.25)));
}
internal sealed record BlockDecoration(Rect Bounds, IBrush? Fill, IBrush? Border, bool LeftBorderOnly);

/// <summary>Viewport shaping over incremental prefix heights, with a bounded reusable layout cache.</summary>
internal sealed class DocumentLayout : IDisposable
{
    private sealed record Cached(Paragraph Paragraph, double Width, TextLayout Layout);
    private readonly Dictionary<Guid, Cached> _cache = [];
    public List<ParagraphVisual> Paragraphs { get; } = [];
    public List<BlockDecoration> Decorations { get; } = [];
    public double Height { get; private set; }
    public double Width { get; private set; }
    private FontFamily _font = FontFamily.Default;
    private IBrush _foreground = Brushes.Black;
    private IBrush _border = Brushes.Gray;

    private LayoutHeightIndex _heights = new();
    private DocumentIndex? _index;
    private Thickness _padding;
    private Rect _viewport;
    private double _previousViewportY = double.NaN;
    private long _clock;
    public int ShapedParagraphs { get; private set; }
    public int DisposedLayouts { get; private set; }
    public int CachedParagraphs => _cache.Count;
    public int GeometryNodes => _heights.CreatedNodes;
    public double AnchorAdjustment { get; private set; }
    private const int CacheLimit = 256;
    private const long CacheBytes = 16 * 1024 * 1024;
    private readonly Dictionary<Guid, long> _uses = [];
    private bool _collectDecorations;

    public void Build(FlowDocument document, double width, FontFamily font, IBrush foreground, IBrush border,
        Thickness padding, Rect? viewport = null)
    {
        width = Math.Max(48, width);
        var reset = !Equals(_font, font) || !Equals(_foreground, foreground) || Math.Abs(Width - width) > .1 || _padding != padding;
        var view = viewport ?? new Rect(0, 0, width, 600);
        var anchor = Math.Abs(view.Y - _previousViewportY) < .1
            ? Paragraphs.FirstOrDefault(p => p.Bounds.Bottom >= view.Top && p.Bounds.Top <= view.Bottom) : null;
        if (reset) Clear();
        _font = font; _foreground = foreground; _border = border; _padding = padding;
        Width = width; _viewport = view; _previousViewportY = view.Y;
        _index = new(document);
        _heights.Synchronize(_index.Tree, Math.Max(24, width - padding.Left - padding.Right));
        AnchorAdjustment = 0;
        MeasureViewport();
        if (anchor is not null && _index.Tree.Paths?.Find(anchor.Position.Paragraph.Id) is not null)
        {
            var location = Locate(anchor.Position.Paragraph.Id);
            AnchorAdjustment = location.Origin.Y - anchor.Origin.Y;
            if (Math.Abs(AnchorAdjustment) > .1)
            {
                _viewport = _viewport.Translate(new Vector(0, AnchorAdjustment));
                _previousViewportY = _viewport.Y;
                MeasureViewport();
            }
        }
        Evict();
    }

    private void MeasureViewport()
    {
        // Height estimates can move newly measured paragraphs into this region.
        // Repeat until stable, with a bounded number of convergence passes.
        for (var pass = 0; pass < 4; pass++)
        {
            var before = _heights.Root.Height;
            Paragraphs.Clear(); Decorations.Clear();
            var overscan = Math.Min(400, Math.Max(80, _viewport.Height));
            _collectDecorations = true;
            Collect(_heights.Root, _padding.Left, _padding.Top, Math.Max(0, _viewport.Top - overscan), _viewport.Bottom + overscan);
            _collectDecorations = false;
            Height = _padding.Top + _heights.Root.Height + _padding.Bottom;
            if (Math.Abs(before - _heights.Root.Height) < .1) break;
        }
        // Recompute origins after the final height corrections without more shaping.
        Reposition();
    }
    private TextLayout Shape(LayoutHeightIndex.Node node)
    {
        var paragraph = (Paragraph)node.Source.Source!;
        var width = Math.Max(16, node.Available - node.Indent);
        if (!_cache.TryGetValue(paragraph.Id, out var cached) || !ReferenceEquals(cached.Paragraph, paragraph) || Math.Abs(cached.Width - width) > .1)
        {
            if (cached is not null) { cached.Layout.Dispose(); DisposedLayouts++; }
            cached = new(paragraph, width, CreateTextLayout(paragraph, width, _font, _foreground));
            _cache[paragraph.Id] = cached; ShapedParagraphs++;
        }
        _uses[paragraph.Id] = ++_clock;
        var height = Math.Max(cached.Layout.Height, paragraph.DefaultStyle.FontSize * 1.25);
        if (Math.Abs(node.ContentHeight - height) > .01) { node.ContentHeight = height; node.Update(); }
        return cached.Layout;
    }
    private void Collect(LayoutHeightIndex.Node node, double x, double y, double top, double bottom)
    {
        if (y > bottom || y + node.Height < top) return;
        switch (node.Source.Source)
        {
            case Paragraph paragraph:
                var layout = Shape(node);
                var marker = paragraph.Style.List switch { ListKind.Bullet => "\u2022", ListKind.Numbered => Number(paragraph.Id) + ".", _ => null };
                var visual = new ParagraphVisual(_index!.ById(paragraph.Id), layout, new(x + node.Indent, y + paragraph.Style.SpaceBefore), Math.Max(16, node.Available - node.Indent), marker);
                var existing = Paragraphs.FindIndex(p => p.Position.Paragraph.Id == paragraph.Id);
                if (existing >= 0) Paragraphs[existing] = visual; else Paragraphs.Add(visual);
                break;
            case Section section:
                var slot = Decorations.Count;
                if (_collectDecorations) Decorations.Add(new(default, null, null, false));
                CollectBranch(node.Children, x + section.Padding, y + section.Padding, top, bottom);
                if (_collectDecorations) Decorations[slot] = new(new Rect(x, y, node.Available, node.Height - 10), Brush(section.Background), Brush(section.BorderColor), true);
                break;
            case Table table:
                var cellWidth = node.Available / table.ColumnCount;
                foreach (var cell in node.IntersectingCells(top - y, bottom - y))
                {
                    var model = (TableCell)cell.Source.Source!;
                    var cellY = y + node.RowOffsets[cell.Source.Row];
                    var cellHeight = node.RowOffsets[cell.Source.Row + model.RowSpan] - node.RowOffsets[cell.Source.Row];
                    if (cellY > bottom || cellY + cellHeight < top) continue;
                    Collect(cell, x + cell.Source.Column * cellWidth, cellY, double.MinValue, double.MaxValue);
                    cellHeight = node.RowOffsets[cell.Source.Row + model.RowSpan] - node.RowOffsets[cell.Source.Row];
                    if (_collectDecorations) Decorations.Add(new(new Rect(x + cell.Source.Column * cellWidth, cellY, cellWidth * model.ColumnSpan, cellHeight), Brush(model.Background), _border, false));
                }
                break;
            case TableCell:
                CollectBranch(node.Children, x + 8, y + 8, top, bottom);
                break;
            default:
                CollectBranch(node.Children, x, y, top, bottom);
                break;
        }
    }
    private void CollectBranch(LayoutHeightIndex.Branch? branch, double x, double y, double top, double bottom)
    {
        if (branch is null || y > bottom || y + branch.Height < top) return;
        CollectBranch(branch.Left, x, y, top, bottom);
        y += branch.Left?.Height ?? 0;
        Collect(branch.Value, x, y, top, bottom);
        y += branch.Value.Height;
        CollectBranch(branch.Right, x, y, top, bottom);
    }
    private (LayoutHeightIndex.Node Node, Point Origin) Locate(Guid id)
    {
        var path = _index!.Tree.Paths!.Find(id)!.Value;
        var node = _heights.Root; var x = _padding.Left; var y = _padding.Top;
        foreach (var key in path.Keys())
        {
            if (node.Source.Source is Table table)
            {
                var cell = node.Children!.Find(key);
                x += cell.Source.Column * node.Available / table.ColumnCount;
                y += node.RowOffsets[cell.Source.Row];
                node = cell; continue;
            }
            if (node.Source.Source is Section section) { x += section.Padding; y += section.Padding; }
            else if (node.Source.Source is TableCell) { x += 8; y += 8; }
            y += node.Children!.Prefix(key); node = node.Children.Find(key);
        }
        if (node.Source.Source is Paragraph paragraph) { x += node.Indent; y += paragraph.Style.SpaceBefore; }
        return (node, new(x, y));
    }
    private void Reposition()
    {
        for (var i = 0; i < Paragraphs.Count; i++)
            Paragraphs[i] = Paragraphs[i] with { Origin = Locate(Paragraphs[i].Position.Paragraph.Id).Origin };
        Height = _padding.Top + _heights.Root.Height + _padding.Bottom;
    }
    private int Number(Guid id)
    {
        var keys = _index!.Tree.Paths!.Find(id)!.Value.Keys().ToArray();
        var node = _heights.Root;
        for (var i = 0; i < keys.Length - 1; i++) node = node.Children!.Find(keys[i]);
        var paragraph = (Paragraph)node.Children!.Find(keys[^1]).Source.Source!;
        return node.Children.NumberBefore(keys[^1], paragraph.Style.ListLevel) + 1;
    }
    private void Evict()
    {
        var pinned = Paragraphs.Select(p => p.Position.Paragraph.Id).ToHashSet();
        var bytes = _cache.Values.Sum(c => 256L + c.Paragraph.Length * 32L);
        foreach (var id in _cache.Keys.OrderBy(id => _uses.GetValueOrDefault(id)).ToArray())
        {
            if (_cache.Count <= CacheLimit && bytes <= CacheBytes) break;
            if (pinned.Contains(id)) continue;
            var cached = _cache[id]; bytes -= 256L + cached.Paragraph.Length * 32L;
            cached.Layout.Dispose(); DisposedLayouts++; _cache.Remove(id); _uses.Remove(id);
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
            // Preset decorations are mutable AvaloniaObjects shared globally. Own
            // these objects on the surface's dispatcher, just like its layouts.
            if (style.Underline || style.Hyperlink is not null) decorations.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
            if (style.Strikethrough) decorations.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
            var properties = new GenericTextRunProperties(
                Typeface(style, font), style.FontSize * (style.Baseline == Baseline.Normal ? 1 : .75),
                decorations, Brush(style.Foreground) ?? (style.Hyperlink is not null ? Brush("#3478CE") : foreground),
                Brush(style.Background), style.Baseline switch
                { Baseline.Subscript => BaselineAlignment.Subscript, Baseline.Superscript => BaselineAlignment.Superscript, _ => BaselineAlignment.Baseline });
            overrides.Add(new(offset, run.Storage.Length, properties));
            offset += run.Storage.Length;
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

    public ParagraphVisual? At(int position)
    {
        if (_index is null) return null;
        var entry = _index.At(position);
        var existing = Paragraphs.FirstOrDefault(p => p.Position.Paragraph.Id == entry.Paragraph.Id);
        if (existing is not null) return existing;
        PruneTargets();
        var location = Locate(entry.Paragraph.Id);
        // Measure the target and its intersecting row dependencies, even offscreen.
        Collect(_heights.Root, _padding.Left, _padding.Top, location.Origin.Y, location.Origin.Y + 1);
        Reposition();
        existing = Paragraphs.FirstOrDefault(p => p.Position.Paragraph.Id == entry.Paragraph.Id);
        if (existing is null)
        {
            var layout = Shape(location.Node); location = Locate(entry.Paragraph.Id);
            existing = new(entry, layout, location.Origin, Math.Max(16, location.Node.Available - location.Node.Indent), null);
            Paragraphs.Add(existing); Reposition();
        }
        Evict(); return existing;
    }
    public Rect Caret(int position)
    {
        var visual = At(position);
        if (visual is null) return new Rect(0, 0, 1.5, 20);
        var rect = visual.Layout.HitTestTextPosition(Math.Clamp(position - visual.Position.Start, 0, visual.Position.Paragraph.Length));
        return new Rect(visual.Origin.X + rect.X, visual.Origin.Y + rect.Y, 1.5, Math.Max(rect.Height, visual.Position.Paragraph.DefaultStyle.FontSize * 1.1));
    }

    public int HitTest(Point point)
    {
        if (_index is null) return 0;
        PruneTargets();
        Collect(_heights.Root, _padding.Left, _padding.Top, point.Y - 20, point.Y + 20);
        Reposition();
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

    private void PruneTargets()
    {
        var region = _viewport.Inflate(400);
        Paragraphs.RemoveAll(p => !p.Bounds.Intersects(region));
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
        DisposedLayouts += _cache.Count;
        _cache.Clear(); _uses.Clear(); Paragraphs.Clear(); Decorations.Clear();
        _heights = new(); _index = null;
        _previousViewportY = double.NaN;
    }
    public void Dispose() => Clear();
}
