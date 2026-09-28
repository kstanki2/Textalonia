using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>Floating image gestures show geometry only until release, then commit one edit.</summary>
internal sealed class ImagePointerController : IDisposable
{
    internal bool IsActive => _anchor is not null;
    internal Rect PreviewBounds => _preview;
    private readonly DocumentInputContext _context;
    private IPointer? _pointer;
    private InlineVisual? _anchor;
    private Point _start;
    private Rect _preview;
    private int _revision;
    private bool _resize;
    private bool _changed;

    internal ImagePointerController(DocumentInputContext context)
    {
        _context = context;
        context.Session.Changed += SessionChanged;
    }

    private void SessionChanged(object? sender, EventArgs e)
    {
        if (_anchor is not null && (_context.Session.Revision != _revision || _context.Session.IsReadOnly ||
            _context.Session.Selection.Start != _anchor.Position || _context.Session.Selection.Length != 1)) Cancel();
    }

    internal bool Pressed(PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse) return false;
        var properties = e.GetCurrentPoint(_context.Surface).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return false;
        _context.Surface.EnsureLayout(_context.Surface.Bounds.Width);
        var point = e.GetPosition(_context.Surface);
        var visual = SelectedVisual();
        var resize = visual is not null && HandleBounds(visual.Bounds).Contains(point);
        if (!resize) visual = _context.Surface.HitTestImage(point);
        if (visual?.IsPositioned != true || visual.Descriptor.Placement is not { Anchor: not ImageAnchorKind.Inline } placement) return false;
        // Alt permits selecting an image behind text without swallowing ordinary text selection.
        if (placement.Wrap == ImageWrapKind.BehindText && !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !resize) return false;
        _context.Surface.Focus(); _context.CancelComposition(); _context.Editor.ClearTableCellSelection();
        _context.Session.Select(visual.Position, visual.Position + 1);
        _context.PreferredCaretX = null;
        if (properties.IsLeftButtonPressed && !_context.Session.IsReadOnly)
        {
            _revision = _context.Session.Revision; _pointer = e.Pointer; _anchor = visual;
            _start = point; _preview = visual.Bounds; _resize = resize; _changed = false;
            e.Pointer.Capture(_context.Surface);
        }
        e.Handled = true; return true;
    }

    internal bool Moved(PointerEventArgs e)
    {
        if (_anchor is null || e.Pointer != _pointer || _pointer.Captured != _context.Surface) return false;
        if (!e.GetCurrentPoint(_context.Surface).Properties.IsLeftButtonPressed) { Cancel(); return true; }
        var delta = e.GetPosition(_context.Surface) - _start;
        _changed = Math.Abs(delta.X) >= 2 || Math.Abs(delta.Y) >= 2;
        if (_resize)
        {
            var zoom = _context.Surface.ViewZoom;
            var width = Math.Clamp(_anchor.Bounds.Width + delta.X, zoom, 10000 * zoom);
            var height = Math.Clamp(_anchor.Bounds.Height + delta.Y, zoom, 10000 * zoom);
            if (_anchor.Descriptor.Placement!.LockAspectRatio)
            {
                var ratio = _anchor.Descriptor.Width / _anchor.Descriptor.Height;
                if (Math.Abs(delta.X) >= Math.Abs(delta.Y)) height = width / ratio;
                else width = height * ratio;
                var scale = Math.Min(1, 10000 * zoom / Math.Max(width, height));
                width *= scale; height *= scale;
            }
            _preview = new(_anchor.Bounds.Position, new Size(width, height));
        }
        else _preview = _anchor.Bounds.Translate(delta);
        _context.InvalidateVisual(); e.Handled = true; return true;
    }

    internal bool Released(PointerReleasedEventArgs e)
    {
        if (_anchor is null || e.Pointer != _pointer || e.InitialPressMouseButton != MouseButton.Left) return false;
        var anchor = _anchor; var preview = _preview; var resize = _resize; var changed = _changed;
        var valid = _context.Session.Revision == _revision && !_context.Session.IsReadOnly;
        Cancel();
        if (changed && valid)
        {
            var placement = anchor.Descriptor.Placement!;
            var zoom = _context.Surface.ViewZoom;
            _context.Editor.Run(() =>
            {
                if (resize) _context.Editor.UpdateImage(anchor.Descriptor.Id, placement, preview.Width / zoom, preview.Height / zoom);
                else _context.Editor.UpdateImage(anchor.Descriptor.Id, placement with
                {
                    X = Math.Clamp(placement.X + (preview.X - anchor.Bounds.X) / zoom, -100000, 100000),
                    Y = Math.Clamp(placement.Y + (preview.Y - anchor.Bounds.Y) / zoom, -100000, 100000)
                });
            });
        }
        e.Handled = true; return true;
    }

    internal void CaptureLost(PointerCaptureLostEventArgs e) { if (_pointer == e.Pointer) Cancel(); }
    internal void Cancel()
    {
        _anchor = null; _changed = false;
        var pointer = _pointer; _pointer = null;
        if (pointer?.Captured == _context.Surface) pointer.Capture(null);
        _context.InvalidateVisual();
    }

    private InlineVisual? SelectedVisual() => _context.Surface.GeometryInlineVisuals().LastOrDefault(visual =>
        visual.IsPositioned && visual.Descriptor.Placement is { Anchor: not ImageAnchorKind.Inline } && _context.Surface.IsInlineSelected(visual));
    private static Rect HandleBounds(Rect bounds) => new(bounds.Right - 4, bounds.Bottom - 4, 8, 8);

    internal void Render(DrawingContext drawing)
    {
        var visual = _anchor ?? SelectedVisual();
        if (visual is null) return;
        var bounds = _anchor is null ? visual.Bounds : _preview;
        var pen = new Pen(Brushes.DodgerBlue, 1, DashStyle.Dash);
        drawing.DrawRectangle(null, pen, bounds);
        if (!_context.Session.IsReadOnly) drawing.DrawRectangle(Brushes.White, new Pen(Brushes.DodgerBlue, 1), HandleBounds(bounds));
    }

    public void Dispose() { _context.Session.Changed -= SessionChanged; Cancel(); }
}
