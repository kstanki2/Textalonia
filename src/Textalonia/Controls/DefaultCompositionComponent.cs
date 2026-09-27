using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Textalonia.Model;
using ImeSelection = Avalonia.Input.TextInput.TextSelection;

namespace Textalonia.Controls;

/// <summary>Native IME integration with a transient preview document and separately committed text input.</summary>
public class DefaultCompositionComponent : DocumentInputComponent, ICompositionComponent
{
    private InputClient? _client;
    private string? _preedit;
    private int? _cursor;
    private FlowDocument? _baseDocument;
    private Editing.TextSelection _baseSelection;
    public TextInputMethodClient? Client => _client;
    public FlowDocument? PreviewDocument { get; private set; }
    public int? CaretPosition => _preedit is null || AttachedContext is null ? null :
        Context.Session.Selection.Start + Math.Clamp(_cursor ?? _preedit.Length, 0, _preedit.Length);
    public bool IsComposing => _preedit is not null;
    protected override void OnAttached() => _client = new InputClient(this);
    protected override void OnDetached()
    {
        Clear();
        var client = _client;
        _client = null;
        client?.Reset();
    }
    private void Clear() { _preedit = null; _cursor = null; PreviewDocument = null; _baseDocument = null; }
    public virtual void Cancel()
    {
        if (!IsComposing) return;
        Clear();
        _client?.Reset();
        AttachedContext?.Refresh();
    }
    public virtual void Refresh()
    {
        if (AttachedContext is null) return;
        if (IsComposing && (Context.Editor.IsReadOnly || !ReferenceEquals(_baseDocument, Context.Editor.Document) ||
            _baseSelection != Context.Session.Selection))
        {
            Clear();
            _client?.Reset();
        }
        _client?.Notify();
    }
    public virtual void Activate() => _client?.Activate();
    public virtual void SetPreedit(string? text, int? cursor)
    {
        if (AttachedContext is null) return;
        if (Context.Editor.IsReadOnly) text = null;
        if (string.IsNullOrEmpty(text) && _preedit is null) return;
        _preedit = string.IsNullOrEmpty(text) ? null : text;
        _cursor = cursor;
        if (_preedit is null) Clear();
        else
        {
            var preview = new Editing.EditorSession(Context.Editor.Document);
            preview.Select(Context.Session.Selection.Anchor, Context.Session.Selection.Active);
            var paragraph = Context.Session.Index.At(Context.Session.Selection.Active).Paragraph;
            var typing = new DocumentStyleResolver(Context.Session.Document).ResolveText(paragraph, Context.Session.TypingStyle);
            preview.ApplyStyle(_ => typing with { Underline = true, UnderlineKind = UnderlineKind.None });
            preview.InsertText(_preedit);
            PreviewDocument = preview.Document;
            _baseDocument = Context.Editor.Document;
            _baseSelection = Context.Session.Selection;
        }
        Context.Refresh(true);
    }
    public virtual void TextInput(TextInputEventArgs e)
    {
        if (e.Handled || AttachedContext is null || Context.Editor.IsReadOnly || string.IsNullOrEmpty(e.Text)) return;
        SetPreedit(null, null);
        Context.Session.InsertText(e.Text, true);
        Context.PreferredCaretX = null;
        e.Handled = true;
    }

    private sealed class InputClient(DefaultCompositionComponent owner) : TextInputMethodClient
    {
        private DocumentInputContext? Current => ReferenceEquals(owner._client, this) ? owner.AttachedContext : null;
        public override Visual TextViewVisual => Current?.Surface!;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => true;
        private ParagraphPosition? Paragraph => Current?.Session.Index.At(Current.Session.Selection.Active);
        public override string SurroundingText => Paragraph?.Paragraph.Text ?? "";
        public override Rect CursorRectangle => Current?.CaretRectangle ?? default;
        public override ImeSelection Selection
        {
            get
            {
                if (Current is not { } context || Paragraph is not { } paragraph) return new(0, 0);
                return new(Math.Clamp(context.Session.Selection.Anchor - paragraph.Start, 0, paragraph.Paragraph.Length),
                    Math.Clamp(context.Session.Selection.Active - paragraph.Start, 0, paragraph.Paragraph.Length));
            }
            set
            {
                if (Current is { } context && Paragraph is { } paragraph)
                    context.Session.Select(paragraph.Start + Math.Clamp(value.Start, 0, paragraph.Paragraph.Length),
                        paragraph.Start + Math.Clamp(value.End, 0, paragraph.Paragraph.Length));
            }
        }
        public override void SetPreeditText(string? text) { if (Current is not null) owner.SetPreedit(text, null); }
        public override void SetPreeditText(string? text, int? cursorPos) { if (Current is not null) owner.SetPreedit(text, cursorPos); }
        public override void ExecuteContextMenuAction(ContextMenuAction action)
        {
            if (Current?.Editor is not { } editor) return;
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
