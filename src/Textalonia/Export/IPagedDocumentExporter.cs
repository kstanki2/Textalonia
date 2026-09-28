using System.Collections.Immutable;
using Textalonia.Rendering;

namespace Textalonia.Export;

/// <summary>One-based, inclusive physical page range; independent of section numbering.</summary>
public sealed record PageRange(int FirstPage, int LastPage)
{
    public ImmutableArray<int> Resolve(int pageCount)
    {
        if (pageCount < 1 || FirstPage < 1 || LastPage < FirstPage || LastPage > pageCount)
            throw new ArgumentOutOfRangeException(nameof(FirstPage), "The range must identify existing physical pages.");
        return Enumerable.Range(FirstPage - 1, LastPage - FirstPage + 1).ToImmutableArray();
    }
}

public sealed record PagedOutputDiagnostic(string Code, string Message, int? PageIndex = null);
public sealed record PagedOutputProgress(int CompletedPages, int TotalPages, int PageIndex);
public sealed record PagedOutputResult(int PageCount, ImmutableArray<PagedOutputDiagnostic> Diagnostics);

/// <summary>Explicit backend support. Conformance is never inferred from ordinary PDF export.</summary>
public sealed record PagedExportCapabilities(bool SelectableText, bool Images, bool Links, bool Metadata,
    bool TaggedPdf = false, bool PdfA = false, bool PdfUa = false);

public sealed record PdfMetadata
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; }
}

public sealed record PagedExportOptions
{
    public PageRange? Pages { get; init; }
    public PdfMetadata Metadata { get; init; } = new();
}

/// <summary>
/// Exports the supplied exact snapshot without reflow. The caller owns both the renderer and stream.
/// A failed write may leave partial bytes in the destination; exporters must not close or rewind it.
/// </summary>
public interface IPagedDocumentExporter
{
    PagedExportCapabilities Capabilities { get; }
    Task<PagedOutputResult> ExportAsync(DocumentRenderer renderer, Stream destination,
        PagedExportOptions? options = null, IProgress<PagedOutputProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
