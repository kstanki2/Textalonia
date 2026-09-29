using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    /// <summary>Inserts one Unicode text element through the normal text editing and undo path.</summary>
    public bool InsertSymbol(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol.Length is 0 or > 64 || symbol.Any(char.IsControl) ||
            new StringInfo(symbol).LengthInTextElements != 1 || !HasValidSurrogates(symbol))
            throw new ArgumentException("Enter one valid Unicode symbol or grapheme.", nameof(symbol));
        if (!CanEdit(EditOperation.Text)) return false;
        var revision = Session.Revision;
        Session.InsertText(symbol);
        return Session.Revision != revision;
    }

    /// <summary>Shows a keyboard accessible symbol picker and restores the editor focus on close.</summary>
    public Task<bool> ShowInsertSymbolDialogAsync()
    {
        var dialog = new FormattingDialog(UiText("InsertSymbol.Title", "Insert symbol"), UiText("Insert", "Insert"), Commands.Localize);
        var value = dialog.TextField(UiText("InsertSymbol.Symbol", "Symbol"), new FormattingValue<string?>("Ω"), _ => { });
        var choices = new WrapPanel();
        foreach (var symbol in new[] { "Ω", "π", "µ", "©", "®", "€", "→", "•", "✓", "§", "°", "–", "—" })
        {
            var choice = new Button { Content = symbol, MinWidth = 36, MinHeight = 32, Margin = new Thickness(2) };
            AutomationProperties.SetName(choice, $"{UiText("InsertSymbol.Choose", "Choose symbol")} {symbol}");
            choice.Click += (_, _) => { value.Text = symbol; value.Focus(); };
            choices.Children.Add(choice);
        }
        dialog.Body.Children.Add(choices);
        return ShowFormattingDialog(dialog, () =>
        {
            if (!InsertSymbol(value.Text ?? ""))
                throw new InvalidOperationException("The symbol cannot be inserted at this selection.");
        });
    }

    /// <summary>Shows built-in and typed custom properties; Apply commits one undo step.</summary>
    public Task<bool> ShowDocumentPropertiesDialogAsync()
    {
        var initial = Document.CoreProperties;
        var dialog = new FormattingDialog(UiText("DocumentProperties.Title", "Document properties"), localize: Commands.Localize);
        TextBox Field(string key, string label, string? value) =>
            dialog.TextField(UiText("DocumentProperties." + key, label), new FormattingValue<string?>(value), _ => { });
        var title = Field("Name", "Title", initial.Title);
        var subject = Field("Subject", "Subject", initial.Subject);
        var creator = Field("Author", "Author", initial.Creator);
        var keywords = Field("Keywords", "Keywords", initial.Keywords);
        var description = Field("Description", "Description", initial.Description);
        var category = Field("Category", "Category", initial.Category);
        var status = Field("ContentStatus", "Content status", initial.ContentStatus);
        var identifier = Field("Identifier", "Identifier", initial.Identifier);
        var language = Field("Language", "Language", initial.Language);
        var revision = Field("Revision", "Revision", initial.Revision);
        var version = Field("Version", "Version", initial.Version);
        var lastModifiedBy = Field("LastModifiedBy", "Last modified by", initial.LastModifiedBy);
        var created = dialog.TextField(UiText("DocumentProperties.Created", "Created (ISO 8601)"),
            new FormattingValue<string?>(initial.Created?.ToString("O", CultureInfo.InvariantCulture)), _ => { });
        var modified = dialog.TextField(UiText("DocumentProperties.Modified", "Modified (ISO 8601)"),
            new FormattingValue<string?>(initial.Modified?.ToString("O", CultureInfo.InvariantCulture)), _ => { });

        dialog.Body.Children.Add(new TextBlock { Text = UiText("DocumentProperties.Custom", "Custom properties"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
        var rows = new List<CustomPropertyRow>();
        var custom = new StackPanel { Spacing = 6 };
        dialog.Body.Children.Add(custom);
        void AddRow(DocumentCustomProperty property)
        {
            var name = new TextBox { Text = property.Name, PlaceholderText = UiText("DocumentProperties.CustomName", "Name"), Width = 170 };
            var type = new ComboBox { ItemsSource = Enum.GetValues<DocumentPropertyType>(), SelectedItem = property.Type, Width = 130 };
            var value = new TextBox { Text = property.Value, PlaceholderText = UiText("DocumentProperties.CustomValue", "Value") };
            var remove = new Button { Content = UiText("DocumentProperties.Remove", "Remove") };
            AutomationProperties.SetName(name, UiText("DocumentProperties.CustomName", "Custom property name"));
            AutomationProperties.SetName(type, UiText("DocumentProperties.CustomType", "Custom property type"));
            AutomationProperties.SetName(value, UiText("DocumentProperties.CustomValue", "Custom property value"));
            AutomationProperties.SetName(remove, UiText("DocumentProperties.RemoveCustom", "Remove custom property"));
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { name, type, remove } };
            var body = new StackPanel { Spacing = 3, Children = { heading, value } };
            var row = new CustomPropertyRow(body, name, type, value);
            rows.Add(row); custom.Children.Add(body);
            remove.Click += (_, _) => { rows.Remove(row); custom.Children.Remove(body); };
        }
        foreach (var property in Document.CustomProperties) AddRow(property);
        dialog.Button(UiText("DocumentProperties.AddCustom", "Add custom property"), () => AddRow(new DocumentCustomProperty()));
        return ShowFormattingDialog(dialog, () =>
        {
            var core = initial with
            {
                Title = Clean(title.Text), Subject = Clean(subject.Text), Creator = Clean(creator.Text),
                Keywords = Clean(keywords.Text), Description = Clean(description.Text), Category = Clean(category.Text),
                ContentStatus = Clean(status.Text), Identifier = Clean(identifier.Text), Language = Clean(language.Text),
                Revision = Clean(revision.Text), Version = Clean(version.Text), LastModifiedBy = Clean(lastModifiedBy.Text),
                Created = ParseDate(created.Text), Modified = ParseDate(modified.Text)
            };
            var properties = rows.Select(row => new DocumentCustomProperty
            {
                Name = row.Name.Text?.Trim() ?? "",
                Type = row.Type.SelectedItem is DocumentPropertyType type ? type : DocumentPropertyType.Text,
                Value = row.Value.Text ?? ""
            }).ToArray();
            if (Document.CoreProperties == core && Document.CustomProperties.SequenceEqual(properties)) return;
            if (!Session.SetDocumentProperties(core, properties))
                throw new InvalidOperationException("Document properties cannot be edited in the current mode.");
        });
    }

    private static DateTimeOffset? ParseDate(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date : throw new FormatException("Enter a valid ISO 8601 date and time.");

    private static bool HasValidSurrogates(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLowSurrogate(text[i])) return false;
            if (!char.IsHighSurrogate(text[i])) continue;
            if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }

    private sealed record CustomPropertyRow(StackPanel Body, TextBox Name, ComboBox Type, TextBox Value);

    private string UiText(string key, string fallback) => Commands.Localize?.Invoke("Textalonia.UI." + key) is { Length: > 0 } value ? value : fallback;
}
