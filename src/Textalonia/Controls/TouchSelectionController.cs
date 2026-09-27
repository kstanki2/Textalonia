using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Textalonia.Controls;

/// <summary>
/// Touch-only selection state. A pending contact is deliberately unhandled and
/// uncaptured so the ancestor ScrollViewer can win a pan. All distances are DIP.
/// </summary>
internal sealed class TouchSelectionController : IDisposable
{
    internal const double MovementTolerance = 12;
    internal const double HandleHitRadius = 22;
    private readonly DocumentInputContext _context;
    private readonly DispatcherTimer _holdTimer;
    private readonly SelectionAutoScroller _autoscroll;
    private Point _dragOffset;
    private bool _caretDrag;
    private IPointer? _pointer;
    private Point _pressedAt;
    private int _revision;
    private int _fixedEndpoint;
    private bool _pending;
    private bool _dragging;
    private bool _openContextOnRelease;
    private bool _disposed;
    internal bool HandlesVisible { get; private set; }
    internal bool IsPending => _pending;
    internal bool IsDragging => _dragging;

    internal TouchSelectionController(DocumentInputContext context)
    {
        _context = context;
        _autoscroll = new SelectionAutoScroller(context, () => _pointer?.Captured == context.Surface);
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _holdTimer.Tick += HoldElapsed;
        context.Surface.LostFocus += LostFocus;
        context.Surface.KeyDown += KeyDown;
        context.Surface.EffectiveViewportChanged += ViewportChanged;
        context.Editor.DocumentChanged += DocumentChanged;
    }

    internal bool Pressed(PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch)
        {
            Cancel(hideHandles: true);
            return false;
        }
        if (_disposed) return true;
        // A second finger cancels selection ownership; leave multi-touch to the host.
        if (_pointer is not null) { Cancel(); return true; }
        _context.Surface.EnsureLayout(_context.Surface.Bounds.Width);
        var point = e.GetPosition(_context.Surface);
        var handles = HandlePositions();
        _pointer = e.Pointer;
        _pressedAt = point;
        _revision = _context.Session.Revision;
        if (HandlesVisible && HitHandle(point, handles, out var start))
        {
            var selection = _context.Session.Selection;
            _fixedEndpoint = start ? selection.End : selection.Start;
            _caretDrag = selection.IsEmpty;
            var draggedEndpoint = start ? selection.Start : selection.End;
            var endpoint = _context.Surface.SelectionEndpointCaret(!selection.IsEmpty && draggedEndpoint == selection.Anchor);
            _dragOffset = point - (endpoint?.Center ?? point);
            _context.Surface.Focus();
            _context.CancelComposition();
            _dragging = true;
            e.Pointer.Capture(_context.Surface);
            e.Handled = true;
        }
        else
        {
            _pending = true;
            _holdTimer.Start();
        }
        return true;
    }

    internal bool Moved(PointerEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch) return false;
        if (_disposed || !ReferenceEquals(_pointer, e.Pointer)) return true;
        var point = e.GetPosition(_context.Surface);
        if (_pending && Distance(point, _pressedAt) > MovementTolerance)
        {
            Cancel();
            return true;
        }
        if (_dragging)
        {
            _openContextOnRelease = false;
            point -= _dragOffset;
            var viewport = Viewport;
            // Re-read the viewport after keyboard opening, resize or rotation.
            if (_caretDrag)
            {
                point = new Point(Math.Clamp(point.X, viewport.Left, viewport.Right),
                    Math.Clamp(point.Y, viewport.Top, viewport.Bottom));
                if (_context.TryHitTest(point, out _)) _context.Surface.MoveVisualCaretToPoint(point, extend: false);
            }
            else if (!_autoscroll.IsActive) _autoscroll.Begin(_fixedEndpoint, point);
            else _autoscroll.Update(point);
            e.Handled = true;
        }
        return true;
    }

    internal bool Released(PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch) return false;
        if (_disposed || !ReferenceEquals(_pointer, e.Pointer)) return true;
        var openContext = _openContextOnRelease;
        var selected = _dragging;
        if (_pending && _context.Session.Revision == _revision &&
            Distance(e.GetPosition(_context.Surface), _pressedAt) <= MovementTolerance)
        {
            _context.Surface.Focus();
            _context.CancelComposition();
            _context.Surface.EnsureLayout(_context.Surface.Bounds.Width);
            if (_context.TryHitTest(e.GetPosition(_context.Surface), out var position))
            {
                _context.Surface.MoveVisualCaretToPoint(e.GetPosition(_context.Surface), extend: false);
                HandlesVisible = true;
                selected = true;
                if (!_context.Editor.IsReadOnly) _context.ActivateInputPane();
            }
        }
        Cancel();
        if (selected) e.Handled = true;
        if (openContext) _context.Surface.ContextMenu?.Open(_context.Surface);
        _context.InvalidateVisual();
        return true;
    }

    internal void CaptureLost(PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(_pointer, e.Pointer)) Cancel();
    }

    private void HoldElapsed(object? sender, EventArgs e) => CompleteLongPress();

    // Separate from clock scheduling so headless tests qualify the transition
    // without pretending their synthetic contacts are native device evidence.
    internal void CompleteLongPress()
    {
        _holdTimer.Stop();
        if (_disposed || !_pending || _pointer is null || _context.Session.Revision != _revision) return;
        _context.Surface.Focus();
        _context.CancelComposition();
        _context.Surface.EnsureLayout(_context.Surface.Bounds.Width);
        if (!_context.TryHitTest(_pressedAt, out var position)) { Cancel(); return; }
        var session = _context.Session;
        var paragraph = session.Index.At(position);
        var start = position;
        var end = position;
        while (start > paragraph.Start && !char.IsWhiteSpace(session.Index.CharAt(start - 1)))
            start = session.PreviousCaret(start);
        while (end < paragraph.End && !char.IsWhiteSpace(session.Index.CharAt(end)))
            end = session.NextCaret(end);
        if (start == end && end < paragraph.End) end = session.NextCaret(end);
        session.Select(start, end);
        _context.PreferredCaretX = null;
        _pending = false;
        _dragging = true;
        _fixedEndpoint = start;
        _caretDrag = false;
        _dragOffset = default;
        _openContextOnRelease = true;
        HandlesVisible = true;
        _pointer.Capture(_context.Surface);
        _context.InvalidateVisual();
    }

    private Rect Viewport => _context.Surface.InteractionViewport;

    internal (Point? Start, Point? End) HandlePositions()
    {
        if (!HandlesVisible || _disposed || _context.IsComposing || _context.Editor.LayoutError is not null) return default;
        var selection = _context.Session.Selection;
        return (selection.IsEmpty ? null : Position(selection.Start), Position(selection.End));
    }

    private Point? Position(int position)
    {
        var surface = _context.Surface;
        var viewport = Viewport;
        // Never materialize distant endpoints during rendering.
        if (!surface.Layout.Paragraphs.Any(p => position >= p.TextStart && position <= p.TextEnd && p.Bounds.Intersects(viewport)))
            return null;
        try
        {
            var selection = _context.Session.Selection;
            var endpoint = surface.SelectionEndpointCaret(!selection.IsEmpty && position == selection.Anchor);
            if (endpoint is not { } caret || !caret.Intersects(viewport)) return null;
            var visual = surface.Layout.At(position);
            if (visual?.Clip is { } clip) viewport = viewport.Intersect(clip);
            if (viewport.Width < 16 || viewport.Height < 16) return null;
            return new Point(Math.Clamp(caret.Center.X, viewport.Left + 8, Math.Max(viewport.Left + 8, viewport.Right - 8)),
                Math.Clamp(caret.Bottom + 7, viewport.Top + 8, Math.Max(viewport.Top + 8, viewport.Bottom - 8)));
        }
        catch (ShapingLimitExceededException error) { surface.RejectLayout(error); return null; }
    }

    private static double Distance(Point first, Point second) => Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static bool HitHandle(Point point, (Point? Start, Point? End) handles, out bool start)
    {
        var startDistance = handles.Start is { } a ? Distance(a, point) : double.PositiveInfinity;
        var endDistance = handles.End is { } b ? Distance(b, point) : double.PositiveInfinity;
        start = startDistance < endDistance;
        return Math.Min(startDistance, endDistance) <= HandleHitRadius;
    }

    internal void Render(DrawingContext drawing)
    {
        if (_disposed || (!_context.Surface.IsFocused && _context.Surface.ContextMenu?.IsOpen != true)) return;
        var (start, end) = HandlePositions();
        foreach (var point in new[] { start, end })
            if (point is { } center) drawing.DrawEllipse(Brushes.DodgerBlue, new Pen(Brushes.White, 1), center, 7, 7);
    }

    internal void Cancel(bool hideHandles = false)
    {
        _holdTimer.Stop();
        _autoscroll.Stop();
        _pending = false;
        _dragging = false;
        _openContextOnRelease = false;
        var pointer = _pointer;
        _pointer = null;
        if (pointer?.Captured == _context.Surface) pointer.Capture(null);
        if (hideHandles) HandlesVisible = false;
        _context.InvalidateVisual();
    }

    private void LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Cancel();
    private void KeyDown(object? sender, KeyEventArgs e) => Cancel(hideHandles: true);
    private void DocumentChanged(object? sender, EventArgs e) => Cancel(hideHandles: true);
    private void ViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (_pending) Cancel();
        _context.InvalidateVisual();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Cancel(hideHandles: true);
        _disposed = true;
        _holdTimer.Tick -= HoldElapsed;
        _context.Surface.LostFocus -= LostFocus;
        _context.Surface.KeyDown -= KeyDown;
        _context.Surface.EffectiveViewportChanged -= ViewportChanged;
        _context.Editor.DocumentChanged -= DocumentChanged;
    }
}
