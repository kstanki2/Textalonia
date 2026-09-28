using Avalonia;
using Avalonia.Media;
using Textalonia.Layout;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class DocumentSurface
{
    private readonly PaginationEngine _pagination = new();
    private PageLayoutSnapshot? _pagedLayout;
    private FlowDocument? _pagedDocument;
    private PaginationOptions? _paginationOptions;
    private FontFamily? _pageFont;
    private IBrush? _pageForeground, _pageBorder;
    internal bool HasPagedLayout => _pagedLayout is not null;
    internal PageLayoutSnapshot? PagedLayout => _pagedLayout;
    internal double ViewZoom => Editor?.Zoom ?? 1;
    private Guid GeometryStoryId => Editor?.ViewMode == DocumentViewMode.PrintLayout ? Editor.ActiveStoryId : Guid.Empty;
    private int GeometryStoryPage => Editor is { ActiveStoryId: var id } && id != Guid.Empty &&
        Editor.Document.Stories.TryGetValue(id, out var story) && story.Kind is DocumentStoryKind.Header or DocumentStoryKind.Footer
        ? Editor.ActiveStoryPageIndex : -1;
    private IEnumerable<LineFragment> ActivePageFragments => _pagedLayout is null ? [] : GeometryStoryId == Guid.Empty
        ? _pagedLayout.Fragments : _pagedLayout.StoryFragments.Where(f => f.StoryKey == GeometryStoryId &&
            (GeometryStoryPage < 0 || f.PageIndex == GeometryStoryPage));
    private double GeometryHeight => (_pagedLayout?.Height ?? _layout.Height) * ViewZoom;
    private double GeometryWidth => (_pagedLayout?.Width ?? _layout.Width) * ViewZoom;
    internal Rect ToSurface(Rect rect) => new(rect.X * ViewZoom, rect.Y * ViewZoom, rect.Width * ViewZoom, rect.Height * ViewZoom);
    private Rect ToDocument(Rect rect) => new(rect.X / ViewZoom, rect.Y / ViewZoom, rect.Width / ViewZoom, rect.Height / ViewZoom);
    private Point ToDocument(Point point) => new(point.X / ViewZoom, point.Y / ViewZoom);

    private void BuildPagedLayout(FlowDocument document)
    {
        var editor = Editor!;
        var options = new PaginationOptions { PageGap = editor.PageGap, PagesPerRow = editor.PagesPerRow,
            Draft = editor.ViewMode == DocumentViewMode.Draft, MaxShapingCharacters = editor.MaxShapingCharacters };
        var foreground = editor.Foreground ?? Brushes.Black;
        var border = editor.BorderBrush ?? Brushes.Gray;
        if (_pagedLayout is null || !ReferenceEquals(_pagedDocument, document) || options != _paginationOptions ||
            !Equals(_pageFont, editor.FontFamily) || !Equals(_pageForeground, foreground) || !Equals(_pageBorder, border))
        {
            var snapshot = _pagination.Paginate(document, editor.FontFamily, foreground, border, options);
            _pagedLayout?.Dispose();
            _pagedLayout = snapshot;
            _pagedDocument = document; _paginationOptions = options;
            _pageFont = editor.FontFamily; _pageForeground = foreground; _pageBorder = border;
        }
        if (editor.ActiveStoryId != Guid.Empty && _pagedLayout.StoryRegions.FirstOrDefault(r => r.StoryId == editor.ActiveStoryId) is { } first &&
            !_pagedLayout.StoryRegions.Any(r => r.StoryId == editor.ActiveStoryId && r.PageIndex == editor.ActiveStoryPageIndex))
            editor.ActiveStoryPageIndex = first.PageIndex;
        var top = (editor.Scroller?.Offset.Y ?? 0) / ViewZoom;
        var left = (editor.Scroller?.Offset.X ?? 0) / ViewZoom;
        var current = _pagedLayout.Pages.Select((page, index) => (page, index)).MinBy(item =>
            Math.Abs(item.page.Bounds.Top - top) * 10000 + Math.Abs(item.page.Bounds.Left - left)).index + 1;
        editor.UpdatePageStatus(_pagedLayout.Pages.Length, current);
    }

    private void ClearPagedLayout()
    {
        _pagedLayout?.Dispose(); _pagedLayout = null; _pagedDocument = null;
        _paginationOptions = null;
        _pagination.Clear();
    }

    internal Rect GeometryCaret(int position) => GeometryCaret(VisualCaret.Logical(position));
    private Rect GeometryCaret(VisualCaret caret)
    {
        if (_pagedLayout is { } pages) return ToSurface(pages.ClipCaret(pages.Caret(GeometryStoryId, caret, GeometryStoryPage), GeometryStoryId, caret, GeometryStoryPage));
        var bounds = _layout.Caret(caret);
        if (_layout.At(caret.Position)?.Clip is { } clip) bounds = bounds.Intersect(clip);
        return ToSurface(bounds);
    }
    private VisualCaret GeometryHitTestCaret(Point point) => _pagedLayout is { } pages
        ? pages.HitTestStoryCaret(GeometryStoryId, ToDocument(point), GeometryStoryPage) : _layout.HitTestCaret(ToDocument(point));
    internal int GeometryHitTest(Point point) => GeometryHitTestCaret(point).Position;
    internal IEnumerable<Rect> GeometrySelectionRects(int start, int length) =>
        (_pagedLayout is { } pages ? pages.SelectionRects(GeometryStoryId, start, length, GeometryStoryPage) : _layout.SelectionRects(start, length)).Select(ToSurface);
    internal IEnumerable<(int Start, int End, Rect Bounds)> GeometryRanges() => _pagedLayout is { } pages
        ? ActivePageFragments.Select(fragment => (fragment.Start, fragment.Start + fragment.Length, ToSurface(fragment.Bounds)))
        : _layout.Paragraphs.Select(visual => (visual.TextStart, visual.TextEnd, ToSurface(visual.Bounds)));
    internal (int Start, int End) GeometryLineRange(int position)
    {
        int start, end, paragraphEnd;
        if (_pagedLayout is { } pages)
        {
            var fragment = ActivePageFragments.LastOrDefault(fragment => fragment.Start <= position && fragment.Start + fragment.Length >= position)
                ?? ActivePageFragments.First();
            start = fragment.Start; end = start + fragment.Length;
            paragraphEnd = Editor!.Session.Index.ById(fragment.ParagraphId).End;
        }
        else
        {
            var visual = _layout.At(position)!;
            using var lease = visual.Acquire();
            var lineIndex = lease.Layout.GetLineIndexFromCharacterIndex(position - visual.TextStart, false);
            var line = lease.Layout.TextLines[Math.Clamp(lineIndex, 0, visual.Page.LineCount - 1)];
            start = visual.TextStart + line.FirstTextSourceIndex; end = start + line.Length;
            paragraphEnd = visual.Position.End;
        }
        var documentLength = Editor!.Session.Index.Length;
        if (end == paragraphEnd && end < documentLength) end++;
        return (start, Math.Min(documentLength, end));
    }
    internal double PagedCaretColumnX => (_pagedLayout?.CaretColumnX(GeometryStoryId, CurrentVisualCaret, GeometryStoryPage) ?? 0) * ViewZoom;
    internal void MovePagedCaret(bool down, bool page, double preferredX, bool extend)
    {
        if (_pagedLayout is not { } snapshot) return;
        if (page && GeometryStoryId != Guid.Empty && GeometryStoryPage >= 0)
        {
            var instances = snapshot.StoryRegions.Where(r => r.StoryId == GeometryStoryId && r.Bounds.Height > 0).OrderBy(r => r.PageIndex);
            var target = down ? instances.FirstOrDefault(r => r.PageIndex > GeometryStoryPage) : instances.LastOrDefault(r => r.PageIndex < GeometryStoryPage);
            if (target is not null)
            {
                SetStoryInstanceContext(target);
                Editor!.ActivateStoryInstance(target.StoryId, target.PageIndex);
                SelectVisualCaret(CurrentVisualCaret, extend);
                Editor.GoToPage(target.PageIndex);
            }
            return;
        }
        if (page && Editor?.ViewMode == DocumentViewMode.Draft)
        {
            var caret = snapshot.Caret(GeometryStoryId, CurrentVisualCaret, GeometryStoryPage);
            var x = caret.X - snapshot.CaretColumnX(GeometryStoryId, CurrentVisualCaret, GeometryStoryPage) + preferredX / ViewZoom;
            var distance = Math.Max(40, Editor.Scroller?.Viewport.Height ?? 300) / ViewZoom;
            SelectVisualCaret(snapshot.HitTestCaret(new Point(x, caret.Center.Y + (down ? distance : -distance))), extend);
        }
        else SelectVisualCaret(page ? snapshot.MovePageCaret(GeometryStoryId, CurrentVisualCaret, down, preferredX / ViewZoom, GeometryStoryPage)
            : snapshot.MoveVerticalCaret(GeometryStoryId, CurrentVisualCaret, down, preferredX / ViewZoom, GeometryStoryPage), extend);
    }
    internal void ResetVerticalNavigation()
    {
        if (_inputContext is not null) _inputContext.PreferredCaretX = null;
    }
    private VisualCaret GeometryMoveCaret(VisualCaret caret, bool right, bool word) => _pagedLayout is { } pages
        ? word ? pages.MoveWordCaret(GeometryStoryId, caret, right, GeometryStoryPage) : pages.MoveCaret(GeometryStoryId, caret, right, GeometryStoryPage)
        : word ? _layout.MoveWordCaret(caret, right) : _layout.MoveCaret(caret, right);
    private VisualCaret GeometryLineBoundary(VisualCaret caret, bool end) => _pagedLayout is { } pages
        ? pages.LineBoundary(GeometryStoryId, caret, end, GeometryStoryPage) : _layout.LineBoundary(caret, end);
    private VisualCaret GeometryCollapseSelection(VisualCaret anchor, VisualCaret active, bool right) => _pagedLayout is { } pages
        ? pages.CollapseSelection(GeometryStoryId, anchor, active, right, GeometryStoryPage) : _layout.CollapseSelection(anchor, active, right);
    internal IReadOnlyList<TableCellVisual> GeometryTableCells() =>
        (_pagedLayout is { } pages ? pages.TableCells(GeometryStoryId, GeometryStoryPage) : _layout.TableCells()).Select(cell => cell with
        { Bounds = ToSurface(cell.Bounds), Clip = cell.Clip is { } clip ? ToSurface(clip) : null }).ToArray();
    internal TableCellVisual? GeometryHitTestTableCell(Point point, Guid? tableId = null) => GeometryTableCells()
        .LastOrDefault(cell => (tableId is null || cell.Table.Id == tableId) && cell.VisibleBounds.Contains(point));
    internal IEnumerable<InlineVisual> GeometryInlineVisuals() =>
        (_pagedLayout is { } pages ? pages.InlineVisuals() : _layout.InlineVisuals()).Select(visual => visual with
        { Bounds = ToSurface(visual.Bounds), Clip = visual.Clip is { } clip ? ToSurface(clip) : null });
}
