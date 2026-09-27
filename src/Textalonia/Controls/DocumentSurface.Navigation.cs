using Avalonia;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class DocumentSurface
{
    private VisualCaret? _visualCaret;
    private VisualCaret? _visualAnchor;
    private FlowDocument? _visualCaretDocument;

    private VisualCaret CurrentVisualCaret
    {
        get
        {
            var position = DisplayCaret;
            if (_composition.IsComposing || !ReferenceEquals(_visualCaretDocument, Editor?.Document) ||
                _visualCaret is not { } caret || caret.Position != position)
                return VisualCaret.Logical(position);
            return caret;
        }
    }

    internal Rect? SelectionEndpointCaret(bool anchor)
    {
        if (Editor is null) return null;
        EnsureLayout(Bounds.Width);
        if (Editor.LayoutError is not null) return null;
        var selection = Editor.Session.Selection;
        var caret = !anchor || selection.IsEmpty ? CurrentVisualCaret :
            ReferenceEquals(_visualCaretDocument, Editor.Document) && _visualAnchor is { } stored && stored.Position == selection.Anchor
                ? stored : VisualCaret.Logical(selection.Anchor);
        try
        {
            var bounds = _layout.Caret(caret);
            if (_layout.At(caret.Position)?.Clip is { } clip) bounds = bounds.Intersect(clip);
            return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
        }
        catch (ShapingLimitExceededException error) { RejectLayout(error); return null; }
    }
    internal void RememberPointerCaret(VisualCaret caret)
    {
        if (Editor is null) return;
        if (Editor.Session.Selection.IsEmpty) _visualAnchor = CurrentVisualCaret;
        _visualCaretDocument = Editor.Document;
        _visualCaret = caret;
    }

    internal void SelectVisualCaret(VisualCaret caret, bool extend)
    {
        if (Editor is null) return;
        var selection = Editor.Session.Selection;
        if (!extend) _visualAnchor = caret;
        else if (selection.IsEmpty) _visualAnchor = CurrentVisualCaret;
        _visualCaret = caret;
        _visualCaretDocument = Editor.Document;
        Editor.Session.Select(extend ? selection.Anchor : caret.Position, caret.Position);
    }

    internal void MoveVisualCaret(bool right, bool extend, bool word = false)
    {
        if (Editor is null || Editor.LayoutError is not null) return;
        var selection = Editor.Session.Selection;
        var caret = CurrentVisualCaret;
        var anchor = ReferenceEquals(_visualCaretDocument, Editor.Document) && _visualAnchor is { } stored && stored.Position == selection.Anchor
            ? stored : VisualCaret.Logical(selection.Anchor);
        SelectVisualCaret(!extend && !selection.IsEmpty ? _layout.CollapseSelection(anchor, caret, right) : word ? _layout.MoveWordCaret(caret, right) : _layout.MoveCaret(caret, right), extend);
    }

    internal void MoveVisualLineBoundary(bool end, bool extend)
    {
        if (Editor?.LayoutError is null) SelectVisualCaret(_layout.LineBoundary(CurrentVisualCaret, end), extend);
    }

    internal bool MoveVisualCaretToPoint(Point point, bool extend)
    {
        if (Editor?.LayoutError is not null) return false;
        var caret = _layout.HitTestCaret(point);
        SelectVisualCaret(caret, extend);
        return true;
    }
}
