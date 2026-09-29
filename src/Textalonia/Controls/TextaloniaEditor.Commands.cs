using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Input;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>Stable identifiers shared by editor command surfaces, shortcuts and automation.</summary>
public enum EditorCommandId
{
    Undo, Redo, Cut, Copy, Paste, PasteSpecial, SelectAll,
    Bold, Italic, Underline, Strikethrough,
    Find, Replace, Navigation,
    InsertTable, InsertSymbol, InsertBookmark, InsertMergeField, InsertPageField,
    Styles, Font, Paragraph, Tabs, PageSetup, PageNumbering, DocumentProperties,
    InsertPicture, PictureProperties, RemovePicture, Watermark, TableProperties, HeaderFooterOptions,
    FootnoteOptions, EndnoteOptions, InsertFootnote, InsertEndnote,
    EditHeader, EditFooter, CloseStory, UpdateFields, Proofing,
    PrintPreview, Print, ExportPdf,
    ViewSimple, ViewDraft, ViewPrintLayout, ToggleRulers, ZoomIn, ZoomOut, SetZoom, FitWidth, FitPage,
    SetPagesPerRow, SetPageGap, GoToPage,
    TableInsertRow, TableInsertColumn, TableDeleteRows, TableDeleteColumns, TableMergeCells, TableSplitCells,
    RenameBookmark, DeleteBookmark, NavigateToBookmark, NavigateToOutline
}

/// <summary>Presentation metadata for a command. Localization keys are stable; default text is an English fallback.</summary>
public sealed record EditorCommandDescriptor(
    EditorCommandId Id, string LocalizationKey, string DefaultText, string Group,
    Type? ParameterType = null, IReadOnlyList<KeyGesture>? Shortcuts = null)
{
    public KeyGesture? Shortcut => Shortcuts?.FirstOrDefault();
}

/// <summary>Dimensions supplied to the Insert Table command.</summary>
public sealed record EditorTableSize(int Rows, int Columns);

/// <summary>The current and replacement names supplied to the Rename Bookmark command.</summary>
public sealed record EditorBookmarkRename(string Name, string NewName);

/// <summary>A live, editor-bound command with permission and checked state.</summary>
public sealed class TextaloniaCommand : ICommand, INotifyPropertyChanged
{
    private readonly TextaloniaEditor _editor;
    private readonly TextaloniaCommandCatalog _catalog;
    private readonly Func<CommandCapability> _capability;
    private readonly Func<object?, bool> _validParameter;
    private readonly Func<object?, Task> _execute;
    private readonly Func<(bool? Checked, bool Mixed)>? _checkedState;
    private readonly bool _focusDocument;
    private CommandCapability _currentCapability;
    private bool? _isChecked;
    private bool _isMixed;
    private bool _running;

    internal TextaloniaCommand(TextaloniaEditor editor, TextaloniaCommandCatalog catalog,
        EditorCommandDescriptor descriptor, Func<CommandCapability> capability,
        Func<object?, bool> validParameter, Func<object?, Task> execute,
        Func<(bool? Checked, bool Mixed)>? checkedState, bool focusDocument)
    {
        _editor = editor; _catalog = catalog; Descriptor = descriptor;
        _capability = capability; _validParameter = validParameter; _execute = execute;
        _checkedState = checkedState; _focusDocument = focusDocument;
        Refresh();
    }

    public EditorCommandDescriptor Descriptor { get; }
    public EditorCommandId Id => Descriptor.Id;
    public string LocalizationKey => Descriptor.LocalizationKey;
    public string DisplayText => _catalog.Localize?.Invoke(LocalizationKey) is { Length: > 0 } localized ? localized : Descriptor.DefaultText;
    public KeyGesture? Shortcut => Descriptor.Shortcut;
    public CommandCapability Capability => _currentCapability;
    public bool IsVisible => Capability != CommandCapability.Hidden;
    public bool? IsChecked => _isChecked;
    public bool IsMixed => _isMixed;
    public bool IsRunning => _running;

    public event EventHandler? CanExecuteChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CanExecute(object? parameter) => !_running && _capability() == CommandCapability.Enabled && _validParameter(parameter);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    /// <summary>Executes through the same path as ICommand, and reports whether execution was admitted.</summary>
    public async Task<bool> ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return false;
        _running = true; Raise(nameof(IsRunning)); CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            _editor.LastCommandErrorReset();
            await _execute(parameter);
            if (_focusDocument) _editor.FocusDocument();
            return true;
        }
        catch (Exception exception)
        {
            _editor.ReportError(exception);
            return false;
        }
        finally
        {
            _running = false; Raise(nameof(IsRunning));
            Refresh(); CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void Refresh()
    {
        var capability = _capability();
        if (capability != _currentCapability)
        {
            _currentCapability = capability;
            Raise(nameof(Capability)); Raise(nameof(IsVisible));
        }
        if (_checkedState is { } state)
        {
            var (isChecked, mixed) = state();
            if (_isChecked != isChecked) { _isChecked = isChecked; Raise(nameof(IsChecked)); }
            if (_isMixed != mixed) { _isMixed = mixed; Raise(nameof(IsMixed)); }
        }
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RefreshText() => Raise(nameof(DisplayText));
    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>Per-editor command registry. Hosts can bind a command, invoke it by ID, or replace its text by localization key.</summary>
public sealed class TextaloniaCommandCatalog
{
    private readonly TextaloniaEditor _editor;
    private readonly Dictionary<EditorCommandId, TextaloniaCommand> _items = [];
    private Func<string, string?>? _localize;
    internal event EventHandler? LocalizationChanged;

    internal TextaloniaCommandCatalog(TextaloniaEditor editor) => _editor = editor;

    public TextaloniaCommand this[EditorCommandId id] => _items[id];
    public IReadOnlyCollection<TextaloniaCommand> All => _items.Values;
    /// <summary>Optional host localizer. Return null to use the command's default English text.</summary>
    public Func<string, string?>? Localize
    {
        get => _localize;
        set
        {
            _localize = value;
            foreach (var command in _items.Values) command.RefreshText();
            LocalizationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool TryGet(EditorCommandId id, out TextaloniaCommand? command) => _items.TryGetValue(id, out command);
    public bool CanExecute(EditorCommandId id, object? parameter = null) => _items.TryGetValue(id, out var command) && command.CanExecute(parameter);
    public void Execute(EditorCommandId id, object? parameter = null) => _items[id].Execute(parameter);
    public Task<bool> ExecuteAsync(EditorCommandId id, object? parameter = null) => _items[id].ExecuteAsync(parameter);
    public void Refresh() { foreach (var command in _items.Values) command.Refresh(); }

    /// <summary>Executes a declared shortcut. Returns true for a recognized shortcut even when editing is disabled.</summary>
    public bool TryExecuteShortcut(Key key, KeyModifiers modifiers)
    {
        foreach (var command in _items.Values)
        {
            if (command.Descriptor.Shortcuts is not { } shortcuts ||
                !shortcuts.Any(shortcut => shortcut.Key == key && shortcut.KeyModifiers == modifiers)) continue;
            _editor.CancelCommandComposition();
            if (command.CanExecute(null)) command.Execute(null);
            return true;
        }
        return false;
    }

    internal TextaloniaCommand Add(TextaloniaEditor editor, EditorCommandId id, string text, string group,
        Func<CommandCapability> capability, Func<object?, Task> execute,
        Type? parameterType = null, Func<object?, bool>? validParameter = null,
        Func<(bool? Checked, bool Mixed)>? checkedState = null, bool focusDocument = true,
        params KeyGesture[] shortcuts)
    {
        var descriptor = new EditorCommandDescriptor(id, $"Textalonia.Commands.{id}", text, group,
            parameterType, shortcuts.Length == 0 ? null : shortcuts);
        var command = new TextaloniaCommand(editor, this, descriptor, capability,
            validParameter ?? (_ => true), execute, checkedState, focusDocument);
        _items.Add(id, command);
        return command;
    }
}

public partial class TextaloniaEditor
{
    /// <summary>Commands shared by built-in UI, keyboard shortcuts and host automation.</summary>
    public TextaloniaCommandCatalog Commands { get; private set; } = null!;

    private TextaloniaCommandCatalog CreateCommandCatalog()
    {
        var catalog = new TextaloniaCommandCatalog(this);
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyGesture Shortcut(Key key, bool shift = false) => new(key, primary | (shift ? KeyModifiers.Shift : KeyModifiers.None));
        CommandCapability Edit(EditOperation operation) => Session.GetCapability(operation);
        CommandCapability Read() => CommandCapability.Enabled;
        CommandCapability ClipboardRead() => Session.EditPolicy.GetCapability(EditOperation.Clipboard);
        CommandCapability Table() => CellSelection is not null || Session.CurrentCell() is not null
            ? Edit(EditOperation.Tables) : CommandCapability.Disabled;
        CommandCapability Picture() => CurrentImageOrOle is not null ? Edit(EditOperation.InlineObjects) : CommandCapability.Disabled;
        CommandCapability HeaderFooter(bool footer)
        {
            var current = Session.CurrentSection?.Id;
            var index = Document.Sections.FindIndex(section => section.Id == current);
            return index >= 0 && Document.ResolveHeaderFooter(index, footer, HeaderFooterVariant.Primary) is not null
                ? CommandCapability.Enabled : Edit(EditOperation.Structure);
        }
        static Task Done(Action action) { action(); return Task.CompletedTask; }
        void Action(EditorCommandId id, string text, string group, Func<CommandCapability> capability,
            Action execute, Func<(bool? Checked, bool Mixed)>? state = null, params KeyGesture[] shortcuts) =>
            catalog.Add(this, id, text, group, capability, _ => Done(execute), checkedState: state,
                focusDocument: group != "View" && id is not (EditorCommandId.Find or EditorCommandId.Replace), shortcuts: shortcuts);
        void Dialog(EditorCommandId id, string text, string group, Func<CommandCapability> capability, Func<Task<bool>> show) =>
            catalog.Add(this, id, text, group, capability, async _ => { await show(); }, focusDocument: false);
        Func<(bool? Checked, bool Mixed)> Format(Func<SelectionFormattingState, FormattingValue<bool>> read) => () =>
        {
            var value = read(FormattingState);
            return (value.IsMixed ? null : value.Value, value.IsMixed);
        };
        Func<(bool? Checked, bool Mixed)> View(DocumentViewMode mode) => () => (ViewMode == mode, false);

        Action(EditorCommandId.Bold, "Bold", "Home", () => Edit(EditOperation.Formatting), ToggleSelectedBold, Format(s => s.Bold), Shortcut(Key.B));
        Action(EditorCommandId.Italic, "Italic", "Home", () => Edit(EditOperation.Formatting), ToggleSelectedItalic, Format(s => s.Italic), Shortcut(Key.I));
        Action(EditorCommandId.Underline, "Underline", "Home", () => Edit(EditOperation.Formatting), ToggleSelectedUnderline, Format(s => s.Underline), Shortcut(Key.U));
        Action(EditorCommandId.Strikethrough, "Strikethrough", "Home", () => Edit(EditOperation.Formatting), ToggleSelectedStrikethrough, Format(s => s.Strikethrough));
        Action(EditorCommandId.Undo, "Undo", "Home", () => Edit(EditOperation.Undo), Session.Undo, null, Shortcut(Key.Z));
        Action(EditorCommandId.Redo, "Redo", "Home", () => Edit(EditOperation.Redo), Session.Redo, null, Shortcut(Key.Z, true), Shortcut(Key.Y));
        catalog.Add(this, EditorCommandId.Cut, "Cut", "Home", () => Edit(EditOperation.Clipboard), _ => CutAsync(),
            validParameter: _ => !Session.Selection.IsEmpty || CellSelection is not null, shortcuts: [Shortcut(Key.X)]);
        catalog.Add(this, EditorCommandId.Copy, "Copy", "Home", ClipboardRead, _ => CopyAsync(),
            validParameter: _ => !Session.Selection.IsEmpty || CellSelection is not null, shortcuts: [Shortcut(Key.C)]);
        catalog.Add(this, EditorCommandId.Paste, "Paste", "Home", () => Edit(EditOperation.Clipboard), _ => PasteAsync(),
            shortcuts: [Shortcut(Key.V)]);
        catalog.Add(this, EditorCommandId.PasteSpecial, "Paste Special", "Home", () => Edit(EditOperation.Clipboard),
            parameter => PasteSpecialAsync((PasteSpecialFormat)parameter!), typeof(PasteSpecialFormat),
            parameter => parameter is PasteSpecialFormat value && Enum.IsDefined(value));
        Action(EditorCommandId.SelectAll, "Select All", "Home", Read, Session.SelectAll, null, Shortcut(Key.A));

        Action(EditorCommandId.Find, "Find", "Home", Read, () => { _surface?.Composition.Cancel(); RequestFind(); }, null, Shortcut(Key.F));
        Action(EditorCommandId.Replace, "Replace", "Home", Read, () => { _surface?.Composition.Cancel(); RequestReplace(); }, null, Shortcut(Key.H));
        Action(EditorCommandId.Navigation, "Navigation", "View", Read,
            () => { _surface?.Composition.Cancel(); if (ShowToolbar) _toolbar?.OpenNavigation(); NavigationRequested?.Invoke(this, EventArgs.Empty); });

        catalog.Add(this, EditorCommandId.InsertTable, "Insert Table", "Insert", () => Edit(EditOperation.Tables),
            parameter => Done(() =>
            {
                var size = parameter as EditorTableSize;
                InsertTable(size?.Rows ?? 2, size?.Columns ?? 3);
            }), typeof(EditorTableSize), parameter => parameter is null or EditorTableSize { Rows: > 0, Columns: > 0 });
        catalog.Add(this, EditorCommandId.InsertSymbol, "Insert Symbol", "Insert", () => Edit(EditOperation.Text),
            async parameter => { if (parameter is string symbol) InsertSymbol(symbol); else await ShowInsertSymbolDialogAsync(); },
            typeof(string), parameter => parameter is null or string { Length: > 0 });
        catalog.Add(this, EditorCommandId.InsertBookmark, "Bookmark", "Insert", () => Edit(EditOperation.Metadata),
            parameter => Done(() => AddBookmark((string)parameter!)), typeof(string),
            parameter => parameter is string name && InlineDescriptor.ValidKey(name), focusDocument: false);
        catalog.Add(this, EditorCommandId.RenameBookmark, "Rename Bookmark", "References", () => Edit(EditOperation.Metadata),
            parameter => Done(() =>
            {
                var rename = (EditorBookmarkRename)parameter!;
                RenameBookmark(rename.Name, rename.NewName);
            }), typeof(EditorBookmarkRename),
            parameter => parameter is EditorBookmarkRename rename && InlineDescriptor.ValidKey(rename.NewName) &&
                Session.Document.Bookmarks.Any(bookmark => bookmark.Name == rename.Name) &&
                (rename.Name == rename.NewName || !Session.Document.Bookmarks.Any(bookmark => bookmark.Name == rename.NewName)),
            focusDocument: false);
        catalog.Add(this, EditorCommandId.DeleteBookmark, "Delete Bookmark", "References", () => Edit(EditOperation.Metadata),
            parameter => Done(() => DeleteBookmark((string)parameter!)), typeof(string),
            parameter => parameter is string name && Session.Document.Bookmarks.Any(bookmark => bookmark.Name == name),
            focusDocument: false);
        catalog.Add(this, EditorCommandId.NavigateToBookmark, "Go to Bookmark", "References", Read,
            parameter => Done(() => NavigateToBookmark((string)parameter!)), typeof(string),
            parameter => parameter is string name && Session.Document.Bookmarks.Any(bookmark => bookmark.Name == name),
            focusDocument: false);
        catalog.Add(this, EditorCommandId.NavigateToOutline, "Go to Heading", "References", Read,
            parameter => Done(() => NavigateToOutline((DocumentOutlineEntry)parameter!)), typeof(DocumentOutlineEntry),
            parameter => parameter is DocumentOutlineEntry entry &&
                (entry.StoryId == Guid.Empty || Session.Document.Stories.ContainsKey(entry.StoryId)) &&
                Session.Document.GetStoryIndex(entry.StoryId).Tree.Paths?.Find(entry.ParagraphId) is not null,
            focusDocument: false);
        catalog.Add(this, EditorCommandId.InsertMergeField, "Merge Field", "Mailings", () => Edit(EditOperation.InlineObjects),
            parameter => Done(() => InsertMergeField((string)parameter!)), typeof(string),
            parameter => parameter is string { Length: > 0 });
        catalog.Add(this, EditorCommandId.InsertPageField, "Page Field", "Insert", () => Edit(EditOperation.InlineObjects),
            parameter => Done(() => InsertPageField(parameter is PageFieldKind kind ? kind : PageFieldKind.Page)),
            typeof(PageFieldKind), parameter => parameter is null || parameter is PageFieldKind kind && Enum.IsDefined(kind));

        Dialog(EditorCommandId.Styles, "Styles", "Home", () => Edit(EditOperation.Formatting), ShowStylesDialogAsync);
        Dialog(EditorCommandId.Font, "Font", "Home", () => Edit(EditOperation.Formatting), ShowFontDialogAsync);
        Dialog(EditorCommandId.Paragraph, "Paragraph", "Home", () => Edit(EditOperation.Formatting), ShowParagraphDialogAsync);
        Dialog(EditorCommandId.Tabs, "Tabs", "Home", () => Edit(EditOperation.Formatting), ShowTabsDialogAsync);
        Dialog(EditorCommandId.PageSetup, "Page Setup", "Page Layout", () => Edit(EditOperation.Structure), ShowPageSetupDialogAsync);
        Dialog(EditorCommandId.PageNumbering, "Page Numbering", "Page Layout", () => Edit(EditOperation.Structure), ShowPageNumberingDialogAsync);
        Dialog(EditorCommandId.DocumentProperties, "Document Properties", "File", () => Edit(EditOperation.Metadata), ShowDocumentPropertiesDialogAsync);
        Dialog(EditorCommandId.InsertPicture, "Picture", "Insert", () => Edit(EditOperation.InlineObjects), ShowInsertImageDialogAsync);
        Dialog(EditorCommandId.PictureProperties, "Picture Properties", "Picture", Picture, ShowImagePropertiesDialogAsync);
        Action(EditorCommandId.RemovePicture, "Remove Picture", "Picture", Picture, RemoveCurrentImageOrOle);
        Dialog(EditorCommandId.Watermark, "Watermark", "Page Layout", () => Edit(EditOperation.Structure), ShowWatermarkDialogAsync);
        Dialog(EditorCommandId.TableProperties, "Table Properties", "Table", Table, ShowTablePropertiesDialogAsync);
        Dialog(EditorCommandId.HeaderFooterOptions, "Header and Footer Options", "Header/Footer", () => Edit(EditOperation.Structure), ShowHeaderFooterDialogAsync);
        Dialog(EditorCommandId.FootnoteOptions, "Footnote Options", "References", () => Edit(EditOperation.Structure),
            () => ShowNoteSettingsDialogAsync(DocumentNoteKind.Footnote));
        Dialog(EditorCommandId.EndnoteOptions, "Endnote Options", "References", () => Edit(EditOperation.Structure),
            () => ShowNoteSettingsDialogAsync(DocumentNoteKind.Endnote));
        Action(EditorCommandId.InsertFootnote, "Insert Footnote", "References", () => Edit(EditOperation.Structure), () => InsertFootnote());
        Action(EditorCommandId.InsertEndnote, "Insert Endnote", "References", () => Edit(EditOperation.Structure), () => InsertEndnote());
        Action(EditorCommandId.EditHeader, "Edit Header", "Header/Footer", () => HeaderFooter(false), EditHeader);
        Action(EditorCommandId.EditFooter, "Edit Footer", "Header/Footer", () => HeaderFooter(true), EditFooter);
        Action(EditorCommandId.CloseStory, "Close Header or Footer", "Header/Footer",
            () => ActiveStoryId == Guid.Empty ? CommandCapability.Disabled : CommandCapability.Enabled, CloseStory);
        Action(EditorCommandId.UpdateFields, "Update Fields", "References", () => Edit(EditOperation.Metadata), () => UpdateFieldsWithLayout());
        Action(EditorCommandId.Proofing, "Next Spelling Issue", "Proofing", Read, () => SelectNextSpellingError());

        Dialog(EditorCommandId.PrintPreview, "Print Preview", "File", Read, ShowPrintPreviewDialogAsync);
        Dialog(EditorCommandId.Print, "Print", "File", () => PrintService is null ? CommandCapability.Disabled : CommandCapability.Enabled, ShowPrintDialogAsync);
        Dialog(EditorCommandId.ExportPdf, "Export PDF", "File", () => PdfExporter is null ? CommandCapability.Disabled : CommandCapability.Enabled, ShowExportPdfDialogAsync);

        Action(EditorCommandId.ViewSimple, "Simple View", "View", Read, () => ViewMode = DocumentViewMode.Simple, View(DocumentViewMode.Simple));
        Action(EditorCommandId.ViewDraft, "Draft View", "View", Read, () => ViewMode = DocumentViewMode.Draft, View(DocumentViewMode.Draft));
        Action(EditorCommandId.ViewPrintLayout, "Print Layout", "View", Read, () => ViewMode = DocumentViewMode.PrintLayout, View(DocumentViewMode.PrintLayout));
        Action(EditorCommandId.ToggleRulers, "Rulers", "View", Read, () => ShowRulers = !ShowRulers,
            () => (ShowRulers, false));
        Action(EditorCommandId.ZoomIn, "Zoom In", "View", () => Zoom < 5 ? CommandCapability.Enabled : CommandCapability.Disabled,
            () => Zoom = Math.Min(5, Math.Round(Zoom + .1, 2)));
        Action(EditorCommandId.ZoomOut, "Zoom Out", "View", () => Zoom > .1 ? CommandCapability.Enabled : CommandCapability.Disabled,
            () => Zoom = Math.Max(.1, Math.Round(Zoom - .1, 2)));
        catalog.Add(this, EditorCommandId.SetZoom, "Set Zoom", "View", Read,
            parameter => Done(() => Zoom = (double)parameter!), typeof(double),
            parameter => parameter is double zoom && double.IsFinite(zoom) && zoom is >= .1 and <= 5,
            focusDocument: false);
        Action(EditorCommandId.FitWidth, "Fit Width", "View", Read, FitWidth);
        Action(EditorCommandId.FitPage, "Fit Page", "View", Read, FitPage);
        catalog.Add(this, EditorCommandId.SetPagesPerRow, "Pages per Row", "View", Read,
            parameter => Done(() => PagesPerRow = (int)parameter!), typeof(int),
            parameter => parameter is int count && count is >= 1 and <= 8, focusDocument: false);
        catalog.Add(this, EditorCommandId.SetPageGap, "Page Gap", "View", Read,
            parameter => Done(() => PageGap = (double)parameter!), typeof(double),
            parameter => parameter is double gap && double.IsFinite(gap) && gap is >= 0 and <= 1000,
            focusDocument: false);
        catalog.Add(this, EditorCommandId.GoToPage, "Go to Page", "View", Read,
            parameter => Done(() => GoToPage((int)parameter! - 1)), typeof(int),
            parameter => parameter is int page && page >= 1 && page <= PageCount, focusDocument: false);

        Action(EditorCommandId.TableInsertRow, "Insert Row", "Table", Table, () => InsertTableRow());
        Action(EditorCommandId.TableInsertColumn, "Insert Column", "Table", Table, () => InsertTableColumn());
        Action(EditorCommandId.TableDeleteRows, "Delete Rows", "Table", Table, DeleteTableRows);
        Action(EditorCommandId.TableDeleteColumns, "Delete Columns", "Table", Table, DeleteTableColumns);
        Action(EditorCommandId.TableMergeCells, "Merge Cells", "Table",
            () => CellSelection is null ? CommandCapability.Disabled : Edit(EditOperation.Tables), MergeSelectedTableCells);
        Action(EditorCommandId.TableSplitCells, "Split Cells", "Table", Table, SplitSelectedTableCells);
        return catalog;
    }

    private void OnCommandRelevantPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == ViewModeProperty || change.Property == ZoomProperty ||
            change.Property == PageCountProperty || change.Property == CurrentPageNumberProperty ||
            change.Property == ShowRulersProperty)
            Commands.Refresh();
    }

    internal void LastCommandErrorReset() => LastError = null;
    internal void CancelCommandComposition() => _surface?.Composition.Cancel();
}
