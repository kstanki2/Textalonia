# Architecture

## Layers

`Model` contains immutable documents: FlowDocument -> Paragraph / Section / Table -> RichRun + TextStyle. It has no controls or live visual objects. Tables retain physical cells behind merged spans; visible text comes from anchor cells.

`DocumentIndex` maps a snapshot to a linear UTF-16 string and paragraph positions. Paragraph separators count as one position. It supplies selection, clipboard, search, and IME coordinates. Cell/section container IDs keep text editing from accidentally destroying structure.

`Editing.EditorSession` owns the current snapshot, directional selection, insertion style, and bounded undo/redo stacks. Each edit replaces affected paragraph content and publishes a snapshot. Adjacent typed characters coalesce until navigation, formatting, another operation, or an 800 ms pause breaks the group. Application operations enter the same history through Execute.

`Serialization.IDocumentFormat` operates on snapshots and caller-owned streams. The native version-1 JSON format preserves the entire model. External formats intentionally map only supported features. Codecs parse data; they do not instantiate XAML or execute document code.

`Controls.TextaloniaEditor` exposes Avalonia styled properties, binding, commands, clipboard and notifications. Its template composes TextaloniaToolbar, ScrollViewer, and DocumentSurface. TextaloniaViewer starts the same control in read-only mode.

`DocumentSurface` handles input and draws shaped native Avalonia TextLayouts. Its TextInputMethodClient displays transient composition without changing the committed document. DocumentLayout caches layouts for unchanged paragraphs, positions sections/tables, and uses the same geometry for painting, hit tests, carets, and selection.

## Performance boundaries

The editing engine uses a persistent AVL document tree with subtree UTF-16 lengths,
paragraph counts, stable fractional sibling keys and an ID-to-path tree. Built-in
text/style edits rebuild affected paths. `Session.Execute`, external loads and
structural table operations validate and index arbitrary snapshots through the
full fallback. Dense sibling labels also use that fallback after 256 fractional
bits, bounding label size without changing saved snapshots. Undo/redo restore indexed snapshots. See [ADR 002](ADR-002-SCALABLE-CORE.md).

Run text uses a persistent rope of immutable string slices. Input chunks are at
most 2,048 code units; adjacent small pieces coalesce with at most 128 units copied.
Splits/joins preserve snapshots and styles. Grapheme queries use .NET segmentation
with context across all adjacent pieces/styles. A conservative context scan may
cover a whole uninterrupted non-ASCII sequence, but never unrelated paragraphs.

Public immutable arrays, `RichRun.Text`, `Paragraph.Text`, `DocumentIndex.Text`
and `DocumentIndex.Paragraphs` remain complete compatibility views. Reading a
complete view is intentionally linear on first materialization. Compatibility
arrays memoize their identity after explicit access; their potential allocation
is included in retention estimates. Complete text is not cached by history. `ReadText(start, length)`,
`CharAt`, search and position lookup avoid document flattening. Native export still
writes the unchanged v1 schema and is necessarily linear in exported content.

`TextaloniaEditor.SynchronizeText` defaults to true. It publishes complete text
on each document revision. Set it to false when binding `Document` to avoid that
cost; the Avalonia `Text` property then retains its last assigned/published value.
A changed `Text` assignment still loads plain text, and enabling synchronization
immediately publishes current text. Automation explicitly reads current text.

Layout metadata tracks estimated/measured subtree heights separately from shaping.
Prefix queries find the viewport and distant targets. Changed document paths retain
unaffected geometry and shapes; numbered-list counters also have prefix summaries.
Sections propagate child height changes. Tables measure cells intersecting required
rows, including spans; height redistribution uses the original row-major span rule.
Unspanned cell height changes update only their row maximum and following row
offsets; spanning cells redistribute row heights through the full span dependency
set, without reshaping unrelated cells. Per-row dependency lists find intersecting
cells without scanning the whole table on every viewport pass. Width, font or foreground changes reset shaping. A paragraph anchor
compensates for height corrections above the viewport.

The reusable shape cache is limited to 256 paragraphs and an estimated 16 MiB
(256 + 32 times UTF-16 length per shape). The current viewport, up to 400 DIP of
overscan on each side, and required target/row dependencies are pinned until the
next build and may exceed those limits. Eviction and detach dispose layouts.
One paragraph is still an indivisible Avalonia TextLayout shaping unit: long
paragraphs remain the outstanding latency/allocation bottleneck. The shape byte
estimate is not a native-memory bound or a measured process-working-set guarantee.

History defaults to 100 entries and 64 MiB. `HistoryByteLimit` bounds
`RetainedHistoryBytes`, an estimate of storage owned exclusively by undo/redo.
Two reference-counted ownership graphs count shared tree nodes, paths, string
chunks, table row arrays, covered cells and merge backups once, subtracting current
snapshot storage. Fixed estimates cover node/record headers, arrays, styles and
80 bytes per state; run descriptors are conservatively charged with their paragraph.
Allocator overhead, GC/weak-cache tables, caller-held snapshots and UI caches are
outside this estimate. Limits are enforced after edits, undo/redo and configuration
changes; oldest undo entries are evicted first, then farthest redo entries. An
oversized entry can leave no undo/redo. Coalesced typing is checked on every edit;
load and `UndoLimit=0` release all history. This budget is an engine-owned estimate,
not a promise about total managed heap size.

## Public extension points

- Bind Document or Text, and style/retemplate TextaloniaEditor.
- Supply an independent toolbar through public ICommand properties and Session methods.
- Apply custom immutable operations with Session.Execute.
- Supply serialization through IDocumentFormat.
- Draw offset-based TextHighlights and handle HyperlinkActivated.
- Use EditorSession without UI, or immutable snapshots without an editing session.

The keyboard/mouse implementation is currently in DocumentSurface; a swappable input-component system is future work. The automation peer exposes a value provider, not full text-range automation.

## Verification

Tests cover formatting-preserving edits, Unicode graphemes, newline semantics, selection restoration, history, readonly behavior, search, random replacements against a string reference, table merges, codecs, invalid documents, and XML entity rejection. Avalonia headless tests exercise bindings, real keyboard/pointer dispatch, clipboard, composition and Skia rendering.

Screenshots are generated under artifacts when tests run. CI is configured for Windows, Linux, and macOS; local verification only demonstrates the environment on which it actually ran. Native operating-system clipboard/IME/touch/screen-reader behavior still needs platform testing.

Phase 1 adds a deterministic corpus, a frozen v1 file/public API snapshot, replayable edit sequences,
and full-control performance measurements. See [the baseline report](BASELINE-REPORT.md),
[compatibility rules](COMPATIBILITY.md), and [performance budgets](PERFORMANCE.md) before changing
storage, indexing, layout or binding behavior. [Native procedures](NATIVE-BASELINES.md) and their
pending evidence remain separate from automated passes.
