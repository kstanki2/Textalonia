using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Avalonia.Skia.Helpers;
using Avalonia.Threading;
using SkiaSharp;
using Textalonia.Export;
using Textalonia.Rendering;
using Textalonia.Model;

namespace Textalonia.Pdf.Skia;

/// <summary>Optional standard PDF output using the same positioned drawing as print preview.
/// Requires an initialized Avalonia Skia application and its UI thread. No tagged, archival or
/// accessibility conformance profile is advertised.</summary>
public sealed class PdfExporter : IPagedDocumentExporter
{
    public PagedExportCapabilities Capabilities { get; } = new(true, true, true, true);

    public async Task<PagedOutputResult> ExportAsync(DocumentRenderer renderer, Stream destination,
        PagedExportOptions? options = null, IProgress<PagedOutputProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination must be writable.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        renderer.Validate();
        options ??= new();
        ArgumentNullException.ThrowIfNull(options.Metadata);
        var pages = options.Pages?.Resolve(renderer.PageCount) ?? Enumerable.Range(0, renderer.PageCount).ToImmutableArray();
        if (pages.IsEmpty) throw new InvalidOperationException("The snapshot contains no physical pages.");
        foreach (var definition in renderer.Snapshot.Document.Fonts)
            if (definition.EmbeddingRights is { } rights &&
                (!FontEmbeddingPolicy.CanEmbed(rights, forEditing: false) || (rights & FontEmbeddingRights.NoSubsetting) != 0))
                throw new NotSupportedException($"The document font '{definition.FamilyName}' does not permit this PDF backend's embedding/subsetting policy.");
        var metadata = options.Metadata;
        // The PDF canvas records in RasterDpi units. Avalonia's public bridge resets its transform
        // to identity, so 96 gives the required 72/96 point conversion without scaling glyphs twice.
        var pdfMetadata = new SKDocumentPdfMetadata
        {
            Title = metadata.Title, Author = metadata.Author, Subject = metadata.Subject,
            Keywords = metadata.Keywords, Creator = metadata.Creator ?? "Textalonia",
            Producer = "Textalonia.Pdf.Skia", RasterDpi = 96, EncodingQuality = 100
        };
        using var buffer = new SKDynamicMemoryWStream();
        using var text = new PdfTextRenderer(renderer.Snapshot.Document.Fonts);
        using (var pdf = SKDocument.CreatePdf(buffer, pdfMetadata) ?? throw new NotSupportedException("PDF output is unavailable in this Skia build."))
        {
            for (var completed = 0; completed < pages.Length; completed++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = pages[completed];
                var size = renderer.Snapshot.Pages[index].Bounds.Size;
                var canvas = pdf.BeginPage((float)(size.Width * .75), (float)(size.Height * .75));
                var visual = new PageDrawing(renderer, index, size, text);

                await DrawingContextHelper.RenderAsync(canvas, visual, new Rect(size), new Vector(96, 96));
                canvas.ResetMatrix();
                foreach (var link in renderer.GetLinks(index))
                {
                    var r = link.Bounds;
                    using var annotation = canvas.DrawUrlAnnotation(new SKRect((float)r.Left, (float)r.Top,
                        (float)r.Right, (float)r.Bottom), link.Target);
                }
                pdf.EndPage();
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(completed + 1, pages.Length, index));
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }
            pdf.Close();
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Native callbacks never write to caller code. Managed copying propagates stream failures
        // safely and leaves caller streams open, including non-seekable destinations.
        using var data = buffer.DetachAsData();
        using var source = data.AsStream();
        await source.CopyToAsync(destination, cancellationToken);
        return new(pages.Length, renderer.Diagnostics);
    }

    // Visual is only the public Avalonia-to-Skia drawing bridge: no Control, template, editor,
    // native window, interaction, live inline control or independent layout is involved.
    private sealed class PageDrawing : Visual
    {
        private readonly DocumentRenderer _renderer;
        private readonly int _pageIndex;
        private readonly PdfTextRenderer _text;
        public PageDrawing(DocumentRenderer renderer, int pageIndex, Size size, PdfTextRenderer text)
        { _renderer = renderer; _pageIndex = pageIndex; _text = text; Bounds = new Rect(size); }
        public override void Render(DrawingContext context) => _renderer.DrawPage(context, _pageIndex, _text);
    }
}
