using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.Controls;

public sealed record TextHighlight(int Start, int Length, IBrush Brush);
public sealed class HyperlinkEventArgs(string uri) : EventArgs { public string Uri { get; } = uri; }
public sealed class EditorErrorEventArgs(Exception exception) : EventArgs { public Exception Exception { get; } = exception; }

[TemplatePart("PART_Surface", typeof(DocumentSurface), IsRequired = true)]
[TemplatePart("PART_ScrollViewer", typeof(ScrollViewer), IsRequired = true)]
[TemplatePart("PART_Toolbar", typeof(TextaloniaToolbar))]
public class TextaloniaEditor : TemplatedControl
{
    public static readonly StyledProperty<FlowDocument?> DocumentProperty =
        AvaloniaProperty.Register<TextaloniaEditor, FlowDocument?>(nameof(Document), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<TextaloniaEditor, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> SynchronizeTextProperty =
        AvaloniaProperty.Register<TextaloniaEditor, bool>(nameof(SynchronizeText), true);
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<TextaloniaEditor, bool>(nameof(IsReadOnly));
    public static readonly StyledProperty<bool> ShowToolbarProperty = AvaloniaProperty.Register<TextaloniaEditor, bool>(nameof(ShowToolbar), true);
    public static readonly StyledProperty<bool> AcceptsTabProperty = AvaloniaProperty.Register<TextaloniaEditor, bool>(nameof(AcceptsTab));
    public static readonly StyledProperty<string> PlaceholderTextProperty = AvaloniaProperty.Register<TextaloniaEditor, string>(nameof(PlaceholderText), "Start writing...");
    public static readonly StyledProperty<IBrush> SelectionBrushProperty =
        AvaloniaProperty.Register<TextaloniaEditor, IBrush>(nameof(SelectionBrush), new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(85, 59, 130, 246)));
    public static readonly StyledProperty<int> SelectionStartProperty =
        AvaloniaProperty.Register<TextaloniaEditor, int>(nameof(SelectionStart), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<int> SelectionEndProperty =
        AvaloniaProperty.Register<TextaloniaEditor, int>(nameof(SelectionEnd), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<Thickness> DocumentPaddingProperty =
        AvaloniaProperty.Register<TextaloniaEditor, Thickness>(nameof(DocumentPadding), new Thickness(28));
    private static readonly DataFormat<string> NativeClipboardFormat = DataFormat.CreateStringApplicationFormat("org.textalonia.document");
    private static readonly DataFormat<byte[]> WindowsHtmlFormat = DataFormat.CreateBytesPlatformFormat("HTML Format");
    private static readonly DataFormat<string> HtmlClipboardFormat = DataFormat.CreateStringPlatformFormat(OperatingSystem.IsMacOS() ? "public.html" : "text/html");

    private bool _synchronizing;
    private int _textRevision = -1;
    private readonly List<EditorCommand> _commands = [];
    private DocumentSurface? _surface;
    private TextaloniaToolbar? _toolbar;
    internal ScrollViewer? Scroller { get; private set; }

    public TextaloniaEditor()
    {
        Session.Changed += OnSessionChanged;
        Highlights.CollectionChanged += (_, _) => _surface?.InvalidateVisual();
        BoldCommand = Command(Session.ToggleBold);
        ItalicCommand = Command(Session.ToggleItalic);
        UnderlineCommand = Command(Session.ToggleUnderline);
        StrikethroughCommand = Command(Session.ToggleStrikethrough);
        UndoCommand = Command(Session.Undo, () => Session.CanUndo);
        RedoCommand = Command(Session.Redo, () => Session.CanRedo);
        CutCommand = AsyncCommand(CutAsync, () => !IsReadOnly && !Session.Selection.IsEmpty);
        CopyCommand = AsyncCommand(CopyAsync, () => !Session.Selection.IsEmpty);
        PasteCommand = AsyncCommand(PasteAsync, () => !IsReadOnly);
        SelectAllCommand = Command(Session.SelectAll, () => true);
        SetCurrentValue(DocumentProperty, Session.Document);
    }

    public EditorSession Session { get; } = new();
    public ObservableCollection<TextHighlight> Highlights { get; } = [];
    public FlowDocument Document { get => GetValue(DocumentProperty) ?? Session.Document; set => SetValue(DocumentProperty, value); }
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>False opts into document binding without eager full-text notifications. Text retains its last published value.</summary>
    public bool SynchronizeText { get => GetValue(SynchronizeTextProperty); set => SetValue(SynchronizeTextProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public bool ShowToolbar { get => GetValue(ShowToolbarProperty); set => SetValue(ShowToolbarProperty, value); }
    public bool AcceptsTab { get => GetValue(AcceptsTabProperty); set => SetValue(AcceptsTabProperty, value); }
    public string PlaceholderText { get => GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }
    public IBrush SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public int SelectionStart { get => GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public int SelectionEnd { get => GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    public Thickness DocumentPadding { get => GetValue(DocumentPaddingProperty); set => SetValue(DocumentPaddingProperty, value); }
    public string SelectedText => Session.SelectedText;
    public Exception? LastError { get; private set; }

    public ICommand BoldCommand { get; }
    public ICommand ItalicCommand { get; }
    public ICommand UnderlineCommand { get; }
    public ICommand StrikethroughCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand SelectAllCommand { get; }
    public event EventHandler? DocumentChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler<HyperlinkEventArgs>? HyperlinkActivated;
    public event EventHandler<EditorErrorEventArgs>? OperationFailed;
    public event EventHandler? FindRequested;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_surface is not null) _surface.Editor = null;
        if (_toolbar is not null) _toolbar.Editor = null;
        base.OnApplyTemplate(e);
        _surface = e.NameScope.Get<DocumentSurface>("PART_Surface");
        Scroller = e.NameScope.Get<ScrollViewer>("PART_ScrollViewer");
        _toolbar = e.NameScope.Find<TextaloniaToolbar>("PART_Toolbar");
        _surface.Editor = this;
        if (_toolbar is not null) _toolbar.Editor = this;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_synchronizing) return;
        if (change.Property == DocumentProperty)
        {
            if (!ReferenceEquals(change.NewValue, Session.Document)) Session.Load((FlowDocument?)change.NewValue ?? new());
        }
        else if (change.Property == TextProperty)
        {
            if ((string?)change.NewValue != Session.Index.Text) Session.Load(FlowDocument.FromText((string?)change.NewValue ?? ""));
        }
        else if (change.Property == IsReadOnlyProperty) Session.IsReadOnly = IsReadOnly;
        else if (change.Property == SynchronizeTextProperty && SynchronizeText) OnSessionChanged(this, EventArgs.Empty);
        else if (change.Property == SelectionStartProperty || change.Property == SelectionEndProperty)
            Session.Select(SelectionStart, SelectionEnd);
        else if (change.Property == ForegroundProperty || change.Property == FontFamilyProperty ||
                 change.Property == DocumentPaddingProperty || change.Property == PlaceholderTextProperty || change.Property == BorderBrushProperty)
            _surface?.Refresh();
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        var documentChanged = !ReferenceEquals(GetValue(DocumentProperty), Session.Document);
        var selectionChanged = SelectionStart != Session.Selection.Anchor || SelectionEnd != Session.Selection.Active;
        _synchronizing = true;
        try
        {
            SetCurrentValue(DocumentProperty, Session.Document);
            if (SynchronizeText && (_textRevision != Session.Revision || sender == this))
            { SetCurrentValue(TextProperty, Session.Index.Text); _textRevision = Session.Revision; }
            SetCurrentValue(SelectionStartProperty, Session.Selection.Anchor);
            SetCurrentValue(SelectionEndProperty, Session.Selection.Active);
            SetCurrentValue(IsReadOnlyProperty, Session.IsReadOnly);
        }
        finally { _synchronizing = false; }
        foreach (var command in _commands) command.RaiseCanExecuteChanged();
        _surface?.Refresh(selectionChanged || documentChanged && Session.LastEdit is { Reset: false }, invalidateLayout: documentChanged);
        if (documentChanged) DocumentChanged?.Invoke(this, EventArgs.Empty);
        if (selectionChanged) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FocusDocument() => _surface?.Focus();
    public void SelectAll() => Session.SelectAll();
    public void Undo() => Session.Undo();
    public void Redo() => Session.Redo();
    public void InsertText(string text) => Session.InsertText(text);
    public void InsertTable(int rows = 2, int columns = 3) => Session.InsertTable(rows, columns);
    public void ApplyStyle(Func<TextStyle, TextStyle> change) => Session.ApplyStyle(change);
    public bool FindNext(string text, bool matchCase = false) => Session.FindNext(text, matchCase);
    public int ReplaceAll(string find, string replace, bool matchCase = false) => Session.ReplaceAll(find, replace, matchCase);

    public async Task LoadAsync(Stream stream, IDocumentFormat format, CancellationToken cancellationToken = default)
    {
        var revision = Session.Revision;
        var document = await format.LoadAsync(stream, cancellationToken);
        if (Session.Revision != revision) throw new InvalidOperationException("The document changed while the file was loading.");
        Document = document;
    }
    public Task SaveAsync(Stream stream, IDocumentFormat format, CancellationToken cancellationToken = default) =>
        format.SaveAsync(Session.Document, stream, cancellationToken);

    public async Task CopyAsync() => await CopyCoreAsync();
    private async Task<bool> CopyCoreAsync()
    {
        if (Session.Selection.IsEmpty || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
        var fragment = Session.CopySelection();
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText(Session.SelectedText);
        item.Set(NativeClipboardFormat, DocumentFormats.Json.Serialize(fragment));
        var html = DocumentFormats.Html.Serialize(fragment);
        if (OperatingSystem.IsWindows()) item.Set(WindowsHtmlFormat, WindowsHtml(html));
        else item.Set(HtmlClipboardFormat, html);
        data.Add(item);
        // Ownership transfers to the clipboard; do not dispose data here.
        await clipboard.SetDataAsync(data);
        return true;
    }

    public async Task CutAsync()
    {
        if (IsReadOnly) return;
        var revision = Session.Revision;
        var selection = Session.Selection;
        if (await CopyCoreAsync() && !IsReadOnly && Session.Revision == revision && Session.Selection == selection)
            Session.InsertText("");
    }

    public async Task PasteAsync()
    {
        if (IsReadOnly || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var revision = Session.Revision;
        var selection = Session.Selection;
        using var data = await clipboard.TryGetDataAsync();
        if (data is null) return;
        FlowDocument? fragment = null;
        var native = await data.TryGetValueAsync(NativeClipboardFormat);
        if (native is not null)
        {
            try { fragment = DocumentFormats.Json.Parse(native); }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or NotSupportedException) { }
        }
        if (fragment is null)
        {
            var html = OperatingSystem.IsWindows()
                ? (await data.TryGetValueAsync(WindowsHtmlFormat) is { } bytes ? Encoding.UTF8.GetString(bytes).TrimEnd('\0') : null)
                : await data.TryGetValueAsync(HtmlClipboardFormat);
            if (html is not null)
            {
                var start = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
                var end = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
                if (start >= 0 && end > start) html = html[(start + 20)..end];
                try { fragment = DocumentFormats.Html.Parse(html); }
                catch (FormatException) { }
            }
        }
        var text = fragment is null ? await data.TryGetTextAsync() : null;
        if (IsReadOnly || revision != Session.Revision || Session.Selection != selection) return;
        if (fragment is not null) Session.InsertDocument(fragment);
        else if (text is not null) Session.InsertText(text);
    }

    private static byte[] WindowsHtml(string html)
    {
        var fragment = html[(html.IndexOf("<body>", StringComparison.Ordinal) + 6)..html.LastIndexOf("</body>", StringComparison.Ordinal)];
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        const string template = "Version:1.0\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\nStartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, 0, 0, 0, 0));
        var start = headerLength + Encoding.UTF8.GetByteCount(prefix);
        var end = start + Encoding.UTF8.GetByteCount(fragment);
        var header = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, headerLength, end + Encoding.UTF8.GetByteCount(suffix), start, end);
        return Encoding.UTF8.GetBytes(header + prefix + fragment + suffix);
    }

    internal void OpenLink(string uri) => HyperlinkActivated?.Invoke(this, new(uri));
    internal void RequestFind()
    {
        _toolbar?.OpenFind();
        FindRequested?.Invoke(this, EventArgs.Empty);
    }
    internal void ReportError(Exception exception)
    {
        LastError = exception; OperationFailed?.Invoke(this, new(exception));
    }
    internal void Run(Action action)
    {
        try { LastError = null; action(); FocusDocument(); }
        catch (Exception ex) { ReportError(ex); }
    }
    private EditorCommand Command(Action execute, Func<bool>? canExecute = null) =>
        AsyncCommand(() => { execute(); return Task.CompletedTask; }, canExecute ?? (() => !IsReadOnly));
    private EditorCommand AsyncCommand(Func<Task> execute, Func<bool> canExecute)
    {
        var command = new EditorCommand(async () =>
        {
            LastError = null;
            try { await execute(); FocusDocument(); }
            catch (Exception ex) { ReportError(ex); }
        }, canExecute);
        _commands.Add(command); return command;
    }
}

internal sealed class EditorCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    private bool _running;
    public bool CanExecute(object? parameter) => !_running && canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; RaiseCanExecuteChanged();
        try { await execute(); }
        finally { _running = false; RaiseCanExecuteChanged(); }
    }
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public class TextaloniaViewer : TextaloniaEditor
{
    protected override Type StyleKeyOverride => typeof(TextaloniaEditor);
    public TextaloniaViewer() { IsReadOnly = true; ShowToolbar = false; }
}
