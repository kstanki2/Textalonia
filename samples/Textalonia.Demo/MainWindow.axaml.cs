using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Textalonia.Model;
using Textalonia.Serialization;

namespace Textalonia.Demo;

public partial class MainWindow : Window
{
    private bool _dirty;
    private bool _loading;
    private ConversionReport _lastConversionReport = ConversionReport.Empty;
    public MainWindow()
    {
        InitializeComponent();
        Editor.KeyboardComponent = new DemoKeyboardComponent();
        Editor.CaretComponent = new DemoCaretComponent();
        SampleInlineControls.Configure(Editor);
        Editor.Document = SampleDocument.Create();
        Editor.DocumentChanged += (_, _) =>
        {
            if (!_loading) _dirty = true;
            Editor.Highlights.Clear();
            UpdateCounts();
        };
        Editor.OperationFailed += (_, e) => Status.Text = e.Exception.Message;
        Editor.ConversionCompleted += (_, e) => RecordConversionReport(e.Report);
        ConversionReportButton.Click += async (_, _) => await ShowConversionReportAsync(_lastConversionReport);
        Editor.HyperlinkActivated += (_, e) => Status.Text = "Link selected: " + e.Uri;
        ReadOnlyToggle.IsCheckedChanged += (_, _) => Editor.IsReadOnly = ReadOnlyToggle.IsChecked == true;
        ThemeToggle.IsCheckedChanged += (_, _) => RequestedThemeVariant = ThemeToggle.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
        NewButton.Click += async (_, _) =>
        {
            if (!await ConfirmDiscardAsync()) return;
            ReplaceDocument(new FlowDocument()); Status.Text = "New document"; Editor.FocusDocument();
        };
        OpenButton.Click += async (_, _) => await OpenAsync();
        SaveButton.Click += async (_, _) => await SaveAsync();
        IntegrationsButton.Click += (_, _) => new IntegrationWindow().Show(this);
        Closing += async (_, e) =>
        {
            if (!_dirty || e.IsProgrammatic) return;
            e.Cancel = true;
            if (await ConfirmDiscardAsync()) { _dirty = false; Close(); }
        };
        Status.Text = "Preview package · Select text to format it. Ctrl/Cmd+F opens search.";
        UpdateCounts();
    }

    private static IReadOnlyList<FilePickerFileType> FileTypes => DocumentFormats.BuiltIn.Select(format =>
        new FilePickerFileType(format.Name) { Patterns = format.Extensions.Select(ext => "*" + ext).ToArray() }).ToArray();

    private async Task OpenAsync()
    {
        try
        {
            if (!await ConfirmDiscardAsync()) return;
            var revision = Editor.Session.Revision;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            { Title = "Open a document", AllowMultiple = false, FileTypeFilter = FileTypes });
            if (files.Count == 0) return;
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            var result = await DocumentFormats.ForPath(file.Name).LoadWithReportAsync(stream);
            if (revision != Editor.Session.Revision) throw new InvalidOperationException("The document changed while loading. Open the file again to replace it.");
            ReplaceDocument(result.Document);
            Status.Text = "Opened " + file.Name;
            await ShowConversionReportAsync(result.Report);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private async Task SaveAsync()
    {
        try
        {
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save a document", SuggestedFileName = "Document.textalonia", DefaultExtension = "textalonia",
                FileTypeChoices = FileTypes, ShowOverwritePrompt = true
            });
            if (file is null) return;
            var snapshot = Editor.Document;
            var format = DocumentFormats.ForPath(file.Name);
            var result = await DocumentFileStorage.SaveAsync(snapshot, format, file.OpenWriteAsync);
            if (ReferenceEquals(snapshot, Editor.Document) && format == DocumentFormats.Json) _dirty = false;
            Status.Text = "Saved " + file.Name + (format == DocumentFormats.Json ? "" : " · Use Textalonia format to preserve all editor features.");
            await ShowConversionReportAsync(result.Report);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void RecordConversionReport(ConversionReport report)
    {
        _lastConversionReport = report;
        ConversionReportButton.IsEnabled = !report.Diagnostics.IsEmpty;
        if (!report.Diagnostics.IsEmpty)
            Status.Text = $"{report.Diagnostics.Length} conversion notice(s) · Open Conversion report for details.";
    }
    private async Task ShowConversionReportAsync(ConversionReport report)
    {
        RecordConversionReport(report);
        if (report.Diagnostics.IsEmpty) return;
        var dialog = new Window
        {
            Title = "Conversion report", Width = 680, Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        var content = new DockPanel { Margin = new Avalonia.Thickness(20), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Bottom);
        content.Children.Add(close);
        content.Children.Add(new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = string.Join("\n\n", report.Diagnostics.Select(d =>
                    $"{d.Severity}: {d.Code}\n{d.UnsupportedFeature}\n{d.Fallback}" +
                    (d.SourceLocation is { } location ? $"\nSource: {location}" : "") +
                    (d.ModelId is { } id ? $"\nModel: {id}" : ""))),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 0, 0, 16)
            }
        });
        dialog.Content = content;
        await dialog.ShowDialog(this);
    }
    private void ReplaceDocument(FlowDocument document)
    {
        _loading = true;
        try { Editor.Document = document; _dirty = false; }
        finally { _loading = false; }
        UpdateCounts();
    }
    private void UpdateCounts()
    {
        var text = Editor.Text;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Counts.Text = $"{words:N0} words   ·   {new DocumentIndex(Editor.Document).Paragraphs.Length:N0} paragraphs";
    }
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_dirty) return true;
        var dialog = new Window
        {
            Title = "Unsaved document", Width = 400, Height = 170, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Keep editing" };
        var discard = new Button { Content = "Discard changes" };
        cancel.Click += (_, _) => dialog.Close(false);
        discard.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(discard);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 24,
            Children = { new TextBlock { Text = "Discard unsaved changes to this document?", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons }
        };
        return await dialog.ShowDialog<bool>(this);
    }
}
