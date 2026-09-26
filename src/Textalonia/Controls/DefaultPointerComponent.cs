using Avalonia.Input;

namespace Textalonia.Controls;

/// <summary>Standard pointer selection and hyperlink activation. Detach releases pointer capture.</summary>
public class DefaultPointerComponent : DocumentInputComponent, IPointerComponent
{
    private bool _dragging;
    private IPointer? _pointer;
    protected override void OnDetached()
    {
        _dragging = false;
        if (_pointer?.Captured == Context.Surface) _pointer.Capture(null);
        _pointer = null;
    }
    public virtual void PointerPressed(PointerPressedEventArgs e)
    {
        if (Context.Editor is null) return;
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
        var link = paragraph.Paragraph.StyleAt(position - paragraph.Start).Hyperlink;
        if (link is not null && (Context.Editor.IsReadOnly || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        { Context.Editor.OpenLink(link); e.Handled = true; return; }
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
        Context.ActivateInputPane();
    }
    public virtual void PointerMoved(PointerEventArgs e)
    {
        if (!_dragging || Context.Editor is null || e.Pointer.Captured != Context.Surface) return;
        Context.Surface.EnsureLayout(Context.Surface.Bounds.Width);
        if (Context.TryHitTest(e.GetPosition(Context.Surface), out var position)) Context.Editor.Session.Select(Context.Editor.Session.Selection.Anchor, position);
    }
    public virtual void PointerReleased(PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false; e.Pointer.Capture(null); e.Handled = true;
    }
    public virtual void PointerCaptureLost(PointerCaptureLostEventArgs e)
    { _dragging = false; }

}
