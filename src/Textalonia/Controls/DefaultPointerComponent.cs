using Textalonia.Model;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Textalonia.Editing;

namespace Textalonia.Controls;

/// <summary>Standard pointer selection and hyperlink activation. Detach releases pointer capture.</summary>
public class DefaultPointerComponent : DocumentInputComponent, IPointerComponent
{
    private bool _dragging;
    private IPointer? _pointer;
    private SelectionAutoScroller? _autoscroll;
    internal SelectionAutoScroller? AutoScroller => _autoscroll;
    private TouchSelectionController? _touch;
    private TablePointerController? _table;
    internal TouchSelectionController? TouchSelection => _touch;
    private PointerPressedEventArgs? _pendingContentDrag;
    private Point _contentDragStart;
    private int _contentClickPosition;
    private int _contentPressRevision;
    private TextSelection _contentPressSelection;
    internal void RenderInteractionAdorners(DrawingContext drawing)
    {
        _table?.Render(drawing);
        _touch?.Render(drawing);
    }
    internal void CancelInteractions()
    {
        _table?.Cancel();
        _touch?.Cancel();
        CancelGesture();
    }
    protected override void OnAttached()
    {
        _autoscroll = new SelectionAutoScroller(Context, () => _pointer?.Captured == Context.Surface, CancelGesture);
        _touch = new TouchSelectionController(Context);
        _table = new TablePointerController(Context);
        Context.Surface.LostFocus += FocusLost;
    }
    private void FocusLost(object? sender, FocusChangedEventArgs e) => CancelInteractions();
    private void CancelGesture()
    {
        _dragging = false;
        _pendingContentDrag = null;
        _autoscroll?.Stop();
        var pointer = _pointer;
        _pointer = null;
        if (pointer?.Captured == Context.Surface) pointer.Capture(null);
    }
    protected override void OnDetached()
    {
        Context.Surface.LostFocus -= FocusLost;
        CancelGesture();
        _autoscroll = null;
        _touch?.Dispose(); _touch = null;
        _table?.Dispose(); _table = null;
    }
    public virtual void PointerPressed(PointerPressedEventArgs e)
    {
        if (Context.Editor is null) return;
        if (_table?.Pressed(e) == true) return;
        if (_touch?.Pressed(e) == true) return;
        var properties = e.GetCurrentPoint(Context.Surface).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return;
        Context.Surface.Focus(); Context.CancelComposition(); Context.Surface.EnsureLayout(Context.Surface.Bounds.Width);
        if (!Context.TryHitTest(e.GetPosition(Context.Surface), out var position)) return;
        var session = Context.Editor.Session;
        if (properties.IsRightButtonPressed)
        {
            if (position < session.Selection.Start || position > session.Selection.End) session.Select(position, position);
            return;
        }
        var paragraph = session.Index.At(position);
        var link = new DocumentStyleResolver(session.Document).ResolveText(paragraph.Paragraph,
            paragraph.Paragraph.StyleAt(position - paragraph.Start)).Hyperlink;
        if (link is not null && (Context.Editor.IsReadOnly || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        { Context.Editor.OpenLink(link); e.Handled = true; return; }
        if (e.ClickCount == 1 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) &&
            !session.Selection.IsEmpty && position >= session.Selection.Start && position < session.Selection.End)
        {
            _pendingContentDrag = e;
            _contentPressRevision = session.Revision;
            _contentPressSelection = session.Selection;
            _contentDragStart = e.GetPosition(Context.Surface);
            _contentClickPosition = position;
            _pointer = e.Pointer;
            e.Pointer.Capture(Context.Surface);
            e.Handled = true;
            return;
        }
        if (e.ClickCount >= 3) session.Select(paragraph.Start, paragraph.End);
        else if (e.ClickCount == 2)
        {
            var start = position;
            while (start > paragraph.Start && !char.IsWhiteSpace(session.Index.CharAt(start - 1))) start = session.PreviousCaret(start);
            var end = position;
            while (end < paragraph.End && !char.IsWhiteSpace(session.Index.CharAt(end))) end = session.NextCaret(end);
            session.Select(start, end);
        }
        else session.Select(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? session.Selection.Anchor : position, position);
        Context.PreferredCaretX = null;
        _dragging = true; _pointer = e.Pointer; e.Pointer.Capture(Context.Surface); e.Handled = true;
        // A double/triple click keeps its complete range until motion begins.
        if (e.ClickCount == 1) _autoscroll?.Begin(session.Selection.Anchor, e.GetPosition(Context.Surface));
        Context.ActivateInputPane();
    }
    public virtual void PointerMoved(PointerEventArgs e)
    {
        if (_table?.Moved(e) == true) return;
        if (_touch?.Moved(e) == true) return;
        if (_pendingContentDrag is { } press)
        {
            if (!ReferenceEquals(e.Pointer, _pointer)) return;
            if (_pointer.Captured != Context.Surface || !e.GetCurrentPoint(Context.Surface).Properties.IsLeftButtonPressed ||
                Context.Session.Revision != _contentPressRevision || Context.Session.Selection != _contentPressSelection)
            { CancelGesture(); e.Handled = true; return; }
            if ((Math.Abs(e.GetPosition(Context.Surface).X - _contentDragStart.X) >= 6 || Math.Abs(e.GetPosition(Context.Surface).Y - _contentDragStart.Y) >= 6))
            {
                CancelGesture();
                Context.Surface.StartContentDrag(press);
            }
            e.Handled = true;
            return;
        }
        if (!_dragging || !ReferenceEquals(e.Pointer, _pointer) || e.Pointer.Captured != Context.Surface) return;
        if (!e.GetCurrentPoint(Context.Surface).Properties.IsLeftButtonPressed) { CancelGesture(); return; }
        if (_autoscroll?.IsActive != true) _autoscroll?.Begin(Context.Session.Selection.Anchor, e.GetPosition(Context.Surface));
        else _autoscroll.Update(e.GetPosition(Context.Surface));
    }
    public virtual void PointerReleased(PointerReleasedEventArgs e)
    {
        if (_table?.Released(e) == true) return;
        if (_touch?.Released(e) == true) return;
        if (_pendingContentDrag is not null)
        {
            if (!ReferenceEquals(e.Pointer, _pointer) || e.InitialPressMouseButton != MouseButton.Left) return;
            var position = _contentClickPosition;
            CancelGesture();
            if (Context.Session.Revision == _contentPressRevision && Context.Session.Selection == _contentPressSelection)
                Context.Session.Select(position, position);
            Context.ActivateInputPane();
            e.Handled = true;
            return;
        }
        if (!_dragging || !ReferenceEquals(e.Pointer, _pointer) || e.InitialPressMouseButton != MouseButton.Left) return;
        CancelGesture(); e.Handled = true;
    }
    public virtual void PointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _table?.CaptureLost(e); _touch?.CaptureLost(e);
        if (ReferenceEquals(e.Pointer, _pointer)) CancelGesture();
    }

}
