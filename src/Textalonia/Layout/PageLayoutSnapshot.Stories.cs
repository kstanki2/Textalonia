using System.Collections.Immutable;
using Avalonia;
using Textalonia.Controls;
using Textalonia.Model;

namespace Textalonia.Layout;

/// <summary>The physical instance used when evaluating page fields in a repeated story.</summary>
public sealed record PageContext(int PageIndex, int PageNumber, int PageCount, Guid SectionId, int SectionPageCount);

/// <summary>A visible instance or continuation of a secondary story, in document coordinates.</summary>
public sealed record StoryRegion(Guid StoryId, DocumentStoryKind Kind, int PageIndex, Rect Bounds,
    int Start, int End, bool IsContinuation, string? Marker, PageContext Context)
{
    public string? SeparatorText { get; init; }
}

public sealed record StoryHit(Guid StoryId, int Position, int PageIndex);

public sealed partial class PageLayoutSnapshot
{
    private readonly ImmutableArray<(Guid StoryId, int Page, TableCellVisual Cell)> _storyCells;
    public ImmutableArray<LineFragment> StoryFragments { get; }
    public ImmutableArray<StoryRegion> StoryRegions { get; }
    /// <summary>Content preserved in the model whose layout needs a documented fallback.</summary>
    public ImmutableArray<string> LayoutDiagnostics { get; }

    public StoryHit HitTestStory(Point point)
    {
        var region = StoryRegions.LastOrDefault(r => r.Bounds.Contains(point));
        if (region is null) return new(Guid.Empty, HitTest(point), GetPageIndex(HitTest(point)));
        return new(region.StoryId, HitTestStoryCaret(region.StoryId, point, region.PageIndex).Position, region.PageIndex);
    }

    // Navigation uses the same bidi and grapheme implementation as the body. A short lived facade
    // owns independent measurement leases, including when the same story appears on several sheets.
    private PageLayoutSnapshot StoryView(Guid storyId, int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fragments = StoryFragments.Where(f => f.StoryKey == storyId && (pageIndex < 0 || f.PageIndex == pageIndex)).ToImmutableArray();
        return new(Document.GetStoryDocument(storyId), Pages, fragments, [], [], _font, _foreground, FontDiagnostics);
    }

    public Rect Caret(Guid storyId, int position, int pageIndex = -1) => Caret(storyId, VisualCaret.Logical(position), pageIndex);
    internal Rect Caret(Guid storyId, VisualCaret caret, int pageIndex = -1)
    {
        if (storyId == Guid.Empty) return Caret(caret);
        using var view = StoryView(storyId, pageIndex); return view.Caret(caret);
    }
    internal VisualCaret HitTestStoryCaret(Guid storyId, Point point, int pageIndex = -1)
    {
        if (storyId == Guid.Empty) return HitTestCaret(point);
        using var view = StoryView(storyId, pageIndex); return view.HitTestCaret(point);
    }
    internal Rect ClipCaret(Rect bounds, Guid storyId, VisualCaret caret, int pageIndex = -1)
    {
        if (storyId == Guid.Empty) return ClipCaret(bounds, caret);
        using var view = StoryView(storyId, pageIndex); return view.ClipCaret(bounds, caret);
    }
    public int GetPageIndex(Guid storyId, int position)
    {
        if (storyId == Guid.Empty) return GetPageIndex(position);
        using var view = StoryView(storyId, -1); return view.GetPageIndex(position);
    }
    public IEnumerable<Rect> SelectionRects(Guid storyId, int start, int length, int pageIndex = -1)
    {
        if (storyId == Guid.Empty)
        { foreach (var rect in SelectionRects(start, length)) yield return rect; yield break; }
        using var view = StoryView(storyId, pageIndex);
        foreach (var rect in view.SelectionRects(start, length)) yield return rect;
    }
    internal double CaretColumnX(Guid storyId, VisualCaret caret, int pageIndex = -1)
    {
        if (storyId == Guid.Empty) return CaretColumnX(caret);
        using var view = StoryView(storyId, pageIndex); return view.CaretColumnX(caret);
    }
    internal VisualCaret MoveCaret(Guid storyId, VisualCaret caret, bool right, int pageIndex = -1)
    {
        if (storyId == Guid.Empty) return MoveCaret(caret, right);
        using var view = StoryView(storyId, pageIndex); return view.MoveCaret(caret, right);
    }
    internal VisualCaret MoveWordCaret(Guid storyId, VisualCaret caret, bool right, int pageIndex = -1)
    {
        if (storyId == Guid.Empty) return MoveWordCaret(caret, right);
        using var view = StoryView(storyId, pageIndex); return view.MoveWordCaret(caret, right);
    }
    internal VisualCaret MoveVerticalCaret(Guid storyId, VisualCaret caret, bool down, double preferredX, int pageIndex = -1)
    {
        if (storyId == Guid.Empty) return MoveVerticalCaret(caret, down, preferredX);
        using var view = StoryView(storyId, pageIndex); return view.MoveVerticalCaret(caret, down, preferredX);
    }
    internal VisualCaret MovePageCaret(Guid storyId, VisualCaret caret, bool down, double preferredX, int pageIndex = -1)
    {
        if (storyId == Guid.Empty) return MovePageCaret(caret, down, preferredX);
        using var view = StoryView(storyId, -1); return view.MovePageCaret(caret, down, preferredX);
    }
    internal VisualCaret LineBoundary(Guid storyId, VisualCaret caret, bool end, int pageIndex = -1)
    {
        if (storyId == Guid.Empty) return LineBoundary(caret, end);
        using var view = StoryView(storyId, pageIndex); return view.LineBoundary(caret, end);
    }
    internal VisualCaret CollapseSelection(Guid storyId, VisualCaret anchor, VisualCaret active, bool right, int pageIndex = -1)
    {
        if (storyId == Guid.Empty) return CollapseSelection(anchor, active, right);
        using var view = StoryView(storyId, pageIndex); return view.CollapseSelection(anchor, active, right);
    }
    internal IReadOnlyList<TableCellVisual> TableCells(Guid storyId, int pageIndex = -1) => storyId == Guid.Empty ? _cells :
        _storyCells.Where(c => c.StoryId == storyId && (pageIndex < 0 || c.Page == pageIndex)).Select(c => c.Cell).ToArray();
}
