using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Textalonia.Model;
using ImeSelection = Avalonia.Input.TextInput.TextSelection;

namespace Textalonia.Controls;

/// <summary>Native shaped-text surface used by the editor template.</summary>
public class DocumentSurface : Control
{
    private readonly DocumentLayout _layout = new();
    private readonly DispatcherTimer _blink;
    private readonly InputClient _inputClient;
    private TextaloniaEditor? _editor;
    private FlowDocument? _layoutDocument;
    private double _layoutWidth;
    private double _measuredLayoutHeight;
    private bool _dirty = true;
    private bool _caretVisible = true;
    private bool _dragging;
    private double? _preferredX;
    private string? _preedit;
    private int? _preeditCursor;
    private FlowDocument? _composition;
    private FlowDocument? _compositionBase;
    private Rect _viewport;
    internal DocumentLayout Layout => _layout;

    public DocumentSurface()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        ClipToBounds = true;
        _inputClient = new(this);
        _layout.AnchorShifted += ApplyAnchorAdjustment;
        _blink = new DispatcherTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Background, (_, _) =>
        { _caretVisible = !_caretVisible; InvalidateVisual(); });
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) =>
        {
            if (Editor is { IsReadOnly: false }) e.Client = _inputClient;
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
            _editor = value;
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
        if (_composition is not null && (Editor is null || Editor.IsReadOnly || !ReferenceEquals(_compositionBase, Editor.Document)))
        {
            _preedit = null; _preeditCursor = null; _composition = null; _compositionBase = null;
            _inputClient.Reset();
        }
        _dirty |= invalidateLayout;
        _caretVisible = true;
        InvalidateMeasure(); InvalidateVisual();
        _inputClient.Notify();
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
    private int DisplayCaret => Editor is null ? 0 : _composition is not null
        ? Editor.Session.Selection.Start + Math.Clamp(_preeditCursor ?? _preedit!.Length, 0, _preedit!.Length)
        : Editor.Session.Selection.Active;

    private void EnsureLayout(double width)
    {
        if (Editor is null) return;
        width = double.IsFinite(width) && width > 48 ? width : 800;
        var document = _composition ?? Editor.Document;
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
    }
    private void RejectLayout(ShapingLimitExceededException error)
    {
        // No partial or approximate text geometry is exposed after rejection.
        // Retain the extent so an offscreen target cannot jump the scroll view.
        _layout.Clear(); _dragging = false;
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
        if (_composition is null)
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
        if (Editor.Session.Index.Length == 0 && _composition is null && Editor.Document.Blocks is [{ } block] && block is Paragraph)
        {
            using var placeholder = new TextLayout(Editor.PlaceholderText, new Typeface(Editor.FontFamily), 16,
                new SolidColorBrush(Color.FromArgb(135, 128, 128, 128)), maxWidth: Math.Max(20, Bounds.Width - Editor.DocumentPadding.Left - Editor.DocumentPadding.Right));
            placeholder.Draw(context, new Point(Editor.DocumentPadding.Left, Editor.DocumentPadding.Top));
        }
        // Rendering must not discover an offscreen caret's geometry: doing so
        // can refine prefix heights and invalidate scrolling during this pass.
        if (IsFocused && !Editor.IsReadOnly && _caretVisible && _layout.Paragraphs.Any(p =>
            DisplayCaret >= p.TextStart && (DisplayCaret < p.TextEnd || DisplayCaret == p.TextEnd && p.TextEnd == p.Position.End) && p.Bounds.Intersects(viewport)))
        {
            var caret = CaretRectangle;
            var visual = _layout.Paragraphs.FirstOrDefault(p => DisplayCaret >= p.TextStart &&
                (DisplayCaret < p.TextEnd || DisplayCaret == p.TextEnd && p.TextEnd == p.Position.End));
            if (visual?.Clip is { } clip) caret = caret.Intersect(clip);
            if (caret.Width > 0 && caret.Height > 0) context.FillRectangle(Editor.Foreground ?? Brushes.Black, caret);
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _caretVisible = true; _blink.Start(); InvalidateVisual();
    }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _blink.Stop(); CancelComposition(); InvalidateVisual();
        Editor?.Session.BreakUndoGroup();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _blink.Stop(); _dragging = false; _layout.Clear(); _dirty = true;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || Editor is null || Editor.IsReadOnly || string.IsNullOrEmpty(e.Text)) return;
        SetPreedit(null, null);
        Editor.Session.InsertText(e.Text, true);
        _preferredX = null; e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Editor is null) return;
        var session = Editor.Session;
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        var command = e.KeyModifiers.HasFlag(primary) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var word = OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Alt) : command;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        void Move(int position)
        {
            CancelComposition();
            session.Select(shift ? session.Selection.Anchor : position, position);
        }
        if (command)
        {
            System.Windows.Input.ICommand? action = e.Key switch
            {
                Key.B => Editor.BoldCommand, Key.I => Editor.ItalicCommand, Key.U => Editor.UnderlineCommand,
                Key.Z => shift ? Editor.RedoCommand : Editor.UndoCommand,
                Key.Y => Editor.RedoCommand, Key.C => Editor.CopyCommand, Key.X => Editor.CutCommand,
                Key.V => Editor.PasteCommand, Key.A => Editor.SelectAllCommand, _ => null
            };
            if (action is not null) { CancelComposition(); if (action.CanExecute(null)) action.Execute(null); e.Handled = true; return; }
            if (e.Key == Key.F) { Editor.RequestFind(); e.Handled = true; return; }
        }
        EnsureLayout(Bounds.Width);
        try
        {
            switch (e.Key)
            {
                case Key.Left:
                    Move(!shift && !session.Selection.IsEmpty ? session.Selection.Start :
                        word ? session.PreviousWord(session.Selection.Active) : session.PreviousCaret(session.Selection.Active));
                    _preferredX = null; break;
                case Key.Right:
                    Move(!shift && !session.Selection.IsEmpty ? session.Selection.End :
                        word ? session.NextWord(session.Selection.Active) : session.NextCaret(session.Selection.Active));
                    _preferredX = null; break;
                case Key.Up:
                case Key.Down:
                case Key.PageUp:
                case Key.PageDown:
                    var caret = CaretRectangle;
                    if (Editor.LayoutError is not null) return;
                    _preferredX ??= caret.X;
                    var direction = e.Key is Key.Up or Key.PageUp ? -1 : 1;
                    var distance = e.Key is Key.PageUp or Key.PageDown ? Math.Max(40, Editor.Scroller?.Viewport.Height ?? 300) : caret.Height;
                    if (TryHitTest(new Point(_preferredX.Value, caret.Y + caret.Height / 2 + direction * distance), out var hitTarget)) Move(hitTarget);
                    break;
                case Key.Home:
                case Key.End:
                    if (command) Move(e.Key == Key.Home ? 0 : session.Index.Length);
                    else
                    {
                        if (Editor.LayoutError is not null) return;
                        var p = _layout.At(session.Selection.Active);
                        if (p is not null)
                        {
                            using var lease = p.Acquire();
                            var lineIndex = lease.Layout.GetLineIndexFromCharacterIndex(session.Selection.Active - p.TextStart, false);
                            var line = lease.Layout.TextLines[Math.Clamp(lineIndex, 0, p.Page.LineCount - 1)];
                            Move(p.TextStart + line.FirstTextSourceIndex + (e.Key == Key.End ? line.Length - line.NewLineLength : 0));
                        }
                    }
                    _preferredX = null; break;
                case Key.Back: CancelComposition(); session.DeleteBackward(word); _preferredX = null; break;
                case Key.Delete: CancelComposition(); session.DeleteForward(word); _preferredX = null; break;
                case Key.Enter: CancelComposition(); if (shift) session.InsertText("\u2028"); else session.InsertParagraph(); _preferredX = null; break;
                case Key.Tab:
                    if (session.CurrentCell() is { } cell)
                    {
                        var cellIds = cell.Table.Rows.SelectMany(r => r).Select(c => c.Id).ToHashSet();
                        var cells = session.Index.Paragraphs.Where(p => cellIds.Contains(p.ContainerId)).GroupBy(p => p.ContainerId).ToArray();
                        var current = Array.FindIndex(cells, g => g.Key == cell.Table.Rows[cell.Row][cell.Column].Id);
                        var next = current + (shift ? -1 : 1);
                        if (next >= 0 && next < cells.Length) Move(cells[next].First().Start);
                        else if (!shift && !Editor.IsReadOnly && cell.Table.Rows.SelectMany(r => r).All(c => c.RowSpan == 1 && c.ColumnSpan == 1))
                        {
                            var rowIndex = cell.Table.Rows.Length;
                            session.UpdateCurrentTable((table, _, _) => table.InsertRow(rowIndex));
                            var table = session.CurrentCell()?.Table;
                            if (table is not null)
                            {
                                var target = session.Index.Paragraphs.First(p => p.ContainerId == table.Rows[rowIndex][0].Id);
                                session.Select(target.Start, target.Start);
                            }
                        }
                        else return;
                    }
                    else if (Editor.AcceptsTab) session.InsertText("\t");
                    else return;
                    break;
                case Key.Escape:
                    if (_preedit is not null) CancelComposition();
                    else session.Select(session.Selection.Active, session.Selection.Active);
                    break;
                default: return;
            }
        }
        catch (ShapingLimitExceededException error) { RejectLayout(error); }
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is null) return;
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return;
        Focus(); CancelComposition(); EnsureLayout(Bounds.Width);
        if (!TryHitTest(e.GetPosition(this), out var position)) return;
        var session = Editor.Session;
        if (properties.IsRightButtonPressed)
        {
            if (position < session.Selection.Start || position > session.Selection.End) session.Select(position, position);
            return;
        }
        var paragraph = session.Index.At(position);
        var link = paragraph.Paragraph.StyleAt(position - paragraph.Start).Hyperlink;
        if (link is not null && (Editor.IsReadOnly || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        { Editor.OpenLink(link); e.Handled = true; return; }
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
        _preferredX = null;
        _dragging = true; e.Pointer.Capture(this); e.Handled = true;
        _inputClient.Activate();
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || Editor is null || e.Pointer.Captured != this) return;
        EnsureLayout(Bounds.Width);
        if (TryHitTest(e.GetPosition(this), out var position)) Editor.Session.Select(Editor.Session.Selection.Anchor, position);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false; e.Pointer.Capture(null); e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); _dragging = false; }

    private void CancelComposition()
    {
        if (_preedit is null) return;
        SetPreedit(null, null); _inputClient.Reset();
    }
    internal void SetPreedit(string? text, int? cursor)
    {
        if (Editor is null) return;
        if (Editor.IsReadOnly) text = null;
        if (string.IsNullOrEmpty(text) && _preedit is null) return;
        _preedit = string.IsNullOrEmpty(text) ? null : text;
        _preeditCursor = cursor;
        if (_preedit is null) { _composition = null; _compositionBase = null; }
        else
        {
            var preview = new Editing.EditorSession(Editor.Document);
            preview.Select(Editor.Session.Selection.Anchor, Editor.Session.Selection.Active);
            preview.ApplyStyle(_ => Editor.Session.TypingStyle with { Underline = true });
            preview.InsertText(_preedit);
            _composition = preview.Document;
            _compositionBase = Editor.Document;
        }
        Refresh(true);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SurfaceAutomationPeer(this);

    private sealed class SurfaceAutomationPeer(DocumentSurface owner) : ControlAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => owner.Editor?.IsReadOnly ?? true;
        public string Value => owner.Editor?.Session.Index.Text ?? "";
        public void SetValue(string? value)
        {
            if (IsReadOnly) throw new InvalidOperationException("The document is read-only.");
            owner.Editor!.Session.SelectAll(); owner.Editor.Session.InsertText(value ?? "");
        }
        protected override string GetClassNameCore() => nameof(TextaloniaEditor);
        protected override string GetNameCore() => owner.Editor is null ? "Textalonia editor" :
            Avalonia.Automation.AutomationProperties.GetName(owner.Editor) ?? "Textalonia editor";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
        protected override object? GetProviderCore(Type providerType) => providerType == typeof(IValueProvider) ? this : base.GetProviderCore(providerType);
    }

    private sealed class InputClient(DocumentSurface surface) : TextInputMethodClient
    {
        public override Visual TextViewVisual => surface;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => true;
        private ParagraphPosition? Paragraph => surface.Editor?.Session.Index.At(surface.Editor.Session.Selection.Active);
        public override string SurroundingText => Paragraph?.Paragraph.Text ?? "";
        public override Rect CursorRectangle => surface.CaretRectangle;
        public override ImeSelection Selection
        {
            get
            {
                if (surface.Editor is not { } editor || Paragraph is not { } paragraph) return new(0, 0);
                return new(Math.Clamp(editor.Session.Selection.Anchor - paragraph.Start, 0, paragraph.Paragraph.Length),
                    Math.Clamp(editor.Session.Selection.Active - paragraph.Start, 0, paragraph.Paragraph.Length));
            }
            set
            {
                if (surface.Editor is { } editor && Paragraph is { } paragraph)
                    editor.Session.Select(paragraph.Start + Math.Clamp(value.Start, 0, paragraph.Paragraph.Length),
                        paragraph.Start + Math.Clamp(value.End, 0, paragraph.Paragraph.Length));
            }
        }
        public override void SetPreeditText(string? text) => surface.SetPreedit(text, null);
        public override void SetPreeditText(string? text, int? cursorPos) => surface.SetPreedit(text, cursorPos);
        public override void ExecuteContextMenuAction(ContextMenuAction action)
        {
            var editor = surface.Editor;
            if (editor is null) return;
            var command = action switch
            {
                ContextMenuAction.Copy => editor.CopyCommand, ContextMenuAction.Cut => editor.CutCommand,
                ContextMenuAction.Paste => editor.PasteCommand, ContextMenuAction.SelectAll => editor.SelectAllCommand, _ => null
            };
            if (command?.CanExecute(null) == true) command.Execute(null);
        }
        public void Notify() { RaiseSurroundingTextChanged(); RaiseSelectionChanged(); RaiseCursorRectangleChanged(); }
        public void Activate() => RaiseInputPaneActivationRequested();
        public void Reset() => RequestReset();
    }
}
