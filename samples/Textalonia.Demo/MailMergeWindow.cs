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
using Textalonia.Layout;
using Textalonia.MailMerge;
using Textalonia.Model;
using Textalonia.Model.Fields;
using Textalonia.Pdf.Skia;
using Textalonia.Rendering;
using Textalonia.Serialization;

namespace Textalonia.Demo;

/// <summary>A host-owned template, recipient preview, and batch generation example.</summary>
public sealed class MailMergeWindow : Window
{
    private const string SampleRecords = """
        [
          { "CustomerName": "Alex Chen", "Company": "Northwind", "Balance": 1250.5, "DueDate": "2026-10-15",
            "Items": [{ "Description": "Consulting", "Quantity": 2, "Price": 500 }, { "Description": "Support", "Quantity": 1, "Price": 250.5 }] },
          { "CustomerName": "Sam Rivera", "Company": "Contoso", "Balance": 87.25, "DueDate": "2026-10-20",
            "Items": [{ "Description": "Support", "Quantity": 1, "Price": 87.25 }] },
          { "CustomerName": "Taylor Morgan", "Balance": 460, "DueDate": "2026-11-01", "Items": [] }
        ]
        """;
    internal const string InvoiceSampleRecords = """
        [
          { "CustomerName": "Alex Chen", "Company": "Northwind", "Balance": 1250.5, "DueDate": "2026-10-15",
            "Items": [
              { "Description": "Consulting", "Quantity": 2, "Price": 500,
                "Taxes": [{ "TaxName": "Sales tax", "Amount": 40 }, { "TaxName": "Local tax", "Amount": 10 }] },
              { "Description": "Support", "Quantity": 1, "Price": 250.5, "Taxes": [] }
            ] },
          { "CustomerName": "Sam Rivera", "Company": "Contoso", "Balance": 87.25, "DueDate": "2026-10-20",
            "Items": [{ "Description": "Support", "Quantity": 1, "Price": 87.25, "Taxes": [] }] },
          { "CustomerName": "Taylor Morgan", "Balance": 460, "DueDate": "2026-11-01", "Items": [] }
        ]
        """;
    private readonly TextaloniaEditor _template = new() { Document = CreateTemplate() };
    private readonly TextaloniaViewer _preview = new();
    private readonly TextaloniaViewer _output = new();
    private readonly TextaloniaViewer _combinedOutput = new();
    private readonly TextBox _data = new() { Text = SampleRecords, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _recipient = new();
    private readonly ComboBox _previewRecipient = new() { MinWidth = 190 };
    private readonly CheckBox _includeRecipient = new() { Content = "Include recipient", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _selectionSummary = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _generatedRecipient = new() { MinWidth = 170 };
    private readonly Button _saveAll = new() { Content = "Save all as DOCX ZIP...", IsEnabled = false };
    private readonly Button _saveCombinedDocx = new() { Content = "Save combined DOCX...", IsEnabled = false };
    private readonly Button _saveCombinedPdf = new() { Content = "Save combined PDF...", IsEnabled = false };
    private readonly MailMergeOptions _options = new() { Culture = CultureInfo.GetCultureInfo("en-US") };
    private FlowDocument[] _generated = [];
    private FlowDocument? _combined;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _records = [];
    private bool[] _included = [];
    private string? _parsedData;
    private bool _updatingRecipientControls;
    private int _recordIndex;
    private bool _dirty;
    private bool _generationIsCurrent;
    private bool _combinedIsCurrent;
    private FlowDocument? _generatedTemplate;
    private string? _generatedData;
    private FlowDocument? _combinedTemplate;
    private string? _combinedData;
    private bool HasCurrentBatch => _generationIsCurrent && ReferenceEquals(_generatedTemplate, _template.Document) && _generatedData == (_data.Text ?? "");
    private bool HasCurrentCombined => _combinedIsCurrent && _combined is not null && ReferenceEquals(_combinedTemplate, _template.Document) && _combinedData == (_data.Text ?? "");

    public MailMergeWindow(FlowDocument? template = null, string? recordsJson = null)
    {
        if (template is not null) _template.Document = template;
        if (recordsJson is not null) _data.Text = recordsJson;
        Title = "Mail merge studio"; Width = 1220; Height = 860; MinWidth = 800; MinHeight = 600;
        AutomationProperties.SetName(_template, "Mail merge template");
        AutomationProperties.SetName(_preview, "Recipient preview");
        AutomationProperties.SetName(_output, "Generated document");
        AutomationProperties.SetName(_combinedOutput, "Combined document");
        AutomationProperties.SetName(_data, "Recipient records JSON");
        AutomationProperties.SetName(_previewRecipient, "Preview recipient");
        AutomationProperties.SetName(_includeRecipient, "Include recipient in selected batch");
        AutomationProperties.SetName(_generatedRecipient, "Generated recipient");
        var heading = new TextBlock { Text = "Edit a template, preview recipients, then generate documents.", FontSize = 20, FontWeight = FontWeight.SemiBold };
        var previous = Button("Previous recipient", () => RefreshPreview(-1));
        var next = Button("Next recipient", () => RefreshPreview(1));
        var refresh = Button("Refresh preview", () => RefreshPreview());
        var generate = Button("Generate all", GenerateAll);
        var generateSelected = Button("Generate selected", GenerateSelected);
        var generateCombined = Button("Generate combined", GenerateCombined);
        var selectAll = Button("Select all recipients", () => SetAllRecipientsIncluded(true));
        var selectNone = Button("Clear recipient selection", () => SetAllRecipientsIncluded(false));
        var sample = Button("Open sample in new window", () => new MailMergeWindow().Show(this));
        var invoiceSample = Button("Open invoice sample", () => new MailMergeWindow(CreateInvoiceTemplate(), InvoiceSampleRecords).Show(this));
        var saveTemplate = new Button { Content = "Save template..." };
        saveTemplate.Click += async (_, _) => await SaveTemplateAsync();
        _saveAll.Click += async (_, _) => await SaveAllAsync();
        _saveCombinedDocx.Click += async (_, _) => await SaveCombinedDocxAsync();
        _saveCombinedPdf.Click += async (_, _) => await SaveCombinedPdfAsync();
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { previous, next, _previewRecipient, _includeRecipient, _selectionSummary,
                     selectAll, selectNone, refresh, generate, generateSelected, generateCombined,
                     saveTemplate, _saveAll, _saveCombinedDocx, _saveCombinedPdf, sample, invoiceSample })
        { control.Margin = new Thickness(0, 0, 8, 6); buttons.Children.Add(control); }
        var previewPanel = new DockPanel();
        _recipient.Margin = new Thickness(8); DockPanel.SetDock(_recipient, Dock.Top);
        previewPanel.Children.Add(_recipient); previewPanel.Children.Add(_preview);
        var dataHelp = new TextBlock
        {
            Text = "Enter an array of recipient objects. Child collections can be arrays of objects, including empty arrays and nested collections. Use exact field names; ISO yyyy-MM-dd strings become dates. Preview formatting uses en-US. Missing fields require a fallback or produce an error.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        var dataPanel = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(dataHelp, Dock.Top); dataPanel.Children.Add(dataHelp); dataPanel.Children.Add(_data);
        var outputPanel = new DockPanel();
        var outputHelp = new TextBlock
        {
            Text = "Generated documents are snapshots. Generate again after editing the template, recipients, or selection.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8)
        };
        DockPanel.SetDock(outputHelp, Dock.Top); outputPanel.Children.Add(outputHelp);
        _generatedRecipient.Margin = new Thickness(8); DockPanel.SetDock(_generatedRecipient, Dock.Top);
        outputPanel.Children.Add(_generatedRecipient); outputPanel.Children.Add(_output);
        var combinedPanel = new DockPanel();
        var combinedHelp = new TextBlock
        {
            Text = "Selected recipients are joined into one paginated document. Each recipient begins a new section with page numbering restarted at one.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8)
        };
        DockPanel.SetDock(combinedHelp, Dock.Top);
        combinedPanel.Children.Add(combinedHelp);
        combinedPanel.Children.Add(_combinedOutput);
        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Preview", Content = previewPanel },
                new TabItem { Header = "Recipients (JSON)", Content = dataPanel },
                new TabItem { Header = "Generated documents", Content = outputPanel },
                new TabItem { Header = "Combined document", Content = combinedPanel }
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
        _previewRecipient.SelectionChanged += (_, _) =>
        {
            if (!_updatingRecipientControls && _previewRecipient.SelectedIndex >= 0)
            {
                _recordIndex = _previewRecipient.SelectedIndex;
                RefreshPreview();
            }
        };
        _includeRecipient.PropertyChanged += (_, change) =>
        {
            if (change.Property == CheckBox.IsCheckedProperty)
                SetCurrentRecipientIncluded(_includeRecipient.IsChecked == true);
        };
        generate.Click += (_, _) =>
        {
            _saveAll.IsEnabled = HasCurrentBatch;
            if (HasCurrentBatch) tabs.SelectedIndex = 2;
        };
        generateSelected.Click += (_, _) =>
        {
            _saveAll.IsEnabled = HasCurrentBatch;
            if (HasCurrentBatch) tabs.SelectedIndex = 2;
        };
        generateCombined.Click += (_, _) =>
        {
            _saveCombinedDocx.IsEnabled = HasCurrentCombined;
            _saveCombinedPdf.IsEnabled = HasCurrentCombined;
            if (HasCurrentCombined) tabs.SelectedIndex = 3;
        };
        _template.DocumentChanged += (_, _) =>
        {
            _dirty = true;
            InvalidateGenerated();
            _status.Text = "Template changed. Refresh preview or generate again to update the results.";
        };
        _template.OperationFailed += (_, e) => _status.Text = e.Exception.Message;
        _data.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) return;
            _parsedData = null;
            InvalidateGenerated();
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
            var records = LoadRecipients();
            var index = Math.Clamp(_recordIndex + movement, 0, records.Count - 1);
            var preview = MailMergeProcessor.Preview(_template.Document, records[index], _options);
            _preview.Document = preview; _recordIndex = index;
            _updatingRecipientControls = true;
            try
            {
                _previewRecipient.SelectedIndex = index;
                _includeRecipient.IsChecked = _included[index];
            }
            finally { _updatingRecipientControls = false; }
            UpdateSelectionSummary();
            _recipient.Text = $"Recipient {index + 1} of {records.Count} · Read-only preview";
            _status.Text = "Template fields: " + string.Join(", ", MailMergeProcessor.GetFieldNames(_template.Document));
        }
        catch (Exception error) { _status.Text = "Preview failed: " + error.Message; }
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> LoadRecipients()
    {
        var data = _data.Text ?? "";
        if (_parsedData == data) return _records;
        var records = ParseRecords(data);
        _records = records;
        _included = Enumerable.Repeat(true, records.Count).ToArray();
        _parsedData = data;
        _recordIndex = Math.Clamp(_recordIndex, 0, records.Count - 1);
        _updatingRecipientControls = true;
        try
        {
            _previewRecipient.ItemsSource = Enumerable.Range(0, records.Count).Select(i => RecipientLabel(i, records[i])).ToArray();
            _previewRecipient.SelectedIndex = _recordIndex;
            _includeRecipient.IsChecked = true;
        }
        finally { _updatingRecipientControls = false; }
        UpdateSelectionSummary();
        return records;
    }

    private static string RecipientLabel(int index, IReadOnlyDictionary<string, object?> record)
    {
        var name = record.TryGetValue("CustomerName", out var customer) ? customer
            : record.TryGetValue("Name", out var person) ? person : null;
        return name is string { Length: > 0 } value ? $"Recipient {index + 1}: {value}" : $"Recipient {index + 1}";
    }

    private void UpdateSelectionSummary() =>
        _selectionSummary.Text = $"{_included.Count(included => included)} of {_included.Length} selected";

    private void InvalidateGenerated()
    {
        _generationIsCurrent = false;
        _combinedIsCurrent = false;
        _saveAll.IsEnabled = false;
        _saveCombinedDocx.IsEnabled = false;
        _saveCombinedPdf.IsEnabled = false;
    }

    private void SetCurrentRecipientIncluded(bool included)
    {
        if (_updatingRecipientControls || _recordIndex < 0 || _recordIndex >= _included.Length) return;
        if (_included[_recordIndex] == included) return;
        _included[_recordIndex] = included;
        InvalidateGenerated();
        UpdateSelectionSummary();
        _status.Text = "Recipient selection changed. Generate again to update the batch.";
    }

    private void SetAllRecipientsIncluded(bool included)
    {
        try
        {
            LoadRecipients();
            Array.Fill(_included, included);
            _updatingRecipientControls = true;
            try { _includeRecipient.IsChecked = included; }
            finally { _updatingRecipientControls = false; }
            InvalidateGenerated();
            UpdateSelectionSummary();
            _status.Text = "Recipient selection changed. Generate again to update the batch.";
        }
        catch (Exception error) { _status.Text = "Selection failed: " + error.Message; }
    }

    private void GenerateAll() => GenerateRecords(all: true);

    private void GenerateSelected() => GenerateRecords(all: false);

    private void GenerateRecords(bool all)
    {
        _generationIsCurrent = false;
        try
        {
            var template = _template.Document;
            var data = _data.Text ?? "";
            var records = LoadRecipients();
            var indices = Enumerable.Range(0, records.Count).Where(i => all || _included[i]).ToArray();
            if (indices.Length == 0) throw new FormatException("Select at least one recipient before generating the selected batch.");
            var generated = MailMergeProcessor.MergeMany(template, indices.Select(i => records[i]), _options).ToArray();
            _generated = generated;
            _generatedRecipient.ItemsSource = indices.Select(i => RecipientLabel(i, records[i])).ToArray();
            _generatedRecipient.SelectedIndex = 0;
            _output.Document = generated[0];
            _generatedTemplate = template; _generatedData = data;
            _generationIsCurrent = true;
            _status.Text = $"Generated {generated.Length} documents. Save all to export a ZIP of DOCX files.";
        }
        catch (Exception error) { _status.Text = "Generation failed: " + error.Message; }
    }

    private void GenerateCombined()
    {
        _combinedIsCurrent = false;
        try
        {
            var template = _template.Document;
            var data = _data.Text ?? "";
            var records = LoadRecipients();
            var indices = Enumerable.Range(0, records.Count).Where(i => _included[i]).ToArray();
            if (indices.Length == 0) throw new FormatException("Select at least one recipient before generating a combined document.");
            var mergeOptions = _options with { FieldOptions = _options.FieldOptions ?? new FieldEvaluationOptions() };
            var combined = MailMergeCombinedProcessor.Merge(template, indices.Select(i => records[i]), mergeOptions,
                new CombinedMailMergeOptions
                {
                    PageNumberPolicy = CombinedPageNumberPolicy.RestartEachRecord,
                    HeaderFooterPolicy = CombinedHeaderFooterPolicy.EachRecord,
                    CompleteDocument = (document, token) =>
                    {
                        using var engine = new PaginationEngine();
                        return engine.UpdateFields(document, new FieldEvaluationOptions
                        { Culture = _options.Culture, OnlyDirty = true, CancellationToken = token }).Document;
                    }
                });
            _combined = combined;
            _combinedOutput.Document = combined;
            _combinedTemplate = template;
            _combinedData = data;
            _combinedIsCurrent = true;
            _status.Text = $"Combined {indices.Length} recipients into one document with {combined.Sections.Length} section(s).";
        }
        catch (Exception error) { _status.Text = "Combined generation failed: " + error.Message; }
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

    private async Task SaveCombinedDocxAsync()
    {
        try
        {
            if (!HasCurrentCombined) { _status.Text = "Generate the combined document from the current template and recipients before saving."; return; }
            using var buffer = new MemoryStream();
            var result = await DocumentFormats.Docx.SaveWithReportAsync(_combined!, buffer);
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save combined mail merge document", SuggestedFileName = "MailMergeCombined.docx", DefaultExtension = "docx",
                FileTypeChoices = [new("Word document") { Patterns = ["*.docx"] }], ShowOverwritePrompt = true
            });
            if (file is null) return;
            buffer.Position = 0;
            await using (var stream = await file.OpenWriteAsync())
            {
                if (stream.CanSeek) { stream.Position = 0; stream.SetLength(0); }
                await buffer.CopyToAsync(stream); await stream.FlushAsync();
            }
            _status.Text = $"Saved combined DOCX to {file.Name}." +
                (result.Report.Diagnostics.IsEmpty ? "" : $" {result.Report.Diagnostics.Length} conversion notice(s).");
        }
        catch (Exception error) { _status.Text = "Save failed: " + error.Message; }
    }

    private async Task SaveCombinedPdfAsync()
    {
        try
        {
            if (!HasCurrentCombined) { _status.Text = "Generate the combined document from the current template and recipients before saving."; return; }
            using var engine = new PaginationEngine();
            using var pages = engine.Paginate(_combined!);
            using var renderer = new DocumentRenderer(pages);
            using var buffer = new MemoryStream();
            await new PdfExporter().ExportAsync(renderer, buffer);
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save combined mail merge PDF", SuggestedFileName = "MailMergeCombined.pdf", DefaultExtension = "pdf",
                FileTypeChoices = [new("PDF document") { Patterns = ["*.pdf"] }], ShowOverwritePrompt = true
            });
            if (file is null) return;
            buffer.Position = 0;
            await using (var stream = await file.OpenWriteAsync())
            {
                if (stream.CanSeek) { stream.Position = 0; stream.SetLength(0); }
                await buffer.CopyToAsync(stream); await stream.FlushAsync();
            }
            _status.Text = $"Saved {renderer.PageCount} combined PDF page(s) to {file.Name}.";
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

    public static FlowDocument CreateInvoiceTemplate()
    {
        static Paragraph Field(string name, string? format = null) => new([new RichRun(MergeFields.Create(name, format))]);
        static Paragraph Marker(string name) => Field(name);
        var table = Table.Create(7, 3) with { AutoFit = TableAutoFit.Window, RepeatHeaderRows = 1 };
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Paragraph("Description")] });
        table = table.SetCell(0, 1, table.Rows[0][1] with { Blocks = [new Paragraph("Quantity")] });
        table = table.SetCell(0, 2, table.Rows[0][2] with { Blocks = [new Paragraph("Price / tax")] });
        table = table.SetCell(1, 0, table.Rows[1][0] with { Blocks = [Marker("TableStart:Items")] });
        table = table.SetCell(2, 0, table.Rows[2][0] with { Blocks = [Field("Description")] });
        table = table.SetCell(2, 1, table.Rows[2][1] with { Blocks = [Field("Quantity")] });
        table = table.SetCell(2, 2, table.Rows[2][2] with { Blocks = [Field("Price", "C2")] });
        table = table.SetCell(3, 0, table.Rows[3][0] with { Blocks = [Marker("TableStart:Taxes")] });
        table = table.SetCell(4, 0, table.Rows[4][0] with { Blocks = [new Paragraph([
            new RichRun("  Tax: "), new RichRun(MergeFields.Create("TaxName"))])] });
        table = table.SetCell(4, 2, table.Rows[4][2] with { Blocks = [Field("Amount", "C2")] });
        table = table.SetCell(5, 0, table.Rows[5][0] with { Blocks = [Marker("TableEnd:Taxes")] });
        table = table.SetCell(6, 0, table.Rows[6][0] with { Blocks = [Marker("TableEnd:Items")] });
        return new FlowDocument([
            new Paragraph("Invoice") { Style = new() { HeadingLevel = 1 } },
            new Paragraph([new RichRun("Bill to: "), new RichRun(MergeFields.Create("CustomerName"), new() { Bold = true })]),
            new Paragraph([new RichRun("Company: "), new RichRun(MergeFields.Create("Company", fallbackText: "Individual customer"))]),
            new Paragraph([new RichRun("Due: "), new RichRun(MergeFields.Create("DueDate", "MMMM d, yyyy"))]),
            table,
            new Paragraph([new RichRun("Balance due: "), new RichRun(MergeFields.Create("Balance", "C2"))])
        ]);
    }

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
            records.Add(ParseRecord(item));
        }
        if (records.Count == 0) throw new FormatException("Enter at least one recipient.");
        return records;
    }

    private static IReadOnlyDictionary<string, object?> ParseRecord(JsonElement item)
    {
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
                JsonValueKind.Object => ParseRecord(property.Value),
                JsonValueKind.Array => ParseChildRecords(property.Name, property.Value),
                _ => throw new FormatException($"Field '{property.Name}' has an unsupported JSON value.")
            };
            if (!record.TryAdd(property.Name, value)) throw new FormatException($"Duplicate field name '{property.Name}'.");
        }
        if (record.Count == 0) throw new FormatException("Recipient and child records must contain at least one field.");
        return record;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ParseChildRecords(string name, JsonElement array)
    {
        var children = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new FormatException($"Child collection '{name}' must contain only objects.");
            children.Add(ParseRecord(item));
        }
        return children;
    }
}
