using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Textalonia.Model;
using Textalonia.Editing;

namespace Textalonia.Controls;

/// <summary>Optional toolbar; applications can use the same editor commands in their own UI.</summary>
public class TextaloniaToolbar : WrapPanel
{
    public static readonly StyledProperty<TextaloniaEditor?> EditorProperty =
        AvaloniaProperty.Register<TextaloniaToolbar, TextaloniaEditor?>(nameof(Editor));
    private readonly List<(ToggleButton Button, Func<SelectionFormattingState, bool?> Read)> _toggles = [];
    private readonly List<Control> _editingControls = [];
    private ComboBox? _font;
    private ComboBox? _size;
    private ComboBox? _heading;
    private Button? _findButton;
    private TextBox? _query;
    private bool _updating;
    public TextaloniaEditor? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }

    public TextaloniaToolbar()
    {
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(8, 6);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty) return;
        if (change.OldValue is TextaloniaEditor old) old.Session.Changed -= SessionChanged;
        if (change.NewValue is TextaloniaEditor editor) editor.Session.Changed += SessionChanged;
        Build(); Refresh();
    }
    private void SessionChanged(object? sender, EventArgs e) => Refresh();

    private void Build()
    {
        Children.Clear(); _toggles.Clear(); _editingControls.Clear();
        if (Editor is not { } editor) return;
        _heading = Choice(["Body", "Heading 1", "Heading 2", "Heading 3"], 128, "Paragraph style");
        _heading.SelectionChanged += (_, _) => { if (!_updating) editor.Run(() => editor.Session.SetHeading(_heading.SelectedIndex)); };
        _font = Choice(["Default", "Arial", "Georgia", "Times New Roman", "Consolas"], 114, "Font family");
        _font.SelectionChanged += (_, _) =>
        {
            if (!_updating && _font.SelectedItem is string font)
                editor.Run(() => editor.ApplyStyle(s => s with { FontFamily = font == "Default" ? null : font }));
        };
        _size = Choice(["12", "14", "16", "18", "20", "24", "28", "32", "34", "40", "48", "64"], 64, "Font size");
        _size.SelectionChanged += (_, _) =>
        {
            if (!_updating && _size.SelectedItem is string size)
                editor.Run(() => editor.ApplyStyle(s => s with { FontSize = double.Parse(size, System.Globalization.CultureInfo.InvariantCulture) }));
        };
        Toggle("B", "Bold", editor.BoldCommand, state => Indicator(state.Bold)).FontWeight = FontWeight.Bold;
        Toggle("I", "Italic", editor.ItalicCommand, state => Indicator(state.Italic)).FontStyle = FontStyle.Italic;
        Toggle("U", "Underline", editor.UnderlineCommand, state => Indicator(state.Underline));
        Toggle("S", "Strikethrough", editor.StrikethroughCommand, state => Indicator(state.Strikethrough));
        AddFlyout("Color", "Text color", Palette(false));
        AddFlyout("Highlight", "Text highlight", Palette(true));
        AddFlyout("Paragraph", "Paragraph formatting", ParagraphMenu());
        AddFlyout("Insert", "Insert link or table", InsertMenu());
        ActionButton("Clear", "Clear character formatting", () => editor.Session.ClearFormatting());
        CommandButton("Undo", "Undo", editor.UndoCommand);
        CommandButton("Redo", "Redo", editor.RedoCommand);
        _findButton = AddFlyout("Find", "Find and replace", FindPanel(), editing: false);
    }

    private ComboBox Choice(string[] values, double width, string name)
    {
        var box = new ComboBox { ItemsSource = values, Width = width, Margin = new Thickness(2), MinHeight = 32, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(box, name); AutomationProperties.SetName(box, name);
        Children.Add(box); _editingControls.Add(box); return box;
    }
    private ToggleButton Toggle(string text, string name, System.Windows.Input.ICommand command, Func<SelectionFormattingState, bool?> read)
    {
        var button = new ToggleButton { Content = text, Command = command, IsThreeState = true, MinWidth = 32, MinHeight = 32, Margin = new Thickness(2), Padding = new Thickness(8, 4) };
        ToolTip.SetTip(button, name); AutomationProperties.SetName(button, name);
        Children.Add(button); _toggles.Add((button, read)); return button;
    }
    private Button CommandButton(string text, string name, System.Windows.Input.ICommand command)
    {
        var button = MakeButton(text, name); button.Command = command; Children.Add(button); return button;
    }
    private Button ActionButton(string text, string name, Action action)
    {
        var button = MakeButton(text, name);
        button.Click += (_, _) => Editor?.Run(action);
        Children.Add(button); _editingControls.Add(button); return button;
    }
    private static Button MakeButton(string text, string name)
    {
        var button = new Button { Content = text, MinHeight = 32, FontSize = 13, Margin = new Thickness(2), Padding = new Thickness(7, 4) };
        ToolTip.SetTip(button, name); AutomationProperties.SetName(button, name); return button;
    }
    private Button AddFlyout(string text, string name, Control content, bool editing = true)
    {
        var button = MakeButton(text, name);
        button.Flyout = new Flyout { Content = content };
        Children.Add(button); if (editing) _editingControls.Add(button); return button;
    }
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 6) };
    private Button MenuAction(string text, Action action, bool editing = true)
    {
        var button = MakeButton(text, text); button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Click += (_, _) => Editor?.Run(action);
        if (editing) _editingControls.Add(button);
        return button;
    }

    private Control Palette(bool background)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Label(background ? "Highlight color" : "Text color"));
        var colors = new WrapPanel { Width = 210 };
        foreach (var color in new[] { "#172033", "#FFFFFF", "#E24D4D", "#E78935", "#F5D76E", "#3F9E79", "#3D7CC9", "#8C68C5", "#F2B6C5", "#D9E9FA" })
        {
            var button = new Button { Width = 36, Height = 30, Margin = new Thickness(3), Background = new SolidColorBrush(Color.Parse(color)) };
            AutomationProperties.SetName(button, color); ToolTip.SetTip(button, color);
            button.Click += (_, _) => Editor?.Run(() => Editor.ApplyStyle(s => background ? s with { Background = color } : s with { Foreground = color }));
            colors.Children.Add(button);
        }
        panel.Children.Add(colors);
        panel.Children.Add(MenuAction("Automatic / none", () => Editor!.ApplyStyle(s => background ? s with { Background = null } : s with { Foreground = null })));
        return panel;
    }

    private Control ParagraphMenu()
    {
        var panel = new StackPanel { Spacing = 2, MinWidth = 180 };
        foreach (var alignment in Enum.GetValues<ParagraphAlignment>())
            panel.Children.Add(MenuAction("Align " + alignment.ToString().ToLowerInvariant(), () => Editor!.Session.ApplyParagraphStyle(s => s with { Alignment = alignment })));
        panel.Children.Add(new Separator());
        panel.Children.Add(MenuAction("Bulleted list", () => Editor!.Session.ToggleList(ListKind.Bullet)));
        panel.Children.Add(MenuAction("Numbered list", () => Editor!.Session.ToggleList(ListKind.Numbered)));
        panel.Children.Add(MenuAction("Increase indent", () => Editor!.Session.ApplyParagraphStyle(s => s.List == ListKind.None ? s with { Indent = Math.Min(1000, s.Indent + 24) } : s with { ListLevel = Math.Min(8, s.ListLevel + 1), ListStart = null, ListRestart = false })));
        panel.Children.Add(MenuAction("Decrease indent", () => Editor!.Session.ApplyParagraphStyle(s => s.List == ListKind.None ? s with { Indent = Math.Max(0, s.Indent - 24) } : s with { ListLevel = Math.Max(0, s.ListLevel - 1), ListStart = null, ListRestart = false })));
        panel.Children.Add(MenuAction("Toggle right-to-left", ToggleRightToLeft));
        panel.Children.Add(MenuAction("Subscript", () => ToggleBaseline(Baseline.Subscript)));
        panel.Children.Add(MenuAction("Superscript", () => ToggleBaseline(Baseline.Superscript)));
        return panel;
    }

    private Control InsertMenu()
    {
        var panel = new StackPanel { Width = 260, Spacing = 4 };
        panel.Children.Add(Label("Hyperlink"));
        var url = new TextBox { PlaceholderText = "https://example.com", Margin = new Thickness(2) };
        AutomationProperties.SetName(url, "Link address"); panel.Children.Add(url);
        panel.Children.Add(MenuAction("Apply link", () =>
        {
            if (!FlowDocument.IsSafeHyperlink(url.Text ?? "")) throw new FormatException("Enter an http, https, or mailto URL.");
            if (Editor!.Session.Selection.IsEmpty)
            {
                var start = Editor.Session.Selection.Active;
                Editor.Session.InsertText(url.Text!);
                Editor.Session.Select(start, start + url.Text!.Length);
            }
            Editor.ApplyStyle(s => s with { Hyperlink = url.Text, Underline = true });
        }));
        panel.Children.Add(MenuAction("Remove link", () => Editor!.ApplyStyle(s => s with { Hyperlink = null })));
        panel.Children.Add(new Separator());
        panel.Children.Add(Label("Table"));
        var dimensions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var rows = new NumericUpDown { Value = 3, Minimum = 1, Maximum = 50, Width = 115, FormatString = "0", PlaceholderText = "Rows" };
        var columns = new NumericUpDown { Value = 3, Minimum = 1, Maximum = 12, Width = 115, FormatString = "0", PlaceholderText = "Columns" };
        AutomationProperties.SetName(rows, "Table rows"); AutomationProperties.SetName(columns, "Table columns");
        dimensions.Children.Add(rows); dimensions.Children.Add(columns); panel.Children.Add(dimensions);
        panel.Children.Add(MenuAction("Insert table", () => Editor!.InsertTable((int)(rows.Value ?? 3), (int)(columns.Value ?? 3))));
        panel.Children.Add(MenuAction("Add row below", () => Editor!.Session.UpdateCurrentTable((t, r, _) => t.InsertRow(r + 1))));
        panel.Children.Add(MenuAction("Add column after", () => Editor!.Session.UpdateCurrentTable((t, _, c) => t.InsertColumn(c + 1))));
        panel.Children.Add(MenuAction("Merge with cell on right", () => Editor!.Session.UpdateCurrentTable((t, r, c) => t.MergeCells(r, c, 1, 2))));
        panel.Children.Add(MenuAction("Merge with cell below", () => Editor!.Session.UpdateCurrentTable((t, r, c) => t.MergeCells(r, c, 2, 1))));
        panel.Children.Add(MenuAction("Split cell", () => Editor!.Session.UpdateCurrentTable((t, r, c) => t.SplitCell(r, c))));
        panel.Children.Add(MenuAction("Shade cell", () => Editor!.Session.UpdateCurrentTable((t, r, c) => t.SetCell(r, c, t.Rows[r][c] with { Background = "#D9E9FA" }))));
        panel.Children.Add(MenuAction("Delete row", () => Editor!.Session.UpdateCurrentTable((t, r, _) => t.RemoveRow(r))));
        panel.Children.Add(MenuAction("Delete column", () => Editor!.Session.UpdateCurrentTable((t, _, c) => t.RemoveColumn(c))));
        panel.Children.Add(MenuAction("Delete table", () => Editor!.Session.DeleteCurrentTable()));
        return new ScrollViewer { Content = panel, MaxHeight = 520 };
    }

    private Control FindPanel()
    {
        var panel = new StackPanel { Width = 270, Spacing = 6 };
        panel.Children.Add(Label("Find and replace"));
        _query = new TextBox { PlaceholderText = "Find text" };
        var replacement = new TextBox { PlaceholderText = "Replace with" };
        var caseSensitive = new CheckBox { Content = "Match case" };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(_query, "Find text"); AutomationProperties.SetName(replacement, "Replacement text");
        panel.Children.Add(_query); panel.Children.Add(replacement); panel.Children.Add(caseSensitive);
        var next = MakeButton("Find next", "Find next");
        next.Click += (_, _) =>
        {
            if (Editor is null) return;
            var found = Editor.FindNext(_query.Text ?? "", caseSensitive.IsChecked == true);
            result.Text = found ? "Match selected" : "No matches";
            Editor.Highlights.Clear();
            foreach (var match in Editor.Session.FindAll(_query.Text ?? "", caseSensitive.IsChecked == true).Take(500))
                Editor.Highlights.Add(new(match.Start, match.Length, new SolidColorBrush(Color.FromArgb(85, 245, 190, 65))));
        };
        panel.Children.Add(next);
        panel.Children.Add(MenuAction("Replace all", () =>
        {
            result.Text = $"{Editor!.ReplaceAll(_query.Text ?? "", replacement.Text ?? "", caseSensitive.IsChecked == true)} replacement(s)";
            Editor.Highlights.Clear();
        }));
        panel.Children.Add(MenuAction("Clear search highlights", () => Editor!.Highlights.Clear(), false));
        panel.Children.Add(result);
        return panel;
    }

    public void OpenFind()
    {
        if (_findButton?.Flyout is not { } flyout) return;
        flyout.ShowAt(_findButton);
        _query?.Focus();
    }

    private void ToggleRightToLeft()
    {
        var value = Editor!.Session.FormattingState.Paragraph(s => s.RightToLeft);
        var enabled = value.IsMixed || !value.Value;
        Editor.Session.ApplyParagraphStyle(s => s with { RightToLeft = enabled });
    }
    private void ToggleBaseline(Baseline baseline)
    {
        var value = Editor!.Session.FormattingState.Text(s => s.Baseline);
        var selected = !value.IsMixed && value.Value == baseline ? Baseline.Normal : baseline;
        Editor.ApplyStyle(s => s with { Baseline = selected });
    }

    private static bool? Indicator(FormattingValue<bool> value) => value.IsMixed ? null : value.Value;

    private void Refresh()
    {
        if (Editor is not { } editor) return;
        _updating = true;
        try
        {
            var state = editor.Session.FormattingState;
            foreach (var (button, read) in _toggles) button.IsChecked = read(state);
            foreach (var control in _editingControls) control.IsEnabled = !editor.IsReadOnly;
            if (_heading is not null) _heading.SelectedIndex = state.HeadingLevel.IsMixed ? -1 : Math.Min(3, state.HeadingLevel.Value);
            if (_font is not null) _font.SelectedItem = state.FontFamily.IsMixed ? null : state.FontFamily.Value ?? "Default";
            if (_size is not null) _size.SelectedItem = state.FontSize.IsMixed ? null : state.FontSize.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { _updating = false; }
    }
}
