using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Textalonia.Export;
using Textalonia.Layout;
using Textalonia.Rendering;

namespace Textalonia.Printing;

/// <summary>
/// A host's native printing adapter. The core library does not install a printer or a native dialog.
/// Adapters must honor cancellation, await all drawing before returning, and never dispose snapshots or renderers.
/// Drawing must run on the Avalonia UI thread; native spooler work may run elsewhere.
/// </summary>
public interface IPrintService
{
    Task<PrintCapabilities> GetCapabilitiesAsync(string? printerName, CancellationToken cancellationToken);
    /// <summary>Shows the host dialog. Null means the user cancelled; no output may be submitted here.</summary>
    Task<PrintOptions?> ShowDialogAsync(PrintDialogRequest request, CancellationToken cancellationToken);
    /// <summary>
    /// Submits the validated job. Report completed output pages, including copies, with the original zero-based
    /// snapshot page index. Exceptions and cancellation propagate to the caller; already spooled pages cannot be undone.
    /// </summary>
    Task PrintAsync(PrintJob job, IProgress<PagedOutputProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Capabilities for one printer. Paper sizes are physical dimensions in 1/96-inch units.</summary>
public sealed record PrintCapabilities
{
    public string PrinterName { get; init; } = "Default printer";
    public bool IsAvailable { get; init; } = true;
    public bool SupportsPrintDialog { get; init; }
    public bool SupportsQuickPrint { get; init; } = true;
    public bool SupportsPageRanges { get; init; } = true;
    public int MaxCopies { get; init; } = 1;
    public bool SupportsCollation { get; init; }
    public bool SupportsMixedPageSizes { get; init; }
    public bool SupportsCustomPageSizes { get; init; } = true;
    public ImmutableArray<Size> PaperSizes { get; init; } = [];
}

/// <summary>Print settings. Ranges are one-based physical pages, independent of section page numbering.</summary>
public sealed record PrintOptions
{
    public string? PrinterName { get; init; }
    public string JobName { get; init; } = "Document";
    /// <summary>Empty means every page. Overlapping ranges are rejected; pages print in document order.</summary>
    public ImmutableArray<PageRange> PageRanges { get; init; } = [];
    public int Copies { get; init; } = 1;
    public bool Collate { get; init; } = true;
}

/// <summary>The immutable snapshot is captured before the dialog; editing the live document cannot change this job.</summary>
public sealed record PrintDialogRequest(PageLayoutSnapshot Snapshot, PrintOptions InitialOptions, PrintCapabilities Capabilities);

/// <summary>A validated print job. The adapter borrows its renderer only until PrintAsync completes.</summary>
public sealed class PrintJob
{
    private readonly DocumentRenderer _renderer;
    private bool _active = true;
    internal PrintJob(DocumentRenderer renderer, PrintOptions options, PrintCapabilities capabilities, ImmutableArray<int> pages)
    { _renderer = renderer; Options = options; Capabilities = capabilities; PageIndices = pages; TotalPages = checked(pages.Length * options.Copies); }

    public PageLayoutSnapshot Snapshot => _renderer.Snapshot;
    public PrintOptions Options { get; }
    public PrintCapabilities Capabilities { get; }
    /// <summary>Selected original zero-based page indices, each appearing once in ascending order.</summary>
    public ImmutableArray<int> PageIndices { get; }
    /// <summary>Total output pages, including all copies.</summary>
    public int TotalPages { get; }

    /// <summary>Enumerates selected page indices in copy/collation order for adapters that render each copy.</summary>
    public IEnumerable<int> EnumerateOutputPages()
    {
        if (Options.Collate)
            for (var copy = 0; copy < Options.Copies; copy++)
                foreach (var page in PageIndices) yield return page;
        else
            foreach (var page in PageIndices)
                for (var copy = 0; copy < Options.Copies; copy++) yield return page;
    }

    /// <summary>Draws a selected source page at its physical size, with page-local coordinates in 1/96-inch units.</summary>
    public void DrawPage(DrawingContext context, int pageIndex)
    {
        if (!_active) throw new InvalidOperationException("The print job has completed; its renderer is no longer borrowed.");
        if (!PageIndices.Contains(pageIndex)) throw new ArgumentOutOfRangeException(nameof(pageIndex), "The page is not selected for this job.");
        _renderer.DrawPage(context, pageIndex);
    }

    internal void Complete() => _active = false;
}
