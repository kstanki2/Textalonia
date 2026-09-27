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
        if (_pagedLayout is { } pages) return ToSurface(pages.ClipCaret(pages.Caret(caret), caret));
        var bounds = _layout.Caret(caret);
        if (_layout.At(caret.Position)?.Clip is { } clip) bounds = bounds.Intersect(clip);
        return ToSurface(bounds);
    }
    private VisualCaret GeometryHitTestCaret(Point point) => _pagedLayout is { } pages
        ? pages.HitTestCaret(ToDocument(point)) : _layout.HitTestCaret(ToDocument(point));
    internal int GeometryHitTest(Point point) => GeometryHitTestCaret(point).Position;
    internal IEnumerable<Rect> GeometrySelectionRects(int start, int length) =>
        (_pagedLayout is { } pages ? pages.SelectionRects(start, length) : _layout.SelectionRects(start, length)).Select(ToSurface);
    internal IEnumerable<(int Start, int End, Rect Bounds)> GeometryRanges() => _pagedLayout is { } pages
        ? pages.Fragments.Select(fragment => (fragment.Start, fragment.Start + fragment.Length, ToSurface(fragment.Bounds)))
        : _layout.Paragraphs.Select(visual => (visual.TextStart, visual.TextEnd, ToSurface(visual.Bounds)));
    internal (int Start, int End) GeometryLineRange(int position)
    {
        int start, end, paragraphEnd;
        if (_pagedLayout is { } pages)
        {
            var fragment = pages.Fragments.LastOrDefault(fragment => fragment.Start <= position && fragment.Start + fragment.Length >= position)
                ?? pages.Fragments[0];
            start = fragment.Start; end = start + fragment.Length;
            paragraphEnd = new DocumentIndex(_layoutDocument!).ById(fragment.ParagraphId).End;
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
    internal double PagedCaretColumnX => (_pagedLayout?.CaretColumnX(CurrentVisualCaret) ?? 0) * ViewZoom;
    internal void MovePagedCaret(bool down, bool page, double preferredX, bool extend)
    {
        if (_pagedLayout is not { } snapshot) return;
        if (page && Editor?.ViewMode == DocumentViewMode.Draft)
        {
            var caret = snapshot.Caret(CurrentVisualCaret);
            var x = caret.X - snapshot.CaretColumnX(CurrentVisualCaret) + preferredX / ViewZoom;
            var distance = Math.Max(40, Editor.Scroller?.Viewport.Height ?? 300) / ViewZoom;
            SelectVisualCaret(snapshot.HitTestCaret(new Point(x, caret.Center.Y + (down ? distance : -distance))), extend);
        }
        else SelectVisualCaret(page ? snapshot.MovePageCaret(CurrentVisualCaret, down, preferredX / ViewZoom)
            : snapshot.MoveVerticalCaret(CurrentVisualCaret, down, preferredX / ViewZoom), extend);
    }
    internal void ResetVerticalNavigation()
    {
        if (_inputContext is not null) _inputContext.PreferredCaretX = null;
    }
    private VisualCaret GeometryMoveCaret(VisualCaret caret, bool right, bool word) => _pagedLayout is { } pages
        ? word ? pages.MoveWordCaret(caret, right) : pages.MoveCaret(caret, right)
        : word ? _layout.MoveWordCaret(caret, right) : _layout.MoveCaret(caret, right);
    private VisualCaret GeometryLineBoundary(VisualCaret caret, bool end) => _pagedLayout is { } pages
        ? pages.LineBoundary(caret, end) : _layout.LineBoundary(caret, end);
    private VisualCaret GeometryCollapseSelection(VisualCaret anchor, VisualCaret active, bool right) => _pagedLayout is { } pages
        ? pages.CollapseSelection(anchor, active, right) : _layout.CollapseSelection(anchor, active, right);
    internal IReadOnlyList<TableCellVisual> GeometryTableCells() =>
        (_pagedLayout is { } pages ? pages.TableCells() : _layout.TableCells()).Select(cell => cell with
        { Bounds = ToSurface(cell.Bounds), Clip = cell.Clip is { } clip ? ToSurface(clip) : null }).ToArray();
    internal TableCellVisual? GeometryHitTestTableCell(Point point, Guid? tableId = null) => GeometryTableCells()
        .LastOrDefault(cell => (tableId is null || cell.Table.Id == tableId) && cell.VisibleBounds.Contains(point));
    private IEnumerable<InlineVisual> GeometryInlineVisuals() =>
        (_pagedLayout is { } pages ? pages.InlineVisuals() : _layout.InlineVisuals()).Select(visual => visual with
        { Bounds = ToSurface(visual.Bounds), Clip = visual.Clip is { } clip ? ToSurface(clip) : null });
}
