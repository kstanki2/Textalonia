using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Textalonia.Model;
using Textalonia.Editing;
using Textalonia.Model.Fields;

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
    private Button? _mergeFieldUpdate;
    private Guid? _mergeFieldId;
    private TextaloniaFindReplacePanel? _findPanel;
    private Button? _navigationButton;
    private ComboBox? _viewMode;
    private NumericUpDown? _zoom;
    private NumericUpDown? _pagesPerRow;
    private NumericUpDown? _pageGap;
    private NumericUpDown? _pageNumber;
    private TextBlock? _pageStatus;
    private TextBlock? _storyStatus;
    private bool _updating;
    private readonly List<(NumericUpDown Control, Func<SelectionFormattingState, FormattingValue<double?>> Read)> _numericFormatting = [];
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
        if (change.OldValue is TextaloniaEditor old) { old.Session.Changed -= SessionChanged; old.TableCellSelectionChanged -= SessionChanged; old.PropertyChanged -= EditorPropertyChanged; }
        if (change.NewValue is TextaloniaEditor editor) { editor.Session.Changed += SessionChanged; editor.TableCellSelectionChanged += SessionChanged; editor.PropertyChanged += EditorPropertyChanged; }
        Build(); Refresh();
    }
    private void SessionChanged(object? sender, EventArgs e) => Refresh();
    private void EditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextaloniaEditor.ViewModeProperty || e.Property == TextaloniaEditor.ZoomProperty ||
            e.Property == TextaloniaEditor.PagesPerRowProperty || e.Property == TextaloniaEditor.PageGapProperty ||
            e.Property == TextaloniaEditor.PageCountProperty || e.Property == TextaloniaEditor.CurrentPageNumberProperty ||
            e.Property == TextaloniaEditor.IsReadOnlyProperty) Refresh();
    }

    private void Build()
    {
        if (_findPanel is not null) _findPanel.Editor = null;
        Children.Clear(); _toggles.Clear(); _editingControls.Clear(); _numericFormatting.Clear();
        _mergeFieldUpdate = null; _mergeFieldId = null; _storyStatus = null;
        _viewMode = null; _zoom = null; _pagesPerRow = null; _pageGap = null; _pageNumber = null; _pageStatus = null;
        if (Editor is not { } editor) return;
        _heading = Choice(["Body", "Heading 1", "Heading 2", "Heading 3", "Heading 4", "Heading 5", "Heading 6"], 128, "Paragraph style");
        _heading.SelectionChanged += (_, _) => { if (!_updating) editor.Run(() => editor.SetSelectedHeading(_heading.SelectedIndex)); };
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
        AddFlyout("Typography", "Typography and spacing", TypographyMenu());
        AddFlyout("Paragraph", "Paragraph formatting", ParagraphMenu());
        DialogButton("Styles", "Create, edit or apply named styles", editor.ShowStylesDialogAsync);
        DialogButton("Font…", "Font dialog", editor.ShowFontDialogAsync);
        DialogButton("Paragraph…", "Paragraph dialog", editor.ShowParagraphDialogAsync);
        DialogButton("Tabs…", "Tabs dialog", editor.ShowTabsDialogAsync);
        DialogButton("Page setup…", "Page setup dialog", editor.ShowPageSetupDialogAsync);
        DialogButton("Page numbering…", "Page numbering dialog", editor.ShowPageNumberingDialogAsync);
        var viewButton = AddFlyout("View", "Document view, zoom and page navigation", ViewMenu(), editing: false);
        viewButton.Flyout!.Opened += (_, _) => Refresh();
        AddOutputFlyout();
        AddFlyout("Insert", "Insert link or table", InsertMenu());
        DialogButton("Table\u2026", "Table properties", editor.ShowTablePropertiesDialogAsync);
        AddFlyout("Stories", "Headers, footers and notes", StoriesMenu(), editing: false);
        AddMergeFieldFlyout();
        ActionButton("Clear", "Clear character formatting", () => editor.ApplyStyle(_ => TextStyle.Default));
        CommandButton("Undo", "Undo", editor.UndoCommand);
        CommandButton("Redo", "Redo", editor.RedoCommand);
        _findPanel = new TextaloniaFindReplacePanel { Editor = editor };
        _findButton = AddFlyout("Find", "Find and replace", _findPanel, editing: false);
        _findPanel.CloseRequested += (_, _) => _findButton.Flyout?.Hide();
        AddNavigationFlyout();
        AddFieldsFlyout();
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
    private void DialogButton(string text, string name, Func<Task<bool>> show)
    {
        var button = MakeButton(text, name);
        button.Click += async (_, _) => await show();
        Children.Add(button); _editingControls.Add(button);
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

    private void AddOutputFlyout()
    {
        var panel = new StackPanel { Spacing = 4, Width = 240 };
        var preview = MakeButton("Print preview…", "Print preview");
        var export = MakeButton("Export PDF…", "Export PDF");
        var print = MakeButton("Print…", "Print dialog");
        var quick = MakeButton("Quick print", "Quick print to the default printer");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        foreach (var control in new Control[] { preview, export, print, quick, status }) panel.Children.Add(control);
        var button = AddFlyout("Output", "Print preview, print and PDF export", panel, editing: false);
        button.Flyout!.Opened += (_, _) =>
        {
            export.IsEnabled = Editor?.PdfExporter is not null;
            print.IsEnabled = quick.IsEnabled = Editor?.PrintService is not null;
            status.Text = string.Join(Environment.NewLine, new[]
            {
                export.IsEnabled ? null : "PDF export requires a host PDF exporter.",
                print.IsEnabled ? null : "Printing requires a host print service."
            }.Where(value => value is not null));
        };
        preview.Click += async (_, _) => { button.Flyout.Hide(); if (Editor is { } editor) await editor.ShowPrintPreviewDialogAsync(); };
        export.Click += async (_, _) => { button.Flyout.Hide(); if (Editor is { } editor) await editor.ShowExportPdfDialogAsync(); };
        print.Click += async (_, _) => { button.Flyout.Hide(); if (Editor is { } editor) await editor.ShowPrintDialogAsync(); };
        quick.Click += async (_, _) => { button.Flyout.Hide(); if (Editor is { } editor) await editor.QuickPrintAsync(); };
    }

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
            panel.Children.Add(MenuAction("Align " + alignment.ToString().ToLowerInvariant(), () => Editor!.ApplyParagraphStyle(s => s with { Alignment = alignment })));
        panel.Children.Add(new Separator());
        panel.Children.Add(MenuAction("Bulleted list", () => Editor!.ToggleSelectedList(ListKind.Bullet)));
        panel.Children.Add(MenuAction("Numbered list", () => Editor!.ToggleSelectedList(ListKind.Numbered)));
        panel.Children.Add(MenuAction("Restart numbering at 1", () => Editor!.RestartSelectedList()));
        panel.Children.Add(MenuAction("Continue previous list", () => Editor!.ContinuePreviousList()));
        panel.Children.Add(MenuAction("Increase indent", () => Editor!.ApplyParagraphStyle(s => s.List == ListKind.None ? s with { Indent = Math.Min(1000, s.Indent + 24) } : s with { ListLevel = Math.Min(8, s.ListLevel + 1), ListStart = null, ListRestart = false })));
        panel.Children.Add(MenuAction("Decrease indent", () => Editor!.ApplyParagraphStyle(s => s.List == ListKind.None ? s with { Indent = Math.Max(0, s.Indent - 24) } : s with { ListLevel = Math.Max(0, s.ListLevel - 1), ListStart = null, ListRestart = false })));
        panel.Children.Add(MenuAction("Toggle right-to-left", ToggleRightToLeft));
        panel.Children.Add(MenuAction("Subscript", () => ToggleBaseline(Baseline.Subscript)));
        panel.Children.Add(MenuAction("Superscript", () => ToggleBaseline(Baseline.Superscript)));
        return panel;
    }

    private static NumericUpDown Number(string name, decimal value, decimal minimum, decimal maximum)
    {
        var control = new NumericUpDown { Value = value, Minimum = minimum, Maximum = maximum, Increment = 1, FormatString = "0.###", PlaceholderText = name };
        AutomationProperties.SetName(control, name); ToolTip.SetTip(control, name); return control;
    }

    private Control TypographyMenu()
    {
        var panel = new StackPanel { Width = 220, Spacing = 4 };
        Add("Font weight", 1, 1000, state => AsNullable(state.FontWeight), value => Editor!.ApplyStyle(s => s with { FontWeight = (int)value }));
        Add("Font stretch", 1, 9, state => AsNullable(state.FontStretch), value => Editor!.ApplyStyle(s => s with { FontStretch = (int)value }));
        Add("Letter spacing", -100, 100, state => AsNullable(state.LetterSpacing), value => Editor!.ApplyParagraphStyle(s => s with { LetterSpacing = value }));
        Add("Line height", 1, 1000, state => state.Paragraph(s => s.LineHeight), value => Editor!.ApplyParagraphStyle(s => s with { LineHeight = value }));
        panel.Children.Add(MenuAction("Automatic line height", () => Editor!.ApplyParagraphStyle(s => s with { LineHeight = null })));
        Add("Space before", 0, 1000, state => AsNullable(state.Paragraph(s => s.SpaceBefore)), value => Editor!.ApplyParagraphStyle(s => s with { SpaceBefore = value }));
        Add("Space after", 0, 1000, state => AsNullable(state.Paragraph(s => s.SpaceAfter)), value => Editor!.ApplyParagraphStyle(s => s with { SpaceAfter = value }));
        Add("Left indent", 0, 1000, state => AsNullable(state.Paragraph(s => s.Indent)), value => Editor!.ApplyParagraphStyle(s => s with { Indent = value }));
        Add("Right indent", 0, 1000, state => AsNullable(state.Paragraph(s => s.RightIndent)), value => Editor!.ApplyParagraphStyle(s => s with { RightIndent = value }));
        Add("First line indent", -1000, 1000, state => AsNullable(state.Paragraph(s => s.FirstLineIndent)), value => Editor!.ApplyParagraphStyle(s => s with { FirstLineIndent = value }));
        return new ScrollViewer { Content = panel, MaxHeight = 520 };

        void Add(string name, decimal minimum, decimal maximum, Func<SelectionFormattingState, FormattingValue<double?>> read, Action<double> apply)
        {
            panel.Children.Add(Label(name));
            var control = Number(name, minimum, minimum, maximum);
            control.ValueChanged += (_, _) => { if (!_updating && control.Value is { } value) Editor?.Run(() => apply((double)value), focusDocument: false); };
            control.LostFocus += (_, _) => Refresh();
            _numericFormatting.Add((control, read)); _editingControls.Add(control); panel.Children.Add(control);
        }
    }
    private static FormattingValue<double?> AsNullable(FormattingValue<double> value) => new(value.Value, value.IsMixed);
    private static FormattingValue<double?> AsNullable(FormattingValue<int> value) => new(value.Value, value.IsMixed);

    private Control StoriesMenu()
    {
        var panel = new StackPanel { Width = 280, Spacing = 4 };
        _storyStatus = new TextBlock(); AutomationProperties.SetName(_storyStatus, "Active story"); panel.Children.Add(_storyStatus);
        panel.Children.Add(Label("Headers and footers"));
        foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
        {
            panel.Children.Add(MenuAction("Edit " + variant.ToString().ToLowerInvariant() + " header", () => Editor!.EditHeaderFooter(false, variant), editing: false));
            panel.Children.Add(MenuAction("Edit " + variant.ToString().ToLowerInvariant() + " footer", () => Editor!.EditHeaderFooter(true, variant), editing: false));
        }
        panel.Children.Add(MenuAction("Link to previous", () => Editor!.LinkHeaderFooterToPrevious(true)));
        panel.Children.Add(MenuAction("Unlink from previous", () => Editor!.LinkHeaderFooterToPrevious(false)));
        AddDialog("Header and footer options…", () => Editor!.ShowHeaderFooterDialogAsync());
        panel.Children.Add(new Separator());
        panel.Children.Add(MenuAction("Return to document (Esc)", () => Editor!.CloseStory(), editing: false));
        panel.Children.Add(Label("Page fields"));
        foreach (var field in Enum.GetValues<PageFieldKind>())
            panel.Children.Add(MenuAction("Insert " + field, () => Editor!.InsertPageField(field)));
        panel.Children.Add(Label("Notes"));
        var mark = new TextBox { PlaceholderText = "Custom mark (blank for numbering)" };
        AutomationProperties.SetName(mark, "Custom note mark"); panel.Children.Add(mark);
        panel.Children.Add(MenuAction("Insert footnote", () => Editor!.InsertFootnote(string.IsNullOrEmpty(mark.Text) ? null : mark.Text)));
        panel.Children.Add(MenuAction("Insert endnote", () => Editor!.InsertEndnote(string.IsNullOrEmpty(mark.Text) ? null : mark.Text)));
        panel.Children.Add(MenuAction("Delete active note", () =>
        {
            var note = Editor!.Document.Notes.FirstOrDefault(n => n.StoryId == Editor.ActiveStoryId);
            if (note is not null) Editor.Session.RemoveNote(note.Id);
        }));
        AddDialog("Footnote options…", () => Editor!.ShowNoteSettingsDialogAsync(DocumentNoteKind.Footnote));
        AddDialog("Endnote options…", () => Editor!.ShowNoteSettingsDialogAsync(DocumentNoteKind.Endnote));
        return new ScrollViewer { Content = panel, MaxHeight = 520 };

        void AddDialog(string label, Func<Task<bool>> show)
        {
            var button = MakeButton(label, label);
            button.Click += async (_, _) => await show();
            _editingControls.Add(button); panel.Children.Add(button);
        }
    }

    private Control InsertMenu()
    {
        var panel = new StackPanel { Width = 260, Spacing = 4 };
        panel.Children.Add(Label("Breaks"));
        panel.Children.Add(MenuAction("Insert page break", () => Editor!.Session.InsertPageBreak()));
        panel.Children.Add(MenuAction("Insert column break", () => Editor!.Session.InsertColumnBreak()));
        foreach (var kind in Enum.GetValues<SectionBreakKind>())
            panel.Children.Add(MenuAction("Insert section: " + kind, () => Editor!.Session.InsertSectionBreak(kind)));
        panel.Children.Add(new Separator());
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
        panel.Children.Add(MenuAction("Add row below", () => Editor!.InsertTableRow()));
        panel.Children.Add(MenuAction("Add column after", () => Editor!.InsertTableColumn()));
        panel.Children.Add(MenuAction("Select current cell", () => Editor!.SelectCurrentTableCell()));
        panel.Children.Add(MenuAction("Extend selection right", () => Editor!.ExtendTableCellSelection(0, 1)));
        panel.Children.Add(MenuAction("Extend selection down", () => Editor!.ExtendTableCellSelection(1, 0)));
        panel.Children.Add(MenuAction("Merge selected cells", () => Editor!.MergeSelectedTableCells()));
        panel.Children.Add(MenuAction("Merge with cell on right", () => Editor!.Session.UpdateCurrentTable((t, r, c) => t.MergeCells(r, c, 1, 2))));
        panel.Children.Add(MenuAction("Merge with cell below", () => Editor!.Session.UpdateCurrentTable((t, r, c) => t.MergeCells(r, c, 2, 1))));
        panel.Children.Add(MenuAction("Split cell", () => Editor!.SplitSelectedTableCells()));
        panel.Children.Add(MenuAction("Shade cell", () => Editor!.SetTableCellBackground("#D9E9FA")));
        panel.Children.Add(MenuAction("Delete row", () => Editor!.DeleteTableRows()));
        panel.Children.Add(MenuAction("Delete column", () => Editor!.DeleteTableColumns()));
        panel.Children.Add(MenuAction("Delete table", () => Editor!.DeleteSelectedTable()));
        panel.Children.Add(new Separator());
        panel.Children.Add(Label("Cell borders and padding"));
        var padding = Number("Cell padding", 8, 0, 1000);
        panel.Children.Add(padding);
        panel.Children.Add(MenuAction("Apply cell padding", () =>
        {
            var value = (double)(padding.Value ?? 8); Editor!.SetTableCellPadding(new(value, value, value, value));
        }));
        var borderWidth = Number("Cell border width", 1, 0, 1000);
        panel.Children.Add(borderWidth);
        var borderKind = new ComboBox { ItemsSource = Enum.GetValues<BorderKind>(), SelectedItem = BorderKind.Solid };
        AutomationProperties.SetName(borderKind, "Cell border kind"); panel.Children.Add(borderKind);
        var borderColor = new TextBox { Text = "#808080", PlaceholderText = "Border color" };
        AutomationProperties.SetName(borderColor, "Cell border color"); panel.Children.Add(borderColor);
        panel.Children.Add(MenuAction("Apply cell borders", () =>
        {
            var side = new BorderSide((double)(borderWidth.Value ?? 1), borderColor.Text) { Kind = (BorderKind)borderKind.SelectedItem! };
            Editor!.SetTableCellBorders(new(side, side, side, side));
        }));
        panel.Children.Add(MenuAction("Remove cell borders", () => Editor!.SetTableCellBorders(new())));
        panel.Children.Add(Label("Table sizing"));
        panel.Children.Add(MenuAction("Narrow column", () => Editor!.ResizeCurrentTableTrack(TableResizeAxis.Column, -8)));
        panel.Children.Add(MenuAction("Widen column", () => Editor!.ResizeCurrentTableTrack(TableResizeAxis.Column, 8)));
        panel.Children.Add(MenuAction("Shorten row", () => Editor!.ResizeCurrentTableTrack(TableResizeAxis.Row, -8)));
        panel.Children.Add(MenuAction("Taller row", () => Editor!.ResizeCurrentTableTrack(TableResizeAxis.Row, 8)));
        var columnWidth = Number("Relative column width", 1, .001m, 100000);
        panel.Children.Add(columnWidth);
        panel.Children.Add(MenuAction("Apply column width", () => Editor!.SetTableColumnWidth((double)(columnWidth.Value ?? 1))));
        var rowHeight = Number("Row height", 36, 1, 100000);
        panel.Children.Add(rowHeight);
        var rowMode = new ComboBox { ItemsSource = Enum.GetValues<TableRowHeightMode>(), SelectedItem = TableRowHeightMode.AtLeast };
        AutomationProperties.SetName(rowMode, "Row height policy"); panel.Children.Add(rowMode);
        panel.Children.Add(MenuAction("Apply row height", () => Editor!.SetTableRowHeight((double)(rowHeight.Value ?? 36), (TableRowHeightMode)rowMode.SelectedItem!)));
        panel.Children.Add(new TextBlock { Text = "Alt+drag: select cells. Alt+Shift+arrows: extend selection. Drag cell edges to resize. Escape: cancel.", TextWrapping = TextWrapping.Wrap });
        return new ScrollViewer { Content = panel, MaxHeight = 520 };
    }

    private Control ViewMenu()
    {
        var panel = new StackPanel { Width = 260, Spacing = 6 };
        panel.Children.Add(Label("Document view"));
        _viewMode = new ComboBox { ItemsSource = Enum.GetValues<DocumentViewMode>(), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_viewMode, "Document view"); panel.Children.Add(_viewMode);
        _viewMode.SelectionChanged += (_, _) =>
        {
            if (!_updating && _viewMode.SelectedItem is DocumentViewMode mode) Editor?.Run(() => Editor.ViewMode = mode, focusDocument: false);
        };
        panel.Children.Add(Label("Zoom (%)"));
        _zoom = Number("Zoom (%)", 100, 10, 500); panel.Children.Add(_zoom);
        _zoom.ValueChanged += (_, _) =>
        {
            if (!_updating && _zoom.Value is { } value) Editor?.Run(() => Editor.Zoom = (double)value / 100, focusDocument: false);
        };
        panel.Children.Add(MenuAction("Fit page width", () => Editor!.FitWidth(), editing: false));
        panel.Children.Add(MenuAction("Fit whole page", () => Editor!.FitPage(), editing: false));
        panel.Children.Add(Label("Pages per row"));
        _pagesPerRow = Number("Pages per row", 1, 1, 8); panel.Children.Add(_pagesPerRow);
        _pagesPerRow.ValueChanged += (_, _) =>
        {
            if (!_updating && _pagesPerRow.Value is { } value) Editor?.Run(() => Editor.PagesPerRow = (int)value, focusDocument: false);
        };
        panel.Children.Add(Label("Page gap (DIP)"));
        _pageGap = Number("Page gap (DIP)", 24, 0, 1000); panel.Children.Add(_pageGap);
        _pageGap.ValueChanged += (_, _) =>
        {
            if (!_updating && _pageGap.Value is { } value) Editor?.Run(() => Editor.PageGap = (double)value, focusDocument: false);
        };
        _pageStatus = new TextBlock(); AutomationProperties.SetName(_pageStatus, "Page status"); panel.Children.Add(_pageStatus);
        _pageNumber = Number("Page number", 1, 1, 1000000); panel.Children.Add(_pageNumber);
        panel.Children.Add(MenuAction("Go to page", () => Editor!.GoToPage((int)(_pageNumber.Value ?? 1) - 1), editing: false));
        panel.Children.Add(MenuAction("Previous page", () => Editor!.GoToPage(Math.Max(0, Editor.CurrentPageNumber - 2)), editing: false));
        panel.Children.Add(MenuAction("Next page", () => Editor!.GoToPage(Math.Min(Editor.PageCount - 1, Editor.CurrentPageNumber)), editing: false));
        return new ScrollViewer { Content = panel, MaxHeight = 520 };
    }

    private void AddMergeFieldFlyout()
    {
        var panel = new StackPanel { Width = 280, Spacing = 6 };
        panel.Children.Add(Label("Merge field"));
        var name = new TextBox { PlaceholderText = "CustomerName" };
        var format = new TextBox { PlaceholderText = "Optional: N2 or MMMM d, yyyy" };
        var useFallback = new CheckBox { Content = "Use a fallback for missing values" };
        var fallback = new TextBox { PlaceholderText = "Fallback text (may be empty)", IsEnabled = false };
        AutomationProperties.SetName(name, "Merge field name");
        AutomationProperties.SetName(format, "Merge field value format");
        AutomationProperties.SetName(fallback, "Merge field fallback text");
        panel.Children.Add(Label("Field name")); panel.Children.Add(name);
        panel.Children.Add(Label("Value format")); panel.Children.Add(format);
        panel.Children.Add(useFallback); panel.Children.Add(fallback);
        useFallback.IsCheckedChanged += (_, _) => fallback.IsEnabled = useFallback.IsChecked == true;
        string? ValueFormat() => string.IsNullOrEmpty(format.Text) ? null : format.Text;
        string? Fallback() => useFallback.IsChecked == true ? fallback.Text ?? "" : null;
        var insert = MenuAction("Insert merge field", () => Editor!.InsertMergeField(name.Text ?? "", ValueFormat(), Fallback()));
        panel.Children.Add(insert);
        var update = MenuAction("Update selected merge field", () =>
        {
            if (_mergeFieldId is { } id && Editor?.CurrentMergeField?.Id == id)
                Editor.UpdateMergeField(id, name.Text ?? "", ValueFormat(), Fallback());
        });
        _mergeFieldUpdate = update;
        panel.Children.Add(update);
        panel.Children.Add(new TextBlock
        {
            Text = "Select a field or place the caret beside it to edit its definition. Field names must match your recipient data.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12
        });
        var button = AddFlyout("Merge field", "Insert or edit a merge field", panel);
        button.Flyout!.Opened += (_, _) =>
        {
            var current = Editor?.CurrentMergeField;
            _mergeFieldId = current?.Id;
            if (current?.Payload is MergeFieldInlinePayload field)
            {
                name.Text = field.Name; format.Text = field.Format;
                useFallback.IsChecked = field.FallbackText is not null; fallback.Text = field.FallbackText;
            }
            else
            {
                name.Text = ""; format.Text = ""; useFallback.IsChecked = false; fallback.Text = "";
            }
            update.IsEnabled = _mergeFieldId is not null && Editor?.IsReadOnly == false;
            name.Focus();
        };
        insert.Click += (_, _) => { if (Editor?.LastError is null) button.Flyout.Hide(); };
        update.Click += (_, _) => { if (Editor?.LastError is null) button.Flyout.Hide(); };
    }

    public void OpenFind() => OpenFind(false);

    public void OpenFind(bool replace)
    {
        if (_findButton?.Flyout is not { } flyout) return;
        flyout.ShowAt(_findButton);
        _findPanel?.RefreshResults(); _findPanel?.FocusQuery(replace);
    }

    public void OpenNavigation()
    {
        if (_navigationButton?.Flyout is { } flyout) flyout.ShowAt(_navigationButton);
    }

    private void AddNavigationFlyout()
    {
        var panel = new StackPanel { Width = 310, Spacing = 6 };
        var outline = new ComboBox { PlaceholderText = "Heading", HorizontalAlignment = HorizontalAlignment.Stretch };
        var bookmarks = new ComboBox { PlaceholderText = "Bookmark", HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { PlaceholderText = "Bookmark name" };
        var tooltip = new TextBox { PlaceholderText = "Link tooltip (optional)" };
        var activation = new ComboBox { ItemsSource = Enum.GetValues<InternalLinkActivation>(), SelectedItem = InternalLinkActivation.ModifierClick };
        AutomationProperties.SetName(outline, "Document outline"); AutomationProperties.SetName(bookmarks, "Bookmarks");
        AutomationProperties.SetName(name, "Bookmark name"); AutomationProperties.SetName(tooltip, "Internal link tooltip");
        AutomationProperties.SetName(activation, "Internal link activation");
        panel.Children.Add(Label("Outline")); panel.Children.Add(outline);
        panel.Children.Add(MenuAction("Go to heading", () => { if (outline.SelectedItem is OutlineItem item) Editor!.NavigateToOutline(item.Entry); }, false));
        panel.Children.Add(Label("Bookmarks")); panel.Children.Add(bookmarks); panel.Children.Add(name);
        bookmarks.SelectionChanged += (_, _) => { if (bookmarks.SelectedItem is string selected) name.Text = selected; };
        panel.Children.Add(MenuAction("Go to bookmark", () => { if (bookmarks.SelectedItem is string selected) Editor!.NavigateToBookmark(selected); }, false));
        panel.Children.Add(MenuAction("Add bookmark at selection", () => { Editor!.AddBookmark(name.Text ?? ""); Refresh(); }));
        panel.Children.Add(MenuAction("Rename bookmark", () => { if (bookmarks.SelectedItem is string selected) { Editor!.RenameBookmark(selected, name.Text ?? ""); Refresh(); } }));
        panel.Children.Add(MenuAction("Delete bookmark", () => { if (bookmarks.SelectedItem is string selected) { Editor!.DeleteBookmark(selected); Refresh(); } }));
        panel.Children.Add(Label("Internal link on selection")); panel.Children.Add(tooltip); panel.Children.Add(activation);
        panel.Children.Add(MenuAction("Link selection to bookmark", () =>
        {
            if (bookmarks.SelectedItem is string selected)
                Editor!.SetInternalLink(selected, string.IsNullOrEmpty(tooltip.Text) ? null : tooltip.Text,
                    activation.SelectedItem is InternalLinkActivation value ? value : InternalLinkActivation.ModifierClick);
        }));
        panel.Children.Add(MenuAction("Remove internal link", () => Editor!.ApplyStyle(s => s with { InternalLink = null })));
        _navigationButton = AddFlyout("Navigate", "Outline, bookmarks and internal links", new ScrollViewer { Content = panel, MaxHeight = 560 }, editing: false);
        _navigationButton.Flyout!.Opened += (_, _) => Refresh();
        void Refresh()
        {
            if (Editor is not { } editor) return;
            var selectedName = bookmarks.SelectedItem as string;
            outline.ItemsSource = editor.GetOutline().Select(e => new OutlineItem(e)).ToArray();
            bookmarks.ItemsSource = editor.Document.Bookmarks.Select(b => b.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (selectedName is not null) bookmarks.SelectedItem = selectedName;
        }
    }
    private sealed record OutlineItem(DocumentOutlineEntry Entry)
    { public override string ToString() => new string(' ', (Entry.Level - 1) * 2) + Entry.Text; }

    private void AddFieldsFlyout()
    {
        var panel = new StackPanel { Width = 310, Spacing = 6 };
        var instruction = new TextBox { PlaceholderText = "Field instruction, e.g. DATE or TOC", Text = "DATE" };
        var fields = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Field" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var previewCodes = new CheckBox { Content = "Show field codes", IsChecked = true };
        var previewText = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180 };
        AutomationProperties.SetName(previewCodes, "Preview field codes"); AutomationProperties.SetName(previewText, "Read-only active story field preview");
        var previewContent = new StackPanel { Spacing = 4 };
        previewContent.Children.Add(previewCodes); previewContent.Children.Add(previewText);
        var preview = new Expander { Header = "Preview active story", Content = previewContent, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(instruction, "Field instruction"); AutomationProperties.SetName(fields, "Document fields");
        AutomationProperties.SetName(status, "Field update status");
        panel.Children.Add(Label("Fields")); panel.Children.Add(instruction);
        panel.Children.Add(MenuAction("Insert field", () => { Editor!.Session.InsertField(instruction.Text ?? ""); Refresh(); }));
        panel.Children.Add(MenuAction("Update fields", () =>
        {
            var result = Editor!.UpdateFieldsWithLayout(new() { Clock = DateTimeOffset.Now, Culture = System.Globalization.CultureInfo.CurrentCulture });
            status.Text = $"{result.UpdatedCount} field(s) updated" + (result.Diagnostics.IsEmpty ? "" : Environment.NewLine + string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message).Distinct().Take(5)));
            Refresh();
        }));
        panel.Children.Add(status); panel.Children.Add(fields);
        panel.Children.Add(preview);
        previewCodes.IsCheckedChanged += (_, _) => RefreshPreview();
        preview.PropertyChanged += (_, change) => { if (change.Property == Expander.IsExpandedProperty) RefreshPreview(); };
        fields.SelectionChanged += (_, _) => { if (fields.SelectedItem is FieldItem item) instruction.Text = item.Instruction; };
        panel.Children.Add(MenuAction("Apply field instruction", () => { if (fields.SelectedItem is FieldItem item) { Editor!.Session.SetFieldInstruction(item.Id, instruction.Text ?? ""); Refresh(); } }));
        panel.Children.Add(MenuAction("Lock field", () => { if (fields.SelectedItem is FieldItem item) { Editor!.Session.SetFieldLocked(item.Id, true); Refresh(); } }));
        panel.Children.Add(MenuAction("Unlock field", () => { if (fields.SelectedItem is FieldItem item) { Editor!.Session.SetFieldLocked(item.Id, false); Refresh(); } }));
        panel.Children.Add(MenuAction("Remove field, keep result", () => { if (fields.SelectedItem is FieldItem item) { Editor!.Session.RemoveField(item.Id); Refresh(); } }));
        panel.Children.Add(Label("Contents and captions"));
        panel.Children.Add(MenuAction("Insert table of contents", () => { Editor!.Session.InsertField("TOC \\o \"1-3\" \\h"); Refresh(); }));
        panel.Children.Add(MenuAction("Insert list of figures", () => { Editor!.Session.InsertField("TOC \\c \"Figure\" \\h"); Refresh(); }));
        panel.Children.Add(MenuAction("Insert list of tables", () => { Editor!.Session.InsertField("TOC \\c \"Table\" \\h"); Refresh(); }));
        var label = new TextBox { Text = "Figure", PlaceholderText = "Caption label" };
        var caption = new TextBox { PlaceholderText = "Caption text" };
        AutomationProperties.SetName(label, "Caption label"); AutomationProperties.SetName(caption, "Caption text");
        panel.Children.Add(label); panel.Children.Add(caption);
        panel.Children.Add(MenuAction("Insert caption", () => { Editor!.Session.InsertCaption(label.Text ?? "Figure", caption.Text ?? ""); Refresh(); }));
        _editingControls.AddRange([instruction, label, caption]);
        var button = AddFlyout("Fields", "Insert and update document fields", new ScrollViewer { Content = panel, MaxHeight = 560 }, editing: false);
        button.Flyout!.Opened += (_, _) => Refresh();
        void Refresh()
        {
            if (Editor is not { } editor) return;
            var selected = (fields.SelectedItem as FieldItem)?.Id;
            var items = editor.Document.Fields.Select(f => new FieldItem(f.Id, f.Instruction, f.IsLocked)).ToArray();
            fields.ItemsSource = items;
            fields.SelectedItem = items.FirstOrDefault(f => f.Id == selected);
            RefreshPreview();
        }
        void RefreshPreview()
        {
            if (!preview.IsExpanded || Editor is not { } editor) { previewText.Text = ""; return; }
            previewText.Text = previewCodes.IsChecked == true
                ? FieldCodeProjection.Create(editor.Document, editor.ActiveStoryId, showAll: true).Text
                : editor.Document.GetStoryDocument(editor.ActiveStoryId).PlainText;
        }
    }
    private sealed record FieldItem(Guid Id, string Instruction, bool Locked)
    { public override string ToString() => Instruction + (Locked ? " (locked)" : ""); }
    private void ToggleRightToLeft()
    {
        var value = Editor!.FormattingState.Paragraph(s => s.RightToLeft);
        var enabled = value.IsMixed || !value.Value;
        Editor.ApplyParagraphStyle(s => s with { RightToLeft = enabled });
    }
    private void ToggleBaseline(Baseline baseline)
    {
        var value = Editor!.FormattingState.Text(s => s.Baseline);
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
            if (_storyStatus is not null) _storyStatus.Text = editor.Document.Stories.TryGetValue(editor.ActiveStoryId, out var activeStory)
                ? "Editing " + activeStory.Kind.ToString().ToLowerInvariant() : "Editing document body";
            var state = editor.FormattingState;
            foreach (var (button, read) in _toggles) button.IsChecked = read(state);
            foreach (var control in _editingControls) control.IsEnabled = !editor.IsReadOnly;
            if (_mergeFieldUpdate is not null)
                _mergeFieldUpdate.IsEnabled = !editor.IsReadOnly && _mergeFieldId is not null && editor.CurrentMergeField?.Id == _mergeFieldId;
            foreach (var (control, read) in _numericFormatting)
            {
                if (control.IsKeyboardFocusWithin) continue;
                var value = read(state); control.Value = value.IsMixed || value.Value is null ? null : (decimal)value.Value;
                control.PlaceholderText = value.IsMixed ? "Mixed" : "Automatic";
            }
            if (_heading is not null) _heading.SelectedIndex = state.HeadingLevel.IsMixed ? -1 : Math.Min(6, state.HeadingLevel.Value);
            if (_font is not null) _font.SelectedItem = state.FontFamily.IsMixed ? null : state.FontFamily.Value ?? "Default";
            if (_size is not null) _size.SelectedItem = state.FontSize.IsMixed ? null : state.FontSize.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            if (_viewMode is not null) _viewMode.SelectedItem = editor.ViewMode;
            if (_zoom is not null && !_zoom.IsKeyboardFocusWithin) _zoom.Value = (decimal)(editor.Zoom * 100);
            if (_pagesPerRow is not null && !_pagesPerRow.IsKeyboardFocusWithin) _pagesPerRow.Value = editor.PagesPerRow;
            if (_pageGap is not null && !_pageGap.IsKeyboardFocusWithin) _pageGap.Value = (decimal)editor.PageGap;
            if (_pageStatus is not null) _pageStatus.Text = $"Page {editor.CurrentPageNumber} of {editor.PageCount}";
            if (_pageNumber is not null)
            {
                _pageNumber.Maximum = Math.Max(1, editor.PageCount);
                if (!_pageNumber.IsKeyboardFocusWithin) _pageNumber.Value = editor.CurrentPageNumber;
            }
        }
        finally { _updating = false; }
    }
}
