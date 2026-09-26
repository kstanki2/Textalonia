using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>Native shaped-text surface used by the editor template.</summary>
public partial class DocumentSurface : Control
{
    private readonly DocumentLayout _layout = new();
    private readonly IKeyboardComponent _defaultKeyboard = new DefaultKeyboardComponent();
    private readonly IPointerComponent _defaultPointer = new DefaultPointerComponent();
    private readonly ICaretComponent _defaultCaret = new DefaultCaretComponent();
    private readonly ICompositionComponent _defaultComposition = new DefaultCompositionComponent();
    private IKeyboardComponent _keyboard = null!;
    private IPointerComponent _pointer = null!;
    private ICaretComponent _caret = null!;
    private ICompositionComponent _composition = null!;
    private readonly HashSet<IDocumentInputComponent> _attachedInputComponents = new(ReferenceEqualityComparer.Instance);
    private DocumentInputContext? _inputContext;
    private bool _isAttached;
    internal ICompositionComponent Composition => _composition;
    internal bool HasComposition => _composition.IsComposing;
    private TextaloniaEditor? _editor;
    private FlowDocument? _layoutDocument;
    private double _layoutWidth;
    private double _measuredLayoutHeight;
    private bool _dirty = true;
    private Rect _viewport;
    internal DocumentLayout Layout => _layout;

    public DocumentSurface()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        ClipToBounds = true;
        _keyboard = _defaultKeyboard; _pointer = _defaultPointer; _caret = _defaultCaret; _composition = _defaultComposition;
        _layout.AnchorShifted += ApplyAnchorAdjustment;
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) =>
        {
            if (!e.Handled && ReferenceEquals(e.Source, this) && _inputContext is not null && Editor is { IsReadOnly: false })
            { e.Client = _composition.Client; e.Handled = e.Client is not null; }
        });
        EffectiveViewportChanged += (_, e) =>
        {
            if (_viewport == e.EffectiveViewport) return;
            _viewport = e.EffectiveViewport; _dirty = true; InvalidateMeasure(); InvalidateVisual();
        };
    }

    public TextaloniaEditor? Editor
    {
        get => _editor;
        internal set
        {
            if (ReferenceEquals(_editor, value)) return;
            DetachInputComponents();
            ResetInlineViews();
            _editor = value;
            UpdateInputComponents();
            ContextMenu = value is null ? null : new ContextMenu
            {
                ItemsSource = new object[]
                {
                    new MenuItem { Header = "Undo", Command = value.UndoCommand },
                    new MenuItem { Header = "Redo", Command = value.RedoCommand },
                    new Separator(),
                    new MenuItem { Header = "Cut", Command = value.CutCommand },
                    new MenuItem { Header = "Copy", Command = value.CopyCommand },
                    new MenuItem { Header = "Paste", Command = value.PasteCommand },
                    new Separator(),
                    new MenuItem { Header = "Select all", Command = value.SelectAllCommand }
                }
            };
            Refresh();
        }
    }

    internal void Refresh(bool bringCaret = false, bool invalidateLayout = true)
    {
        var previousPreview = _composition.PreviewDocument;
        if (_inputContext is not null) _composition.Refresh();
        _dirty |= invalidateLayout || !ReferenceEquals(previousPreview, _composition.PreviewDocument);
        if (_inputContext is not null) _caret.Reset();
        InvalidateMeasure(); InvalidateVisual();
        if (bringCaret && IsFocused && Editor is not null)
            Dispatcher.UIThread.Post(() =>
            {
                if (Editor is not null && TopLevel.GetTopLevel(this) is not null)
                {
                    EnsureLayout(Bounds.Width);
                    var caret = CaretRectangle;
                    // Resolving a distant caret can refine the document extent.
                    // Publish that measurement before the scroll viewer clamps
                    // the request against its previous, estimated extent.
                    for (var pass = 0; pass < 4 && Editor.LayoutError is null &&
                        Math.Abs(_measuredLayoutHeight - _layout.Height) > .1; pass++)
                    {
                        InvalidateMeasure(); this.UpdateLayout();
                        caret = CaretRectangle;
                    }
                    if (Editor.LayoutError is null) this.BringIntoView(caret.Inflate(4));
                }
            }, DispatcherPriority.Loaded);
    }

    internal Rect CaretRectangle
    {
        get
        {
            EnsureLayout(Bounds.Width);
            if (Editor?.LayoutError is not null) return default;
            var height = _layout.Height;
            Rect caret;
            try { caret = _layout.Caret(DisplayCaret); }
            catch (ShapingLimitExceededException error) { RejectLayout(error); return default; }
            if (Math.Abs(height - _layout.Height) > .1)
                Dispatcher.UIThread.Post(InvalidateMeasure, DispatcherPriority.Loaded);
            return caret;
        }
    }
    private int DisplayCaret => _composition.CaretPosition ?? Editor?.Session.Selection.Active ?? 0;

    internal void EnsureLayout(double width)
    {
        if (Editor is null) return;
        width = double.IsFinite(width) && width > 48 ? width : 800;
        var document = _composition.PreviewDocument ?? Editor.Document;
        if (!_dirty && ReferenceEquals(_layoutDocument, document) && Math.Abs(_layoutWidth - width) < .1) return;
        _layoutDocument = document; _layoutWidth = width; _dirty = false;
        try
        {
            _layout.Build(document, width, Editor.FontFamily, Editor.Foreground ?? Brushes.Black,
                Editor.BorderBrush ?? Brushes.Gray, Editor.DocumentPadding,
                _viewport.Width > 0 && _viewport.Height > 0 ? _viewport : new Rect(0, Editor.Scroller?.Offset.Y ?? 0, width, 500),
                Editor.MaxShapingCharacters);
        }
        catch (ShapingLimitExceededException error) { RejectLayout(error); return; }
        Editor.SetLayoutError(null);
        ApplyAnchorAdjustment(_layout.AnchorAdjustment);
        UpdateInlineViews();
    }
    internal void RejectLayout(ShapingLimitExceededException error)
    {
        // No partial or approximate text geometry is exposed after rejection.
        // Retain the extent so an offscreen target cannot jump the scroll view.
        _layout.Clear(); ResetInlineViews();
        Editor?.SetLayoutError(error);
        InvalidateVisual();
    }
    private bool TryHitTest(Point point, out int position)
    {
        position = 0;
        if (Editor?.LayoutError is not null) return false;
        try { position = _layout.HitTest(point); return true; }
        catch (ShapingLimitExceededException error) { RejectLayout(error); return false; }
    }
    private void ApplyAnchorAdjustment(double adjustment)
    {
        if (Math.Abs(adjustment) > .1 && Editor?.Scroller is { } scroller)
            scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, scroller.Offset.Y + adjustment));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureLayout(availableSize.Width);
        _measuredLayoutHeight = _layout.Height;
        return new(double.IsFinite(availableSize.Width) ? availableSize.Width : _layout.Width, Math.Max(90, _measuredLayoutHeight));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Editor is null) return;
        EnsureLayout(Bounds.Width);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var viewport = _viewport.Width > 0 && _viewport.Height > 0 ? _viewport : new Rect(Bounds.Size);
        if (Editor.LayoutError is not null)
        {
            using var message = new TextLayout("This content exceeds the configured rendering limit.",
                new Typeface(Editor.FontFamily), 16, Editor.Foreground, textWrapping: TextWrapping.Wrap,
                maxWidth: Math.Max(20, viewport.Width - Editor.DocumentPadding.Left - Editor.DocumentPadding.Right));
            message.Draw(context, new Point(Editor.DocumentPadding.Left, viewport.Top + Editor.DocumentPadding.Top));
            return;
        }
        foreach (var decoration in _layout.Decorations)
        {
            if (!decoration.Bounds.Intersects(viewport)) continue;
            decoration.Draw(context);
        }
        foreach (var highlight in Editor.Highlights)
            foreach (var rect in _layout.SelectionRects(highlight.Start, highlight.Length))
                if (rect.Intersects(viewport)) context.FillRectangle(highlight.Brush, rect);
        var selection = Editor.Session.Selection;
        if (!HasComposition)
            foreach (var rect in _layout.SelectionRects(selection.Start, selection.Length))
                if (rect.Intersects(viewport)) context.FillRectangle(Editor.SelectionBrush, rect);
        foreach (var paragraph in _layout.Paragraphs)
        {
            if (!paragraph.Bounds.Intersects(viewport)) continue;
            paragraph.Draw(context, viewport);
            if (paragraph.Marker is not null)
            {
                var marker = new FormattedText(paragraph.Marker, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    DocumentLayout.Typeface(paragraph.Position.Paragraph.DefaultStyle, Editor.FontFamily), paragraph.Position.Paragraph.DefaultStyle.FontSize, Editor.Foreground);
                var origin = new Point(paragraph.Origin.X - marker.Width - 10, paragraph.Origin.Y);
                if (paragraph.Clip is { } clip)
                {
                    using var scope = context.PushClip(clip);
                    context.DrawText(marker, origin);
                }
                else context.DrawText(marker, origin);
            }
        }
        DrawInlineImages(context, viewport);
        if (Editor.Session.Index.Length == 0 && !HasComposition && Editor.Document.Blocks is [{ } block] && block is Paragraph)
        {
            using var placeholder = new TextLayout(Editor.PlaceholderText, new Typeface(Editor.FontFamily), 16,
                new SolidColorBrush(Color.FromArgb(135, 128, 128, 128)), maxWidth: Math.Max(20, Bounds.Width - Editor.DocumentPadding.Left - Editor.DocumentPadding.Right));
            placeholder.Draw(context, new Point(Editor.DocumentPadding.Left, Editor.DocumentPadding.Top));
        }
        // Rendering must not discover an offscreen caret's geometry: doing so
        // can refine prefix heights and invalidate scrolling during this pass.
        if (IsFocused && !Editor.IsReadOnly && _layout.Paragraphs.Any(p =>
            DisplayCaret >= p.TextStart && (DisplayCaret < p.TextEnd || DisplayCaret == p.TextEnd && p.TextEnd == p.Position.End) && p.Bounds.Intersects(viewport)))
        {
            var caret = CaretRectangle;
            var visual = _layout.Paragraphs.FirstOrDefault(p => DisplayCaret >= p.TextStart &&
                (DisplayCaret < p.TextEnd || DisplayCaret == p.TextEnd && p.TextEnd == p.Position.End));
            if (visual?.Clip is { } clip) caret = caret.Intersect(clip);
            if (caret.Width > 0 && caret.Height > 0) _caret.Render(context, caret, Editor.Foreground ?? Brushes.Black);
        }
    }

    internal void UpdateInputComponents()
    {
        if (Editor is null || !_isAttached) return;
        if (_inputContext is null)
        {
            _inputContext = new DocumentInputContext(this, Editor);
            _composition = Editor.CompositionComponent ?? _defaultComposition;
            _keyboard = Editor.KeyboardComponent ?? _defaultKeyboard;
            _pointer = Editor.PointerComponent ?? _defaultPointer;
            _caret = Editor.CaretComponent ?? _defaultCaret;
            try
            {
                AttachComponent(_composition); AttachComponent(_keyboard);
                AttachComponent(_pointer); AttachComponent(_caret);
            }
            catch { DetachInputComponents(); throw; }
        }
        else
        {
            ReplaceComponent(ref _composition, Editor.CompositionComponent ?? _defaultComposition);
            ReplaceComponent(ref _keyboard, Editor.KeyboardComponent ?? _defaultKeyboard);
            ReplaceComponent(ref _pointer, Editor.PointerComponent ?? _defaultPointer);
            ReplaceComponent(ref _caret, Editor.CaretComponent ?? _defaultCaret);
        }
        Refresh();
    }
    private void ReplaceComponent<T>(ref T current, T replacement) where T : IDocumentInputComponent
    {
        if (ReferenceEquals(current, replacement)) return;
        var previous = current;
        current = replacement;
        if (!ReferenceEquals(_composition, previous) && !ReferenceEquals(_keyboard, previous) &&
            !ReferenceEquals(_pointer, previous) && !ReferenceEquals(_caret, previous) && _attachedInputComponents.Remove(previous))
        {
            try { previous.Detach(); }
            catch (Exception error) { Editor?.ReportError(error); }
        }
        try { AttachComponent(current); }
        catch
        {
            current = previous;
            AttachComponent(previous);
            throw;
        }
    }
    private void AttachComponent(IDocumentInputComponent component)
    {
        if (_attachedInputComponents.Contains(component)) return;
        component.Attach(_inputContext!);
        _attachedInputComponents.Add(component);
    }
    private void DetachInputComponents()
    {
        if (_inputContext is null) return;
        _inputContext = null;
        // Every component is offered cleanup even if a host component throws.
        foreach (var component in _attachedInputComponents.ToArray())
        {
            try { component.Detach(); }
            catch (Exception error) { Editor?.ReportError(error); }
        }
        _attachedInputComponents.Clear();
    }

    internal bool HitTestDocument(Point point, out int position)
    {
        EnsureLayout(Bounds.Width);
        return TryHitTest(point, out position);
    }
    internal int? GetLineBoundary(int position, bool end)
    {
        EnsureLayout(Bounds.Width);
        if (Editor?.LayoutError is not null) return null;
        try
        {
            var paragraph = _layout.At(position);
            if (paragraph is null) return null;
            using var lease = paragraph.Acquire();
            var index = lease.Layout.GetLineIndexFromCharacterIndex(position - paragraph.TextStart, false);
            var line = lease.Layout.TextLines[Math.Clamp(index, 0, paragraph.Page.LineCount - 1)];
            return paragraph.TextStart + line.FirstTextSourceIndex + (end ? line.Length - line.NewLineLength : 0);
        }
        catch (ShapingLimitExceededException error) { RejectLayout(error); return null; }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (_inputContext is not null) _caret.FocusChanged(IsFocused);
    }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_inputContext is not null) _caret.FocusChanged(false);
        CancelComposition();
        Editor?.Session.BreakUndoGroup();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateInputComponents();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        DetachInputComponents(); ResetInlineViews();
        _layout.Clear(); _dirty = true;
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!e.Handled && ReferenceEquals(e.Source, this) && _inputContext is not null) _composition.TextInput(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && ReferenceEquals(e.Source, this) && _inputContext is not null) _keyboard.KeyDown(e);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.Handled && ReferenceEquals(e.Source, this) && _inputContext is not null) _pointer.PointerPressed(e);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!e.Handled && ReferenceEquals(e.Source, this) && _inputContext is not null) _pointer.PointerMoved(e);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!e.Handled && ReferenceEquals(e.Source, this) && _inputContext is not null) _pointer.PointerReleased(e);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_inputContext is not null) _pointer.PointerCaptureLost(e);
    }
    internal void CancelComposition() { if (_inputContext is not null) _composition.Cancel(); }
    internal void SetPreedit(string? text, int? cursor) { if (_inputContext is not null) _composition.SetPreedit(text, cursor); }
    protected override AutomationPeer OnCreateAutomationPeer() => new DocumentSurfaceAutomationPeer(this);
}
