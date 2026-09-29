using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>Reusable document outline and bookmark navigation panel.</summary>
public sealed class TextaloniaNavigationPanel : StackPanel
{
    public static readonly StyledProperty<TextaloniaEditor?> EditorProperty =
        AvaloniaProperty.Register<TextaloniaNavigationPanel, TextaloniaEditor?>(nameof(Editor));

    private readonly TextBlock _outlineLabel = new();
    private readonly TextBlock _bookmarkLabel = new();
    private readonly ListBox _outline = new() { MaxHeight = 150 };
    private readonly ListBox _bookmarks = new() { MaxHeight = 110 };
    private readonly TextBox _bookmarkName = new();
    private readonly Button _goHeading;
    private readonly Button _goBookmark;
    private readonly Button _addBookmark;
    private readonly Button _renameBookmark;
    private readonly Button _deleteBookmark;
    private readonly Button _close;
    private Func<string, string?>? _localize;
    private TextaloniaEditor? _observedEditor;
    private bool _attached;

    public TextaloniaEditor? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    /// <summary>Optional host lookup for Textalonia.Navigation.* keys.</summary>
    public Func<string, string?>? Localize { get => _localize; set { _localize = value; ApplyLabels(); } }
    public event EventHandler? CloseRequested;

    public TextaloniaNavigationPanel()
    {
        Width = 320; Spacing = 5;
        Children.Add(_outlineLabel);
        Children.Add(_outline);
        _goHeading = Button(() => { if (_outline.SelectedItem is OutlineItem item) Editor?.Commands.Execute(EditorCommandId.NavigateToOutline, item.Entry); });
        Children.Add(_goHeading);
        Children.Add(_bookmarkLabel);
        Children.Add(_bookmarks);
        _goBookmark = Button(() => { if (_bookmarks.SelectedItem is string name) Editor?.Commands.Execute(EditorCommandId.NavigateToBookmark, name); });
        Children.Add(_goBookmark);
        _bookmarkName.MinHeight = 30;
        Children.Add(_bookmarkName);
        var edit = new WrapPanel();
        _addBookmark = Button(() =>
        {
            if (Editor is { } editor)
                editor.Commands.Execute(EditorCommandId.InsertBookmark, _bookmarkName.Text);
            RefreshItems();
        });
        _renameBookmark = Button(() =>
        {
            if (Editor is { } editor && _bookmarks.SelectedItem is string selected)
                editor.Commands.Execute(EditorCommandId.RenameBookmark, new EditorBookmarkRename(selected, _bookmarkName.Text ?? ""));
            RefreshItems();
        });
        _deleteBookmark = Button(() =>
        {
            if (Editor is { } editor && _bookmarks.SelectedItem is string selected)
                editor.Commands.Execute(EditorCommandId.DeleteBookmark, selected);
            RefreshItems();
        });
        edit.Children.Add(_addBookmark); edit.Children.Add(_renameBookmark); edit.Children.Add(_deleteBookmark);
        Children.Add(edit);
        _close = Button(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        Children.Add(_close);
        _bookmarks.SelectionChanged += (_, _) =>
        {
            if (_bookmarks.SelectedItem is string name) _bookmarkName.Text = name;
            RefreshButtons();
        };
        _outline.SelectionChanged += (_, _) => RefreshButtons();
        _bookmarkName.TextChanged += (_, _) => RefreshButtons();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                if (_outline.IsKeyboardFocusWithin && _outline.SelectedItem is OutlineItem heading)
                { Editor?.Commands.Execute(EditorCommandId.NavigateToOutline, heading.Entry); e.Handled = true; }
                else if (_bookmarks.IsKeyboardFocusWithin && _bookmarks.SelectedItem is string bookmark)
                { Editor?.Commands.Execute(EditorCommandId.NavigateToBookmark, bookmark); e.Handled = true; }
            }
        };
        ApplyLabels();
        RefreshItems();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty) return;
        if (_observedEditor is not null) Unsubscribe();
        if (_attached) Subscribe(Editor);
        ApplyLabels(); RefreshItems();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; Subscribe(Editor); RefreshItems(); }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; Unsubscribe(); base.OnDetachedFromVisualTree(e); }

    private void Subscribe(TextaloniaEditor? editor)
    {
        if (editor is null || ReferenceEquals(editor, _observedEditor)) return;
        _observedEditor = editor;
        editor.Session.Changed += EditorStateChanged;
        editor.DocumentChanged += EditorStateChanged;
        editor.PropertyChanged += EditorPropertyChanged;
        editor.Commands.LocalizationChanged += LocalizationChanged;
    }

    private void Unsubscribe()
    {
        if (_observedEditor is not { } editor) return;
        editor.Session.Changed -= EditorStateChanged;
        editor.DocumentChanged -= EditorStateChanged;
        editor.PropertyChanged -= EditorPropertyChanged;
        editor.Commands.LocalizationChanged -= LocalizationChanged;
        _observedEditor = null;
    }

    private void EditorStateChanged(object? sender, EventArgs e) => RefreshItems();
    private void EditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => RefreshButtons();
    private void LocalizationChanged(object? sender, EventArgs e) => ApplyLabels();

    /// <summary>Rebuilds the visible outline and bookmark lists from the current document.</summary>
    public void RefreshItems()
    {
        var heading = (_outline.SelectedItem as OutlineItem)?.Entry;
        var bookmark = _bookmarks.SelectedItem as string;
        var outlines = Editor?.GetOutline().Select(entry => new OutlineItem(entry)).ToArray() ?? [];
        var bookmarks = Editor?.Document.Bookmarks.Select(item => item.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray() ?? [];
        _outline.ItemsSource = outlines;
        _bookmarks.ItemsSource = bookmarks;
        _outline.SelectedItem = outlines.FirstOrDefault(item => Equals(item.Entry, heading));
        _bookmarks.SelectedItem = bookmarks.FirstOrDefault(item => item == bookmark);
        RefreshButtons();
    }

    public void FocusNavigation() => _outline.Focus();

    private void RefreshButtons()
    {
        var editor = Editor;
        var capability = editor?.Session.GetCapability(EditOperation.Metadata) ?? CommandCapability.Disabled;
        var canEdit = capability == CommandCapability.Enabled;
        _goHeading.IsEnabled = _outline.SelectedItem is OutlineItem item && editor?.Commands.CanExecute(EditorCommandId.NavigateToOutline, item.Entry) == true;
        _goBookmark.IsEnabled = _bookmarks.SelectedItem is string name && editor?.Commands.CanExecute(EditorCommandId.NavigateToBookmark, name) == true;
        foreach (var control in new Control[] { _bookmarkName, _addBookmark, _renameBookmark, _deleteBookmark })
            control.IsVisible = capability != CommandCapability.Hidden;
        _bookmarkName.IsEnabled = canEdit;
        _addBookmark.IsEnabled = editor?.Commands.CanExecute(EditorCommandId.InsertBookmark, _bookmarkName.Text) == true;
        _renameBookmark.IsEnabled = _bookmarks.SelectedItem is string selected &&
            editor?.Commands.CanExecute(EditorCommandId.RenameBookmark, new EditorBookmarkRename(selected, _bookmarkName.Text ?? "")) == true;
        _deleteBookmark.IsEnabled = _bookmarks.SelectedItem is string bookmark &&
            editor?.Commands.CanExecute(EditorCommandId.DeleteBookmark, bookmark) == true;
    }

    private void ApplyLabels()
    {
        AutomationProperties.SetName(this, Text("Panel", "Document navigation"));
        AutomationProperties.SetName(_outline, Text("DocumentOutline", "Document outline"));
        AutomationProperties.SetName(_bookmarks, Text("Bookmarks", "Bookmarks"));
        AutomationProperties.SetName(_bookmarkName, Text("BookmarkName", "Bookmark name"));
        _outlineLabel.Text = Text("Outline", "Outline");
        _bookmarkLabel.Text = Text("Bookmarks", "Bookmarks");
        _bookmarkName.PlaceholderText = Text("BookmarkName", "Bookmark name");
        Set(_goHeading, "GoToHeading", "Go to heading");
        Set(_goBookmark, "GoToBookmark", "Go to bookmark");
        Set(_addBookmark, "AddBookmark", "Add bookmark");
        Set(_renameBookmark, "RenameBookmark", "Rename bookmark");
        Set(_deleteBookmark, "DeleteBookmark", "Delete bookmark");
        Set(_close, "Close", "Close navigation");
    }

    private string Text(string key, string fallback)
    {
        var localizationKey = "Textalonia.Navigation." + key;
        return Localize?.Invoke(localizationKey) is { Length: > 0 } local ? local :
            Editor?.Commands.Localize?.Invoke(localizationKey) is { Length: > 0 } catalog ? catalog : fallback;
    }

    private void Set(Button button, string key, string fallback)
    {
        var text = Text(key, fallback);
        button.Content = text;
        AutomationProperties.SetName(button, text);
    }

    private static Button Button(Action action)
    {
        var button = new Button { MinHeight = 30, Margin = new Thickness(2), Padding = new Thickness(8, 4) };
        button.Click += (_, _) => action();
        return button;
    }

    private sealed record OutlineItem(DocumentOutlineEntry Entry)
    {
        public override string ToString() => new string(' ', Math.Max(0, Entry.Level - 1) * 2) + Entry.Text;
    }
}
