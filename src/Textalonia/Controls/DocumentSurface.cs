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
    private bool _rendering;
    private bool _anchorAdjustmentPosted;
    private double _pendingAnchorAdjustment;
    internal DocumentLayout Layout => _layout;
    internal bool IsSelectingWithPointer { get; set; }
    internal Rect InteractionViewport => _viewport.Width > 0 && _viewport.Height > 0
        ? _viewport.Intersect(new Rect(Bounds.Size))
        : new Rect(0, Editor?.Scroller?.Offset.Y ?? 0, Bounds.Width, Editor?.Scroller?.Viewport.Height ?? Bounds.Height);

    public DocumentSurface()
    {
        Focusable = true;
        InitializeDragDrop();
        Cursor = new Cursor(StandardCursorType.Ibeam);
        ClipToBounds = true;
        _keyboard = _defaultKeyboard; _pointer = _defaultPointer; _caret = _defaultCaret; _composition = _defaultComposition;
        _layout.AnchorShifted += adjustment => ApplyAnchorAdjustment(adjustment * ViewZoom);
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
            if (_editor is not null) { _layout.Clear(releaseHyphenation: true); ClearPagedLayout(); }
            _pendingAnchorAdjustment = 0;
            ResetInlineViews();
            _editor = value;
            UpdateInputComponents();
            ContextMenu = value is null ? null : CreateEditorContextMenu(value);
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
        if (bringCaret && IsFocused && Editor is not null && !IsSelectingWithPointer)
            Dispatcher.UIThread.Post(() =>
            {
                if (Editor is not null && !IsSelectingWithPointer && TopLevel.GetTopLevel(this) is not null)
                {
                    EnsureLayout(Bounds.Width);
                    var caret = CaretRectangle;
                    // Resolving a distant caret can refine the document extent.
                    // Publish that measurement before the scroll viewer clamps
                    // the request against its previous, estimated extent.
                    for (var pass = 0; pass < 4 && Editor.LayoutError is null &&
                        Math.Abs(_measuredLayoutHeight - GeometryHeight) > .1; pass++)
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
            var height = GeometryHeight;
            Rect caret;
            try { caret = GeometryCaret(CurrentVisualCaret); }
            catch (ShapingLimitExceededException error) { RejectLayout(error); return default; }
            if (Math.Abs(height - GeometryHeight) > .1)
                Dispatcher.UIThread.Post(InvalidateMeasure, DispatcherPriority.Loaded);
            return caret;
        }
    }
    private int DisplayCaret => _composition.CaretPosition ?? Editor?.Session.Selection.Active ?? 0;

    internal void EnsureLayout(double width)
    {
        if (Editor is null) return;
        width = double.IsFinite(width) && width > 48 ? width : 800;
        var document = _composition.PreviewDocument ?? Editor.TablePreviewDocument ?? Editor.PresentationDocument;
        if (Editor.ViewMode != DocumentViewMode.PrintLayout && Editor.ActiveStoryId != Guid.Empty)
            document = ReferenceEquals(document, Editor.Document) ? Editor.Session.ActiveDocument : ProjectStory(document, Editor.ActiveStoryId);
        if (Editor.ViewMode == DocumentViewMode.Simple) document = DisplayNoteMarks(document);
        if (!_dirty && ReferenceEquals(_layoutDocument, document) && Math.Abs(_layoutWidth - width) < .1) return;
        _layoutDocument = document; _layoutWidth = width; _dirty = false;
        try
        {
            if (Editor.ViewMode == DocumentViewMode.Simple)
            {
                if (_pagedLayout is not null) ClearPagedLayout();
                _layout.Build(document, width / ViewZoom, Editor.FontFamily, Editor.Foreground ?? Brushes.Black,
                    Editor.BorderBrush ?? Brushes.Gray, Editor.DocumentPadding,
                    ToDocument(_viewport.Width > 0 && _viewport.Height > 0 ? _viewport : new Rect(0, Editor.Scroller?.Offset.Y ?? 0, width, 500)),
                    Editor.MaxShapingCharacters, Editor.HyphenationService);
                Editor.UpdatePageStatus(1, 1);
            }
            else
            {
                _layout.Clear();
                BuildPagedLayout(document);
                // A first/even variant can exist before a corresponding physical sheet exists.
                // Keep it editable on a continuous story surface until it has a page instance.
                if (Editor.ActiveStoryId != Guid.Empty && Editor.ViewMode == DocumentViewMode.PrintLayout &&
                    !_pagedLayout!.StoryFragments.Any(fragment => fragment.StoryKey == Editor.ActiveStoryId && fragment.Bounds.Intersect(fragment.Clip).Height > 0))
                {
                    ClearPagedLayout();
                    _layout.Build(ProjectStory(document, Editor.ActiveStoryId), width / ViewZoom, Editor.FontFamily,
                        Editor.Foreground ?? Brushes.Black, Editor.BorderBrush ?? Brushes.Gray, Editor.DocumentPadding,
                        ToDocument(_viewport.Width > 0 && _viewport.Height > 0 ? _viewport : new Rect(0, 0, width, 500)), Editor.MaxShapingCharacters,
                        Editor.HyphenationService);
                }
            }
        }
        catch (ShapingLimitExceededException error) { RejectLayout(error); return; }
        Editor.SetLayoutError(null);
        if (_pagedLayout is null) ApplyAnchorAdjustment(_layout.AnchorAdjustment * ViewZoom);
        UpdateInlineViews();
    }
    internal void RejectLayout(ShapingLimitExceededException error)
    {
        // No partial or approximate text geometry is exposed after rejection.
        // Retain the extent so an offscreen target cannot jump the scroll view.
        _layout.Clear(); ClearPagedLayout(); ResetInlineViews();
        Editor?.SetLayoutError(error);
        InvalidateVisual();
    }
    private bool TryHitTest(Point point, out int position)
    {
        position = 0;
        if (Editor?.LayoutError is not null) return false;
        try { var caret = GeometryHitTestCaret(point); RememberPointerCaret(caret); position = caret.Position; return true; }
        catch (ShapingLimitExceededException error) { RejectLayout(error); return false; }
    }
    private void ApplyAnchorAdjustment(double adjustment)
    {
        if (Math.Abs(adjustment) <= .1) return;
        if (_rendering)
        {
            // Shaping can refine the virtualized anchor while painting. Publish
            // its scroll correction after the compositor finishes this pass.
            _pendingAnchorAdjustment += adjustment;
            if (!_anchorAdjustmentPosted)
            {
                _anchorAdjustmentPosted = true;
                Dispatcher.UIThread.Post(() =>
                {
                    _anchorAdjustmentPosted = false;
                    var pending = _pendingAnchorAdjustment;
                    _pendingAnchorAdjustment = 0;
                    if (_isAttached) ApplyAnchorAdjustment(pending);
                }, DispatcherPriority.Loaded);
            }
            return;
        }
        if (Math.Abs(adjustment) > .1 && Editor?.Scroller is { } scroller)
            scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, scroller.Offset.Y + adjustment));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureLayout(availableSize.Width);
        _measuredLayoutHeight = GeometryHeight;
        return new(Math.Max(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, GeometryWidth), Math.Max(90, _measuredLayoutHeight));
    }

    public override void Render(DrawingContext context)
    {
        _rendering = true;
        try { RenderDocument(context); }
        finally { _rendering = false; }
    }

    private void RenderDocument(DrawingContext context)
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
        var documentViewport = ToDocument(viewport);
        // Loaded transparent images must reveal document content, not the loading placeholder.
        using var imageScope = new Rendering.InlineOutputScope((descriptor, _, _) =>
        {
            if (Rendering.ImageDrawing.ResourceId(descriptor) is not { } resourceId) return false;
            Editor.Document.Resources.TryGetValue(resourceId, out var resource);
            return _inlineImages?.Request(resourceId, resource) is not null;
        });
        using (context.PushTransform(Matrix.CreateScale(ViewZoom, ViewZoom)))
        {
            if (_pagedLayout is { } pages)
            {
                pages.DrawBackgrounds(context, documentViewport);
                foreach (var page in pages.Pages)
                    if (page.Bounds.Intersects(documentViewport)) context.DrawRectangle(null, new Pen(Brushes.Gray, 1), page.Bounds);
            }
            else foreach (var decoration in _layout.Decorations)
                if (decoration.Bounds.Intersects(documentViewport)) decoration.Draw(context);
            if (_pagedLayout is { } watermarkPages)
                watermarkPages.DrawWatermarks(context, documentViewport, (drawing, resourceId, bounds) =>
                {
                    Editor.Document.Resources.TryGetValue(resourceId, out var resource);
                    if (_inlineImages?.Request(resourceId, resource) is { } bitmap)
                        drawing.DrawImage(bitmap, new Rect(bitmap.Size), bounds);
                });
            using (context.PushTransform(Matrix.CreateScale(1 / ViewZoom, 1 / ViewZoom)))
                DrawInlineImages(context, viewport, true);
            DrawStoryOverlay(context);
            foreach (var highlight in Editor.Highlights.Where(h => h.Start >= 0 && h.Length >= 0 && h.Start <= Editor.Session.Index.Length - h.Length))
                foreach (var rect in GeometrySelectionRects(highlight.Start, highlight.Length))
                    if (rect.Intersects(viewport)) context.FillRectangle(highlight.Brush, ToDocument(rect));
            var selection = Editor.Session.Selection;
            if (!HasComposition)
                foreach (var rect in GeometrySelectionRects(selection.Start, selection.Length))
                    if (rect.Intersects(viewport)) context.FillRectangle(Editor.SelectionBrush, ToDocument(rect));
            if (_pagedLayout is { } content) content.DrawContent(context, documentViewport);
            else foreach (var paragraph in _layout.Paragraphs)
            {
                if (!paragraph.Bounds.Intersects(documentViewport)) continue;
                paragraph.Draw(context, documentViewport);
                if (paragraph.Marker is not null)
                {
                    ListMarkerDrawing.Draw(context, paragraph.Marker, paragraph.MarkerStyle ?? paragraph.Position.Paragraph.DefaultStyle,
                        paragraph.MarkerDefinition ?? new(), paragraph.Page.Owner.Paragraph, paragraph.Origin, Editor.FontFamily, Editor.Foreground, paragraph.Clip, fonts: paragraph.MarkerFonts);
                }
            }
            DrawProofingUnderlines(context, viewport);
            if (Editor.Session.Index.Length == 0 && !HasComposition && Editor.Session.ActiveDocument.Blocks is [Paragraph])
            {
                var origin = _pagedLayout is { } emptyPages ? emptyPages.Caret(GeometryStoryId, 0, GeometryStoryPage).Position : new Point(Editor.DocumentPadding.Left, Editor.DocumentPadding.Top);
                using var placeholder = new TextLayout(Editor.PlaceholderText, new Typeface(Editor.FontFamily), 16,
                    new SolidColorBrush(Color.FromArgb(135, 128, 128, 128)), maxWidth: Math.Max(20, documentViewport.Width - origin.X));
                placeholder.Draw(context, origin);
            }
        }
        DrawInlineImages(context, viewport);
        RenderDropPreview(context);
        if (_pointer is DefaultPointerComponent standardPointer) standardPointer.RenderInteractionAdorners(context);
        // Simple view resolves only the already visible shaping windows during painting.
        if (IsFocused && !Editor.IsReadOnly && GeometryRanges().Any(range =>
            DisplayCaret >= range.Start && DisplayCaret <= range.End && range.Bounds.Intersects(viewport)))
        {
            var caret = CaretRectangle;
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
            var caret = position == DisplayCaret ? CurrentVisualCaret : VisualCaret.Logical(position);
            return GeometryLineBoundary(caret, end).Position;
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
        ClearDropPreview();
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
        _pendingAnchorAdjustment = 0;
        ClearDropPreview();
        DetachInputComponents(); ResetInlineViews();
        _layout.Clear(releaseHyphenation: true); ClearPagedLayout(); ClearStoryProjections(); _layoutDocument = null; _dirty = true;
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
        if (e.Key == Key.Escape && ReferenceEquals(e.Source, this) && _pointer is DefaultPointerComponent pointer) pointer.CancelInteractions();
        if (!e.Handled && ReferenceEquals(e.Source, this) && _inputContext is not null) _keyboard.KeyDown(e);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, this)) CaptureProofingContextClick(e);
        base.OnPointerPressed(e);
        if (!e.Handled && ReferenceEquals(e.Source, this) && _inputContext is not null)
        {
            if (HandleStoryPointerPress(e)) return;
            _pointer.PointerPressed(e);
        }
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
