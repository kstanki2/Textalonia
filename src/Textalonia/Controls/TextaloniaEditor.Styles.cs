using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    /// <summary>Embedded-font and substitution diagnostics observed during layout. Documents without embedded fonts retain Avalonia native fallback.</summary>
    public IReadOnlyList<DocumentFontDiagnostic> FontDiagnostics => _surface?.Layout.FontDiagnostics ?? [];

    /// <summary>Applies a paragraph style to selected paragraphs or selected table cells.</summary>
    public void ApplyNamedParagraphStyle(string id, bool clearDirectFormatting = true)
    {
        if (Session.IsReadOnly) return;
        if (CellSelection is null) { Session.ApplyNamedParagraphStyle(id, clearDirectFormatting); return; }
        if (!Session.Document.Styles.Paragraphs.ContainsKey(id)) throw new ArgumentException("Unknown paragraph style.", nameof(id));
        TextStyle Clear(TextStyle value) => clearDirectFormatting ? value with { Overrides = new() } : value;
        FormatSelectedCellParagraphs(paragraph => paragraph with
        {
            Style = paragraph.Style with { StyleId = id, Overrides = clearDirectFormatting ? new() : paragraph.Style.Overrides ?? ParagraphStyleOverrides.FromStyle(paragraph.Style) },
            DefaultStyle = Clear(paragraph.DefaultStyle),
            Runs = paragraph.Runs.Select(run => run with { Style = Clear(run.Style) }).ToImmutableArray()
        });
    }

    /// <summary>Applies a character style to selected text or selected table cells.</summary>
    public void ApplyNamedCharacterStyle(string id, bool clearDirectFormatting = true)
    {
        if (Session.IsReadOnly) return;
        if (CellSelection is null) { Session.ApplyNamedCharacterStyle(id, clearDirectFormatting); return; }
        if (!Session.Document.Styles.Characters.ContainsKey(id)) throw new ArgumentException("Unknown character style.", nameof(id));
        TextStyle Change(TextStyle value) => value with { StyleId = id, Overrides = clearDirectFormatting ? new() : value.Overrides ?? TextStyleOverrides.FromStyle(value) };
        FormatSelectedCellParagraphs(paragraph => paragraph.Format(0, paragraph.Length, Change) with { DefaultStyle = Change(paragraph.DefaultStyle) });
    }

    /// <summary>Opens a font dialog. Only fields changed by the user are applied to a mixed selection.</summary>
    public Task<bool> ShowFontDialogAsync()
    {
        var dialog = new FormattingDialog("Font");
        var state = FormattingState;
        var edits = new Dictionary<string, Func<TextStyle, TextStyle>>();
        void Text(string label, Func<TextStyle, string?> read, Func<TextStyle, string?, TextStyle> write) =>
            dialog.TextField(label, state.Text(read), value => edits[label] = style => write(style, value));
        void Number(string label, Func<TextStyle, double> read, decimal min, decimal max, Func<TextStyle, double, TextStyle> write) =>
            dialog.NumberField(label, state.Text(read), min, max, value => edits[label] = style => write(style, value));
        void Flag(string label, Func<TextStyle, bool> read, Func<TextStyle, bool, TextStyle> write) =>
            dialog.Flag(label, state.Text(read), value => edits[label] = style => write(style, value));
        Text("Font family", s => s.FontFamily, (s, v) => s with { FontFamily = v, ThemeFont = null });
        Number("Size (DIP)", s => s.FontSize, 1, 512, (s, v) => s with { FontSize = v });
        Flag("Bold", s => s.EffectiveBold, (s, v) => s with { Bold = v, FontWeight = null });
        Flag("Italic", s => s.Italic, (s, v) => s with { Italic = v });
        Text("Text color", s => s.Foreground, (s, v) => s with { Foreground = v, ThemeForeground = null });
        Text("Highlight color", s => s.Background, (s, v) => s with { Background = v, ThemeBackground = null });
        dialog.EnumField("Underline", state.Text(s => s.UnderlineKind == UnderlineKind.None && s.Underline ? UnderlineKind.Single : s.UnderlineKind),
            value => edits["Underline"] = s => s with { UnderlineKind = value, Underline = false });
        Text("Underline color", s => s.UnderlineColor, (s, v) => s with { UnderlineColor = v });
        Flag("Underline words only", s => s.UnderlineWordsOnly, (s, v) => s with { UnderlineWordsOnly = v });
        dialog.EnumField("Strike", state.Text(s => s.StrikeKind == StrikeKind.None && s.Strikethrough ? StrikeKind.Single : s.StrikeKind),
            value => edits["Strike"] = s => s with { StrikeKind = value, Strikethrough = false });
        Flag("All caps", s => s.AllCaps, (s, v) => s with { AllCaps = v });
        Flag("Small caps", s => s.SmallCaps, (s, v) => s with { SmallCaps = v });
        Number("Tracking (DIP)", s => s.Tracking, -100, 100, (s, v) => s with { Tracking = v });
        Number("Horizontal scale (%)", s => s.HorizontalScale * 100, 10, 1000, (s, v) => s with { HorizontalScale = v / 100 });
        Number("Baseline offset (DIP)", s => s.BaselineOffset, -512, 512, (s, v) => s with { BaselineOffset = v });
        var kerning = state.Text(s => s.KerningThreshold);
        dialog.NumberField("Kerning threshold (DIP)", new(kerning.Value ?? 0, kerning.IsMixed || kerning.Value is null), 0, 512,
            value => edits["Kerning"] = s => s with { KerningThreshold = value });
        dialog.Button("Use font default kerning", () => edits["Kerning"] = s => s with { KerningThreshold = null });
        Text("Language (BCP-47)", s => s.Language, (s, v) => s with { Language = v });
        Flag("Do not proofread", s => s.NoProof, (s, v) => s with { NoProof = v });
        return ShowFormattingDialog(dialog, () => ApplyStyle(style => edits.Values.Aggregate(style, (value, edit) => edit(value))));
    }

    /// <summary>Opens paragraph formatting, including independently stored outline and page rules.</summary>
    public Task<bool> ShowParagraphDialogAsync()
    {
        var dialog = new FormattingDialog("Paragraph");
        var state = FormattingState;
        var edits = new Dictionary<string, Func<ParagraphStyle, ParagraphStyle>>();
        void Number(string label, Func<ParagraphStyle, double> read, decimal min, decimal max, Func<ParagraphStyle, double, ParagraphStyle> write) =>
            dialog.NumberField(label, state.Paragraph(read), min, max, value => edits[label] = style => write(style, value));
        void Flag(string label, Func<ParagraphStyle, bool> read, Func<ParagraphStyle, bool, ParagraphStyle> write) =>
            dialog.Flag(label, state.Paragraph(read), value => edits[label] = style => write(style, value));
        dialog.EnumField("Alignment", state.Alignment, value => edits["Alignment"] = s => s with { Alignment = value });
        Number("Left indent (DIP)", s => s.Indent, 0, 1000, (s, v) => s with { Indent = v });
        Number("Right indent (DIP)", s => s.RightIndent, 0, 1000, (s, v) => s with { RightIndent = v });
        Number("First line indent (DIP)", s => s.FirstLineIndent, -1000, 1000, (s, v) => s with { FirstLineIndent = v });
        Number("Space before (DIP)", s => s.SpaceBefore, 0, 1000, (s, v) => s with { SpaceBefore = v });
        Number("Space after (DIP)", s => s.SpaceAfter, 0, 1000, (s, v) => s with { SpaceAfter = v });
        dialog.EnumField("Line spacing mode", state.Paragraph(s => s.LineSpacingMode), value => edits["Line spacing mode"] = s => s with { LineSpacingMode = value, LineHeight = null });
        Number("Line spacing (multiple or DIP)", s => s.LineSpacing, 0.1m, 1000, (s, v) => s with { LineSpacing = v });
        Number("Outline level (0 is body)", s => s.OutlineLevel, 0, 9, (s, v) => s with { OutlineLevel = (int)v });
        dialog.TextField("Shading color", state.Paragraph(s => s.Shading), value => edits["Shading"] = s => s with { Shading = value });
        var borderColor = dialog.TextField("All borders color", new FormattingValue<string?>(null), _ => { });
        dialog.NumberField("All borders width (DIP; 0 clears)", new FormattingValue<double>(0, true), 0, 1000,
            value => edits["Borders"] = s => s with { Borders = new(new(value, Clean(borderColor.Text)), new(value, Clean(borderColor.Text)), new(value, Clean(borderColor.Text)), new(value, Clean(borderColor.Text))) });
        Flag("Right to left", s => s.RightToLeft, (s, v) => s with { RightToLeft = v });
        Flag("Contextual spacing", s => s.ContextualSpacing, (s, v) => s with { ContextualSpacing = v });
        Flag("Page break before", s => s.PageBreakBefore, (s, v) => s with { PageBreakBefore = v });
        Flag("Keep with next", s => s.KeepWithNext, (s, v) => s with { KeepWithNext = v });
        Flag("Keep lines together", s => s.KeepTogether, (s, v) => s with { KeepTogether = v });
        Flag("Widow / orphan control", s => s.WidowControl, (s, v) => s with { WidowControl = v });
        Number("Grid character spacing (DIP)", s => s.EastAsianGrid?.CharacterSpacing ?? 0, 0, 1000, (s, v) => s with { EastAsianGrid = (s.EastAsianGrid ?? new()) with { CharacterSpacing = v } });
        Number("Grid line spacing (DIP)", s => s.EastAsianGrid?.LineSpacing ?? 0, 0, 1000, (s, v) => s with { EastAsianGrid = (s.EastAsianGrid ?? new()) with { LineSpacing = v } });
        Flag("Snap to document grid", s => s.SnapToGrid, (s, v) => s with { SnapToGrid = v });
        return ShowFormattingDialog(dialog, () => ApplyParagraphStyle(style => edits.Values.Aggregate(style, (value, edit) => edit(value))));
    }

    /// <summary>Opens a keyboard-accessible tab-stop editor.</summary>
    public Task<bool> ShowTabsDialogAsync()
    {
        var dialog = new FormattingDialog("Tabs");
        var current = FormattingState.Paragraph(s => s.TabStops);
        var stops = current.IsMixed ? new List<TabStop>() : current.Value.ToList();
        var changed = false;
        double? defaultWidth = null;
        dialog.NumberField("Default tab width (DIP; 0 is automatic)", FormattingState.Paragraph(s => s.DefaultTabWidth), 0, 1000, value => defaultWidth = value);
        if (current.IsMixed) dialog.Body.Children.Add(new TextBlock { Text = "Tab stops differ across the selection. Editing this list replaces them.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var list = new ListBox { MinHeight = 100, MaxHeight = 200, ItemsSource = stops.ToArray() };
        AutomationProperties.SetName(list, "Tab stops"); dialog.Body.Children.Add(list);
        var position = dialog.NumberField("Position (DIP)", new(48d), 0, 10000, _ => { });
        var alignment = dialog.EnumField("Tab alignment", new FormattingValue<TabAlignment>(TabAlignment.Left), _ => { });
        var leader = dialog.EnumField("Tab leader", new FormattingValue<TabLeader>(TabLeader.None), _ => { });
        var decimalCharacter = dialog.TextField("Decimal character", new FormattingValue<string?>("."), _ => { });
        dialog.Button("Add or replace tab", () =>
        {
            var mark = decimalCharacter.Text ?? ".";
            if (mark.Length != 1 || char.IsControl(mark[0])) throw new FormatException("Enter one printable decimal character.");
            var at = (double)(position.Value ?? 48);
            stops.RemoveAll(stop => stop.Position == at);
            stops.Add(new(at, (TabAlignment)alignment.SelectedItem!, (TabLeader)leader.SelectedItem!, mark[0]));
            stops.Sort((a, b) => a.Position.CompareTo(b.Position)); changed = true; list.ItemsSource = stops.ToArray();
        });
        dialog.Button("Remove selected tab", () =>
        {
            if (list.SelectedItem is TabStop stop) { stops.Remove(stop); changed = true; list.ItemsSource = stops.ToArray(); }
        });
        dialog.Button("Clear all tabs", () => { stops.Clear(); changed = true; list.ItemsSource = stops.ToArray(); });
        return ShowFormattingDialog(dialog, () => ApplyParagraphStyle(s => s with
        { TabStops = changed ? stops.ToImmutableArray() : s.TabStops, DefaultTabWidth = defaultWidth ?? s.DefaultTabWidth }));
    }

    /// <summary>Creates, edits and applies named character, paragraph and table styles.</summary>
    public Task<bool> ShowStylesDialogAsync()
    {
        var dialog = new FormattingDialog("Styles", "Save style");
        var catalog = Session.Document.Styles;
        var choices = new List<StyleChoice> { new("Paragraph", null, "New paragraph style"), new("Character", null, "New character style"), new("Table", null, "New table style") };
        choices.AddRange(catalog.Paragraphs.Values.OrderBy(s => s.Name ?? s.Id).Select(s => new StyleChoice("Paragraph", s.Id, s.Name ?? s.Id)));
        choices.AddRange(catalog.Characters.Values.OrderBy(s => s.Name ?? s.Id).Select(s => new StyleChoice("Character", s.Id, s.Name ?? s.Id)));
        choices.AddRange(catalog.Tables.Values.OrderBy(s => s.Name ?? s.Id).Select(s => new StyleChoice("Table", s.Id, s.Name ?? s.Id)));
        var choose = new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(choose, "Named style"); dialog.Body.Children.Add(choose);
        var id = dialog.TextField("Style ID", new FormattingValue<string?>(null), _ => { });
        var name = dialog.TextField("Style name", new FormattingValue<string?>(null), _ => { });
        var parent = dialog.TextField("Based on style ID", new FormattingValue<string?>(null), _ => { });
        var linked = dialog.TextField("Linked style ID", new FormattingValue<string?>(null), _ => { });
        var next = dialog.TextField("Next paragraph style ID", new FormattingValue<string?>(null), _ => { });
        var family = dialog.TextField("Font family (blank inherits)", new FormattingValue<string?>(null), _ => { });
        var size = dialog.NumberField("Font size (blank inherits)", new FormattingValue<double>(default, true), 1, 512, _ => { });
        var bold = dialog.Flag("Bold (indeterminate inherits)", new FormattingValue<bool>(default, true), _ => { });
        bold.IsThreeState = true;
        var spacing = dialog.NumberField("Space after (blank inherits)", new FormattingValue<double>(default, true), 0, 1000, _ => { });
        var shading = dialog.TextField("Table shading (blank inherits)", new FormattingValue<string?>(null), _ => { });
        void Load()
        {
            if (choose.SelectedItem is not StyleChoice choice) return;
            var character = choice.Id is { } cid && choice.Kind == "Character" ? catalog.Characters[cid] : null;
            var paragraph = choice.Id is { } pid && choice.Kind == "Paragraph" ? catalog.Paragraphs[pid] : null;
            var table = choice.Id is { } tid && choice.Kind == "Table" ? catalog.Tables[tid] : null;
            var formatting = character?.Formatting ?? paragraph?.TextFormatting ?? new();
            id.Text = choice.Id; id.IsReadOnly = choice.Id is not null; name.Text = character?.Name ?? paragraph?.Name ?? table?.Name;
            parent.Text = character?.BasedOn ?? paragraph?.BasedOn ?? table?.BasedOn;
            linked.Text = character?.LinkedStyle ?? paragraph?.LinkedStyle; linked.IsEnabled = choice.Kind != "Table";
            next.Text = paragraph?.NextStyle; next.IsEnabled = choice.Kind == "Paragraph";
            family.Text = formatting.FontFamily.IsSet ? formatting.FontFamily.Value : null;
            size.Value = formatting.FontSize.IsSet ? (decimal)formatting.FontSize.Value : null;
            bold.IsChecked = formatting.Bold.IsSet ? formatting.Bold.Value : null;
            family.IsEnabled = size.IsEnabled = bold.IsEnabled = choice.Kind != "Table";
            spacing.Value = paragraph?.Formatting.SpaceAfter is { IsSet: true } after ? (decimal)after.Value : null;
            spacing.IsEnabled = choice.Kind == "Paragraph";
            shading.Text = table?.Formatting.Background is { IsSet: true } background ? background.Value : null;
            shading.IsEnabled = choice.Kind == "Table";
        }
        choose.SelectionChanged += (_, _) => Load(); Load();
        dialog.Button("Apply selected saved style", () =>
        {
            if (choose.SelectedItem is not StyleChoice { Id: { } saved } choice) throw new FormatException("Save a style before applying it.");
            dialog.Commit(() =>
            {
                if (choice.Kind == "Paragraph") ApplyNamedParagraphStyle(saved);
                else if (choice.Kind == "Character") ApplyNamedCharacterStyle(saved);
                else Session.UpdateCurrentTable((table, _, _) => table with { StyleId = saved, StyleOverrides = new() });
            });
        });
        return ShowFormattingDialog(dialog, () =>
        {
            var choice = (StyleChoice)choose.SelectedItem!;
            var styleId = Clean(id.Text) ?? throw new FormatException("A style ID is required.");
            var oldText = choice.Kind == "Character" && choice.Id is { } cid ? catalog.Characters[cid].Formatting :
                choice.Kind == "Paragraph" && choice.Id is { } pid ? catalog.Paragraphs[pid].TextFormatting : new TextStyleOverrides();
            var text = oldText with { FontFamily = Clean(family.Text) is { } font ? new(font) : default,
                FontSize = size.Value is { } fontSize ? new((double)fontSize) : default, Bold = bold.IsChecked is { } isBold ? new(isBold) : default };
            if (choice.Kind == "Character")
                catalog = catalog with { Characters = catalog.Characters.SetItem(styleId, new() { Id = styleId, Name = Clean(name.Text), BasedOn = Clean(parent.Text), LinkedStyle = Clean(linked.Text), Formatting = text }) };
            else if (choice.Kind == "Paragraph")
            {
                var oldParagraph = choice.Id is { } existing ? catalog.Paragraphs[existing].Formatting : new ParagraphStyleOverrides();
                catalog = catalog with { Paragraphs = catalog.Paragraphs.SetItem(styleId, new() { Id = styleId, Name = Clean(name.Text), BasedOn = Clean(parent.Text), LinkedStyle = Clean(linked.Text), NextStyle = Clean(next.Text), TextFormatting = text,
                    Formatting = oldParagraph with { SpaceAfter = spacing.Value is { } after ? new((double)after) : default } }) };
            }
            else
            {
                var oldTable = choice.Id is { } existing ? catalog.Tables[existing].Formatting : new TableStyleOverrides();
                catalog = catalog with { Tables = catalog.Tables.SetItem(styleId, new() { Id = styleId, Name = Clean(name.Text), BasedOn = Clean(parent.Text),
                    Formatting = oldTable with { Background = Clean(shading.Text) is { } color ? new(color) : default } }) };
            }
            Session.SetStyles(catalog);
        });
    }

    private async Task<bool> ShowFormattingDialog(FormattingDialog dialog, Action apply)
    {
        if (Session.IsReadOnly || TopLevel.GetTopLevel(this) is not Window owner) return false;
        var revision = Session.Revision; var selection = Session.Selection; var cells = CellSelection;
        dialog.ValidateCommit = () =>
        {
            if (Session.IsReadOnly) throw new InvalidOperationException("The document is read-only.");
            if (Session.Revision != revision || Session.Selection != selection || CellSelection != cells)
                throw new InvalidOperationException("The document or selection changed. Close and reopen this dialog.");
        };
        dialog.Apply = apply;
        try { return await dialog.ShowDialog<bool>(owner); }
        finally { FocusDocument(); }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private sealed record StyleChoice(string Kind, string? Id, string Name)
    { public override string ToString() => $"{Name} ({Kind})"; }
}

internal sealed class FormattingDialog : Window
{
    internal StackPanel Body { get; } = new() { Spacing = 6, Margin = new Avalonia.Thickness(16) };
    internal Action? Apply { get; set; }
    internal Action? ValidateCommit { get; set; }
    private readonly TextBlock _error = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Avalonia.Media.Brushes.Firebrick };

    internal FormattingDialog(string title, string applyLabel = "Apply")
    {
        Title = title; Width = 460; Height = 620; MinWidth = 360; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel();
        var footer = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(16) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); footer.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var apply = new Button { Content = applyLabel, IsDefault = true };
        apply.Click += (_, _) => Attempt(() => Commit(() => Apply?.Invoke()));
        actions.Children.Add(cancel); actions.Children.Add(apply); footer.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = Body }); Content = root;
    }

    internal void Commit(Action apply) { ValidateCommit?.Invoke(); apply(); Close(true); }
    private void Attempt(Action action)
    {
        try { _error.Text = null; action(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { _error.Text = exception.Message; }
    }

    private void Add(string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        Body.Children.Add(new TextBlock { Text = label }); Body.Children.Add(control);
    }

    internal TextBox TextField(string label, FormattingValue<string?> initial, Action<string?> changed)
    {
        var control = new TextBox { Text = initial.IsMixed ? null : initial.Value, PlaceholderText = initial.IsMixed ? "Mixed" : null };
        Add(label, control); control.TextChanged += (_, _) => changed(string.IsNullOrWhiteSpace(control.Text) ? null : control.Text.Trim()); return control;
    }

    internal NumericUpDown NumberField(string label, FormattingValue<double> initial, decimal minimum, decimal maximum, Action<double> changed)
    {
        var control = new NumericUpDown { Minimum = minimum, Maximum = maximum, Value = initial.IsMixed ? null : (decimal)initial.Value, FormatString = "0.###", Increment = 1 };
        Add(label, control); control.ValueChanged += (_, _) => { if (control.Value is { } value) changed((double)value); }; return control;
    }

    internal CheckBox Flag(string label, FormattingValue<bool> initial, Action<bool> changed)
    {
        var control = new CheckBox { Content = label, IsChecked = initial.IsMixed ? null : initial.Value };
        AutomationProperties.SetName(control, label); Body.Children.Add(control);
        control.IsCheckedChanged += (_, _) => { if (control.IsChecked is { } value) changed(value); }; return control;
    }

    internal ComboBox EnumField<T>(string label, FormattingValue<T> initial, Action<T> changed) where T : struct, Enum
    {
        var control = new ComboBox { ItemsSource = Enum.GetValues<T>(), SelectedItem = initial.IsMixed ? null : initial.Value, HorizontalAlignment = HorizontalAlignment.Stretch };
        Add(label, control); control.SelectionChanged += (_, _) => { if (control.SelectedItem is T value) changed(value); }; return control;
    }

    internal void Button(string label, Action action)
    {
        var control = new Button { Content = label }; AutomationProperties.SetName(control, label);
        control.Click += (_, _) => Attempt(action); Body.Children.Add(control);
    }
}
