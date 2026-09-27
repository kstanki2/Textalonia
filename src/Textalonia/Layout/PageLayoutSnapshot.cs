using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Textalonia.Controls;
using Textalonia.Model;

namespace Textalonia.Layout;

/// <summary>View arrangement in unscaled document units (1/96 inch). It never changes document text.</summary>
public sealed record PaginationOptions
{
    public double PageGap { get; init; } = 20;
    public int PagesPerRow { get; init; } = 1;
    public bool Draft { get; init; }
    public int MaxShapingCharacters { get; init; }
}

/// <summary>An exactly measured physical page. Index is zero based; Number is section-aware.</summary>
public sealed record PageLayout(int Index, Guid SectionId, int Number, string NumberText, Rect Bounds,
    Rect ContentBounds, ImmutableArray<Rect> Columns, PageSettings Settings, bool IsBlank = false);

/// <summary>A shaped line in the main story. Offsets are global UTF-16 insertion positions.</summary>
public sealed record LineFragment
{
    internal LineFragment() { }
    public string StoryId => StoryKey == Guid.Empty ? "main" : StoryKey.ToString();
    public Guid StoryKey { get; internal init; }
    public Guid ParagraphId { get; internal init; }
    public Guid SectionId { get; internal init; }
    public int PageIndex { get; internal init; }
    public int ColumnIndex { get; internal init; }
    public int TextStart { get; internal init; }
    public int TextEnd { get; internal init; }
    public int Start => TextStart;
    public int Length => TextEnd - TextStart;
    public Rect Bounds { get; internal init; }
    public Rect Clip { get; internal init; }
    public double Baseline { get; internal init; }
    public int? LineNumber { get; internal init; }
    internal ExactParagraph Measurement { get; init; } = null!;
    internal ExactLine Line { get; init; } = null!;
    internal ParagraphPosition Position { get; init; } = null!;
    internal int SourceStart { get; init; }
    internal Point Origin { get; init; }
    internal Rect ColumnBounds { get; init; }
    internal string? Marker { get; init; }
    internal double LineNumberDistance { get; init; }
    internal ShapedLayoutCache.Lease Acquire() => Measurement.Layout.Acquire(Line.Window);
}

/// <summary>
/// Immutable page and line geometry shared by rendering, editing and output. Owns leases on reusable
/// measurements; dispose snapshots when no longer displayed. Use on the shaping backend's UI thread.
/// </summary>
public sealed partial class PageLayoutSnapshot : IDisposable
{
    private readonly ImmutableArray<ExactParagraph> _measurements;
    private readonly ImmutableArray<BlockDecoration> _decorations;
    private readonly ImmutableArray<TableCellVisual> _cells;
    private readonly FontFamily _font;
    private readonly IBrush _foreground;
    private readonly DocumentIndex _index;
    private bool _disposed;
    public ImmutableArray<PageLayout> Pages { get; }
    public ImmutableArray<LineFragment> Fragments { get; }
    public FlowDocument Document { get; }
    public double Width { get; }
    public double Height { get; }
    public bool IsComplete => true;
    public bool IsDraft { get; internal init; }
    internal void VerifyAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    public IReadOnlyList<DocumentFontDiagnostic> FontDiagnostics { get; }

    internal PageLayoutSnapshot(FlowDocument document, ImmutableArray<PageLayout> pages,
        ImmutableArray<LineFragment> fragments, ImmutableArray<BlockDecoration> decorations,
        ImmutableArray<TableCellVisual> cells, FontFamily font, IBrush foreground,
        IReadOnlyList<DocumentFontDiagnostic> diagnostics,
        ImmutableArray<LineFragment> storyFragments = default, ImmutableArray<StoryRegion> storyRegions = default,
        ImmutableArray<(Guid StoryId, int Page, TableCellVisual Cell)> storyCells = default,
        ImmutableArray<string> layoutDiagnostics = default)
    {
        Document = document; _index = new(document); Pages = pages; Fragments = fragments;
        _decorations = decorations; _cells = cells; _font = font; _foreground = foreground;
        StoryFragments = storyFragments.IsDefault ? [] : storyFragments;
        StoryRegions = storyRegions.IsDefault ? [] : storyRegions;
        _storyCells = storyCells.IsDefault ? [] : storyCells;
        LayoutDiagnostics = layoutDiagnostics.IsDefault ? [] : layoutDiagnostics;
        _measurements = fragments.Concat(StoryFragments).Select(f => f.Measurement).Distinct().ToImmutableArray();
        foreach (var measurement in _measurements) measurement.AddRef();
        Width = pages.Select(p => p.Bounds.Right).DefaultIfEmpty().Max();
        Height = pages.Select(p => p.Bounds.Bottom).DefaultIfEmpty().Max();
        FontDiagnostics = diagnostics.ToArray();
    }

    public void Draw(DrawingContext context, Rect? viewport = null)
    { DrawBackgrounds(context, viewport); DrawContent(context, viewport); }

    public void DrawBackgrounds(DrawingContext context, Rect? viewport = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var page in Pages)
        {
            if (viewport is { } visible && !page.Bounds.Intersects(visible)) continue;
            new BlockDecoration(page.Bounds, DocumentLayout.Brush(page.Settings.Background),
                page.Settings.Borders is null ? null : Brushes.Black, false, page.Settings.Borders).Draw(context);
        }
        foreach (var decoration in _decorations)
            if (viewport is not { } visible || decoration.Bounds.Intersects(visible)) decoration.Draw(context);
    }

    public void DrawContent(DrawingContext context, Rect? viewport = null) => DrawContentForOutput(context, viewport, null);

    internal void DrawContentForOutput(DrawingContext context, Rect? viewport, Rendering.IPageTextRenderer? textRenderer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var fragment in Fragments.Concat(StoryFragments))
        {
            if (viewport is { } visible && !fragment.Bounds.Intersects(visible)) continue;
            using var lease = fragment.Acquire();
            using (context.PushClip(fragment.Clip))
            {
                var line = lease.Layout.TextLines[fragment.Line.Index];
                if (textRenderer is null) line.Draw(context, fragment.Origin);
                else textRenderer.DrawLine(context, line, fragment.Origin);
            }
            if (fragment.Marker is { } marker) DrawLabel(marker, fragment.Origin.X - 24, fragment.Origin.Y, fragment.Measurement.Paragraph.DefaultStyle.FontSize);
            if (fragment.LineNumber is { } number) DrawLabel(number.ToString(CultureInfo.InvariantCulture),
                fragment.ColumnBounds.Left - fragment.LineNumberDistance, fragment.Origin.Y, 10, true);
        }
        foreach (var region in StoryRegions)
            if (region.SeparatorText is { Length: > 0 } separator && (viewport is not { } visible || region.Bounds.Intersects(visible)))
                DrawLabel(separator, region.Bounds.Left, region.Bounds.Top - 18, 10);
        void DrawLabel(string text, double x, double y, double size, bool alignRight = false)
        {
            using var label = new TextLayout(text, new Typeface(_font), size, _foreground,
                flowDirection: FlowDirection.LeftToRight);
            var origin = new Point(alignRight ? x - label.Width : x, y);
            foreach (var line in label.TextLines)
            {
                if (textRenderer is null) line.Draw(context, origin);
                else textRenderer.DrawLine(context, line, origin);
                origin = new Point(origin.X, origin.Y + line.Height);
            }
        }
    }

    public int HitTest(Point point) => HitTestCaret(point).Position;
    internal VisualCaret HitTestCaret(Point point)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fragment = Fragments.Where(f => f.Bounds.Intersect(f.Clip).Height > 0).MinBy(f =>
        {
            var r = f.Bounds.Intersect(f.Clip);
            var dx = Math.Max(Math.Max(r.Left - point.X, 0), point.X - r.Right);
            var dy = Math.Max(Math.Max(r.Top - point.Y, 0), point.Y - r.Bottom);
            return dx * dx + dy * dy * 16;
        });
        if (fragment is null) return VisualCaret.Logical(0);
        return HitTestFragment(fragment, point.X);
    }

    private static VisualCaret HitTestFragment(LineFragment fragment, double x)
    {
        using var lease = fragment.Acquire();
        var line = lease.Layout.TextLines[fragment.Line.Index];
        var hit = line.GetCharacterHitFromDistance(x - fragment.Origin.X);
        var first = fragment.SourceStart + hit.FirstCharacterIndex;
        var caret = new VisualCaret(first, hit.TrailingLength, fragment.TextStart);
        return caret.Position < fragment.TextStart || caret.Position > fragment.TextEnd
            ? new(Math.Clamp(caret.Position, fragment.TextStart, fragment.TextEnd), 0, fragment.TextStart) : caret;
    }

    private LineFragment? FindLine(VisualCaret caret)
    {
        var position = Math.Clamp(caret.Position, 0, _index.Length);
        if (caret.LineStart >= 0)
        {
            var exact = Fragments.FirstOrDefault(f => f.TextStart == caret.LineStart && position >= f.TextStart && position <= f.TextEnd);
            if (exact is not null) return exact;
        }
        return Fragments.LastOrDefault(f => position >= f.TextStart && position <= f.TextEnd &&
            (position < f.TextEnd || f.TextEnd == f.Position.End)) ??
            Fragments.MinBy(f => Math.Min(Math.Abs((long)f.TextStart - position), Math.Abs((long)f.TextEnd - position)));
    }

    public Rect Caret(int position) => Caret(VisualCaret.Logical(position));
    internal Rect Caret(VisualCaret caret)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fragment = FindLine(caret);
        if (fragment is null) return new Rect(0, 0, 1.5, 20);
        using var lease = fragment.Acquire();
        var line = lease.Layout.TextLines[fragment.Line.Index];
        var position = Math.Clamp(caret.Position, fragment.TextStart, fragment.TextEnd);
        var hit = position == caret.Position ? new CharacterHit(caret.FirstCharacterIndex - fragment.SourceStart, caret.TrailingLength)
            : new CharacterHit(position - fragment.SourceStart, 0);
        return new(fragment.Origin.X + line.GetDistanceFromCharacterHit(hit), fragment.Origin.Y, 1.5, fragment.Bounds.Height);
    }
    internal Rect ClipCaret(Rect bounds, int position) => FindLine(VisualCaret.Logical(position)) is { } fragment ? bounds.Intersect(fragment.Clip) : bounds;
    internal Rect ClipCaret(Rect bounds, VisualCaret caret) => FindLine(caret) is { } fragment ? bounds.Intersect(fragment.Clip) : bounds;
    public int GetPageIndex(int position) => FindLine(VisualCaret.Logical(position))?.PageIndex ?? 0;

    public IEnumerable<Rect> SelectionRects(int start, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (start < 0 || length < 0 || start > _index.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        foreach (var fragment in Fragments)
        {
            var from = Math.Max(start, fragment.TextStart); var to = Math.Min(start + length, fragment.TextEnd);
            if (to > from)
            {
                using var lease = fragment.Acquire();
                foreach (var bounds in lease.Layout.TextLines[fragment.Line.Index].GetTextBounds(from - fragment.SourceStart, to - from))
                {
                    var rect = new Rect(fragment.Origin.X + bounds.Rectangle.X, fragment.Origin.Y,
                        bounds.Rectangle.Width, fragment.Bounds.Height).Intersect(fragment.Clip);
                    if (rect.Width > 0 && rect.Height > 0) yield return rect;
                }
            }
            if (fragment.TextEnd == fragment.Position.End && start <= fragment.TextEnd && start + length > fragment.TextEnd)
            {
                var rect = Caret(new VisualCaret(fragment.TextEnd, 0, fragment.TextStart)).WithWidth(5).Intersect(fragment.Clip);
                if (rect.Width > 0 && rect.Height > 0) yield return rect;
            }
        }
    }

    internal IEnumerable<InlineVisual> InlineVisuals()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var fragment in Fragments.Concat(StoryFragments))
        {
            if (!fragment.Position.Paragraph.Runs.Any(r => r.Inline is not null)) continue;
            using var lease = fragment.Acquire();
            var line = lease.Layout.TextLines[fragment.Line.Index];
            foreach (var bounds in line.GetTextBounds(line.FirstTextSourceIndex, line.Length))
                foreach (var run in bounds.TextRunBounds)
                    if (run.TextRun is InlineObjectRun inline)
                    {
                        // Avalonia's native line aligns superscript/subscript at the line's top/bottom;
                        // our typography line additionally includes its measured rise and run offset.
                        var inlineBounds = line is TypographyLine typography ? typography.GetInlineBounds(inline, fragment.Origin) :
                            new Rect(new Point(fragment.Origin.X + run.Rectangle.X, fragment.Origin.Y + (inline.Properties.BaselineAlignment switch
                            {
                                BaselineAlignment.Superscript => 0,
                                BaselineAlignment.Subscript => line.Height - inline.Size.Height,
                                _ => line.Baseline - inline.Baseline
                            })), inline.Size);
                        yield return new(inline.Descriptor, fragment.SourceStart + run.TextSourceCharacterIndex,
                            inlineBounds, fragment.Clip) { StoryId = fragment.StoryKey, PageIndex = fragment.PageIndex };
                    }
        }
    }
    internal IReadOnlyList<TableCellVisual> TableCells() => _cells;
    internal TableCellVisual? HitTestTableCell(Point point, Guid? tableId = null) => _cells.LastOrDefault(c =>
        (tableId is null || c.Table.Id == tableId) && c.VisibleBounds.Contains(point));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var measurement in _measurements) measurement.Release();
    }
}
