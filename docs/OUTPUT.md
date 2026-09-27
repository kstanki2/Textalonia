# Print preview, printing and PDF

DX-04 adds output from the exact physical page snapshot used by pagination.
Preview, host printer adapters and the optional PDF exporter share
`DocumentRenderer`; they do not independently reflow the document. The renderer
includes body content, headers, footers, notes and their evaluated page fields.
The output snapshot remains unchanged when the live document, selection, zoom or
view changes. See [pagination](PAGINATION.md) and [stories](STORIES.md) for the
layout rules and known shaping limits that also apply to output.

## Editor and preview

The toolbar's **Output** menu exposes print preview, PDF export, print and quick
print. These are available in read-only mode when the corresponding service is
configured. Preview supplies physical page navigation, zoom and fit, output
diagnostics and export/print actions. A native host window is required for the
editor's dialogs and PDF file picker.

```csharp
using Textalonia.Pdf.Skia;

editor.PdfExporter = new PdfExporter();
editor.PrintService = myPrintService; // application-provided native adapter
await editor.ShowPrintPreviewDialogAsync();

await using var destination = File.Create("report.pdf");
var result = await editor.ExportPdfAsync(destination);
```

`CreateOutputRenderer()` returns a caller-owned renderer with a captured complete
physical page layout, regardless of Simple/Draft/Print Layout and editor zoom.
Dispose it after every preview, export and print operation using it has finished.
`DocumentPrintPreview` borrows its `Renderer`; its `PageIndex` is zero-based and
`Zoom` accepts 0.1 through 5. Keep that renderer alive while the preview is shown.
An out-of-range page index displays an empty surface.

`ExportPdfAsync` and `PrintAsync` propagate failures to their caller. Dialogs
return a success flag. Commands report failures through
`OperationFailed` and `LastError`; output dialogs also show the failure. Preview
and output do not create history entries or change the active editing story.

## Native printing adapter

The core package supplies `IPrintService`, validation and output job routing.
The application supplies the OS printing integration and native print dialog;
no built-in printer adapter, WPF dependency or installed printer is required by
the core library. Configure `editor.PrintService` to enable printing.

```csharp
using Textalonia.Export;
using Textalonia.Printing;

using var renderer = editor.CreateOutputRenderer();
var result = await myPrintService.PrintDocumentAsync(renderer,
    new PrintOptions
    {
        JobName = "Quarterly report",
        PageRanges = [new PageRange(1, 2)],
        Copies = 2,
        Collate = true
    },
    showDialog: true,
    cancellationToken: cancellationToken);
// A null result means the user cancelled the host dialog.
```

Use `showDialog: false` for quick print. `PageRange` is inclusive and one-based,
using physical sheets rather than the section's displayed page number. Empty
print ranges select every page; overlapping ranges are rejected and selected
pages print in document order. Copies must be positive. `PagedOutputResult.PageCount`
for printing includes copies; it counts output pages, not duplex sheets.

An adapter implements three operations:

- `GetCapabilitiesAsync` returns availability, dialog/quick-print support,
  ranges, maximum copies, collation, mixed/custom paper support and paper sizes.
- `ShowDialogAsync` receives the captured snapshot and initial settings and
  returns settings or null. It must not submit output. Capabilities are queried
  again for the returned printer before the job is submitted.
- `PrintAsync` receives a validated `PrintJob`. Its `PageIndices` are the unique
  original zero-based pages; `EnumerateOutputPages()` supplies copy/collation
  order. `DrawPage` renders a selected page in page-local DIP coordinates.

The job validates requested capabilities before submission and fixes the selected
printer name to the validated capability result. Paper dimensions use 96 DIP per
inch and reflect the oriented physical sheet. Printer device transforms, hard
margins, paper selection,
spooler lifetime and OS errors belong to the adapter. The adapter must await all
drawing before returning and must not dispose the borrowed renderer/snapshot.
Drawing uses the Avalonia UI thread; spooler work may be asynchronous. An adapter
reports progress including copies, and cancellation may leave already spooled
pages printed. A completed job can no longer draw pages.

## Optional PDF package

`Textalonia.Pdf.Skia` is a separate optional package. The core's
`IPagedDocumentExporter` has no PDF backend dependency. Configure `PdfExporter`
as above or call it with a captured renderer:

```csharp
using Textalonia.Export;
using Textalonia.Pdf.Skia;

using var renderer = editor.CreateOutputRenderer();
await using var destination = File.Create("first-page.pdf");
var result = await new PdfExporter().ExportAsync(renderer, destination,
    new PagedExportOptions
    {
        Pages = new PageRange(1, 1),
        Metadata = new PdfMetadata
        {
            Title = "Quarterly report",
            Author = "Example organization"
        }
    },
    cancellationToken: cancellationToken);
```

PDF uses the already shaped glyphs and positions, vector drawing, embedded raster
images, safe external hyperlink annotations, document metadata and physical page
boxes. DIP coordinates convert to PDF points at 72/96. The backend uses
Avalonia.Skia and SkiaSharp; see [dependency notices](../THIRD-PARTY-NOTICES.md).
Applications must distribute the native assets for their target platform and
retain their upstream notices. An initialized Avalonia application using the Skia
backend is required.

Text remains selectable and searchable. The exporter retains supplied glyph
positions and Unicode source clusters, embeds fonts using Skia's PDF backend, and
adds source text for shaped clusters. A local independent PyMuPDF inspection
retained accented Latin, Greek, Cyrillic, Hebrew, contextual Arabic and decomposed
combining-character text. Mixed RTL runs can be extracted in a reader's spatial
order; a tagged logical reading order is not claimed. Font subsetting is performed
by the backend without a guarantee that every subset reduces file size.

Fonts must provide reproducible font data and permit embedding. Unsupported
font-data access or embedding restrictions fail export before destination bytes
are written. Fonts forbidding subsetting are conservatively refused. Installed
and document-supplied fonts retain their own terms; selecting a font does not
grant embedding rights.

`PagedExportCapabilities` advertises backend support separately from PDF
conformance. No tagged, PDF/A or PDF/UA profile is exposed. Logical reading order,
heading/list/table structure, accessibility tags, bookmarks and archival/accessibility
conformance remain future DX-04 work requiring external validator reports.
Ordinary PDF export must not be presented as accessible or archival PDF.

## Ownership, cancellation and diagnostics

Capture and rendering use the Avalonia shaping/UI thread. Immutable model values
can be shared, but a renderer contains thread-affine shaping and image resources.
Do not render one snapshot concurrently or dispose it during an operation.
`IPagedDocumentExporter` and `IPrintService` are host-owned services.
`new DocumentRenderer(snapshot)` borrows the caller-owned snapshot; set
`ownsSnapshot: true` to transfer ownership after successful construction.

The caller owns the destination stream. Output writes at its current position
without rewinding or closing it, including on errors and cancellation. A final
stream-write failure or cancellation may leave partial bytes. Use a temporary
file and rename it after success when an atomic application save is required.
Cancellation is cooperative; synchronous page drawing is not interruptible.
Progress reports include the original zero-based page index and completed/total
output page counts.

Registered inline Avalonia controls are not instantiated for output. Supply a
print representation through `DocumentRenderOptions.InlineProvider`.
`IInlinePrintProvider.TryCreateRepresentation` returns a stable
`IInlinePrintRepresentation` that draws within the supplied bounds, or null when
unsupported. Ownership transfers to the renderer, which disposes representations;
no interactive control is created or captured. Embedded raster images have a
built-in bounded decoder. Providers can supply representations for other resources.

The default `UnsupportedContentPolicy.Strict` throws `PagedOutputException` if
any font, layout or unsupported-content diagnostic exists. Its `Diagnostics`
property explains the rejection. Tolerant mode permits diagnosed fallback:

```csharp
using Textalonia.Rendering;

editor.OutputRenderOptions = new DocumentRenderOptions
{
    UnsupportedContent = UnsupportedContentPolicy.Tolerant,
    InlineProvider = myPrintProvider
};
```

Inspect `PagedOutputResult.Diagnostics` after tolerant output. Unsupported
images/controls retain a placeholder rather than a live control capture. A Draft
snapshot is always rejected; capture physical pages for output. `ImageLimits`
bounds encoded bytes, dimensions and the total decoded pixel count.

Missing/unavailable image resources and font/layout fallbacks are also output
concerns; neither preview nor PDF silently fetches remote resources. Output
diagnostics are independent of the serializer's interchange reports.

## Evidence and qualification

The managed output tests cover shared snapshot page geometry, page ranges,
copies/collation, capability validation, dialog cancellation, immutable snapshot
ownership, unsupported content, progress, stream failures and cancellation.
Backend tests inspect PDF page boxes, text, fonts, links, images and metadata.
Headless tests and PDF inspection do not establish native printer behavior.

Native printer/dialog results on Windows, macOS and Linux, platform-specific
font fallback, a licensed DevExpress comparison and external conformance-validator
reports remain qualification work. Existing explicit Unicode bidi-control
shaping limitations described in [PAGINATION.md](PAGINATION.md) also apply here.

### Reproducing the PDF check

The generated fixture covers two physical pages with repeated headers/page fields,
a table, a transparent embedded image, an external link and multilingual text.
Run the managed fixture test, then the independent reader check (requires PyMuPDF):

```sh
dotnet test tests/Textalonia.Tests -c Release --filter FullyQualifiedName~PdfExporterTests
python scripts/Verify-PdfOutput.py artifacts/dx04-pdf
```

[Verify-PdfOutput.py](../scripts/Verify-PdfOutput.py) checks extracted text, page
boxes, embedded fonts, hyperlink annotations, image transparency and coordinates.
It writes page images and `inspection.json` beside the fixture. The local
2026-09-27 Windows run with PyMuPDF 1.28.2 passed: both pages were 360 by 480 points,
the first glyph origin matched the snapshot at (30, 65.44921875) points, and the
preview/PDF image bounds agreed within one raster pixel. Both repeated page
fields extracted their evaluated values. This is output regression evidence;
it does not validate a PDF conformance profile or an OS printer.
