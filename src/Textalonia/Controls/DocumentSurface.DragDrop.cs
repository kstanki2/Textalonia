using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace Textalonia.Controls;

public partial class DocumentSurface
{
    private int? _dropOffset;
    private int _dropRevision;
    private VisualCaret _dropCaret;
    private bool _contentDragRunning;
    // Like Avalonia DoDragDropAsync, an injected starter takes ownership of the data.
    internal Func<PointerPressedEventArgs, IDataTransfer, DragDropEffects, Task<DragDropEffects>>? NativeDragStarter { get; set; }
    internal bool HasDropPreview => _dropOffset is not null;
    internal VisualCaret? ContentDropCaret => _dropOffset is null ? null : _dropCaret;

    private void InitializeDragDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnContentDragOver);
        AddHandler(DragDrop.DragOverEvent, OnContentDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropPreview());
        AddHandler(DragDrop.DropEvent, OnContentDrop);
    }

    internal bool StartContentDrag(PointerPressedEventArgs initiatingPress)
    {
        if (_contentDragRunning || Editor is not { } editor || editor.Session.CaptureContentDrag() is not { } snapshot) return false;
        _contentDragRunning = true;
        _ = RunContentDragAsync(editor, initiatingPress, snapshot);
        return true;
    }
    private async Task RunContentDragAsync(TextaloniaEditor editor, PointerPressedEventArgs initiatingPress, Editing.ContentDragSnapshot snapshot)
    {
        var token = TextaloniaEditor.RegisterContentDrag(snapshot);
        try
        {
            // Avalonia owns and disposes drag data after native completion, including
            // cancellation. Disposing it here would violate that ownership contract.
            var data = editor.CreateContentDragData(snapshot, token);
            var effects = editor.IsReadOnly ? DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Move;
            var start = NativeDragStarter ?? DragDrop.DoDragDropAsync;
            await start(initiatingPress, data, effects);
            // Only our target's successful DropContent removes source content. An external
            // target returning Move, cancellation, or a failed platform call cannot delete it.
        }
        catch (Exception error) { editor.ReportError(error); }
        finally
        {
            TextaloniaEditor.FinishContentDrag(token);
            _contentDragRunning = false;
            ClearDropPreview();
        }
    }

    private void OnContentDragOver(object? sender, DragEventArgs e)
    {
        try
        {
            CancelComposition();
            if (Editor is not { } editor)
            { ClearDropPreview(); e.DragEffects = DragDropEffects.None; return; }
            EnsureLayout(Bounds.Width);
            if (editor.LayoutError is not null)
            { ClearDropPreview(); e.DragEffects = DragDropEffects.None; return; }
            // Keep the hit's visual affinity while retaining a logical insertion offset.
            // The preview must not alter the editor's selection or pointer affinity.
            var hit = _layout.HitTestCaret(e.GetPosition(this));
            var offset = hit.Position;
            var revision = editor.Session.Revision;
            var effect = editor.GetContentDropEffect(e.DataTransfer, offset, revision, e.KeyModifiers, e.DragEffects);
            if (effect == DragDropEffects.None) ClearDropPreview();
            else
            {
                _dropOffset = offset; _dropRevision = revision;
                _dropCaret = hit;
                InvalidateVisual();
            }
            e.DragEffects = effect; e.Handled = true;
        }
        catch (Exception error) { ClearDropPreview(); e.DragEffects = DragDropEffects.None; Editor?.ReportError(error); }
    }
    private void OnContentDrop(object? sender, DragEventArgs e)
    {
        var offset = _dropOffset; var revision = _dropRevision;
        ClearDropPreview();
        try
        {
            e.DragEffects = offset is { } position && Editor is { } editor
                ? editor.DropContent(e.DataTransfer, position, revision, e.KeyModifiers, e.DragEffects) : DragDropEffects.None;
            e.Handled = true;
            if (e.DragEffects != DragDropEffects.None) Focus();
        }
        catch (Exception error) { e.DragEffects = DragDropEffects.None; Editor?.ReportError(error); }
    }
    internal void ClearDropPreview()
    {
        if (_dropOffset is null) return;
        _dropOffset = null; _dropCaret = default; InvalidateVisual();
    }
    private void RenderDropPreview(DrawingContext context)
    {
        if (_dropOffset is null || Editor is not { IsReadOnly: false } editor || editor.Session.Revision != _dropRevision) return;
        var viewport = _viewport.Width > 0 && _viewport.Height > 0 ? _viewport : new Rect(Bounds.Size);
        var visual = _layout.Paragraphs.FirstOrDefault(p => _dropCaret.Position >= p.TextStart &&
            (_dropCaret.Position < p.TextEnd || _dropCaret.Position == p.TextEnd && p.TextEnd == p.Position.End) && p.Bounds.Intersects(viewport));
        if (visual is null) return;
        // Resolve against the current layout so wrapping, resize, and viewport changes
        // never paint the stale rectangle captured by an earlier DragOver event.
        try
        {
            var caret = _layout.Caret(_dropCaret).WithWidth(2).Intersect(viewport);
            if (visual.Clip is { } clip) caret = caret.Intersect(clip);
            if (caret.Width > 0 && caret.Height > 0) context.FillRectangle(editor.Foreground ?? Brushes.Black, caret);
        }
        catch (ShapingLimitExceededException error) { ClearDropPreview(); RejectLayout(error); }
    }
}
