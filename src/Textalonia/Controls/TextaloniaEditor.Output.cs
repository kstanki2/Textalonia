using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Textalonia.Export;
using Textalonia.Layout;
using Textalonia.Printing;
using Textalonia.Rendering;
using Textalonia.Model.Fields;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    /// <summary>Optional PDF backend. The editor never disposes this host-owned service.</summary>
    public IPagedDocumentExporter? PdfExporter { get; set; }
    /// <summary>Optional platform print adapter. The editor never disposes this host-owned service.</summary>
    public IPrintService? PrintService { get; set; }
    /// <summary>Output representations and unsupported-content policy, captured when output begins.</summary>
    public DocumentRenderOptions? OutputRenderOptions { get; set; }
    /// <summary>Null preserves cached field results. Otherwise output updates a detached snapshot using this explicit clock and resolver policy.</summary>
    public FieldEvaluationOptions? OutputFieldOptions { get; set; }

    /// <summary>
    /// Captures exact physical pages, independently of the current view, zoom, selection and active story.
    /// Call on the UI thread. The caller owns the returned renderer and its snapshot.
    /// </summary>
    public DocumentRenderer CreateOutputRenderer()
    {
        Dispatcher.UIThread.VerifyAccess();
        using var engine = new PaginationEngine();
        var document = Document;
        if (OutputFieldOptions is { } fieldOptions)
        {
            var updated = engine.UpdateFields(document, fieldOptions, FontFamily);
            if (updated.Diagnostics.Any(d => d.Code == "field.pagination-not-converged"))
                throw new InvalidOperationException("Output field pagination did not converge.");
            document = updated.Document;
        }
        var snapshot = engine.Paginate(document, FontFamily, Brushes.Black,
            options: new PaginationOptions { PageGap = 0, MaxShapingCharacters = MaxShapingCharacters });
        try { return new DocumentRenderer(snapshot, OutputRenderOptions, ownsSnapshot: true); }
        catch { snapshot.Dispose(); throw; }
    }

    /// <summary>Exports a captured document without taking ownership of the caller's stream.</summary>
    public async Task<PagedOutputResult> ExportPdfAsync(Stream destination, PagedExportOptions? options = null,
        IProgress<PagedOutputProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        var exporter = PdfExporter ?? throw new NotSupportedException("Configure PdfExporter to enable PDF output.");
        using var renderer = CreateOutputRenderer();
        return await exporter.ExportAsync(renderer, destination, options, progress, cancellationToken);
    }

    /// <summary>Prints a captured document; null indicates cancellation of the host's print dialog.</summary>
    public async Task<PagedOutputResult?> PrintAsync(PrintOptions? options = null, bool showDialog = true,
        IProgress<PagedOutputProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = PrintService ?? throw new NotSupportedException("Configure PrintService to enable printing.");
        using var renderer = CreateOutputRenderer();
        return await service.PrintDocumentAsync(renderer, options, showDialog, progress, cancellationToken);
    }

    /// <summary>Shows page navigation, zoom, PDF export and host printing for one immutable snapshot.</summary>
    public async Task<bool> ShowPrintPreviewDialogAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        try
        {
            LastError = null;
            using var renderer = CreateOutputRenderer();
            var dialog = new OutputPreviewWindow(this, renderer);
            await dialog.ShowDialog(owner);
            return true;
        }
        catch (Exception exception) { ReportError(exception); await ShowOutputErrorAsync(owner, exception); return false; }
        finally { FocusDocument(); }
    }

    /// <summary>Asks the user for a PDF destination and exports a captured snapshot with cancellation.</summary>
    public Task<bool> ShowExportPdfDialogAsync() => ShowOutputDialogAsync("Export PDF", async (renderer, owner) =>
        await ExportWithPickerAsync(renderer, owner));

    /// <summary>Shows the injected platform print dialog, then prints with progress and cancellation.</summary>
    public Task<bool> ShowPrintDialogAsync() => ShowPrintOutputDialogAsync(true);

    /// <summary>Prints to the injected service's default printer with progress and cancellation.</summary>
    public Task<bool> QuickPrintAsync() => ShowPrintOutputDialogAsync(false);

    private Task<bool> ShowPrintOutputDialogAsync(bool showDialog) => ShowOutputDialogAsync("Print", async (renderer, owner) =>
    {
        var service = PrintService ?? throw new NotSupportedException("Configure PrintService to enable printing.");
        return await ShowOutputProgressAsync(owner, "Print", (progress, token) =>
            service.PrintDocumentAsync(renderer, showDialog: showDialog, progress: progress, cancellationToken: token));
    });

    private async Task<bool> ShowOutputDialogAsync(string title, Func<DocumentRenderer, Window, Task<bool>> action)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        try
        {
            LastError = null;
            using var renderer = CreateOutputRenderer();
            return await action(renderer, owner);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) { ReportError(exception); await ShowOutputErrorAsync(owner, exception, title); return false; }
        finally { FocusDocument(); }
    }

    private async Task<bool> ExportWithPickerAsync(DocumentRenderer renderer, Window owner)
    {
        var exporter = PdfExporter ?? throw new NotSupportedException("Configure PdfExporter to enable PDF output.");
        if (!owner.StorageProvider.CanSave) throw new NotSupportedException("A save-file picker is unavailable on this platform.");
        using var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export PDF", SuggestedFileName = "Document.pdf", DefaultExtension = "pdf", ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("PDF document") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] }]
        });
        if (file is null) return false;
        return await ShowOutputProgressAsync(owner, "Export PDF", async (progress, token) =>
        {
            // Finish rendering before opening the selected destination so a rejected document cannot truncate it.
            using var buffer = new MemoryStream();
            var result = await exporter.ExportAsync(renderer, buffer, progress: progress, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            buffer.Position = 0;
            await using var destination = await file.OpenWriteAsync();
            if (destination.CanSeek) { destination.Position = 0; destination.SetLength(0); }
            await buffer.CopyToAsync(destination, token);
            await destination.FlushAsync(token);
            return result;
        });
    }

    private async Task<bool> ShowOutputProgressAsync(Window owner, string title,
        Func<IProgress<PagedOutputProgress>, CancellationToken, Task<PagedOutputResult?>> operation)
    {
        var dialog = new OutputProgressWindow(title, operation, ReportError);
        return await dialog.ShowDialog<bool>(owner);
    }

    private static async Task ShowOutputErrorAsync(Window owner, Exception exception, string title = "Document output")
    {
        var dialog = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var close = OutputButton("Close"); close.IsCancel = true; close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children =
        { new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap }, close } };
        await dialog.ShowDialog(owner);
    }

    private static Button OutputButton(string text)
    {
        var button = new Button { Content = text, MinHeight = 32, Padding = new Thickness(10, 4) };
        AutomationProperties.SetName(button, text); return button;
    }

    private sealed class OutputPreviewWindow : Window
    {
        internal OutputPreviewWindow(TextaloniaEditor editor, DocumentRenderer renderer)
        {
            Title = "Print preview"; Width = 1000; Height = 800; MinWidth = 600; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var preview = new DocumentPrintPreview { Renderer = renderer, Zoom = .65, Margin = new Thickness(20),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            var scroller = new ScrollViewer { Content = preview, Background = Brushes.DimGray,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            var panel = new DockPanel();
            var actions = new WrapPanel { Margin = new Thickness(10), Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Top); panel.Children.Add(actions);
            var previous = OutputButton("Previous page"); var next = OutputButton("Next page");
            var page = new NumericUpDown { Minimum = 1, Maximum = renderer.PageCount, Value = 1,
                Increment = 1, FormatString = "0", Width = 95, Margin = new Thickness(4) };
            AutomationProperties.SetName(page, "Preview page number");
            var count = new TextBlock { Text = $"of {renderer.PageCount}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) };
            var zoom = new NumericUpDown { Minimum = 10, Maximum = 500, Value = 65, Increment = 10,
                FormatString = "0'%'", Width = 110, Margin = new Thickness(4) };
            AutomationProperties.SetName(zoom, "Preview zoom percent");
            var fit = OutputButton("Fit page");
            var export = OutputButton("Export PDF…"); var print = OutputButton("Print…");
            var close = OutputButton("Close"); close.IsCancel = true;
            foreach (var control in new Control[] { previous, page, count, next, zoom, fit, export, print, close }) actions.Children.Add(control);
            var notices = new TextBlock { Margin = new Thickness(12, 4), TextWrapping = TextWrapping.Wrap,
                Text = renderer.Diagnostics.IsEmpty ? "Captured document · output is independent of editor changes." :
                    string.Join(Environment.NewLine, renderer.Diagnostics.Select(diagnostic => diagnostic.Message)) };
            DockPanel.SetDock(notices, Dock.Bottom); panel.Children.Add(notices); panel.Children.Add(scroller); Content = panel;
            void Refresh()
            {
                preview.PageIndex = Math.Clamp((int)(page.Value ?? 1) - 1, 0, renderer.PageCount - 1);
                previous.IsEnabled = preview.PageIndex > 0; next.IsEnabled = preview.PageIndex + 1 < renderer.PageCount;
                scroller.Offset = default;
            }
            page.ValueChanged += (_, _) => Refresh();
            previous.Click += (_, _) => page.Value = Math.Max(1, (page.Value ?? 1) - 1);
            next.Click += (_, _) => page.Value = Math.Min(renderer.PageCount, (page.Value ?? 1) + 1);
            zoom.ValueChanged += (_, _) => preview.Zoom = (double)(zoom.Value ?? 100) / 100;
            fit.Click += (_, _) =>
            {
                var bounds = renderer.Snapshot.Pages[preview.PageIndex].Bounds;
                zoom.Value = (decimal)(100 * Math.Clamp(Math.Min((scroller.Viewport.Width - 40) / bounds.Width,
                    (scroller.Viewport.Height - 40) / bounds.Height), .1, 5));
            };
            export.IsEnabled = editor.PdfExporter is not null; print.IsEnabled = editor.PrintService is not null;
            if (!export.IsEnabled) ToolTip.SetTip(export, "The host has not configured a PDF exporter.");
            if (!print.IsEnabled) ToolTip.SetTip(print, "The host has not configured a print service.");
            export.Click += async (_, _) => await Attempt(() => editor.ExportWithPickerAsync(renderer, this));
            print.Click += async (_, _) => await Attempt(() => editor.ShowOutputProgressAsync(this, "Print", (progress, token) =>
                editor.PrintService!.PrintDocumentAsync(renderer, progress: progress, cancellationToken: token)));
            close.Click += (_, _) => Close();
            Refresh();
            async Task Attempt(Func<Task<bool>> operation)
            {
                try { await operation(); }
                catch (OperationCanceledException) { notices.Text = "Output cancelled."; }
                catch (Exception exception) { editor.ReportError(exception); notices.Text = exception.Message; }
            }
        }
    }

    private sealed class OutputProgressWindow : Window
    {
        internal OutputProgressWindow(string title, Func<IProgress<PagedOutputProgress>, CancellationToken, Task<PagedOutputResult?>> operation,
            Action<Exception> reportError)
        {
            Title = title; Width = 520; Height = 280; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var status = new TextBlock { Text = "Preparing output…", TextWrapping = TextWrapping.Wrap };
            var bar = new ProgressBar { IsIndeterminate = true, Height = 8 };
            var cancel = OutputButton("Cancel"); cancel.IsCancel = true;
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children =
            { new ScrollViewer { Content = status, MaxHeight = 160 }, bar, cancel } };
            var cancellation = new CancellationTokenSource(); var running = true; var succeeded = false;
            void Cancel() { cancellation.Cancel(); status.Text = "Cancelling output…"; cancel.IsEnabled = false; }
            cancel.Click += (_, _) => { if (running) Cancel(); else Close(succeeded); };
            Closing += (_, args) => { if (running) { args.Cancel = true; Cancel(); } };
            Closed += (_, _) => cancellation.Dispose();
            Opened += async (_, _) =>
            {
                try
                {
                    // Let the modal controls render before synchronous page shaping or backend work.
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    var progress = new Progress<PagedOutputProgress>(value =>
                    {
                        if (!running) return;
                        bar.IsIndeterminate = false; bar.Maximum = Math.Max(1, value.TotalPages); bar.Value = value.CompletedPages;
                        status.Text = $"Output page {value.CompletedPages} of {value.TotalPages}";
                    });
                    var result = await operation(progress, cancellation.Token);
                    succeeded = result is not null;
                    status.Text = result is null ? "Output cancelled." : $"Completed {result.PageCount} page(s)." +
                        (result.Diagnostics.IsEmpty ? "" : Environment.NewLine + string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
                }
                catch (OperationCanceledException) { status.Text = "Output cancelled."; }
                catch (Exception exception) { reportError(exception); status.Text = exception.Message; }
                finally { running = false; bar.IsIndeterminate = false; cancel.Content = "Close"; cancel.IsEnabled = true; }
            };
        }
    }
}
