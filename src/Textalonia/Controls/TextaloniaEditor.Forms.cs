using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    /// <summary>The innermost content control containing the active selection.</summary>
    public DocumentContentControl? CurrentContentControl => Session.CurrentContentControl;
    public bool SelectContentControl(Guid id) => Session.SelectContentControl(id);
    public bool SelectNextContentControl(bool backwards = false) => Session.SelectNextContentControl(backwards);
    public bool SetContentControlValue(Guid id, string value) => Session.SetContentControlValue(id, value);
    public bool ToggleContentControl(Guid id) => Session.ToggleContentControl(id);

    /// <summary>Edits a form value with list/date validation. Enter opens this dialog for an atomic value;
    /// Space toggles a checkbox and Tab navigates between editable controls, including other stories.</summary>
    public Task<bool> ShowContentControlValueDialogAsync()
    {
        if (CurrentContentControl is not { SupportsInteraction: true, LockContents: false } control ||
            Session.GetCapability(EditOperation.Forms) != CommandCapability.Enabled) return Task.FromResult(false);
        var dialog = new FormattingDialog(string.IsNullOrWhiteSpace(control.Title) ? "Form value" : control.Title);
        var value = control.Kind == ContentControlKind.CheckBox ? control.IsChecked ? "true" : "false" : control.Value;
        Func<string> readValue;
        if (control.Kind == ContentControlKind.CheckBox)
        {
            var check = dialog.Flag("Checked", new(control.IsChecked), _ => { });
            readValue = () => check.IsChecked == true ? "true" : "false";
        }
        else if (control.Kind == ContentControlKind.DropDown)
        {
            var choices = control.Items.Select(item => new FormValueChoice(item.DisplayText, item.Value)).ToArray();
            var choice = new ComboBox { ItemsSource = choices, HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedItem = choices.FirstOrDefault(item => item.Value == value), PlaceholderText = control.Placeholder };
            AutomationProperties.SetName(choice, "Value"); dialog.Body.Children.Add(choice);
            readValue = () => (choice.SelectedItem as FormValueChoice)?.Value ?? "";
        }
        else
        {
            var text = dialog.TextField(control.Kind == ContentControlKind.Date ? "Date (yyyy-MM-dd)" : "Value", new(value), _ => { });
            text.PlaceholderText = control.Placeholder;
            text.AcceptsReturn = control.Kind == ContentControlKind.RichText;
            readValue = () => text.Text ?? "";
            if (control.Kind == ContentControlKind.ComboBox)
            {
                var choices = control.Items.Select(item => new FormValueChoice(item.DisplayText, item.Value)).ToArray();
                var choice = new ComboBox { ItemsSource = choices, HorizontalAlignment = HorizontalAlignment.Stretch,
                    PlaceholderText = "Choose a suggested value" };
                AutomationProperties.SetName(choice, "Suggested values");
                choice.SelectionChanged += (_, _) => { if (choice.SelectedItem is FormValueChoice item) text.Text = item.Value; };
                dialog.Body.Children.Add(choice);
            }
        }
        return ShowFormattingDialog(dialog, () =>
        {
            if (Session.GetCapability(EditOperation.Forms) != CommandCapability.Enabled)
                throw new InvalidOperationException("Editing this form value is no longer permitted.");
            var replacement = readValue();
            if (control.ValidateValue(replacement) is { } error) throw new FormatException(error);
            Session.SetContentControlValue(control.Id, replacement);
        });
    }

    private sealed record FormValueChoice(string Label, string Value)
    {
        public override string ToString() => Label;
    }
}
