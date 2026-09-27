using System.Collections.Immutable;
using Avalonia;
using Textalonia.Export;
using Textalonia.Rendering;

namespace Textalonia.Printing;

public static class PrintServiceExtensions
{
    /// <summary>
    /// Validates and prints an already captured snapshot, optionally through the host dialog. Returns null when
    /// the dialog is cancelled. PageCount includes copies. The renderer remains owned by the caller on every path.
    /// Cancellation and output errors propagate without changing the source document.
    /// </summary>
    public static async Task<PagedOutputResult?> PrintDocumentAsync(this IPrintService service, DocumentRenderer renderer,
        PrintOptions? options = null, bool showDialog = true, IProgress<PagedOutputProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(renderer);
        cancellationToken.ThrowIfCancellationRequested();
        renderer.Validate();
        options ??= new PrintOptions();
        var pages = ValidateOptions(options, renderer.PageCount);
        var capabilities = await service.GetCapabilitiesAsync(options.PrinterName, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCapabilities(capabilities);
        if (showDialog)
        {
            if (!capabilities.SupportsPrintDialog)
                throw new NotSupportedException("The print service does not provide a print dialog.");
            renderer.Validate();
            options = await service.ShowDialogAsync(new(renderer.Snapshot, options, capabilities), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (options is null) return null;
            pages = ValidateOptions(options, renderer.PageCount);
            // The dialog may select another printer; never reuse the original printer's capabilities.
            capabilities = await service.GetCapabilitiesAsync(options.PrinterName, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCapabilities(capabilities);
        }
        else if (!capabilities.SupportsQuickPrint)
            throw new NotSupportedException("The selected printer does not support quick print.");

        renderer.Validate();
        ValidateJob(renderer, options, capabilities, pages);
        // Freeze the resolved printer as well as the document, even if the system default later changes.
        options = options with { PrinterName = capabilities.PrinterName };
        var job = new PrintJob(renderer, options, capabilities, pages);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await service.PrintAsync(job, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(job.TotalPages, renderer.Diagnostics);
        }
        finally { job.Complete(); }
    }

    private static ImmutableArray<int> ValidateOptions(PrintOptions options, int pageCount)
    {
        if (options.Copies < 1) throw new ArgumentOutOfRangeException(nameof(options), "Copies must be positive.");
        if (string.IsNullOrWhiteSpace(options.JobName)) throw new ArgumentException("A print job name is required.", nameof(options));
        if (options.PrinterName is not null && string.IsNullOrWhiteSpace(options.PrinterName))
            throw new ArgumentException("A printer name must be nonempty, or null for the default printer.", nameof(options));
        if (options.PageRanges.IsDefault) throw new ArgumentException("Page ranges cannot be an uninitialized array.", nameof(options));
        if (options.PageRanges.IsEmpty) return Enumerable.Range(0, pageCount).ToImmutableArray();
        var selected = new SortedSet<int>();
        foreach (var range in options.PageRanges)
        {
            if (range is null) throw new ArgumentException("Page ranges cannot contain null.", nameof(options));
            foreach (var page in range.Resolve(pageCount))
                if (!selected.Add(page)) throw new ArgumentException("Print page ranges cannot overlap.", nameof(options));
        }
        return selected.ToImmutableArray();
    }

    private static void ValidateCapabilities(PrintCapabilities capabilities)
    {
        if (capabilities is null) throw new InvalidOperationException("The print service returned no capabilities.");
        if (string.IsNullOrWhiteSpace(capabilities.PrinterName) || capabilities.MaxCopies < 1 || capabilities.PaperSizes.IsDefault ||
            capabilities.PaperSizes.Any(size => !double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0))
            throw new InvalidOperationException("The print service returned invalid printer capabilities.");
    }

    private static void ValidateJob(DocumentRenderer renderer, PrintOptions options, PrintCapabilities capabilities, ImmutableArray<int> pages)
    {
        if (!capabilities.IsAvailable) throw new InvalidOperationException($"Printer '{capabilities.PrinterName}' is unavailable.");
        if (pages.IsEmpty) throw new ArgumentException("The print job has no pages.", nameof(options));
        if (options.Copies > capabilities.MaxCopies)
            throw new NotSupportedException($"Printer '{capabilities.PrinterName}' supports at most {capabilities.MaxCopies} copies.");
        if ((long)pages.Length * options.Copies > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "The total number of output pages is too large.");
        if (options.Copies > 1 && options.Collate && !capabilities.SupportsCollation)
            throw new NotSupportedException("The selected printer does not support collated copies.");
        if (!capabilities.SupportsPageRanges && pages.Length != renderer.PageCount)
            throw new NotSupportedException("The selected printer does not support page ranges.");
        Size? firstSize = null;
        foreach (var pageIndex in pages)
        {
            var size = renderer.Snapshot.Pages[pageIndex].Bounds.Size;
            if (firstSize is { } first && !capabilities.SupportsMixedPageSizes && !SameSize(first, size))
                throw new NotSupportedException("The selected printer does not support mixed page sizes in one job.");
            firstSize ??= size;
            if (!capabilities.SupportsCustomPageSizes && !capabilities.PaperSizes.Any(paper => SameSize(paper, size)))
                throw new NotSupportedException($"Page {pageIndex + 1} has a paper size unsupported by the selected printer.");
        }
    }

    private static bool SameSize(Size first, Size second) =>
        Math.Abs(first.Width - second.Width) < 0.01 && Math.Abs(first.Height - second.Height) < 0.01;
}
