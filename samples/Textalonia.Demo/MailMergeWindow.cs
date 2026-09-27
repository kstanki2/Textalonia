using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Textalonia.Controls;
using Textalonia.MailMerge;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.Demo;

/// <summary>A host-owned template, recipient preview, and batch generation example.</summary>
public sealed class MailMergeWindow : Window
{
    private const string SampleRecords = """
        [
          { "CustomerName": "Alex Chen", "Company": "Northwind", "Balance": 1250.5, "DueDate": "2026-10-15" },
          { "CustomerName": "Sam Rivera", "Company": "Contoso", "Balance": 87.25, "DueDate": "2026-10-20" },
          { "CustomerName": "Taylor Morgan", "Balance": 460, "DueDate": "2026-11-01" }
        ]
        """;
    private readonly TextaloniaEditor _template = new() { Document = CreateTemplate() };
    private readonly TextaloniaViewer _preview = new();
    private readonly TextaloniaViewer _output = new();
    private readonly TextBox _data = new() { Text = SampleRecords, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _recipient = new();
    private readonly ComboBox _generatedRecipient = new() { MinWidth = 170 };
    private readonly MailMergeOptions _options = new() { Culture = CultureInfo.GetCultureInfo("en-US") };
    private FlowDocument[] _generated = [];
    private int _recordIndex;
    private bool _dirty;
    private bool _generationIsCurrent;
    private FlowDocument? _generatedTemplate;
    private string? _generatedData;
    private bool HasCurrentBatch => _generationIsCurrent && ReferenceEquals(_generatedTemplate, _template.Document) && _generatedData == (_data.Text ?? "");

    public MailMergeWindow(FlowDocument? template = null)
    {
        if (template is not null) _template.Document = template;
        Title = "Mail merge studio"; Width = 1220; Height = 860; MinWidth = 800; MinHeight = 600;
        AutomationProperties.SetName(_template, "Mail merge template");
        AutomationProperties.SetName(_preview, "Recipient preview");
        AutomationProperties.SetName(_output, "Generated document");
        AutomationProperties.SetName(_data, "Recipient records JSON");
        AutomationProperties.SetName(_generatedRecipient, "Generated recipient");
        var heading = new TextBlock { Text = "Edit a template, preview each recipient, then generate documents.", FontSize = 20, FontWeight = FontWeight.SemiBold };
        var previous = Button("Previous recipient", () => RefreshPreview(-1));
        var next = Button("Next recipient", () => RefreshPreview(1));
        var refresh = Button("Refresh preview", () => RefreshPreview());
        var generate = Button("Generate all", GenerateAll);
        var sample = Button("Open sample in new window", () => new MailMergeWindow().Show(this));
        var saveTemplate = new Button { Content = "Save template..." };
        saveTemplate.Click += async (_, _) => await SaveTemplateAsync();
        var saveAll = new Button { Content = "Save all as DOCX ZIP...", IsEnabled = false };
        saveAll.Click += async (_, _) => await SaveAllAsync();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { previous, next, refresh, generate, saveTemplate, saveAll, sample })
        { control.Margin = new Thickness(0, 0, 8, 6); buttons.Children.Add(control); }
        var previewPanel = new DockPanel();
        _recipient.Margin = new Thickness(8); DockPanel.SetDock(_recipient, Dock.Top);
        previewPanel.Children.Add(_recipient); previewPanel.Children.Add(_preview);
        var dataHelp = new TextBlock
        {
            Text = "Enter an array of records. Use exact field names and scalar values; ISO yyyy-MM-dd strings become dates. Preview formatting uses en-US. Missing fields require a fallback or produce an error.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        var dataPanel = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(dataHelp, Dock.Top); dataPanel.Children.Add(dataHelp); dataPanel.Children.Add(_data);
        var outputPanel = new DockPanel();
        var outputHelp = new TextBlock
        {
            Text = "Generated documents are snapshots. Generate again after editing the template or recipients.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8)
        };
        DockPanel.SetDock(outputHelp, Dock.Top); outputPanel.Children.Add(outputHelp);
        _generatedRecipient.Margin = new Thickness(8); DockPanel.SetDock(_generatedRecipient, Dock.Top);
        outputPanel.Children.Add(_generatedRecipient); outputPanel.Children.Add(_output);
        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Preview", Content = previewPanel },
                new TabItem { Header = "Recipients (JSON)", Content = dataPanel },
                new TabItem { Header = "Generated documents", Content = outputPanel }
            }
        };
        var templatePanel = new DockPanel();
        var templateLabel = new TextBlock { Text = "TEMPLATE · Use Merge field in the toolbar to insert or edit fields.", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(templateLabel, Dock.Top); templatePanel.Children.Add(templateLabel); templatePanel.Children.Add(_template);
        var pair = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        pair.Children.Add(templatePanel); Grid.SetColumn(tabs, 1); pair.Children.Add(tabs);
        var layout = new Grid { Margin = new Thickness(20), RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 10 };
        layout.Children.Add(heading); Grid.SetRow(buttons, 1); layout.Children.Add(buttons);
        Grid.SetRow(pair, 2); layout.Children.Add(pair); Grid.SetRow(_status, 3); layout.Children.Add(_status); Content = layout;
        _generatedRecipient.SelectionChanged += (_, _) =>
        {
            if (_generatedRecipient.SelectedIndex is var index && index >= 0 && index < _generated.Length) _output.Document = _generated[index];
        };
        generate.Click += (_, _) =>
        {
            saveAll.IsEnabled = HasCurrentBatch;
            if (HasCurrentBatch) tabs.SelectedIndex = 2;
        };
        _template.DocumentChanged += (_, _) =>
        {
            _dirty = true;
            _generationIsCurrent = false; saveAll.IsEnabled = false;
            _status.Text = "Template changed. Refresh preview or generate again to update the results.";
        };
        _template.OperationFailed += (_, e) => _status.Text = e.Exception.Message;
        _data.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) return;
            _generationIsCurrent = false; saveAll.IsEnabled = false;
            _status.Text = "Recipients changed. Refresh preview or generate again to update the results.";
        };
        Closing += async (_, e) =>
        {
            if (!_dirty || e.IsProgrammatic) return;
            e.Cancel = true;
            if (await ConfirmDiscardAsync()) { _dirty = false; Close(); }
        };
        RefreshPreview();
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text }; AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action(); return button;
    }

    private void RefreshPreview(int movement = 0)
    {
        try
        {
            var records = ParseRecords(_data.Text ?? "");
            var index = Math.Clamp(_recordIndex + movement, 0, records.Count - 1);
            var preview = MailMergeProcessor.Preview(_template.Document, records[index], _options);
            _preview.Document = preview; _recordIndex = index;
            _recipient.Text = $"Recipient {index + 1} of {records.Count} · Read-only preview";
            _status.Text = "Template fields: " + string.Join(", ", MailMergeProcessor.GetFieldNames(_template.Document));
        }
        catch (Exception error) { _status.Text = "Preview failed: " + error.Message; }
    }

    private void GenerateAll()
    {
        _generationIsCurrent = false;
        try
        {
            var template = _template.Document;
            var data = _data.Text ?? "";
            var records = ParseRecords(data);
            var generated = MailMergeProcessor.MergeMany(template, records, _options).ToArray();
            _generated = generated;
            _generatedRecipient.ItemsSource = Enumerable.Range(1, generated.Length).Select(i => $"Recipient {i}").ToArray();
            _generatedRecipient.SelectedIndex = 0;
            _output.Document = generated[0];
            _generatedTemplate = template; _generatedData = data;
            _generationIsCurrent = true;
            _status.Text = $"Generated {generated.Length} documents. Save all to export a ZIP of DOCX files.";
        }
        catch (Exception error) { _status.Text = "Generation failed: " + error.Message; }
    }

    private async Task SaveTemplateAsync()
    {
        try
        {
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save mail merge template", SuggestedFileName = "MailMergeTemplate.textalonia", DefaultExtension = "textalonia",
                FileTypeChoices = [new("Textalonia template") { Patterns = ["*.textalonia"] }], ShowOverwritePrompt = true
            });
            if (file is null) return;
            var snapshot = _template.Document;
            await DocumentFileStorage.SaveAsync(snapshot, DocumentFormats.Json, file.OpenWriteAsync);
            if (ReferenceEquals(snapshot, _template.Document)) _dirty = false;
            _status.Text = "Saved template " + file.Name;
        }
        catch (Exception error) { _status.Text = "Save failed: " + error.Message; }
    }

    private async Task SaveAllAsync()
    {
        try
        {
            if (!HasCurrentBatch) { _status.Text = "Generate documents from the current template and recipients before saving the batch."; return; }
            var snapshot = _generated;
            using var buffer = new MemoryStream();
            var notices = 0;
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                for (var index = 0; index < snapshot.Length; index++)
                {
                    await using var entry = zip.CreateEntry($"Recipient-{index + 1:0000}.docx").Open();
                    var result = await DocumentFormats.Docx.SaveWithReportAsync(snapshot[index], entry);
                    notices += result.Report.Diagnostics.Length;
                }
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save generated mail merge documents", SuggestedFileName = "MailMerge.zip", DefaultExtension = "zip",
                FileTypeChoices = [new("ZIP archive") { Patterns = ["*.zip"] }], ShowOverwritePrompt = true
            });
            if (file is null) return;
            buffer.Position = 0;
            await using (var stream = await file.OpenWriteAsync())
            {
                if (stream.CanSeek) { stream.Position = 0; stream.SetLength(0); }
                await buffer.CopyToAsync(stream); await stream.FlushAsync();
            }
            _status.Text = $"Saved {snapshot.Length} DOCX documents to {file.Name}." + (notices == 0 ? "" : $" {notices} conversion notice(s); some template features were simplified.");
        }
        catch (Exception error) { _status.Text = "Save failed: " + error.Message; }
    }

    public static FlowDocument CreateTemplate() => new([
        new Paragraph("Account reminder") { Style = new() { HeadingLevel = 1 } },
        new Paragraph([new RichRun("Dear "), new RichRun(MergeFields.Create("CustomerName"), new() { Bold = true }), new RichRun(",")]),
        new Paragraph([new RichRun("Company: "), new RichRun(MergeFields.Create("Company", fallbackText: "Individual customer"))]),
        new Paragraph([new RichRun("Your balance of "), new RichRun(MergeFields.Create("Balance", "C2")),
            new RichRun(" is due on "), new RichRun(MergeFields.Create("DueDate", "MMMM d, yyyy")), new RichRun(".")]),
        new Paragraph("Thank you for your business.")
    ]);

    private async Task<bool> ConfirmDiscardAsync()
    {
        var dialog = new Window
        {
            Title = "Unsaved mail merge template", Width = 430, Height = 190, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var keep = new Button { Content = "Keep editing" };
        var discard = new Button { Content = "Discard changes" };
        keep.Click += (_, _) => dialog.Close(false); discard.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 20,
            Children =
            {
                new TextBlock { Text = "Discard unsaved changes to this template? The original document in the main window is unchanged.", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { keep, discard } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> ParseRecords(string json)
    {
        using var source = JsonDocument.Parse(json);
        if (source.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("Recipient data must be a JSON array of objects.");
        var records = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in source.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Each recipient must be a JSON object.");
            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                object? value = property.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => property.Value.GetDecimal(),
                    JsonValueKind.String => DateTime.TryParseExact(property.Value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        ? date : property.Value.GetString(),
                    _ => throw new FormatException($"Field '{property.Name}' must contain a string, number, boolean, or null.")
                };
                if (!record.TryAdd(property.Name, value)) throw new FormatException($"Duplicate field name '{property.Name}'.");
            }
            records.Add(record);
        }
        if (records.Count == 0) throw new FormatException("Enter at least one recipient.");
        return records;
    }
}
