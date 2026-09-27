# Pagination, page setup and document views

DX-02 adds physical page sections, exact main-body pagination and editor page
views. Simple remains the default. DX-03 builds on this with [headers, footers, notes and page fields](STORIES.md).
Printing and general fields remain separate workstreams.

## Authoring in the editor

The optional toolbar exposes the feature without host code:

- **Page setup** edits the section containing the caret: Letter/A4 or custom
  paper size, orientation, margins/gutter, mirrored margins, unequal columns and
  spacing, final-column balancing, background/borders, line numbering and grid.
- **Page numbering** chooses continuation or restart and decimal, Roman or letter
  numbering. This changes section metadata; it does not insert a PAGE field.
- **Insert** adds page, column or continuous/next-page/odd-page/even-page/next-column
  section breaks. **Paragraph** dialog supplies keep, widow/orphan, grid and frame
  placement rules.
- **View** selects Simple, Draft or Print Layout, zoom, fit width/page, page gap,
  pages per row, and page navigation. Page numbers in this panel identify physical
  sheets, independently of a section's displayed numbering format.

Page setup and numbering dialogs apply one undoable edit, keep the selection,
restore editor focus, and leave the document unchanged on Cancel. Validation errors
stay in the dialog. A document, selection or read-only change while a dialog is open
prevents a stale Apply. View controls remain enabled for read-only documents.

```csharp
using Textalonia.Controls;
using Textalonia.Model;

editor.ApplyPageSettings(new PageSettings
{
    Width = DocumentUnits.FromMillimeters(210),
    Height = DocumentUnits.FromMillimeters(297),
    Margins = new EdgeInsets(72, 72, 72, 72),
    Columns = [new PageColumn(1), new PageColumn(2)],
    ColumnSpacing = 24
});
editor.ViewMode = DocumentViewMode.PrintLayout;
editor.Zoom = 1.25;
editor.PagesPerRow = 2;
editor.FitWidth();
editor.GoToPage(0); // zero-based physical page index

await editor.ShowPageSetupDialogAsync();
await editor.ShowPageNumberingDialogAsync();
```

All physical metrics use DIP (96 per inch). Existing document sizes retain their
meaning. `DocumentUnits` explicitly converts inches, points, millimeters and twips.
`Width` and `Height` describe the base portrait paper; landscape swaps their effective
values. Column widths are positive proportional weights after margins, gutter and
column gaps. An empty column array means one column. Invalid dimensions, colors and
numbering are rejected by document validation.

## Sections and editing

`FlowDocument.Sections` is an ordered main-body partition. Its first section uses
`StartParagraphId = Guid.Empty`; later sections reference visible paragraph IDs.
An empty array retains an implicit default Letter section with one-inch margins.
The existing nested decorative `Section` block remains a separate concept.

`EditorSession.CurrentSection` and `CurrentPageSettings` resolve the caret's physical
section. `SetPageSettings`, `SetSectionNumbering`, `InsertSectionBreak` and
`RemoveSection` are undoable and respect read-only mode. Inserting a break midway
through a paragraph splits it at the selection. Page and column breaks are distinct
paragraph-before flags (`PageBreakBefore`, `ColumnBreakBefore`); they are not soft
line-break characters. Removing a section boundary joins content into the preceding
section, whose settings survive. The initial section cannot be removed.

Section page numbering can continue or restart independently of physical page
indexes. Odd/even transitions insert blank physical sheets as needed. A continuous
transition can stay on the same sheet when its effective paper dimensions agree;
a paper-size change starts a new page. DX-03 adds page-number fields and secondary document stories; see [STORIES.md](STORIES.md).

## Exact geometry and views

`Textalonia.Layout.PaginationEngine` uses the same Avalonia shaping backend as flow
layout. It measures actual lines at the available column width and returns a
disposable `PageLayoutSnapshot`; viewport height estimates do not choose page breaks.
The snapshot exposes physical `Pages`, main-story `Fragments`, page/column indexes,
UTF-16 ranges, baselines, clip rectangles, caret and selection geometry, hit testing,
and drawing. Its geometry is unscaled; the editor applies zoom once to drawing and
interaction coordinates. Hosts must dispose snapshots and the engine when finished.

```csharp
using Textalonia.Layout;

// Call on the Avalonia shaping backend's UI thread.
using var engine = new PaginationEngine();
using var pages = engine.Paginate(editor.Document);
var sheetCount = pages.Pages.Length;
var caret = pages.Caret(editor.Session.Selection.Active);
var pageIndex = pages.GetPageIndex(editor.Session.Selection.Active);
```

| View | Behavior |
| --- | --- |
| Simple | Existing virtualized viewport-width flow layout; physical page breaks do not divide the surface. |
| Draft | Continuous page-width layout with no physical page breaks. |
| Print Layout | Physical page sizes, margins, columns and page/section rules; pages can be arranged in rows. |

`Zoom` accepts 0.1–5 (10–500%), `PageGap` 0–1000 DIP, and `PagesPerRow` 1–8.
`FitPage()` selects Print Layout and fits one sheet; `FitWidth()` fits the current
page arrangement. `GoToPage(int)` takes a zero-based physical page index and scrolls
without moving the selection. `PageCount` and `CurrentPageNumber` are observable
read-only properties; the latter is one-based. Continuous views expose one surface.
None of these operations creates an undo entry or changes document text offsets.

The paginated adapter shares fragment geometry for drawing, keyboard/pointer
navigation, selection, caret, inline controls, IME positioning and managed
accessibility ranges. Monitor DPI remains Avalonia's view concern and does not
rewrite stored page metrics.

## Flow rules and bounds

Print Layout applies explicit breaks, paragraph keep-together/keep-with-next and
widow/orphan rules. Oversize keep chains relax their keep constraint to guarantee
progress. A line or atomic inline object taller than the usable page is consumed
once and clipped; it is not retried indefinitely. Paragraphs are split at shaped
line boundaries, preserving storage offsets and atomic content.

Table cell content flows through page pieces without splitting an individual
shaped line. Existing row height, spans, nested blocks and cell decoration remain
part of measurement. Advanced table policies, including repeat headers, positioned
tables and a full Word-compatible row-splitting policy, remain DX-06. Grid snapping
uses the paragraph grid, or the section grid when the paragraph requests snapping.
Line heights are rounded to the grid pitch; character-grid behavior is scoped to
the backend's advance/spacing model. This is not a claim of every East Asian Office
compatibility mode.

`ParagraphStyle.Frame` places a paragraph at X/Y coordinates relative to the current column
content origin, with a fixed width and optional clipping height. An omitted height
grows to the paragraph content. This legacy placement primitive does not wrap
neighboring text or introduce drawing shapes.

Final-column balancing uses twelve bounded exact layout trials. Page snapshots are
complete and synchronous on the UI thread; background shaping is not enabled.
The measurement LRU holds up to 256 entries, alongside a bounded glyph cache.
The previous complete layout and live snapshots additionally retain the measurements
needed by their fragments. One previous layout supplies break-checkpoint reuse. Per-block checkpoints
include section/column state and keep dependencies: unchanged prefixes are reused,
then layout continues until compatible suffix state is reached. Balanced sections
still reflow through their exact trials while reusing shaping. This is a cache
strategy, not a constant-time edit guarantee. Simple-view virtualization and its
existing performance gates remain independent.

## Persistence and external formats

Native JSON writes schema v7 and reads v4/v5/v6/v7. Earlier documents receive the
implicit default physical section. Data XAML retains physical sections and the new
paragraph metadata; clipboard v3 retains native model data and remaps identities.
Whole-document replacement adopts the source's initial section; partial paste keeps
the destination's initial settings and imports copied interior boundaries. Rectangular
cell copy carries no physical sections. Use native JSON or data XAML for lossless
page metadata storage.

DX-03 maps supported DOCX physical sections alongside header/footer and note
ownership. Unsupported section properties still produce explicit diagnostics.
RTF/HTML retain their documented subsets and diagnosed losses. See
[conversion contracts](INTERCHANGE.md) and [story format boundaries](STORIES.md).

## Evidence and remaining qualification

Explicit Unicode bidi embeddings, overrides and isolates (`U+202A` through `U+202E`,
`U+2066` through `U+2069`) have a known shaping limitation in the pinned Avalonia 12.1.3
backend. Repeatedly formatting the same paragraph can lose its explicit direction
state, including in Simple view. Ordinary RTL text remains covered by passing
layout and interaction tests; explicit-control rendering is not qualified.
The skipped regressions in `PaginationContinuationTests` and
`PaginationBidiScopeTests` retain the repeated-shaping and unequal-column cases.
Inspection of the installed backend confirms that `BidiData.Reset` stores false
for its nullable embedding/isolate flags, while `Append` only discovers those
controls when the flags have no value. A backend correction and requalification
are required; pagination does not patch private Avalonia state.

The regression suite covers model validation, section edits/history, persistence,
fixed-font pagination, page geometry, zoom/interaction and page setup dialog behavior.
Run the reproducible pagination probe to capture complete layout and edits near
both ends of fixed-font 100- and 1000-paragraph documents:

```sh
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release -- --pagination-probe artifacts/pagination
```

The probe writes `artifacts/pagination/pagination-probe.json` with timings,
allocations, page/fragment counts and environment details. It is evidence capture,
not a numerical latency guarantee. Native IME, multiple monitor DPI, native
screen-reader bridges, Office-rendered comparisons and a licensed DevExpress
comparison still require retained qualification. Print/PDF output, headers/footers,
notes and page-dependent fields remain separate DX-03/04/05 workstreams.

### Local pagination measurement, 2026-09-27

The [curated summary](baselines/curated/pagination-2026-09-27.csv) was captured
from this DX-02 working tree in Release on Microsoft Windows 10.0.26100 / .NET 8.0.31,
with Inter 16 DIP and the headless Skia backend. One warmup preceded five measured
repetitions. Full layout clears engine caches; edit measurements reuse the preceding
layout. The p95 statistic is the largest of five samples, so this small run is a
reproducibility baseline, not a release latency budget.

| Paragraphs | Operation | Median ms | p95 ms | Pages | Newly measured paragraphs | Reused block checkpoints |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 100 | edit-end | 1.82 | 2.26 | 6 | 1 | 98 |
| 100 | edit-start | 2.13 | 2.57 | 6 | 1 | 0 |
| 100 | full | 84.52 | 93.06 | 6 | 100 | 0 |
| 1000 | edit-end | 13.97 | 16.43 | 81 | 1 | 998 |
| 1000 | edit-start | 10.93 | 23.09 | 81 | 1 | 0 |
| 1000 | full | 163.31 | 176.94 | 81 | 1000 | 0 |

The opening edit changes line breaks and reflows the document; unchanged paragraphs
reuse exact measurements. The ending edit reuses all but two block checkpoints.
Retained glyph-cache bytes ranged from 591680 to 1526144. This counter
excludes document snapshots and line checkpoints; process-wide heap observations
remain in the raw probe file and must not be presented as cache size.
