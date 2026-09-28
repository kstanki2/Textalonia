using System.Collections.ObjectModel;
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
public sealed class ConversionCompletedEventArgs(ConversionReport report) : EventArgs { public ConversionReport Report { get; } = report; }

[TemplatePart("PART_Surface", typeof(DocumentSurface), IsRequired = true)]
[TemplatePart("PART_ScrollViewer", typeof(ScrollViewer), IsRequired = true)]
[TemplatePart("PART_Toolbar", typeof(TextaloniaToolbar))]
public partial class TextaloniaEditor : TemplatedControl
{
    public static readonly StyledProperty<FlowDocument?> DocumentProperty =
        AvaloniaProperty.Register<TextaloniaEditor, FlowDocument?>(nameof(Document), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<TextaloniaEditor, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> SynchronizeTextProperty =
        AvaloniaProperty.Register<TextaloniaEditor, bool>(nameof(SynchronizeText), true);
    public static readonly StyledProperty<int> MaxShapingCharactersProperty =
        AvaloniaProperty.Register<TextaloniaEditor, int>(nameof(MaxShapingCharacters), 0,
            validate: value => value == 0 || value >= ParagraphLayout.WindowLength);
    public static readonly DirectProperty<TextaloniaEditor, ShapingLimitExceededException?> LayoutErrorProperty =
        AvaloniaProperty.RegisterDirect<TextaloniaEditor, ShapingLimitExceededException?>(nameof(LayoutError), editor => editor.LayoutError);
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
    private static readonly DataFormat<string> FragmentClipboardFormat = DataFormat.CreateStringApplicationFormat("org.textalonia.fragment");
    private static readonly DataFormat<string> NativeClipboardFormat = DataFormat.CreateStringApplicationFormat("org.textalonia.document");
    private static readonly DataFormat<byte[]> WindowsHtmlFormat = DataFormat.CreateBytesPlatformFormat("HTML Format");
    private static readonly DataFormat<string> HtmlClipboardFormat = DataFormat.CreateStringPlatformFormat(OperatingSystem.IsMacOS() ? "public.html" : "text/html");

    private bool _synchronizing;
    private bool _preservingView;
    private int _textRevision = -1;
    private ShapingLimitExceededException? _layoutError;
    private readonly List<EditorCommand> _commands = [];
    private DocumentSurface? _surface;
    private TextaloniaToolbar? _toolbar;
    internal ScrollViewer? Scroller { get; private set; }

    public TextaloniaEditor()
    {
        InitializeNavigationCommands();
        Session.Changed += OnSessionChanged;
        Highlights.CollectionChanged += (_, _) => _surface?.InvalidateVisual();
        BoldCommand = Command(ToggleSelectedBold, () => CanEdit(EditOperation.Formatting));
        ItalicCommand = Command(ToggleSelectedItalic, () => CanEdit(EditOperation.Formatting));
        UnderlineCommand = Command(ToggleSelectedUnderline, () => CanEdit(EditOperation.Formatting));
        StrikethroughCommand = Command(ToggleSelectedStrikethrough, () => CanEdit(EditOperation.Formatting));
        UndoCommand = Command(Session.Undo, () => Session.CanUndo && CanEdit(EditOperation.Undo));
        RedoCommand = Command(Session.Redo, () => Session.CanRedo && CanEdit(EditOperation.Redo));
        CutCommand = AsyncCommand(CutAsync, () => CanEdit(EditOperation.Clipboard) && (!Session.Selection.IsEmpty || CellSelection is not null));
        CopyCommand = AsyncCommand(CopyAsync, () => !Session.Selection.IsEmpty || CellSelection is not null);
        PasteCommand = AsyncCommand(PasteAsync, () => CanEdit(EditOperation.Clipboard));
        SelectAllCommand = Command(Session.SelectAll, () => true);
        SetCurrentValue(DocumentProperty, Session.Document);
    }

    public EditorSession Session { get; } = new();
    public ObservableCollection<TextHighlight> Highlights { get; } = [];
    public FlowDocument Document { get => GetValue(DocumentProperty) ?? Session.Document; set => SetValue(DocumentProperty, value); }
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>False opts into document binding without eager full-text notifications. Text retains its last published value.</summary>
    public bool SynchronizeText { get => GetValue(SynchronizeTextProperty); set => SetValue(SynchronizeTextProperty, value); }
    /// <summary>Maximum UTF-16 units per exact shaping input. Zero preserves unrestricted compatibility; positive values must be at least 2048.</summary>
    public int MaxShapingCharacters { get => GetValue(MaxShapingCharactersProperty); set => SetValue(MaxShapingCharactersProperty, value); }
    /// <summary>Current rendering-limit error, or null. The document and its history remain available when rendering is suspended.</summary>
    public ShapingLimitExceededException? LayoutError => _layoutError;
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
    public ConversionReport LastConversionReport { get; private set; } = ConversionReport.Empty;

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
    public event EventHandler<ConversionCompletedEventArgs>? ConversionCompleted;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_surface is not null) _surface.Editor = null;
        if (_toolbar is not null) _toolbar.Editor = null;
        base.OnApplyTemplate(e);
        _surface = e.NameScope.Get<DocumentSurface>("PART_Surface");
        Scroller = e.NameScope.Get<ScrollViewer>("PART_ScrollViewer");
        _toolbar = e.NameScope.Find<TextaloniaToolbar>("PART_Toolbar");
        _surface.Editor = this;
        UpdatePageView();
        if (_toolbar is not null) _toolbar.Editor = this;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModeProperty || change.Property == ZoomProperty ||
            change.Property == PageGapProperty || change.Property == PagesPerRowProperty)
        { UpdatePageView(change); return; }
        if (change.Property == InlineResourceResolverProperty || change.Property == InlineImageOptionsProperty || change.Property == InlineControlFactoriesProperty)
        { _surface?.ResetInlineViews(); _surface?.Refresh(); return; }
        if (change.Property == MaxShapingCharactersProperty) { _surface?.Refresh(); return; }
        if (_synchronizing) return;
        if (change.Property == DocumentProperty)
        {
            if (!ReferenceEquals(change.NewValue, Session.Document)) Session.Load((FlowDocument?)change.NewValue ?? new());
        }
        else if (change.Property == TextProperty)
        {
            if ((string?)change.NewValue != Session.Document.Text) Session.Load(FlowDocument.FromText((string?)change.NewValue ?? ""));
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
        var storyChanged = ObserveActiveStory();
        var documentChanged = !ReferenceEquals(GetValue(DocumentProperty), Session.Document);
        if (documentChanged) _surface?.ClearStoryProjections();
        var selectionChanged = storyChanged || SelectionStart != Session.Selection.Anchor || SelectionEnd != Session.Selection.Active;
        _synchronizing = true;
        try
        {
            SetCurrentValue(DocumentProperty, Session.Document);
            if (SynchronizeText && (_textRevision != Session.Revision || sender == this))
            { SetCurrentValue(TextProperty, Session.ActiveStoryId == Guid.Empty ? Session.Index.Text : Session.Document.Text); _textRevision = Session.Revision; }
            SetCurrentValue(SelectionStartProperty, Session.Selection.Anchor);
            SetCurrentValue(SelectionEndProperty, Session.Selection.Active);
            SetCurrentValue(IsReadOnlyProperty, Session.IsReadOnly);
        }
        finally { _synchronizing = false; }
        foreach (var command in _commands) command.RaiseCanExecuteChanged();
        _surface?.Refresh(!_preservingView && (selectionChanged || documentChanged && Session.LastEdit is { Reset: false }), invalidateLayout: documentChanged || storyChanged);
        OnProofingSessionChanged();
        if (selectionChanged && SpellingDiagnostics.Count > 0) _surface?.RefreshProofingContextMenu();
        if (documentChanged) DocumentChanged?.Invoke(this, EventArgs.Empty);
        if (selectionChanged) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FocusDocument() => _surface?.Focus();
    internal virtual FlowDocument PresentationDocument => Document;
    internal void RefreshPresentation() => _surface?.Refresh();
    internal void ReplaceDocumentPreservingView(FlowDocument document, int anchor, int active)
    {
        _preservingView = true;
        try { Document = document; Session.Select(anchor, active); }
        finally { _preservingView = false; }
    }
    public void SelectAll() => Session.SelectAll();
    public void Undo() => Session.Undo();
    public void Redo() => Session.Redo();
    public void InsertText(string text) => Session.InsertText(text);
    public void InsertTable(int rows = 2, int columns = 3) => Session.InsertTable(rows, columns);
    public void ApplyStyle(Func<TextStyle, TextStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (CellSelection is null) Session.ApplyStyle(change); else ApplySelectedTextStyle(change);
    }
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

    public async Task<DocumentLoadResult> LoadWithReportAsync(Stream stream, IDocumentFormat format,
        ConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var revision = Session.Revision;
        var result = await format.LoadWithReportAsync(stream, options, cancellationToken);
        if (Session.Revision != revision) throw new InvalidOperationException("The document changed while the file was loading.");
        Document = result.Document;
        PublishConversion(result.Report);
        return result;
    }

    public async Task<DocumentSaveResult> SaveWithReportAsync(Stream stream, IDocumentFormat format,
        ConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await format.SaveWithReportAsync(Session.Document, stream, options, cancellationToken);
        PublishConversion(result.Report);
        return result;
    }

    private void PublishConversion(ConversionReport report)
    {
        LastConversionReport = report;
        ConversionCompleted?.Invoke(this, new(report));
    }

    // An injectable adapter lets delayed/failed clipboard operations be exercised without changing platform ownership.
    internal Func<IEditorClipboard?>? ClipboardProvider { get; set; }
    private IEditorClipboard? Clipboard => ClipboardProvider is { } provider ? provider() :
        TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? new PlatformEditorClipboard(clipboard) : null;

    public async Task CopyAsync() => await CopyCoreAsync();
    private async Task<bool> CopyCoreAsync()
    {
        var cells = CellSelection;
        if (Session.Selection.IsEmpty && cells is null || Clipboard is not { } clipboard) return false;
        using var diagnostics = ConversionDiagnostics.Begin();
        var fragment = cells is null ? Session.CopyFragment() : Session.CopyCells(cells.TableId, cells.Row, cells.Column, cells.RowCount, cells.ColumnCount);
        var data = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText(cells is null ? Session.SelectedText : fragment.Document.Text);
        item.Set(FragmentClipboardFormat, ClipboardInterchange.Serialize(fragment));
        // Keep the older native document flavor available to older Textalonia builds.
        item.Set(NativeClipboardFormat, DocumentFormats.Json.Serialize(fragment.Document));
        // Clipboard commands finish preparing all flavors before yielding to platform access.
        // A worker dispatch here would let an immediately following paste overtake the copy.
        string html;
        using (var htmlDiagnostics = ConversionDiagnostics.Begin())
        {
            DocumentFormatExtensions.ReportExportLosses(DocumentFormats.Html, fragment.Document);
            html = DocumentFormats.Html.Serialize(fragment.Document);
            diagnostics.AddRange(htmlDiagnostics.ToReport().Diagnostics.Select(d => d with { Fallback = "HTML fallback: " + d.Fallback }));
        }
        if (OperatingSystem.IsWindows()) item.Set(WindowsHtmlFormat, ClipboardInterchange.EncodeWindowsHtml(html));
        else item.Set(HtmlClipboardFormat, html);
        data.Add(item);
        // Ownership transfers to the clipboard; do not dispose data after a successful transfer.
        await clipboard.SetDataAsync(data);
        PublishConversion(diagnostics.ToReport());
        return true;
    }

    public async Task CutAsync()
    {
        if (!CanEdit(EditOperation.Clipboard)) return;
        var revision = Session.Revision;
        var selection = Session.Selection;
        var cells = CellSelection;
        if (await CopyCoreAsync() && CanEdit(EditOperation.Clipboard) && Session.Revision == revision && Session.Selection == selection && CellSelection == cells)
        {
            if (cells is not null) ClearSelectedTableCellContents();
            else Session.InsertText("");
        }
    }

    public async Task PasteAsync()
    {
        if (!CanEdit(EditOperation.Clipboard) || Clipboard is not { } clipboard) return;
        var revision = Session.Revision;
        var selection = Session.Selection;
        using var diagnostics = ConversionDiagnostics.Begin();
        using var data = await clipboard.TryGetDataAsync();
        if (data is null) return;
        DocumentFragment? fragment = null;
        // Preference order is versioned native, legacy native, HTML, then plain text.
        foreach (var format in new[] { FragmentClipboardFormat, NativeClipboardFormat })
        {
            var native = await data.TryGetValueAsync(format);
            if (native is null) continue;
            try { fragment = ClipboardInterchange.Parse(native); break; }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or NotSupportedException)
            {
                ConversionDiagnostics.Report("clipboard.native-rejected", "Invalid or unsupported native clipboard payload",
                    "Try the next native flavor, then HTML, then plain text.");
            }
        }
        if (fragment is null)
        {
            try
            {
                var html = OperatingSystem.IsWindows()
                    ? (await data.TryGetValueAsync(WindowsHtmlFormat) is { } bytes ? ClipboardInterchange.DecodeWindowsHtml(bytes) : null)
                    : await data.TryGetValueAsync(HtmlClipboardFormat);
                if (html is not null)
                    fragment = new() { Document = DocumentFormats.Html.Parse(ClipboardInterchange.ExtractHtmlFragment(html)) };
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or NotSupportedException)
            {
                ConversionDiagnostics.Report("clipboard.html-rejected", "Invalid or unsupported HTML clipboard payload", "Try plain text.");
            }
        }
        var text = fragment is null ? await data.TryGetTextAsync() : null;
        if (!CanEdit(EditOperation.Clipboard) || revision != Session.Revision || Session.Selection != selection) return;
        if (fragment is not null) Session.InsertFragment(fragment);
        else if (text is not null) Session.InsertText(text);
        PublishConversion(diagnostics.ToReport());
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
    internal void SetLayoutError(ShapingLimitExceededException? error)
    {
        var previous = _layoutError;
        if (previous?.ParagraphId == error?.ParagraphId && previous?.CharacterLimit == error?.CharacterLimit &&
            previous?.RequestedCharacters == error?.RequestedCharacters) return;
        SetAndRaise(LayoutErrorProperty, ref _layoutError, error);
        if (error is not null) ReportError(error);
        else if (ReferenceEquals(LastError, previous)) LastError = null;
    }
    internal void Run(Action action, bool focusDocument = true)
    {
        try { LastError = null; action(); if (focusDocument) FocusDocument(); }
        catch (Exception ex) { ReportError(ex); }
    }
    internal bool CanEdit(EditOperation operation) => Session.GetCapability(operation) == CommandCapability.Enabled;

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



internal interface IEditorClipboard
{
    Task SetDataAsync(DataTransfer data);
    Task<IAsyncDataTransfer?> TryGetDataAsync();
}

internal sealed class PlatformEditorClipboard(IClipboard clipboard) : IEditorClipboard
{
    public Task SetDataAsync(DataTransfer data) => clipboard.SetDataAsync(data);
    public Task<IAsyncDataTransfer?> TryGetDataAsync() => clipboard.TryGetDataAsync();
}
