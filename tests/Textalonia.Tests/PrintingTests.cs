using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Export;
using Textalonia.Layout;
using Textalonia.Model;
using Textalonia.Printing;
using Textalonia.Rendering;
using Xunit;

namespace Textalonia.Tests;

public class PrintingTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Func<Task> test) => fixture.Session.Dispatch(async () => { await test(); return true; }, CancellationToken.None);
    private static readonly PageSettings Paper = new() { Width = 240, Height = 180, Margins = new EdgeInsets(20, 20, 20, 20) };
    private static FlowDocument Document(bool mixedSizes = false)
    {
        var first = new Paragraph("First page");
        var second = new Paragraph("Second page") { Style = new() { PageBreakBefore = true } };
        var third = new Paragraph("Third page") { Style = new() { PageBreakBefore = true } };
        return new FlowDocument([first, second, third]) { Sections = mixedSizes
            ? [new DocumentSection { PageSettings = Paper }, new DocumentSection
                { StartParagraphId = second.Id, PageSettings = Paper with { Width = 300 } }]
            : [new DocumentSection { PageSettings = Paper }] };
    }

    private static PrintCapabilities Capabilities => new()
    {
        PrinterName = "Test printer", SupportsPrintDialog = true, MaxCopies = 10,
        SupportsCollation = true, SupportsMixedPageSizes = true
    };

    [Fact]
    public Task Quick_print_preserves_snapshot_geometry_selection_copy_order_progress_and_ownership() => Run(async () =>
    {
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(Document());
        using var renderer = new DocumentRenderer(snapshot);
        var progress = new ProgressLog();
        var service = new FakePrintService();
        var result = await service.PrintDocumentAsync(renderer,
            new() { PageRanges = [new(3, 3), new(1, 1)], Copies = 2 }, false, progress);

        Assert.Equal(4, result!.PageCount);
        Assert.Equal(0, service.DialogCalls);
        var job = Assert.IsType<PrintJob>(service.Job);
        Assert.Same(snapshot, job.Snapshot);
        Assert.Equal(new[] { 0, 2 }, job.PageIndices);
        Assert.Equal(new[] { 0, 2, 0, 2 }, job.EnumerateOutputPages());
        Assert.Equal(new[] { 1, 2, 3, 4 }, progress.Items.Select(item => item.CompletedPages));
        Assert.All(progress.Items, item => Assert.Equal(4, item.TotalPages));
        Assert.Equal(new[] { 0, 2, 0, 2 }, progress.Items.Select(item => item.PageIndex));
        renderer.Validate();
        var drawing = new DrawingGroup();
        using var context = drawing.Open();
        Assert.Throws<InvalidOperationException>(() => job.DrawPage(context, 0));
        renderer.DrawPage(context, 0);
    });

    [Fact]
    public Task Uncollated_copies_have_explicit_per_page_output_order() => Run(async () =>
    {
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(Document());
        using var renderer = new DocumentRenderer(snapshot);
        var service = new FakePrintService { CapabilitiesFor = _ => Capabilities with { SupportsCollation = false } };
        await service.PrintDocumentAsync(renderer, new() { Copies = 2, Collate = false }, false);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2 }, service.Job!.EnumerateOutputPages());
    });

    [Fact]
    public Task Dialog_sees_original_document_and_revalidates_the_new_printer() => Run(async () =>
    {
        var editor = new TextaloniaEditor { Document = Document() };
        var source = editor.Document;
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(source);
        using var renderer = new DocumentRenderer(snapshot);
        var service = new FakePrintService
        {
            CapabilitiesFor = name => Capabilities with { PrinterName = name ?? "Default", MaxCopies = name == "Other" ? 1 : 10 },
            Dialog = request =>
            {
                Assert.Same(snapshot, request.Snapshot);
                editor.Text = "Edited during dialog";
                return request.InitialOptions with { PrinterName = "Other", Copies = 2 };
            }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => service.PrintDocumentAsync(renderer));
        Assert.Equal(new string?[] { null, "Other" }, service.QueriedPrinters);
        Assert.Null(service.Job);
        Assert.Same(source, snapshot.Document);
        Assert.Equal("Edited during dialog", editor.Text);
        renderer.Validate();
    });

    [Fact]
    public Task Cancelled_dialog_does_not_submit_or_dispose_the_snapshot() => Run(async () =>
    {
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(Document());
        using var renderer = new DocumentRenderer(snapshot);
        var service = new FakePrintService { Dialog = _ => null };
        Assert.Null(await service.PrintDocumentAsync(renderer));
        Assert.Equal(1, service.DialogCalls);
        Assert.Null(service.Job);
        renderer.Validate();
    });

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 4)]
    public Task Invalid_ranges_fail_before_contacting_the_print_service(int first, int last) => Run(async () =>
    {
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(Document());
        using var renderer = new DocumentRenderer(snapshot);
        var service = new FakePrintService();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PrintDocumentAsync(renderer,
            new() { PageRanges = [new(first, last)] }));
        Assert.Empty(service.QueriedPrinters);
        Assert.Null(service.Job);
    });

    [Fact]
    public Task Overlapping_ranges_and_invalid_copies_fail_before_submission() => Run(async () =>
    {
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(Document());
        using var renderer = new DocumentRenderer(snapshot);
        var service = new FakePrintService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrintDocumentAsync(renderer,
            new() { PageRanges = [new(1, 2), new(2, 3)] }, false));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PrintDocumentAsync(renderer, new() { Copies = 0 }, false));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.PrintDocumentAsync(renderer, new() { Copies = 11 }, false));
        Assert.Null(service.Job);
    });

    [Theory]
    [InlineData("quick")]
    [InlineData("ranges")]
    [InlineData("collate")]
    [InlineData("paper")]
    [InlineData("mixed")]
    [InlineData("dialog")]
    public Task Unsupported_printer_features_are_rejected_before_output(string feature) => Run(async () =>
    {
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(Document(feature == "mixed"));
        using var renderer = new DocumentRenderer(snapshot);
        var capabilities = feature switch
        {
            "quick" => Capabilities with { SupportsQuickPrint = false },
            "ranges" => Capabilities with { SupportsPageRanges = false },
            "collate" => Capabilities with { SupportsCollation = false },
            "paper" => Capabilities with { SupportsCustomPageSizes = false, PaperSizes = [new Size(500, 500)] },
            "mixed" => Capabilities with { SupportsMixedPageSizes = false },
            _ => Capabilities with { SupportsPrintDialog = false }
        };
        var options = feature switch
        {
            "ranges" => new PrintOptions { PageRanges = [new(2, 2)] },
            "collate" => new PrintOptions { Copies = 2 },
            _ => new PrintOptions()
        };
        var service = new FakePrintService { CapabilitiesFor = _ => capabilities };
        await Assert.ThrowsAsync<NotSupportedException>(() => service.PrintDocumentAsync(renderer, options, feature == "dialog"));
        Assert.Null(service.Job);
    });

    [Fact]
    public Task Cancellation_before_and_during_submission_preserves_caller_ownership() => Run(async () =>
    {
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(Document());
        using var renderer = new DocumentRenderer(snapshot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new FakePrintService();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrintDocumentAsync(renderer, cancellationToken: cancellation.Token));
        Assert.Empty(service.QueriedPrinters);

        using var duringOutput = new CancellationTokenSource();
        service.OnPrint = _ => duringOutput.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrintDocumentAsync(renderer,
            showDialog: false, cancellationToken: duringOutput.Token));
        renderer.Validate();
        var drawing = new DrawingGroup();
        using var context = drawing.Open();
        Assert.Throws<InvalidOperationException>(() => service.Job!.DrawPage(context, 0));
        renderer.DrawPage(context, 0);
    });

    [Fact]
    public Task Spooler_failure_propagates_without_disposing_or_mutating_the_source() => Run(async () =>
    {
        using var engine = new PaginationEngine();
        var source = Document();
        using var snapshot = engine.Paginate(source);
        using var renderer = new DocumentRenderer(snapshot);
        var expected = new IOException("Printer disconnected");
        var service = new FakePrintService { OnPrint = _ => throw expected };
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => service.PrintDocumentAsync(renderer, showDialog: false)));
        Assert.Same(source, snapshot.Document);
        renderer.Validate();
        var drawing = new DrawingGroup();
        using var context = drawing.Open();
        renderer.DrawPage(context, 0);
        Assert.Throws<InvalidOperationException>(() => service.Job!.DrawPage(context, 0));
    });

    private sealed class ProgressLog : IProgress<PagedOutputProgress>
    {
        public List<PagedOutputProgress> Items { get; } = [];
        public void Report(PagedOutputProgress value) => Items.Add(value);
    }

    private sealed class FakePrintService : IPrintService
    {
        public Func<string?, PrintCapabilities> CapabilitiesFor { get; init; } = _ => Capabilities;
        public Func<PrintDialogRequest, PrintOptions?> Dialog { get; init; } = request => request.InitialOptions;
        public Action<PrintJob>? OnPrint { get; set; }
        public List<string?> QueriedPrinters { get; } = [];
        public int DialogCalls { get; private set; }
        public PrintJob? Job { get; private set; }
        public Task<PrintCapabilities> GetCapabilitiesAsync(string? printerName, CancellationToken cancellationToken)
        { QueriedPrinters.Add(printerName); return Task.FromResult(CapabilitiesFor(printerName)); }
        public Task<PrintOptions?> ShowDialogAsync(PrintDialogRequest request, CancellationToken cancellationToken)
        { DialogCalls++; return Task.FromResult(Dialog(request)); }
        public Task PrintAsync(PrintJob job, IProgress<PagedOutputProgress>? progress, CancellationToken cancellationToken)
        {
            Job = job;
            OnPrint?.Invoke(job);
            var completed = 0;
            foreach (var page in job.EnumerateOutputPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var drawing = new DrawingGroup();
                using var context = drawing.Open();
                job.DrawPage(context, page);
                progress?.Report(new(++completed, job.TotalPages, page));
            }
            return Task.CompletedTask;
        }
    }
}
