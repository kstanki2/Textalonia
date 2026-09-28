# Architecture

## Layers

`Model` contains immutable documents: FlowDocument -> Paragraph / Section / Table -> RichRun + TextStyle. It has no controls or live visual objects. Tables retain physical cells behind merged spans; visible text comes from anchor cells.

`DocumentIndex` maps a snapshot to a linear UTF-16 string and paragraph positions. Paragraph separators count as one position. It supplies selection, clipboard, search, and IME coordinates. Cell/section container IDs keep text editing from accidentally destroying structure.

`Editing.EditorSession` owns the current snapshot, directional selection, insertion style, and bounded undo/redo stacks. Each edit replaces affected paragraph content and publishes a snapshot. Adjacent typed characters coalesce until navigation, formatting, another operation, or an 800 ms pause breaks the group. Application operations enter the same history through Execute.

`Serialization.IDocumentFormat` operates on snapshots and caller-owned streams. Native JSON writes schema v5 and reads v4/v5, preserving the entire model; other versions are rejected. External formats intentionally map only supported features. Codecs parse data; they do not instantiate XAML or execute document code. See [document semantics](DOCUMENT-MODEL.md) for list identity, style precedence, nested cells and structural merge rules.

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
writes schema v5 and is necessarily linear in exported content.

`TextaloniaEditor.SynchronizeText` defaults to true. It publishes complete text
on each document revision. Set it to false when binding `Document` to avoid that
cost; the Avalonia `Text` property then retains its last assigned/published value.
A changed `Text` assignment still loads plain text, and enabling synchronization
immediately publishes current text. Automation explicitly reads current text.

Layout metadata tracks estimated/measured subtree heights separately from shaping.
Branches initially use additive estimates from the document tree's text length and
paragraph count. Nodes and child branches are created only as geometry paths are
visited; refining an estimate updates its ancestors. Exact offscreen prefixes are
not promised before measurement. Prefix queries find the viewport and distant targets. Changed document paths retain
unaffected geometry and shapes. Numbered-list prefix summaries initialize on demand
for their container and are then reused along unchanged branches; this first request
can enumerate that container's metadata without shaping its text. Plain paragraphs
do not allocate numbering arrays.
Sections propagate child height changes. Tables measure cells intersecting required
rows, including spans; height redistribution uses the original row-major span rule.
Unspanned cell height changes update only their row maximum and following row
offsets; spanning cells redistribute row heights through the full span dependency
set, without reshaping unrelated cells. Per-row dependency lists find intersecting
cells without scanning the whole table on every viewport pass. Cells pass the
viewport limits through to their contents, even when a tall cell intersects many
rows. Width, font or foreground changes reset shaping. A text-line anchor
compensates for height corrections above and within the visible paragraph, including
corrections from explicit offscreen caret/IME queries. Painting consumes only already
visible caret geometry; it does not discover offscreen prefixes during a render pass.
Distant caret navigation settles refined extent measurements before sending its
scroll request, so the scroll viewer uses the current height rather than an old estimate.

Long paragraphs use windows of normally at most 2,048 UTF-16 units. Each window
ends at a grapheme boundary and commits complete Avalonia lines; its incomplete
last line is lookahead for wrapping and justification. Rendering, selection,
hit-testing, Home/End and IME coordinates share these window offsets. Only visible
lines are drawn. Line-break checkpoints retain offsets/heights without glyphs and
use binary search for measured text/height targets. Their memory grows with the
measured prefix (one record per window), separately from the shaped-layout budget.
Single-style edits retain the unchanged prefix and rejoin a measured suffix when
line boundaries agree. Mixed-style/paragraph-format changes invalidate checkpoints
conservatively. Exact first visits to distant offsets/heights discover intervening
line breaks, disposing or reusing glyphs as they go. Repeated identical windows
can reuse one discarded shape when all formatting agrees.

The reusable cache is limited to 256 paragraph checkpoint sets, 256 shaped windows
and an estimated 16 MiB (256 + 32 times input UTF-16 length per shape, including
lookahead and the discarded-window reuse slot). The bounded paragraph cache evicts its oldest entry without sorting temporary arrays.
An LRU list maintains resident
shape counts and bytes incrementally. Visible content and explicit targets are
subject to eviction too: visuals retain geometry checkpoints and acquire a short
lease while drawing or answering a geometry query. An evicted shape is recreated
from its checkpoint. Paragraph-cache eviction drops that paragraph's checkpoint
list; any already-collected visuals keep only their own page and owner. Detach
clears the central glyph cache, including shapes recreated by those visuals.
Viewport overscan is at most 160 DIP on each side (with an 80 DIP minimum).

An oversized exact layout is not retained after its lease ends. Its temporary
estimate can exceed 16 MiB while a consumer uses it; `PeakLayoutBytes` records this
high-water estimate (also including the just-created layout before eviction).
This bounds reusable ownership, not the memory required to shape an indivisible
Unicode context. Checkpoint metadata and the height index are separate from the
glyph estimate and remain proportional to measured text and model nodes.

Paragraph-wide bidirectional text still uses the exact full Avalonia shaping path.
The rope maintains a conservative summary for RTL scripts and directional controls,
so ordinary local edits do not scan the complete text merely to choose the path.
A single enormous grapheme or visual line can also exceed the normal window size.
These exact-shaping fallbacks preserve the default rendering contract. Visible glyphs never bypass cache eviction. The shape byte estimate is not a native-memory
bound or a measured process-working-set guarantee.

Hosts needing a strict transient-input policy set `MaxShapingCharacters` to a
positive value (at least 2,048); zero remains the compatibility default. The
limit is checked before each text read, grapheme buffer and shaping attempt,
including window growth and recreated evicted layouts. Growth tries the remaining
allowance before rejecting a request. A rejected exact context is reported through
`LayoutError` and `OperationFailed`, without altering text or inventing approximate
geometry. The surface releases partial layouts and displays a limit message until
layout succeeds again. See [shaping policy](COMPATIBILITY.md#optional-shaping-limit).

For a limit L, every document-paragraph shaping input is at most L UTF-16 units. Reusable glyph
ownership stays within 16 MiB/256 layouts; construction or one active lease adds
at most `256 + 32 * L` estimated bytes. `PeakLayoutBytes` includes unsuccessful
lookahead attempts as well as committed layouts. This is an explicit bound on
engine inputs and its accounting model, not native allocations. Height metadata,
line checkpoints, document storage, export and IME surrounding-text materialization
remain separate. First-time exact distant targets can still discover all preceding
line breaks, one bounded input at a time.

History defaults to 100 entries and 64 MiB. `HistoryByteLimit` bounds
`RetainedHistoryBytes`, an estimate of storage owned exclusively by undo/redo.
One reference-counted ownership table tracks both total and current ownership.
Shared tree nodes, paths, string chunks, table row arrays, covered cells and merge
backups are counted once, subtracting current snapshot storage. Reference visitors
avoid per-node iterator allocations while updating
both ownership domains together, using callbacks reused by the ownership table.
Load/reset clears the old table and initializes
the new roots without walking the discarded graph just to decrement its counts. Fixed estimates cover node/record headers, arrays, styles and
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

Keyboard, pointer, caret and composition defaults are independently replaceable through owned components. Inline descriptors shape as atomic embedded text runs; view-owned caches resolve images asynchronously and explicit registered factories create only visible controls. See [input contracts](INPUT-COMPONENTS.md) and [inline ownership](INLINE-CONTENT.md). The editor exposes a managed text-range contract, while the native automation peer remains value-only pending an Avalonia text-provider bridge; see [accessibility evidence](ACCESSIBILITY.md).

## Verification

Tests cover formatting-preserving edits, Unicode graphemes, newline semantics, selection restoration, history, readonly behavior, search, random replacements against a string reference, table merges, codecs, invalid documents, and XML entity rejection. Avalonia headless tests exercise bindings, real keyboard/pointer dispatch, clipboard, composition and Skia rendering.

Screenshots are generated under artifacts when tests run. CI is configured for Windows, Linux, and macOS; local verification only demonstrates the environment on which it actually ran. Native operating-system clipboard/IME/touch/screen-reader behavior still needs platform testing.

The regression suite includes a deterministic corpus, current-schema fixtures and public API snapshots, replayable edit sequences,
and full-control performance measurements. See [retained benchmark summaries](BENCHMARK-BASELINES.md),
[compatibility rules](COMPATIBILITY.md), and [performance budgets](PERFORMANCE.md) before changing
storage, indexing, layout or binding behavior. [Native procedures](NATIVE-BASELINES.md) and their
pending evidence remain separate from automated passes.
