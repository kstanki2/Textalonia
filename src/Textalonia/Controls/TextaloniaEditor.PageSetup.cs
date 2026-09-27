using System.Collections.Immutable;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    /// <summary>Edits the current physical section's page settings in one undoable operation.</summary>
    public Task<bool> ShowPageSetupDialogAsync()
    {
        var settings = Session.CurrentPageSettings;
        var dialog = new FormattingDialog("Page setup");
        dialog.Body.Children.Add(new TextBlock
        {
            Text = "Applies to the section containing the caret. Measurements use DIP (96 per inch). Width and height describe the portrait paper; landscape swaps them.",
            TextWrapping = TextWrapping.Wrap
        });
        var width = dialog.NumberField("Paper width (DIP)", new(settings.Width), 1, 100000, value => settings = settings with { Width = value });
        var height = dialog.NumberField("Paper height (DIP)", new(settings.Height), 1, 100000, value => settings = settings with { Height = value });
        dialog.Button("Letter paper", () => { width.Value = 816; height.Value = 1056; });
        dialog.Button("A4 paper", () => { width.Value = (decimal)DocumentUnits.FromMillimeters(210); height.Value = (decimal)DocumentUnits.FromMillimeters(297); });
        dialog.EnumField("Orientation", new FormattingValue<PageOrientation>(settings.Orientation), value => settings = settings with { Orientation = value });
        dialog.NumberField("Left margin (DIP)", new(settings.Margins.Left), 0, 100000, value => settings = settings with { Margins = settings.Margins with { Left = value } });
        dialog.NumberField("Top margin (DIP)", new(settings.Margins.Top), 0, 100000, value => settings = settings with { Margins = settings.Margins with { Top = value } });
        dialog.NumberField("Right margin (DIP)", new(settings.Margins.Right), 0, 100000, value => settings = settings with { Margins = settings.Margins with { Right = value } });
        dialog.NumberField("Bottom margin (DIP)", new(settings.Margins.Bottom), 0, 100000, value => settings = settings with { Margins = settings.Margins with { Bottom = value } });
        dialog.NumberField("Gutter (DIP)", new(settings.Gutter), 0, 100000, value => settings = settings with { Gutter = value });
        dialog.Flag("Mirror margins", new(settings.MirrorMargins), value => settings = settings with { MirrorMargins = value });
        var columnWeights = dialog.TextField("Column weights (comma separated)", new FormattingValue<string?>(settings.Columns.IsEmpty ? "1" :
            string.Join(", ", settings.Columns.Select(column => column.Width.ToString(CultureInfo.InvariantCulture)))), _ => { });
        dialog.NumberField("Column spacing (DIP)", new(settings.ColumnSpacing), 0, 100000, value => settings = settings with { ColumnSpacing = value });
        dialog.Flag("Balance final columns", new(settings.BalanceColumns), value => settings = settings with { BalanceColumns = value });
        dialog.TextField("Page background color", new FormattingValue<string?>(settings.Background), value => settings = settings with { Background = value });
        var bordersChanged = false;
        var borderColor = dialog.TextField("All page borders color", new FormattingValue<string?>(settings.Borders?.Left?.Color), _ => bordersChanged = true);
        var borderWidth = dialog.NumberField("All page borders width (DIP; 0 clears)", new(settings.Borders?.Left?.Width ?? 0), 0, 1000, _ => bordersChanged = true);
        var lineNumbering = settings.LineNumbering ?? new LineNumberingSettings();
        var linesEnabled = dialog.Flag("Show line numbers", new(settings.LineNumbering is not null), _ => { });
        dialog.NumberField("First line number", new(lineNumbering.Start), 1, int.MaxValue, value => lineNumbering = lineNumbering with { Start = (int)value });
        dialog.NumberField("Line number interval", new(lineNumbering.CountBy), 1, int.MaxValue, value => lineNumbering = lineNumbering with { CountBy = (int)value });
        dialog.NumberField("Line number distance (DIP)", new(lineNumbering.Distance), 0, 1000, value => lineNumbering = lineNumbering with { Distance = value });
        dialog.EnumField("Restart line numbers", new FormattingValue<LineNumberRestart>(lineNumbering.Restart), value => lineNumbering = lineNumbering with { Restart = value });
        var grid = settings.Grid ?? new EastAsianGrid();
        var gridEnabled = dialog.Flag("Use document grid", new(settings.Grid is not null), _ => { });
        dialog.NumberField("Grid character spacing (DIP)", new(grid.CharacterSpacing), 0, 1000, value => grid = grid with { CharacterSpacing = value });
        dialog.NumberField("Grid line spacing (DIP)", new(grid.LineSpacing), 0, 1000, value => grid = grid with { LineSpacing = value });
        return ShowFormattingDialog(dialog, () =>
        {
            var columns = (columnWeights.Text ?? "").Split(',', StringSplitOptions.TrimEntries);
            if (columns.Length is < 1 or > 32 || columns.Any(value => !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) || !double.IsFinite(weight) || weight <= 0))
                throw new FormatException("Enter between 1 and 32 positive column weights separated by commas, such as 1 or 1, 2.");
            settings = settings with
            {
                Columns = columns.Length == 1 ? [] : columns.Select(value => new PageColumn(double.Parse(value, CultureInfo.InvariantCulture))).ToImmutableArray(),
                LineNumbering = linesEnabled.IsChecked == true ? lineNumbering : null,
                Grid = gridEnabled.IsChecked == true ? grid : null
            };
            if (bordersChanged)
            {
                var side = new BorderSide((double)(borderWidth.Value ?? 0), Clean(borderColor.Text));
                settings = settings with { Borders = side.Width == 0 ? null : new(side, side, side, side) };
            }
            Session.SetPageSettings(settings);
        });
    }

    /// <summary>Edits page-number continuation, restart and format for the current physical section.</summary>
    public Task<bool> ShowPageNumberingDialogAsync()
    {
        var section = Session.CurrentSection;
        var dialog = new FormattingDialog("Page numbering");
        var restart = dialog.Flag("Restart page numbering", new(section?.PageNumberStart is not null), _ => { });
        var start = dialog.NumberField("First page number", new(section?.PageNumberStart ?? 1), 1, int.MaxValue, _ => { });
        var format = dialog.EnumField("Page number format", new FormattingValue<PageNumberFormat>(section?.PageNumberFormat ?? PageNumberFormat.Decimal), _ => { });
        dialog.Body.Children.Add(new TextBlock
        {
            Text = "This sets section numbering metadata. Inserting page-number fields into headers or body content is a separate feature.",
            TextWrapping = TextWrapping.Wrap
        });
        return ShowFormattingDialog(dialog, () => Session.SetSectionNumbering(restart.IsChecked == true ? (int)(start.Value ?? 1) : null, (PageNumberFormat)format.SelectedItem!));
    }
}
