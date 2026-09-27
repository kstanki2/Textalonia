using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Textalonia.Model;

namespace Textalonia.Controls;

internal sealed record ParagraphVisual(ParagraphPosition Position, ParagraphLayout.Page Page, Point Origin, double AvailableWidth, string? Marker, Rect? Clip = null)
{
    public ShapedLayoutCache.Lease Acquire() => Page.Owner.Acquire(Page);
    public int TextStart => Position.Start + Page.Start;
    public int TextEnd => Position.Start + Page.End;
    public Rect Bounds
    {
        get
        {
            var bounds = new Rect(Origin, new Size(AvailableWidth, Page.Height +
                (Page.End == Position.Paragraph.Length ? Math.Max(0, Page.Owner.MinimumHeight - Page.Top - Page.Height) : 0)));
            return Clip?.Intersect(bounds) ?? bounds;
        }
    }
    public void Draw(DrawingContext context, Rect viewport)
    {
        if (Clip is { } bounds)
        {
            using var clip = context.PushClip(bounds);
            DrawLines();
        }
        else DrawLines();
        void DrawLines()
        {
            var y = Origin.Y;
            using var lease = Acquire();
            for (var i = 0; i < Page.LineCount; i++)
            {
                var line = lease.Layout.TextLines[i];
                if (y > viewport.Bottom) break;
                if (y + line.Height >= viewport.Top) line.Draw(context, new Point(Origin.X, y));
                y += line.Height;
            }
        }
    }
}
internal sealed record BlockDecoration(Rect Bounds, IBrush? Fill, IBrush? Border, bool LeftBorderOnly, BlockBorders? Borders = null, Rect? Clip = null)
{
    public void Draw(DrawingContext context)
    {
        using var clip = context.PushClip(Clip ?? Bounds);
        if (Fill is not null) context.FillRectangle(Fill, Bounds);
        if (Borders is null)
        {
            if (LeftBorderOnly && Border is not null) context.FillRectangle(Border, Bounds.WithWidth(3));
            else if (!LeftBorderOnly) context.DrawRectangle(null, new Pen(Border, 1), Bounds);
            return;
        }
        Side(Borders.Left, new Rect(Bounds.X, Bounds.Y, Math.Min(Bounds.Width, Borders.Left?.Width ?? 0), Bounds.Height));
        Side(Borders.Top, new Rect(Bounds.X, Bounds.Y, Bounds.Width, Math.Min(Bounds.Height, Borders.Top?.Width ?? 0)));
        Side(Borders.Right, new Rect(Math.Max(Bounds.X, Bounds.Right - (Borders.Right?.Width ?? 0)), Bounds.Y, Math.Min(Bounds.Width, Borders.Right?.Width ?? 0), Bounds.Height));
        Side(Borders.Bottom, new Rect(Bounds.X, Math.Max(Bounds.Y, Bounds.Bottom - (Borders.Bottom?.Width ?? 0)), Bounds.Width, Math.Min(Bounds.Height, Borders.Bottom?.Width ?? 0)));
        void Side(BorderSide? side, Rect bounds)
        {
            if (side is { Width: > 0 } && (DocumentLayout.Brush(side.Color) ?? Border) is { } brush) context.FillRectangle(brush, bounds);
        }
    }
}

/// <summary>Viewport shaping over incremental prefix heights, with a bounded reusable layout cache.</summary>
internal sealed partial class DocumentLayout : IDisposable
{
    private sealed record Cached(double Width, ParagraphLayout Layout);
    private readonly Dictionary<Guid, Cached> _cache = [];
    private readonly ShapedLayoutCache _glyphs;
    public DocumentLayout() => _glyphs = new(() => DisposedLayouts++);
    public List<ParagraphVisual> Paragraphs { get; } = [];
    public List<BlockDecoration> Decorations { get; } = [];
    public double Height { get; private set; }
    public double Width { get; private set; }
    private FontFamily _font = FontFamily.Default;
    private IBrush _foreground = Brushes.Black;
    private IBrush _border = Brushes.Gray;

    private LayoutHeightIndex _heights = new();
    private DocumentIndex? _index;
    private FlowDocument? _document;
    private DocumentStyleResolver? _resolver;
    private DocumentFontService? _fonts;
    internal IReadOnlyList<DocumentFontDiagnostic> FontDiagnostics => _fonts?.Diagnostics ?? [];
    private Thickness _padding;
    private Rect _viewport;
    private double _previousViewportY = double.NaN;
    private long _clock;
    private int _maxShapingCharacters;
    public int ShapedParagraphs { get; private set; }
    public int DisposedLayouts { get; private set; }
    public int CachedParagraphs => _cache.Count;
    public int CachedLayouts => _glyphs.Count;
    public long CachedLayoutBytes => _glyphs.Bytes;
    public long PeakLayoutBytes => _glyphs.PeakBytes;
    public long ShapedCharacters { get; private set; }
    public int LargestShapingWindow { get; private set; }
    public int GeometryNodes => _heights.CreatedNodes;
    public double AnchorAdjustment { get; private set; }
    private const int CacheLimit = 256;
    private readonly Dictionary<Guid, long> _uses = [];
    private bool _collectDecorations, _building;
    internal event Action<double>? AnchorShifted;

    public void Build(FlowDocument document, double width, FontFamily font, IBrush foreground, IBrush border,
        Thickness padding, Rect? viewport = null, int maxShapingCharacters = 0)
    {
        if (maxShapingCharacters != _maxShapingCharacters) { Clear(); _maxShapingCharacters = maxShapingCharacters; }
        _building = true;
        try { BuildCore(document, width, font, foreground, border, padding, viewport); }
        finally { _building = false; }
    }

    private void BuildCore(FlowDocument document, double width, FontFamily font, IBrush foreground, IBrush border,
        Thickness padding, Rect? viewport)
    {
        width = Math.Max(48, width);
        var formattingChanged = _document is not null && (_document.Styles != document.Styles ||
            _document.Defaults != document.Defaults || _document.Theme != document.Theme ||
            _document.Fonts != document.Fonts || !document.Fonts.IsEmpty && !ReferenceEquals(_document.Resources, document.Resources));
        var reset = formattingChanged || !Equals(_font, font) || !Equals(_foreground, foreground) || Math.Abs(Width - width) > .1 || _padding != padding;
        var view = viewport ?? new Rect(0, 0, width, 600);
        var anchor = Math.Abs(view.Y - _previousViewportY) < .1
            ? Paragraphs.FirstOrDefault(p => p.Bounds.Bottom > view.Top && p.Bounds.Top <= view.Bottom) : null;
        var anchorOffset = anchor?.Page.Start ?? 0;
        var anchorY = anchor?.Origin.Y ?? 0;
        if (anchor is not null)
        {
            using var lease = anchor.Acquire();
            foreach (var line in lease.Layout.TextLines.Take(anchor.Page.LineCount))
            {
                anchorOffset = anchor.Page.Start + line.FirstTextSourceIndex;
                if (anchorY + line.Height > view.Top) break;
                anchorY += line.Height;
            }
        }
        if (reset) Clear();
        _font = font; _foreground = foreground; _border = border; _padding = padding;
        Width = width; _viewport = view; _previousViewportY = view.Y;
        _index = new(document);
        _document = document;
        _resolver = new(document);
        _fonts ??= new(document, font);
        _heights.Synchronize(_index.Tree, Math.Max(24, width - padding.Left - padding.Right), _resolver, _index);
        AnchorAdjustment = 0;
        MeasureViewport();
        if (anchor is not null && _index.Tree.Paths?.Find(anchor.Position.Paragraph.Id) is not null)
        {
            var entry = _index.ById(anchor.Position.Paragraph.Id);
            if (!ReferenceEquals(entry.Paragraph, anchor.Position.Paragraph))
            {
                var before = ParagraphText.For(anchor.Position.Paragraph); var after = ParagraphText.For(entry.Paragraph);
                var prefix = before.CommonPrefix(after);
                var suffix = before.CommonSuffix(after, Math.Min(before.Length, after.Length) - prefix);
                if (anchorOffset >= before.Length - suffix) anchorOffset += after.Length - before.Length;
                else if (anchorOffset > prefix) anchorOffset = prefix;
            }
            var target = entry.Start + Math.Min(anchorOffset, entry.Paragraph.Length);
            for (var pass = 0; pass < 4; pass++)
            {
                var adjustment = Caret(target).Y - anchorY;
                var correction = adjustment - AnchorAdjustment;
                AnchorAdjustment = adjustment;
                _viewport = view.Translate(new Vector(0, adjustment));
                _previousViewportY = _viewport.Y;
                if (Math.Abs(correction) <= .1) break;
                // The corrected viewport can discover more estimated content
                // above the anchor. Include those corrections in the same build.
                MeasureViewport();
            }
        }
        Evict();
    }

    private ParagraphVisual? ViewAnchor() => Paragraphs.FirstOrDefault(p => p.Bounds.Bottom > _viewport.Top && p.Bounds.Top <= _viewport.Bottom);
    private double RestoreViewportAnchor(ParagraphVisual? anchor)
    {
        if (_building || anchor is null) return 0;
        var delta = Locate(anchor.Position.Paragraph.Id).Origin.Y + anchor.Page.Top - anchor.Origin.Y;
        if (Math.Abs(delta) <= .1) return 0;
        _viewport = _viewport.Translate(new Vector(0, delta)); _previousViewportY = _viewport.Y;
        AnchorShifted?.Invoke(delta);
        return delta;
    }

    private void MeasureViewport()
    {
        // Height estimates can move newly measured paragraphs into this region.
        // Repeat until stable, with a bounded number of convergence passes.
        for (var pass = 0; pass < 4; pass++)
        {
            var before = _heights.Root.Height;
            Paragraphs.Clear(); Decorations.Clear();
            var overscan = Math.Min(160, Math.Max(80, _viewport.Height / 2));
            _collectDecorations = true;
            Collect(_heights.Root, _padding.Left, _padding.Top, Math.Max(0, _viewport.Top - overscan), _viewport.Bottom + overscan);
            _collectDecorations = false;
            Reposition();
            if (Math.Abs(before - _heights.Root.Height) < .1) break;
        }
    }
    private ParagraphLayout Shape(LayoutHeightIndex.Node node)
    {
        var paragraph = node.Paragraph!;
        var width = node.TextWidth;
        if (!_cache.TryGetValue(paragraph.Id, out var cached) || Math.Abs(cached.Width - width) > .1)
        {
            cached?.Layout.Dispose();
            cached = new(width, new(paragraph, width, (p, start, text) =>
            {
                ShapedParagraphs++; ShapedCharacters += text.Length;
                LargestShapingWindow = Math.Max(LargestShapingWindow, text.Length);
                return CreateTextLayout(p, width, _font, _foreground, start, text, _fonts);
            }, () => DisposedLayouts++, _glyphs, _maxShapingCharacters));
            _cache[paragraph.Id] = cached;
        }
        else cached.Layout.Update(paragraph);
        _uses[paragraph.Id] = ++_clock;
        Evict();
        return cached.Layout;
    }
    private static void UpdateHeight(LayoutHeightIndex.Node node, ParagraphLayout layout)
    {
        var height = layout.Height;
        if (Math.Abs(node.ContentHeight - height) > .01) { node.ContentHeight = height; node.Update(); }
    }
    private void Collect(LayoutHeightIndex.Node node, double x, double y, double top, double bottom, Rect? clip = null)
    {
        if (y > bottom || y + node.Height < top) return;
        switch (node.Source.Source)
        {
            case Paragraph paragraph:
                paragraph = node.Paragraph!;
                var layout = Shape(node);
                var marker = paragraph.Style.List == ListKind.None ? null :
                    ListNumbering.GetMarker(_document!, paragraph.Id)?.Text;
                var originY = y + node.SpaceBefore;
                foreach (var page in layout.View(top - originY, bottom - originY))
                {
                    page.LastUse = ++_clock;
                    var visual = new ParagraphVisual(_index!.ById(paragraph.Id), page, new(x + node.Indent + page.XOffset, originY + page.Top), Math.Max(16, node.TextWidth - page.XOffset), page.Start == 0 ? marker : null, clip);
                    var existing = _collectDecorations ? -1 : Paragraphs.FindIndex(p => p.Position.Paragraph.Id == paragraph.Id && p.Page.Start == page.Start);
                    if (existing >= 0) Paragraphs[existing] = visual; else Paragraphs.Add(visual);
                }
                UpdateHeight(node, layout);
                if (_collectDecorations && (paragraph.Style.Shading is not null || paragraph.Style.Borders is not null))
                    Decorations.Add(new(new Rect(x + node.Indent, originY, node.TextWidth, node.ContentHeight),
                        Brush(paragraph.Style.Shading), _border, false, paragraph.Style.Borders, clip));
                break;
            case Section section:
                var slot = Decorations.Count;
                if (_collectDecorations) Decorations.Add(new(default, null, null, false));
                var sectionPadding = LayoutHeightIndex.SectionPadding(section);
                CollectBranch(node.Children, x + sectionPadding.Left, y + sectionPadding.Top, top, bottom, clip);
                if (_collectDecorations) Decorations[slot] = new(new Rect(x, y, node.Available, node.Height - 10), Brush(section.Background), Brush(section.BorderColor) ?? (section.Borders is null ? null : _border), true, section.Borders, clip);
                break;
            case Table table:
                foreach (var cell in node.IntersectingCells(top - y, bottom - y))
                {
                    var model = cell.Cell ?? (TableCell)cell.Source.Source!;
                    var cellY = y + node.RowOffsets[cell.Source.Row];
                    var cellHeight = node.RowOffsets[cell.Source.Row + model.RowSpan] - node.RowOffsets[cell.Source.Row];
                    if (cellY > bottom || cellY + cellHeight < top) continue;
                    var cellX = x + LayoutHeightIndex.ColumnOffset(table, node.Available, cell.Source.Column);
                    var cellWidth = LayoutHeightIndex.ColumnWidth(table, node.Available, cell.Source.Column, model.ColumnSpan);
                    var cellBounds = new Rect(cellX, cellY, cellWidth, cellHeight);
                    var exact = !table.RowSizing.IsDefaultOrEmpty && Enumerable.Range(cell.Source.Row, model.RowSpan).All(r => table.RowSizing[r].Mode == TableRowHeightMode.Exact);
                    var cellClip = exact ? (clip?.Intersect(cellBounds) ?? cellBounds) : clip;
                    var cellSlot = Decorations.Count;
                    if (_collectDecorations) Decorations.Add(new(default, null, null, false));
                    Collect(cell, cellX, cellY, top, bottom, cellClip);
                    cellHeight = node.RowOffsets[cell.Source.Row + model.RowSpan] - node.RowOffsets[cell.Source.Row];
                    if (_collectDecorations) Decorations[cellSlot] = new(new Rect(cellX, cellY, cellWidth, cellHeight), Brush(model.Background), _border, false, model.Borders, clip);
                }
                break;
            case TableCell tableCell:
                var cellPadding = LayoutHeightIndex.CellPadding(node.Cell ?? tableCell);
                CollectBranch(node.Children, x + cellPadding.Left, y + cellPadding.Top, top, bottom, clip);
                break;
            default:
                CollectBranch(node.Children, x, y, top, bottom, clip);
                break;
        }
    }
    private void CollectBranch(LayoutHeightIndex.Branch? branch, double x, double y, double top, double bottom, Rect? clip = null)
    {
        if (branch is null || y > bottom || y + branch.Height < top) return;
        CollectBranch(branch.Left, x, y, top, bottom, clip);
        y += branch.Left?.Height ?? 0;
        Collect(branch.Value, x, y, top, bottom, clip);
        y += branch.Value.Height;
        CollectBranch(branch.Right, x, y, top, bottom, clip);
    }
    private (LayoutHeightIndex.Node Node, Point Origin) Locate(Guid id)
    {
        var path = _index!.Tree.Paths!.Find(id)!.Value;
        var node = _heights.Root; var x = _padding.Left; var y = _padding.Top;
        Visit(path);
        void Visit(DocumentPath current)
        {
            if (current.Parent is not null) Visit(current.Parent);
            var key = current.Key;
            if (node.Source.Source is Table table)
            {
                var cell = node.Children!.Find(key);
                x += LayoutHeightIndex.ColumnOffset(table, node.Available, cell.Source.Column);
                y += node.RowOffsets[cell.Source.Row];
                node = cell; return;
            }
            if (node.Source.Source is Section section) { var inset = LayoutHeightIndex.SectionPadding(section); x += inset.Left; y += inset.Top; }
            else if (node.Source.Source is TableCell tableCell) { var inset = LayoutHeightIndex.CellPadding(node.Cell ?? tableCell); x += inset.Left; y += inset.Top; }
            y += node.Children!.Prefix(key); node = node.Children.Find(key);
        }
        if (node.Paragraph is not null) { x += node.Indent; y += node.SpaceBefore; }
        return (node, new(x, y));
    }
    private void Reposition()
    {
        // Resolve lazy geometry paths before recording any origins. A later
        // path can refine a shared prefix used by an earlier visual.
        foreach (var paragraph in Paragraphs) Locate(paragraph.Position.Paragraph.Id);
        for (var i = 0; i < Paragraphs.Count; i++)
            Paragraphs[i] = Paragraphs[i] with { Origin = Locate(Paragraphs[i].Position.Paragraph.Id).Origin + new Vector(Paragraphs[i].Page.XOffset, Paragraphs[i].Page.Top) };
        Height = _padding.Top + _heights.Root.Height + _padding.Bottom;
    }
    private void Evict()
    {
        while (_cache.Count > CacheLimit)
        {
            var id = Guid.Empty; var oldest = long.MaxValue;
            foreach (var use in _uses)
                if (use.Value < oldest) { id = use.Key; oldest = use.Value; }
            _cache[id].Layout.Dispose(); _cache.Remove(id); _uses.Remove(id);
        }
    }

    internal static ShapingTextLayout CreateTextLayout(Paragraph paragraph, double width, FontFamily font, IBrush foreground,
        int start = 0, string? text = null, DocumentFontService? fonts = null) =>
        ShapingTextLayout.Create(paragraph, width, font, foreground, start, text, fonts);

    internal static TextLayout CreateNativeTextLayout(Paragraph paragraph, double width, FontFamily font, IBrush foreground,
        int start = 0, string? text = null)
    {
        text ??= ParagraphText.For(paragraph).ToString();
        if (paragraph.Runs.Any(r => r.Inline is not null))
        {
            var properties = new GenericTextParagraphProperties(
                paragraph.Style.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                paragraph.Style.Alignment switch
                {
                    ParagraphAlignment.Center => TextAlignment.Center, ParagraphAlignment.Right => TextAlignment.Right,
                    ParagraphAlignment.Justify => TextAlignment.Justify, _ => TextAlignment.Left
                }, true, false, InlineTextSource.Properties(paragraph.DefaultStyle, font, foreground),
                TextWrapping.Wrap, paragraph.Style.LineHeight ?? double.NaN, 0, paragraph.Style.LetterSpacing);
            return new TextLayout(new InlineTextSource(paragraph, start, text, font, foreground), properties,
                maxWidth: Math.Max(16, width - (start == 0 ? paragraph.Style.FirstLineIndent : 0)),
                maxLines: start == 0 && paragraph.Style.FirstLineIndent != 0 ? 2 : 0);
        }
        var count = text.Length;
        List<ValueSpan<TextRunProperties>>? overrides = null;
        var offset = 0;
        foreach (var run in paragraph.Runs)
        {
            var from = Math.Max(start, offset); var to = Math.Min(start + count, offset + run.Storage.Length);
            offset += run.Storage.Length;
            if (to <= from) continue;
            var style = run.Style;
            if (paragraph.Runs.Length == 1 && style == paragraph.DefaultStyle && style.Baseline == Baseline.Normal &&
                !style.Underline && !style.Strikethrough && style.Hyperlink is null && style.Foreground is null && style.Background is null)
                break;
            TextDecorationCollection? decorations = null;
            // Preset decorations are mutable AvaloniaObjects shared globally. Own
            // these objects on the surface's dispatcher, just like its layouts.
            if (style.Underline || style.Hyperlink is not null) (decorations ??= []).Add(new TextDecoration { Location = TextDecorationLocation.Underline });
            if (style.Strikethrough) (decorations ??= []).Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
            var properties = new GenericTextRunProperties(
                Typeface(style, font), style.FontSize * (style.Baseline == Baseline.Normal ? 1 : .75),
                decorations, Brush(style.Foreground) ?? (style.Hyperlink is not null ? Brush("#3478CE") : foreground),
                Brush(style.Background), style.Baseline switch
                { Baseline.Subscript => BaselineAlignment.Subscript, Baseline.Superscript => BaselineAlignment.Superscript, _ => BaselineAlignment.Baseline });
            (overrides ??= []).Add(new(from - start, to - from, properties));
        }
        return new TextLayout(text, Typeface(paragraph.DefaultStyle, font), paragraph.DefaultStyle.FontSize, foreground,
            textAlignment: paragraph.Style.Alignment switch
            {
                ParagraphAlignment.Center => TextAlignment.Center, ParagraphAlignment.Right => TextAlignment.Right,
                ParagraphAlignment.Justify => TextAlignment.Justify, _ => TextAlignment.Left
            },
            textWrapping: TextWrapping.Wrap, maxWidth: Math.Max(16, width - (start == 0 ? paragraph.Style.FirstLineIndent : 0)),
            // Keep a lookahead line so a wrapped first line retains paragraph justification.
            maxLines: start == 0 && paragraph.Style.FirstLineIndent != 0 ? 2 : 0,
            lineHeight: paragraph.Style.LineHeight ?? double.NaN, letterSpacing: paragraph.Style.LetterSpacing,
            flowDirection: paragraph.Style.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            textStyleOverrides: overrides);
    }

    internal static Typeface Typeface(TextStyle style, FontFamily fallback) =>
        new(style.FontFamily is null ? fallback : new FontFamily(style.FontFamily), style.Italic ? FontStyle.Italic : FontStyle.Normal, (FontWeight)style.EffectiveFontWeight, (FontStretch)style.FontStretch);
    internal static IBrush? Brush(string? color) => color is null ? null : new SolidColorBrush(Color.Parse(color));

    public ParagraphVisual? At(int position)
    {
        if (_index is null) return null;
        var entry = _index.At(position);
        var existing = Paragraphs.FirstOrDefault(p => p.Position.Paragraph.Id == entry.Paragraph.Id &&
            position >= p.TextStart && (position < p.TextEnd || p.TextEnd == entry.End));
        if (existing is not null) return existing;
        var anchor = ViewAnchor();
        PruneTargets();
        var location = Locate(entry.Paragraph.Id);
        var layout = Shape(location.Node);
        var page = layout.At(Math.Clamp(position - entry.Start, 0, entry.Paragraph.Length));
        page.LastUse = ++_clock;
        UpdateHeight(location.Node, layout);
        location = Locate(entry.Paragraph.Id);
        // Measure the target and its intersecting row dependencies, even offscreen.
        Collect(_heights.Root, _padding.Left, _padding.Top, location.Origin.Y + page.Top, location.Origin.Y + page.Top + 1);
        Reposition();
        existing = Paragraphs.FirstOrDefault(p => p.Position.Paragraph.Id == entry.Paragraph.Id && p.Page.Start == page.Start);
        if (existing is null)
        {
            location = Locate(entry.Paragraph.Id);
            existing = new(entry, page, location.Origin + new Vector(page.XOffset, page.Top), Math.Max(16, location.Node.TextWidth - page.XOffset), null);
            Paragraphs.Add(existing); Reposition();
        }
        RestoreViewportAnchor(anchor);
        Evict(); return existing;
    }
    public Rect Caret(int position) => Caret(VisualCaret.Logical(position));

    public int HitTest(Point point) => HitTestCaret(point).Position;

    internal VisualCaret HitTestCaret(Point point)
    {
        if (_index is null) return VisualCaret.Logical(0);
        var anchor = ViewAnchor();
        PruneTargets();
        Collect(_heights.Root, _padding.Left, _padding.Top, point.Y - 20, point.Y + 20);
        Reposition();
        point += new Vector(0, RestoreViewportAnchor(anchor));
        if (Paragraphs.Count == 0) return VisualCaret.Logical(0);
        var visual = Paragraphs.Where(p => p.Bounds.Height > 0 && p.Bounds.Width > 0).MinBy(p =>
        {
            var r = p.Bounds;
            var dx = Math.Max(Math.Max(r.Left - point.X, 0), point.X - r.Right);
            var dy = Math.Max(Math.Max(r.Top - point.Y, 0), point.Y - r.Bottom);
            return dy * dy * 16 + dx * dx;
        });
        if (visual is null) return VisualCaret.Logical(0);
        var hit = HitTestLine(visual, point);
        Evict();
        return hit;
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
            var from = Math.Max(start, p.TextStart);
            var to = Math.Min(start + length, p.TextEnd);
            if (to > from)
            {
                using var lease = p.Acquire();
                foreach (var rect in lease.Layout.HitTestTextRange(from - p.TextStart, to - from))
                {
                    var bounds = rect.Translate(new Vector(p.Origin.X, p.Origin.Y));
                    if (p.Clip is { } clip) bounds = bounds.Intersect(clip);
                    if (bounds.Width > 0 && bounds.Height > 0) yield return bounds;
                }
            }
            if (p.TextEnd == p.Position.End && start <= p.Position.End && start + length > p.Position.End)
            {
                var caret = Caret(p.Position.End).WithWidth(5);
                if (p.Clip is { } clip) caret = caret.Intersect(clip);
                if (caret.Width > 0 && caret.Height > 0) yield return caret;
            }
        }
    }

    public IEnumerable<InlineVisual> InlineVisuals()
    {
        foreach (var visual in Paragraphs)
        {
            if (!visual.Position.Paragraph.Runs.Any(r => r.Inline is not null)) continue;
            using var lease = visual.Acquire();
            var top = visual.Origin.Y;
            foreach (var line in lease.Layout.TextLines.Take(visual.Page.LineCount))
            {
                foreach (var bounds in line.GetTextBounds(line.FirstTextSourceIndex, line.Length))
                    foreach (var run in bounds.TextRunBounds)
                        if (run.TextRun is InlineObjectRun inline)
                            yield return new(inline.Descriptor, visual.TextStart + run.TextSourceCharacterIndex,
                                new Rect(visual.Origin.X + run.Rectangle.X, top + line.Baseline - inline.Baseline,
                                    inline.Size.Width, inline.Size.Height), visual.Clip);
                top += line.Height;
            }
        }
    }

    public void Clear()
    {
        foreach (var cached in _cache.Values) cached.Layout.Dispose();
        _glyphs.Clear();
        _cache.Clear(); _uses.Clear(); Paragraphs.Clear(); Decorations.Clear();
        _heights = new(); _index = null; _document = null; _resolver = null;
        _fonts?.Dispose(); _fonts = null;
        _previousViewportY = double.NaN;
    }
    public void Dispose() => Clear();
}
