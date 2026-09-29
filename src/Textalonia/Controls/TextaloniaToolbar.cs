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
    private readonly Dictionary<Control, EditOperation> _editingControls = [];
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
    private TextBlock? _selectionStatus;
    private CheckBox? _showRulers;
    private TextBlock? _storyStatus;
    private Button? _pictureProperties;
    private Button? _pictureRemove;
    private Button? _objectExtract;
    private Button? _formValue;
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
        if (change.OldValue is TextaloniaEditor old) { old.Session.Changed -= SessionChanged; old.TableCellSelectionChanged -= SessionChanged; old.PropertyChanged -= EditorPropertyChanged; old.Commands.LocalizationChanged -= LocalizationChanged; }
        if (change.NewValue is TextaloniaEditor editor) { editor.Session.Changed += SessionChanged; editor.TableCellSelectionChanged += SessionChanged; editor.PropertyChanged += EditorPropertyChanged; editor.Commands.LocalizationChanged += LocalizationChanged; }
        Build(); Refresh();
    }
    private void SessionChanged(object? sender, EventArgs e) => Refresh();
    private void LocalizationChanged(object? sender, EventArgs e) { Build(); Refresh(); }
    private string UiText(string key, string fallback) => Editor?.Commands.Localize?.Invoke("Textalonia.UI.Toolbar." + key) is { Length: > 0 } localized
        ? localized : fallback;
    private void EditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextaloniaEditor.ViewModeProperty || e.Property == TextaloniaEditor.ZoomProperty ||
            e.Property == TextaloniaEditor.PagesPerRowProperty || e.Property == TextaloniaEditor.PageGapProperty ||
            e.Property == TextaloniaEditor.PageCountProperty || e.Property == TextaloniaEditor.CurrentPageNumberProperty ||
            e.Property == TextaloniaEditor.IsReadOnlyProperty || e.Property == TextaloniaEditor.ShowRulersProperty) Refresh();
    }

    private void Build()
    {
        if (_findPanel is not null) _findPanel.Editor = null;
        Children.Clear(); _toggles.Clear(); _editingControls.Clear(); _numericFormatting.Clear();
        _mergeFieldUpdate = null; _mergeFieldId = null; _storyStatus = null;
        _pictureProperties = null; _pictureRemove = null; _objectExtract = null; _formValue = null;
        _viewMode = null; _zoom = null; _pagesPerRow = null; _pageGap = null; _pageNumber = null; _pageStatus = null;
        _selectionStatus = null; _showRulers = null;
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
        CatalogButton("Styles", "Create, edit or apply named styles", EditorCommandId.Styles);
        CatalogButton("Properties…", "Document properties", EditorCommandId.DocumentProperties, EditOperation.Metadata);
        CatalogButton("Font…", "Font dialog", EditorCommandId.Font);
        CatalogButton("Paragraph…", "Paragraph dialog", EditorCommandId.Paragraph);
        CatalogButton("Tabs…", "Tabs dialog", EditorCommandId.Tabs);
        CatalogButton("Page setup…", "Page setup dialog", EditorCommandId.PageSetup, EditOperation.Structure);
        CatalogButton("Page numbering…", "Page numbering dialog", EditorCommandId.PageNumbering, EditOperation.Structure);
        var viewButton = AddFlyout("View", "Document view, zoom and page navigation", ViewMenu(), editing: false);
        viewButton.Flyout!.Opened += (_, _) => Refresh();
        AddOutputFlyout();
        AddFlyout("Insert", "Insert link or table", InsertMenu(), editing: false);
        CatalogButton("Symbol…", "Insert Unicode symbol", EditorCommandId.InsertSymbol, EditOperation.Text);
        AddPicturesFlyout();
        DialogButton("Table\u2026", "Table properties", editor.ShowTablePropertiesDialogAsync, EditOperation.Tables);
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
        AddFormsFlyout();
    }

    private ComboBox Choice(string[] values, double width, string name)
    {
        var box = new ComboBox { ItemsSource = values, Width = width, Margin = new Thickness(2), MinHeight = 32, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(box, name); AutomationProperties.SetName(box, name);
        Children.Add(box); _editingControls.Add(box, EditOperation.Formatting); return box;
    }
    private ToggleButton Toggle(string text, string name, System.Windows.Input.ICommand command, Func<SelectionFormattingState, bool?> read)
    {
        var button = new ToggleButton { Content = text, Command = command, IsThreeState = true, MinWidth = 32, MinHeight = 32, Margin = new Thickness(2), Padding = new Thickness(8, 4) };
        ToolTip.SetTip(button, name); AutomationProperties.SetName(button, name);
        Children.Add(button); _editingControls.Add(button, EditOperation.Formatting); _toggles.Add((button, read)); return button;
    }
    private Button CommandButton(string text, string name, System.Windows.Input.ICommand command)
    {
        var button = MakeButton(text, name); button.Command = command; Children.Add(button);
        _editingControls.Add(button, text == "Undo" ? EditOperation.Undo : EditOperation.Redo); return button;
    }
    private Button ActionButton(string text, string name, Action action)
    {
        var button = MakeButton(text, name);
        button.Click += (_, _) => Editor?.Run(action);
        Children.Add(button); _editingControls.Add(button, EditOperation.Formatting); return button;
    }
    private void DialogButton(string text, string name, Func<Task<bool>> show, EditOperation operation = EditOperation.Formatting)
    {
        var button = MakeButton(text, name);
        button.Click += async (_, _) => await show();
        Children.Add(button); _editingControls.Add(button, operation);
    }
    private void CatalogButton(string text, string name, EditorCommandId id, EditOperation operation = EditOperation.Formatting)
    {
        var command = Editor!.Commands[id];
        var localized = Editor.Commands.Localize?.Invoke(command.LocalizationKey);
        var button = MakeButton(localized is { Length: > 0 } ? localized : text,
            localized is { Length: > 0 } ? localized : name);
        button.Command = command;
        Children.Add(button); _editingControls.Add(button, operation);
    }
    private static Button MakeButton(string text, string name)
    {
        var button = new Button { Content = text, MinHeight = 32, FontSize = 13, Margin = new Thickness(2), Padding = new Thickness(7, 4) };
        ToolTip.SetTip(button, name); AutomationProperties.SetName(button, name); return button;
    }
    private Button AddFlyout(string text, string name, Control content, bool editing = true, EditOperation operation = EditOperation.Formatting)
    {
        var button = MakeButton(text, name);
        button.Flyout = new Flyout { Content = content };
        Children.Add(button); if (editing) _editingControls.Add(button, operation); return button;
    }
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 6) };

    private void AddPicturesFlyout()
    {
        var panel = new StackPanel { Width = 290, Spacing = 4 };
        var button = AddFlyout("Pictures", "Pictures, watermarks and embedded objects", panel, editing: false);
        button.Flyout!.Opened += (_, _) => Refresh();
        panel.Children.Add(Label("Pictures"));
        AddDialog("Insert picture…", () => Editor!.ShowInsertImageDialogAsync());
        _pictureProperties = AddDialog("Picture or preview properties…", () => Editor!.ShowImagePropertiesDialogAsync());
        _pictureRemove = MenuAction("Remove selected picture or object", () => Editor!.RemoveCurrentImageOrOle(), operation: EditOperation.InlineObjects);
        panel.Children.Add(_pictureRemove);
        panel.Children.Add(Label("Section watermark"));
        AddDialog("Watermark settings…", () => Editor!.ShowWatermarkDialogAsync());
        AddDialog("Insert image watermark…", () => Editor!.ShowImageWatermarkDialogAsync());
        panel.Children.Add(MenuAction("Remove watermark from section", () => Editor!.RemoveWatermark(), operation: EditOperation.InlineObjects));
        panel.Children.Add(Label("Embedded objects"));
        AddDialog("Insert embedded file and preview…", () => Editor!.ShowInsertOleDialogAsync());
        _objectExtract = AddDialog("Extract selected embedded file…", () => Editor!.ShowExtractOleDialogAsync(), editing: false);

        Button AddDialog(string label, Func<Task<bool>> show, bool editing = true)
        {
            var command = MakeButton(label, label);
            command.Click += async (_, _) => { button.Flyout.Hide(); await show(); };
            panel.Children.Add(command);
            if (editing) _editingControls.Add(command, EditOperation.InlineObjects);
            return command;
        }
    }

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
        preview.Click += (_, _) => { button.Flyout.Hide(); Editor?.Commands.Execute(EditorCommandId.PrintPreview); };
        export.Click += (_, _) => { button.Flyout.Hide(); Editor?.Commands.Execute(EditorCommandId.ExportPdf); };
        print.Click += (_, _) => { button.Flyout.Hide(); Editor?.Commands.Execute(EditorCommandId.Print); };
        quick.Click += async (_, _) => { button.Flyout.Hide(); if (Editor is { } editor) await editor.QuickPrintAsync(); };
    }

    private Button MenuAction(string text, Action action, bool editing = true, EditOperation operation = EditOperation.Formatting)
    {
        var button = MakeButton(text, text); button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Click += (_, _) => Editor?.Run(action);
        if (editing) _editingControls.Add(button, operation);
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
            _numericFormatting.Add((control, read)); _editingControls.Add(control, EditOperation.Formatting); panel.Children.Add(control);
        }
    }
    private static FormattingValue<double?> AsNullable(FormattingValue<double> value) => new(value.Value, value.IsMixed);
    private static FormattingValue<double?> AsNullable(FormattingValue<int> value) => new(value.Value, value.IsMixed);

    private Control StoriesMenu()
    {
        Button MenuActionForOperation(string text, Action action, bool editing = true) => MenuAction(text, action, editing, EditOperation.Structure);
        var panel = new StackPanel { Width = 280, Spacing = 4 };
        _storyStatus = new TextBlock(); AutomationProperties.SetName(_storyStatus, "Active story"); panel.Children.Add(_storyStatus);
        panel.Children.Add(Label("Headers and footers"));
        foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
        {
            panel.Children.Add(MenuActionForOperation("Edit " + variant.ToString().ToLowerInvariant() + " header", () => Editor!.EditHeaderFooter(false, variant), editing: false));
            panel.Children.Add(MenuActionForOperation("Edit " + variant.ToString().ToLowerInvariant() + " footer", () => Editor!.EditHeaderFooter(true, variant), editing: false));
        }
        panel.Children.Add(MenuActionForOperation("Link to previous", () => Editor!.LinkHeaderFooterToPrevious(true)));
        panel.Children.Add(MenuActionForOperation("Unlink from previous", () => Editor!.LinkHeaderFooterToPrevious(false)));
        AddDialog("Header and footer options…", () => Editor!.ShowHeaderFooterDialogAsync());
        panel.Children.Add(new Separator());
        panel.Children.Add(MenuActionForOperation("Return to document (Esc)", () => Editor!.CloseStory(), editing: false));
        panel.Children.Add(Label("Page fields"));
        foreach (var field in Enum.GetValues<PageFieldKind>())
            panel.Children.Add(MenuActionForOperation("Insert " + field, () => Editor!.InsertPageField(field)));
        panel.Children.Add(Label("Notes"));
        var mark = new TextBox { PlaceholderText = "Custom mark (blank for numbering)" };
        AutomationProperties.SetName(mark, "Custom note mark"); panel.Children.Add(mark);
        panel.Children.Add(MenuActionForOperation("Insert footnote", () => Editor!.InsertFootnote(string.IsNullOrEmpty(mark.Text) ? null : mark.Text)));
        panel.Children.Add(MenuActionForOperation("Insert endnote", () => Editor!.InsertEndnote(string.IsNullOrEmpty(mark.Text) ? null : mark.Text)));
        panel.Children.Add(MenuActionForOperation("Delete active note", () =>
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
            _editingControls.Add(button, EditOperation.Structure); panel.Children.Add(button);
        }
    }

    private Control InsertMenu()
    {
        var panel = new StackPanel { Width = 260, Spacing = 4 };
        panel.Children.Add(Label("Breaks"));
        panel.Children.Add(MenuAction("Insert page break", () => Editor!.Session.InsertPageBreak(), operation: EditOperation.Structure));
        panel.Children.Add(MenuAction("Insert column break", () => Editor!.Session.InsertColumnBreak(), operation: EditOperation.Structure));
        foreach (var kind in Enum.GetValues<SectionBreakKind>())
            panel.Children.Add(MenuAction("Insert section: " + kind, () => Editor!.Session.InsertSectionBreak(kind), operation: EditOperation.Structure));
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
        Button TableAction(string text, Action action) => MenuAction(text, action, operation: EditOperation.Tables);
        panel.Children.Add(TableAction("Insert table", () => Editor!.InsertTable((int)(rows.Value ?? 3), (int)(columns.Value ?? 3))));
        panel.Children.Add(TableAction("Add row below", () => Editor!.InsertTableRow()));
        panel.Children.Add(TableAction("Add column after", () => Editor!.InsertTableColumn()));
        panel.Children.Add(MenuAction("Select current cell", () => Editor!.SelectCurrentTableCell(), editing: false));
        panel.Children.Add(MenuAction("Extend selection right", () => Editor!.ExtendTableCellSelection(0, 1), editing: false));
        panel.Children.Add(MenuAction("Extend selection down", () => Editor!.ExtendTableCellSelection(1, 0), editing: false));
        panel.Children.Add(TableAction("Merge selected cells", () => Editor!.MergeSelectedTableCells()));
        panel.Children.Add(TableAction("Merge with cell on right", () => Editor!.Session.UpdateCurrentTable((t, r, c) => t.MergeCells(r, c, 1, 2))));
        panel.Children.Add(TableAction("Merge with cell below", () => Editor!.Session.UpdateCurrentTable((t, r, c) => t.MergeCells(r, c, 2, 1))));
        panel.Children.Add(TableAction("Split cell", () => Editor!.SplitSelectedTableCells()));
        panel.Children.Add(TableAction("Shade cell", () => Editor!.SetTableCellBackground("#D9E9FA")));
        panel.Children.Add(TableAction("Delete row", () => Editor!.DeleteTableRows()));
        panel.Children.Add(TableAction("Delete column", () => Editor!.DeleteTableColumns()));
        panel.Children.Add(TableAction("Delete table", () => Editor!.DeleteSelectedTable()));
        panel.Children.Add(new Separator());
        panel.Children.Add(Label("Cell borders and padding"));
        var padding = Number("Cell padding", 8, 0, 1000);
        panel.Children.Add(padding);
        panel.Children.Add(TableAction("Apply cell padding", () =>
        {
            var value = (double)(padding.Value ?? 8); Editor!.SetTableCellPadding(new(value, value, value, value));
        }));
        var borderWidth = Number("Cell border width", 1, 0, 1000);
        panel.Children.Add(borderWidth);
        var borderKind = new ComboBox { ItemsSource = Enum.GetValues<BorderKind>(), SelectedItem = BorderKind.Solid };
        AutomationProperties.SetName(borderKind, "Cell border kind"); panel.Children.Add(borderKind);
        var borderColor = new TextBox { Text = "#808080", PlaceholderText = "Border color" };
        AutomationProperties.SetName(borderColor, "Cell border color"); panel.Children.Add(borderColor);
        panel.Children.Add(TableAction("Apply cell borders", () =>
        {
            var side = new BorderSide((double)(borderWidth.Value ?? 1), borderColor.Text) { Kind = (BorderKind)borderKind.SelectedItem! };
            Editor!.SetTableCellBorders(new(side, side, side, side));
        }));
        panel.Children.Add(TableAction("Remove cell borders", () => Editor!.SetTableCellBorders(new())));
        panel.Children.Add(Label("Table sizing"));
        panel.Children.Add(TableAction("Narrow column", () => Editor!.ResizeCurrentTableTrack(TableResizeAxis.Column, -8)));
        panel.Children.Add(TableAction("Widen column", () => Editor!.ResizeCurrentTableTrack(TableResizeAxis.Column, 8)));
        panel.Children.Add(TableAction("Shorten row", () => Editor!.ResizeCurrentTableTrack(TableResizeAxis.Row, -8)));
        panel.Children.Add(TableAction("Taller row", () => Editor!.ResizeCurrentTableTrack(TableResizeAxis.Row, 8)));
        var columnWidth = Number("Relative column width", 1, .001m, 100000);
        panel.Children.Add(columnWidth);
        panel.Children.Add(TableAction("Apply column width", () => Editor!.SetTableColumnWidth((double)(columnWidth.Value ?? 1))));
        var rowHeight = Number("Row height", 36, 1, 100000);
        panel.Children.Add(rowHeight);
        var rowMode = new ComboBox { ItemsSource = Enum.GetValues<TableRowHeightMode>(), SelectedItem = TableRowHeightMode.AtLeast };
        AutomationProperties.SetName(rowMode, "Row height policy"); panel.Children.Add(rowMode);
        panel.Children.Add(TableAction("Apply row height", () => Editor!.SetTableRowHeight((double)(rowHeight.Value ?? 36), (TableRowHeightMode)rowMode.SelectedItem!)));
        panel.Children.Add(new TextBlock { Text = "Alt+drag: select cells. Alt+Shift+arrows: extend selection. Drag cell edges to resize. Escape: cancel.", TextWrapping = TextWrapping.Wrap });
        return new ScrollViewer { Content = panel, MaxHeight = 520 };
    }

    private Control ViewMenu()
    {
        var panel = new StackPanel { Width = 260, Spacing = 6 };
        panel.Children.Add(Label(UiText("DocumentView", "Document view")));
        _viewMode = new ComboBox { ItemsSource = Enum.GetValues<DocumentViewMode>(), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_viewMode, UiText("DocumentView", "Document view")); panel.Children.Add(_viewMode);
        _viewMode.SelectionChanged += (_, _) =>
        {
            if (!_updating && _viewMode.SelectedItem is DocumentViewMode mode && Editor is { } editor)
                editor.Commands.Execute(mode switch
                {
                    DocumentViewMode.Simple => EditorCommandId.ViewSimple,
                    DocumentViewMode.Draft => EditorCommandId.ViewDraft,
                    _ => EditorCommandId.ViewPrintLayout
                });
        };
        panel.Children.Add(Label(UiText("ZoomPercent", "Zoom (%)")));
        _zoom = Number(UiText("ZoomPercent", "Zoom (%)"), 100, 10, 500); panel.Children.Add(_zoom);
        _zoom.ValueChanged += (_, _) =>
        {
            if (!_updating && _zoom.Value is { } value) Editor?.Commands.Execute(EditorCommandId.SetZoom, (double)value / 100);
        };
        panel.Children.Add(MenuAction(UiText("FitWidth", "Fit page width"), () => Editor!.Commands.Execute(EditorCommandId.FitWidth), editing: false));
        panel.Children.Add(MenuAction(UiText("FitPage", "Fit whole page"), () => Editor!.Commands.Execute(EditorCommandId.FitPage), editing: false));
        _showRulers = new CheckBox { Content = UiText("ShowRulers", "Show rulers") };
        AutomationProperties.SetName(_showRulers, UiText("ShowRulers", "Show rulers")); panel.Children.Add(_showRulers);
        _showRulers.IsCheckedChanged += (_, _) =>
        {
            if (!_updating) Editor?.Commands.Execute(EditorCommandId.ToggleRulers);
        };
        panel.Children.Add(Label(UiText("PagesPerRow", "Pages per row")));
        _pagesPerRow = Number(UiText("PagesPerRow", "Pages per row"), 1, 1, 8); panel.Children.Add(_pagesPerRow);
        _pagesPerRow.ValueChanged += (_, _) =>
        {
            if (!_updating && _pagesPerRow.Value is { } value) Editor?.Commands.Execute(EditorCommandId.SetPagesPerRow, (int)value);
        };
        panel.Children.Add(Label(UiText("PageGap", "Page gap (DIP)")));
        _pageGap = Number(UiText("PageGap", "Page gap (DIP)"), 24, 0, 1000); panel.Children.Add(_pageGap);
        _pageGap.ValueChanged += (_, _) =>
        {
            if (!_updating && _pageGap.Value is { } value) Editor?.Commands.Execute(EditorCommandId.SetPageGap, (double)value);
        };
        _pageStatus = new TextBlock(); AutomationProperties.SetName(_pageStatus, UiText("PageStatus", "Page status")); panel.Children.Add(_pageStatus);
        _selectionStatus = new TextBlock(); AutomationProperties.SetName(_selectionStatus, UiText("SelectionStatus", "Selection status")); panel.Children.Add(_selectionStatus);
        _pageNumber = Number(UiText("PageNumber", "Page number"), 1, 1, 1000000); panel.Children.Add(_pageNumber);
        panel.Children.Add(MenuAction(UiText("GoToPage", "Go to page"), () => Editor!.Commands.Execute(EditorCommandId.GoToPage, (int)(_pageNumber.Value ?? 1)), editing: false));
        panel.Children.Add(MenuAction(UiText("PreviousPage", "Previous page"), () => Editor!.Commands.Execute(EditorCommandId.GoToPage, Math.Max(1, Editor.CurrentPageNumber - 1)), editing: false));
        panel.Children.Add(MenuAction(UiText("NextPage", "Next page"), () => Editor!.Commands.Execute(EditorCommandId.GoToPage, Math.Min(Editor.PageCount, Editor.CurrentPageNumber + 1)), editing: false));
        return new ScrollViewer { Content = panel, MaxHeight = 520 };
    }

    private void AddMergeFieldFlyout()
    {
        Button MenuActionForOperation(string text, Action action, bool editing = true) => MenuAction(text, action, editing, EditOperation.InlineObjects);
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
        var insert = MenuActionForOperation("Insert merge field", () => Editor!.InsertMergeField(name.Text ?? "", ValueFormat(), Fallback()));
        panel.Children.Add(insert);
        var update = MenuActionForOperation("Update selected merge field", () =>
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
        var button = AddFlyout("Merge field", "Insert or edit a merge field", panel, operation: EditOperation.InlineObjects);
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
            update.IsEnabled = _mergeFieldId is not null && Editor?.CanEdit(EditOperation.InlineObjects) == true;
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
        panel.Children.Add(MenuAction("Add bookmark at selection", () => { Editor!.AddBookmark(name.Text ?? ""); Refresh(); }, operation: EditOperation.Metadata));
        panel.Children.Add(MenuAction("Rename bookmark", () => { if (bookmarks.SelectedItem is string selected) { Editor!.RenameBookmark(selected, name.Text ?? ""); Refresh(); } }, operation: EditOperation.Metadata));
        panel.Children.Add(MenuAction("Delete bookmark", () => { if (bookmarks.SelectedItem is string selected) { Editor!.DeleteBookmark(selected); Refresh(); } }, operation: EditOperation.Metadata));
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
        Button MenuActionForOperation(string text, Action action, bool editing = true) => MenuAction(text, action, editing, EditOperation.Metadata);
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
        panel.Children.Add(MenuActionForOperation("Insert field", () => { Editor!.Session.InsertField(instruction.Text ?? ""); Refresh(); }));
        panel.Children.Add(MenuActionForOperation("Update fields", () =>
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
        panel.Children.Add(MenuActionForOperation("Apply field instruction", () => { if (fields.SelectedItem is FieldItem item) { Editor!.Session.SetFieldInstruction(item.Id, instruction.Text ?? ""); Refresh(); } }));
        panel.Children.Add(MenuActionForOperation("Lock field", () => { if (fields.SelectedItem is FieldItem item) { Editor!.Session.SetFieldLocked(item.Id, true); Refresh(); } }));
        panel.Children.Add(MenuActionForOperation("Unlock field", () => { if (fields.SelectedItem is FieldItem item) { Editor!.Session.SetFieldLocked(item.Id, false); Refresh(); } }));
        panel.Children.Add(MenuActionForOperation("Remove field, keep result", () => { if (fields.SelectedItem is FieldItem item) { Editor!.Session.RemoveField(item.Id); Refresh(); } }));
        panel.Children.Add(Label("Contents and captions"));
        panel.Children.Add(MenuActionForOperation("Insert table of contents", () => { Editor!.Session.InsertField("TOC \\o \"1-3\" \\h"); Refresh(); }));
        panel.Children.Add(MenuActionForOperation("Insert list of figures", () => { Editor!.Session.InsertField("TOC \\c \"Figure\" \\h"); Refresh(); }));
        panel.Children.Add(MenuActionForOperation("Insert list of tables", () => { Editor!.Session.InsertField("TOC \\c \"Table\" \\h"); Refresh(); }));
        var label = new TextBox { Text = "Figure", PlaceholderText = "Caption label" };
        var caption = new TextBox { PlaceholderText = "Caption text" };
        AutomationProperties.SetName(label, "Caption label"); AutomationProperties.SetName(caption, "Caption text");
        panel.Children.Add(label); panel.Children.Add(caption);
        panel.Children.Add(MenuActionForOperation("Insert caption", () => { Editor!.Session.InsertCaption(label.Text ?? "Figure", caption.Text ?? ""); Refresh(); }));
        foreach (var input in new Control[] { instruction, label, caption }) _editingControls.Add(input, EditOperation.Metadata);
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

    private void AddFormsFlyout()
    {
        var panel = new StackPanel { Width = 270, Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = "Tab moves between fields. Space toggles a checkbox. Enter edits a selected list or date value.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(MenuAction("Previous form field", () => Editor!.SelectNextContentControl(true), editing: false));
        panel.Children.Add(MenuAction("Next form field", () => Editor!.SelectNextContentControl(), editing: false));
        _formValue = MakeButton("Edit form value\u2026", "Edit form value");
        _editingControls.Add(_formValue, EditOperation.Forms);
        panel.Children.Add(_formValue);
        var button = AddFlyout("Forms", "Fill document form fields", panel, editing: false);
        _formValue.Click += async (_, _) => { button.Flyout!.Hide(); if (Editor is { } editor) await editor.ShowContentControlValueDialogAsync(); };
        button.Flyout!.Opened += (_, _) => Refresh();
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
            foreach (var (control, operation) in _editingControls)
            {
                var capability = editor.Session.GetCapability(operation);
                control.IsVisible = capability != CommandCapability.Hidden;
                control.IsEnabled = capability == CommandCapability.Enabled;
            }
            if (_formValue is not null) _formValue.IsEnabled = editor.CanEdit(EditOperation.Forms) && editor.CurrentContentControl is { SupportsInteraction: true, LockContents: false };
            if (_pictureProperties is not null) _pictureProperties.IsEnabled = editor.CanEdit(EditOperation.InlineObjects) && editor.CurrentImageOrOle is not null;
            if (_pictureRemove is not null) _pictureRemove.IsEnabled = editor.CanEdit(EditOperation.InlineObjects) && editor.CurrentImageOrOle is not null;
            if (_objectExtract is not null) _objectExtract.IsEnabled = editor.CurrentOleObject is not null;
            if (_mergeFieldUpdate is not null)
                _mergeFieldUpdate.IsEnabled = editor.CanEdit(EditOperation.InlineObjects) && _mergeFieldId is not null && editor.CurrentMergeField?.Id == _mergeFieldId;
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
            if (_showRulers is not null) _showRulers.IsChecked = editor.ShowRulers;
            if (_zoom is not null && !_zoom.IsKeyboardFocusWithin) _zoom.Value = (decimal)(editor.Zoom * 100);
            if (_pagesPerRow is not null && !_pagesPerRow.IsKeyboardFocusWithin) _pagesPerRow.Value = editor.PagesPerRow;
            if (_pageGap is not null && !_pageGap.IsKeyboardFocusWithin) _pageGap.Value = (decimal)editor.PageGap;
            if (_pageStatus is not null) _pageStatus.Text = FormatStatus("PageFormat", "Page {0} of {1}", editor.CurrentPageNumber, editor.PageCount);
            if (_selectionStatus is not null) _selectionStatus.Text = editor.Session.Selection.IsEmpty
                ? FormatStatus("CaretFormat", "Caret {0} of {1}", editor.Session.Selection.Active + 1, editor.Session.Index.Length + 1)
                : FormatStatus("SelectionFormat", "Selected {0} character(s)", editor.Session.Selection.Length);
            if (_pageNumber is not null)
            {
                _pageNumber.Maximum = Math.Max(1, editor.PageCount);
                if (!_pageNumber.IsKeyboardFocusWithin) _pageNumber.Value = editor.CurrentPageNumber;
            }
        }
        finally { _updating = false; }
    }

    private string FormatStatus(string key, string fallback, params object[] values)
    {
        var format = UiText(key, fallback);
        try { return string.Format(System.Globalization.CultureInfo.CurrentUICulture, format, values); }
        catch (FormatException) { return string.Format(System.Globalization.CultureInfo.CurrentUICulture, fallback, values); }
    }
}
