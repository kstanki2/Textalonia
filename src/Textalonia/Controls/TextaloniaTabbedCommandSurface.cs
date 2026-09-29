using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>Document actions owned by a host rather than by the editor.</summary>
public enum TextaloniaHostAction { New, Open, Save, MailMerge }

/// <summary>Request raised by a command surface for a host-owned document action.</summary>
public sealed class TextaloniaHostActionEventArgs(TextaloniaHostAction action) : EventArgs
{
    public TextaloniaHostAction Action { get; } = action;
}

/// <summary>
/// Optional tabbed command surface. It uses an editor's command catalog and can be
/// placed outside the editor template or retemplated by a host.
/// </summary>
[TemplatePart("PART_Tabs", typeof(TabControl), IsRequired = true)]
public class TextaloniaTabbedCommandSurface : TemplatedControl
{
    public static readonly StyledProperty<TextaloniaEditor?> EditorProperty =
        AvaloniaProperty.Register<TextaloniaTabbedCommandSurface, TextaloniaEditor?>(nameof(Editor));

    private readonly List<Action> _detach = [];
    private readonly Dictionary<TextaloniaHostAction, Button> _hostButtons = [];
    private readonly Dictionary<string, TabItem> _contextTabs = [];
    private TabControl? _tabs;
    private NumericUpDown? _pageNumber;
    private Button? _pageButton;
    private NumericUpDown? _zoomPercent;
    private Flyout? _findFlyout;
    private TextaloniaFindReplacePanel? _findPanel;
    private Flyout? _navigationFlyout;
    private TextaloniaNavigationPanel? _navigationPanel;
    private bool _updatingZoom;
    private EventHandler<TextaloniaHostActionEventArgs>? _hostActionRequested;
    private Func<string, string?>? _localize;

    internal Flyout? FindFlyout => _findFlyout;
    internal TextaloniaFindReplacePanel? FindPanel => _findPanel;
    internal Flyout? NavigationFlyout => _navigationFlyout;
    internal TextaloniaNavigationPanel? NavigationPanel => _navigationPanel;

    public TextaloniaEditor? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }

    /// <summary>Optional host localization lookup for Textalonia.CommandSurface.* keys.</summary>
    public Func<string, string?>? Localize
    {
        get => _localize;
        set { _localize = value; BuildTabs(); }
    }

    /// <summary>Raised for New, Open, Save, and Mail Merge, whose storage or data source belongs to the host.</summary>
    public event EventHandler<TextaloniaHostActionEventArgs>? HostActionRequested
    {
        add { _hostActionRequested += value; RefreshHostButtons(); }
        remove { _hostActionRequested -= value; RefreshHostButtons(); }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _tabs = e.NameScope.Get<TabControl>("PART_Tabs");
        BuildTabs();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty) return;
        if (change.OldValue is TextaloniaEditor oldEditor) DetachEditor(oldEditor);
        if (change.NewValue is TextaloniaEditor newEditor) AttachEditor(newEditor);
        BuildTabs();
    }

    private void AttachEditor(TextaloniaEditor editor)
    {
        editor.SelectionChanged += EditorStateChanged;
        editor.FindRequested += FindRequested;
        editor.ReplaceRequested += ReplaceRequested;
        editor.NavigationRequested += NavigationRequested;
        editor.ActiveStoryChanged += EditorStateChanged;
        editor.DocumentChanged += EditorStateChanged;
        editor.Session.Changed += EditorStateChanged;
        editor.TableCellSelectionChanged += EditorStateChanged;
        editor.PropertyChanged += EditorPropertyChanged;
    }

    private void DetachEditor(TextaloniaEditor editor)
    {
        editor.SelectionChanged -= EditorStateChanged;
        editor.FindRequested -= FindRequested;
        editor.ReplaceRequested -= ReplaceRequested;
        editor.NavigationRequested -= NavigationRequested;
        editor.ActiveStoryChanged -= EditorStateChanged;
        editor.DocumentChanged -= EditorStateChanged;
        editor.Session.Changed -= EditorStateChanged;
        editor.TableCellSelectionChanged -= EditorStateChanged;
        editor.PropertyChanged -= EditorPropertyChanged;
    }

    private void EditorStateChanged(object? sender, EventArgs e) => Refresh();
    private void EditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => Refresh();
    private void FindRequested(object? sender, EventArgs e) => OpenFind();
    private void ReplaceRequested(object? sender, EventArgs e) => OpenFind(replace: true);
    private void NavigationRequested(object? sender, EventArgs e) => OpenNavigation();

    /// <summary>Shows the shared find/replace panel and focuses the requested input.</summary>
    public void OpenFind(bool replace = false)
    {
        if (!IsVisible || TopLevel.GetTopLevel(this) is null || _findFlyout is null || _findPanel is null) return;
        _navigationFlyout?.Hide();
        _findFlyout.ShowAt(this);
        _findPanel.RefreshResults();
        var panel = _findPanel;
        var flyout = _findFlyout;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(panel, _findPanel) && flyout.IsOpen) panel.FocusQuery(replace);
        }, DispatcherPriority.Background);
    }

    /// <summary>Shows outline and bookmark navigation for the current document.</summary>
    public void OpenNavigation()
    {
        if (!IsVisible || TopLevel.GetTopLevel(this) is null || _navigationFlyout is null || _navigationPanel is null) return;
        _findFlyout?.Hide();
        _navigationPanel.RefreshItems();
        _navigationFlyout.ShowAt(this);
        var panel = _navigationPanel;
        var flyout = _navigationFlyout;
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(panel, _navigationPanel) && flyout.IsOpen) panel.FocusNavigation();
        }, DispatcherPriority.Background);
    }

    private void BuildTabs()
    {
        _findFlyout?.Hide(); _navigationFlyout?.Hide();
        if (_findPanel is not null) _findPanel.Editor = null;
        if (_navigationPanel is not null) _navigationPanel.Editor = null;
        _findPanel = null; _findFlyout = null; _navigationPanel = null; _navigationFlyout = null;
        foreach (var detach in _detach) detach();
        _detach.Clear(); _hostButtons.Clear(); _contextTabs.Clear();
        _pageNumber = null; _pageButton = null; _zoomPercent = null;
        if (_tabs is null) return;
        var previous = (_tabs.SelectedItem as TabItem)?.Tag as string;
        _tabs.Items.Clear();
        if (Editor is null) return;

        _findPanel = new TextaloniaFindReplacePanel { Editor = Editor };
        _findFlyout = new Flyout { Content = _findPanel };
        _findPanel.CloseRequested += (_, _) => _findFlyout?.Hide();
        _navigationPanel = new TextaloniaNavigationPanel { Editor = Editor, Localize = Localize };
        _navigationFlyout = new Flyout
        {
            Content = new ScrollViewer { Content = _navigationPanel, MaxHeight = 520,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        _navigationPanel.CloseRequested += (_, _) => { _navigationFlyout?.Hide(); Editor?.FocusDocument(); };

        AddTab("File", [HostGroup("Document", TextaloniaHostAction.New, TextaloniaHostAction.Open, TextaloniaHostAction.Save),
            Group("Output", EditorCommandId.PrintPreview, EditorCommandId.Print, EditorCommandId.ExportPdf),
            Group("Properties", EditorCommandId.DocumentProperties)]);
        AddTab("Home", [Group("Clipboard", EditorCommandId.Cut, EditorCommandId.Copy, EditorCommandId.Paste, EditorCommandId.Undo, EditorCommandId.Redo),
            PasteSpecialGroup(), Group("Font", EditorCommandId.Bold, EditorCommandId.Italic, EditorCommandId.Underline, EditorCommandId.Strikethrough, EditorCommandId.Font),
            Group("Paragraph", EditorCommandId.Styles, EditorCommandId.Paragraph, EditorCommandId.Tabs),
            Group("Editing", EditorCommandId.Find, EditorCommandId.Replace, EditorCommandId.SelectAll)]);
        AddTab("Insert", [Group("Content", EditorCommandId.InsertTable, EditorCommandId.InsertPicture, EditorCommandId.InsertSymbol),
            Group("References", EditorCommandId.InsertPageField, EditorCommandId.InsertFootnote, EditorCommandId.InsertEndnote)]);
        AddTab("Page Layout", [Group("Page", EditorCommandId.PageSetup, EditorCommandId.PageNumbering, EditorCommandId.Watermark),
            Group("Stories", EditorCommandId.EditHeader, EditorCommandId.EditFooter, EditorCommandId.HeaderFooterOptions)]);
        AddTab("References", [Group("Fields", EditorCommandId.UpdateFields, EditorCommandId.InsertPageField),
            Group("Notes", EditorCommandId.InsertFootnote, EditorCommandId.InsertEndnote, EditorCommandId.FootnoteOptions, EditorCommandId.EndnoteOptions),
            ParameterGroup("Bookmark", EditorCommandId.InsertBookmark, "Bookmark name")]);
        AddTab("Proofing", [Group("Spelling", EditorCommandId.Proofing), Group("Search", EditorCommandId.Find, EditorCommandId.Replace)]);
        AddTab("Mailings", [ParameterGroup("Merge field", EditorCommandId.InsertMergeField, "Recipient field name"),
            Group("Fields", EditorCommandId.UpdateFields), HostGroup("Merge output", TextaloniaHostAction.MailMerge)]);
        AddTab("View", [Group("Document view", EditorCommandId.ViewSimple, EditorCommandId.ViewDraft, EditorCommandId.ViewPrintLayout),
            ZoomGroup(),
            PageNavigationGroup(), Group("Navigation", EditorCommandId.Navigation, EditorCommandId.ToggleRulers)]);

        _contextTabs.Add("Table", AddTab("Table", [Group("Table tools", EditorCommandId.TableProperties, EditorCommandId.TableInsertRow,
            EditorCommandId.TableInsertColumn, EditorCommandId.TableDeleteRows, EditorCommandId.TableDeleteColumns,
            EditorCommandId.TableMergeCells, EditorCommandId.TableSplitCells)]));
        _contextTabs.Add("Picture", AddTab("Picture", [Group("Picture tools", EditorCommandId.PictureProperties, EditorCommandId.RemovePicture)]));
        _contextTabs.Add("Header/Footer", AddTab("Header/Footer", [Group("Story", EditorCommandId.HeaderFooterOptions,
            EditorCommandId.InsertPageField, EditorCommandId.CloseStory)]));

        _tabs.SelectedItem = _tabs.Items.OfType<TabItem>().FirstOrDefault(tab => Equals(tab.Tag, previous)) ?? _tabs.Items[1];
        Refresh();
    }

    private TabItem AddTab(string title, IEnumerable<Control> groups)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 5) };
        foreach (var group in groups) content.Children.Add(group);
        var tab = new TabItem
        {
            Header = Label("Tabs." + Key(title), title), Tag = title,
            Content = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            }
        };
        AutomationProperties.SetName(tab, Label("Tabs." + Key(title), title) + " commands");
        _tabs!.Items.Add(tab);
        return tab;
    }

    private StackPanel GroupFrame(string name)
    {
        var group = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 8, 0), MinWidth = 88 };
        group.Children.Add(new TextBlock { Text = Label("Groups." + Key(name), name), FontSize = 11, Opacity = .7, Margin = new Thickness(4, 0) });
        return group;
    }

    private static string Key(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());
    private string Label(string key, string fallback) =>
        Localize?.Invoke("Textalonia.CommandSurface." + key) is { Length: > 0 } localized ? localized : fallback;

    private Control Group(string name, params EditorCommandId[] ids)
    {
        var group = GroupFrame(name);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = ids.Length > 4 ? 290 : 220 };
        foreach (var id in ids) buttons.Children.Add(CommandButton(id));
        group.Children.Add(buttons);
        return group;
    }

    private Control PasteSpecialGroup()
    {
        var group = GroupFrame("Paste Special");
        var buttons = new WrapPanel { MaxWidth = 240 };
        buttons.Children.Add(CommandButton(EditorCommandId.PasteSpecial, PasteSpecialFormat.NativeFragment, Label("PasteSpecial.Native", "Native")));
        buttons.Children.Add(CommandButton(EditorCommandId.PasteSpecial, PasteSpecialFormat.Html, Label("PasteSpecial.Html", "HTML")));
        buttons.Children.Add(CommandButton(EditorCommandId.PasteSpecial, PasteSpecialFormat.PlainText, Label("PasteSpecial.PlainText", "Text")));
        group.Children.Add(buttons);
        return group;
    }

    private Control HostGroup(string name, params TextaloniaHostAction[] actions)
    {
        var group = GroupFrame(name);
        var buttons = new WrapPanel { MaxWidth = 230 };
        foreach (var action in actions)
        {
            var defaultText = action == TextaloniaHostAction.MailMerge ? "Mail Merge" : action.ToString();
            var button = new Button { Content = Label("Host." + action, defaultText),
                MinHeight = 30, Margin = new Thickness(2), Padding = new Thickness(8, 4) };
            AutomationProperties.SetName(button, button.Content?.ToString());
            button.Click += (_, _) => _hostActionRequested?.Invoke(this, new TextaloniaHostActionEventArgs(action));
            buttons.Children.Add(button);
            _hostButtons[action] = button;
        }
        group.Children.Add(buttons);
        RefreshHostButtons();
        return group;
    }

    private void RefreshHostButtons()
    {
        foreach (var button in _hostButtons.Values) button.IsEnabled = _hostActionRequested is not null;
    }

    private Control ParameterGroup(string name, EditorCommandId id, string placeholder)
    {
        var group = GroupFrame(name);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var prompt = Label("Placeholders." + Key(placeholder), placeholder);
        var input = new TextBox { PlaceholderText = prompt, Width = 150, MinHeight = 30 };
        AutomationProperties.SetName(input, prompt);
        var button = CommandButton(id, manual: true);
        var command = Editor!.Commands[id];
        void Update()
        {
            input.IsVisible = command.IsVisible;
            input.IsEnabled = command.Capability == CommandCapability.Enabled;
            button.IsEnabled = command.CanExecute(input.Text);
        }
        EventHandler<TextChangedEventArgs> textChanged = (_, _) => Update();
        EventHandler canExecuteChanged = (_, _) => Update();
        input.TextChanged += textChanged;
        command.CanExecuteChanged += canExecuteChanged;
        button.Click += (_, _) => Editor?.Commands.Execute(id, input.Text);
        _detach.Add(() => { input.TextChanged -= textChanged; command.CanExecuteChanged -= canExecuteChanged; });
        row.Children.Add(input); row.Children.Add(button); group.Children.Add(row);
        Update();
        return group;
    }

    private Control PageNavigationGroup()
    {
        var group = GroupFrame("Page");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _pageNumber = new NumericUpDown { Minimum = 1, Maximum = Math.Max(1, Editor!.PageCount), Value = Editor.CurrentPageNumber,
            Width = 68, MinHeight = 30, FormatString = "0" };
        AutomationProperties.SetName(_pageNumber, "Page number");
        var button = CommandButton(EditorCommandId.GoToPage, manual: true);
        _pageButton = button;
        var command = Editor.Commands[EditorCommandId.GoToPage];
        void Update() => button.IsEnabled = command.CanExecute((int)(_pageNumber.Value ?? 1));
        _pageNumber.ValueChanged += (_, _) => Update();
        EventHandler canExecuteChanged = (_, _) => Update();
        command.CanExecuteChanged += canExecuteChanged;
        _detach.Add(() => command.CanExecuteChanged -= canExecuteChanged);
        button.Click += (_, _) => Editor?.Commands.Execute(EditorCommandId.GoToPage, (int)(_pageNumber.Value ?? 1));
        row.Children.Add(_pageNumber); row.Children.Add(button); group.Children.Add(row);
        Update();
        return group;
    }

    private Control ZoomGroup()
    {
        var group = GroupFrame("Zoom");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(CommandButton(EditorCommandId.ZoomOut));
        _zoomPercent = new NumericUpDown
        {
            Minimum = 10, Maximum = 500, Increment = 10,
            Value = (decimal)(Editor!.Zoom * 100), Width = 72, MinHeight = 30, FormatString = "0"
        };
        AutomationProperties.SetName(_zoomPercent, Label("Inputs.ZoomPercent", "Zoom percent"));
        _zoomPercent.ValueChanged += (_, _) =>
        {
            if (!_updatingZoom && _zoomPercent.Value is { } value)
                Editor?.Commands.Execute(EditorCommandId.SetZoom, (double)value / 100);
        };
        row.Children.Add(_zoomPercent);
        row.Children.Add(CommandButton(EditorCommandId.ZoomIn));
        group.Children.Add(row);
        var fit = new WrapPanel { MaxWidth = 200 };
        fit.Children.Add(CommandButton(EditorCommandId.FitWidth));
        fit.Children.Add(CommandButton(EditorCommandId.FitPage));
        group.Children.Add(fit);
        return group;
    }

    private Button CommandButton(EditorCommandId id, object? parameter = null, string? label = null, bool manual = false)
    {
        var command = Editor!.Commands[id];
        Button button = id is EditorCommandId.Bold or EditorCommandId.Italic or EditorCommandId.Underline or
            EditorCommandId.Strikethrough or EditorCommandId.ViewSimple or EditorCommandId.ViewDraft or EditorCommandId.ViewPrintLayout or
            EditorCommandId.ToggleRulers
            ? new ToggleButton { IsThreeState = true } : new Button();
        button.MinHeight = 30;
        button.Margin = new Thickness(2);
        button.Padding = new Thickness(8, 4);
        if (!manual) { button.Command = command; button.CommandParameter = parameter; }
        void Update()
        {
            button.Content = label ?? command.DisplayText;
            button.IsVisible = command.IsVisible;
            if (!manual) button.IsEnabled = command.CanExecute(parameter);
            if (button is ToggleButton toggle) toggle.IsChecked = command.IsChecked;
            var description = command.Shortcut is { } shortcut ? $"{button.Content} ({shortcut})" : button.Content?.ToString();
            ToolTip.SetTip(button, description);
            AutomationProperties.SetName(button, description);
        }
        PropertyChangedEventHandler changed = (_, _) => Update();
        command.PropertyChanged += changed;
        command.CanExecuteChanged += CommandCanExecuteChanged;
        _detach.Add(() => { command.PropertyChanged -= changed; command.CanExecuteChanged -= CommandCanExecuteChanged; });
        Update();
        return button;

        void CommandCanExecuteChanged(object? sender, EventArgs args) => Update();
    }

    private void Refresh()
    {
        if (Editor is not { } editor || _tabs is null) return;
        editor.Commands.Refresh();
        if (_zoomPercent is not null && !_zoomPercent.IsKeyboardFocusWithin)
        {
            _updatingZoom = true;
            try { _zoomPercent.Value = (decimal)(editor.Zoom * 100); }
            finally { _updatingZoom = false; }
        }
        if (_pageNumber is not null)
        {
            _pageNumber.Maximum = Math.Max(1, editor.PageCount);
            if (!_pageNumber.IsKeyboardFocusWithin) _pageNumber.Value = Math.Max(1, editor.CurrentPageNumber);
            if (_pageButton is not null) _pageButton.IsEnabled = editor.Commands.CanExecute(EditorCommandId.GoToPage, (int)(_pageNumber.Value ?? 1));
        }
        var picture = editor.CurrentImageOrOle is not null;
        var table = editor.TableTarget() is not null;
        var story = editor.ActiveStoryId != Guid.Empty &&
            editor.Document.Stories.TryGetValue(editor.ActiveStoryId, out var active) &&
            active.Kind is DocumentStoryKind.Header or DocumentStoryKind.Footer;
        SetContext("Table", table);
        SetContext("Picture", picture);
        SetContext("Header/Footer", story);
    }

    private void SetContext(string key, bool visible)
    {
        var tab = _contextTabs[key];
        var wasVisible = tab.IsVisible;
        tab.IsVisible = visible;
        if (visible && !wasVisible) _tabs!.SelectedItem = tab;
        else if (!visible && _tabs!.SelectedItem == tab) _tabs.SelectedIndex = 1;
    }
}
