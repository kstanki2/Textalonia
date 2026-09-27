# ADR 002: persistent editing and bounded viewport work

Status: implemented. Current boundaries and outstanding qualification are tracked in
[architecture](ARCHITECTURE.md#performance-boundaries) and [performance](PERFORMANCE.md).

## Evidence and decision

The P1 Windows capture measures 10,000-paragraph typing at 21–24 MB allocated per
action, first viewport at 776 ms median, and width change at 2,796 ms. The single
90,000-code-unit paragraph retains about 18 MB for 100 edits. These are different
bottlenecks: full index/tree copying, full shaping, and copied run strings.

Use a persistent AVL tree of document nodes, weighted by visible UTF-16 length
and paragraph count, and an AVL rope of shared string slices inside rich runs.
An indexed prototype and deterministic operation/allocation checks precede the
end-to-end comparison. Do not infer control performance from the rope alone.
Paragraph shaping uses Avalonia TextLayout over windows of up to 2,048 UTF-16
units. Only complete lines are committed; the trailing line provides lookahead
and is reshaped in the next window. Compact line-break checkpoints survive glyph
eviction. Paragraph-wide bidi context retains the exact full-shaping fallback;
a single oversized grapheme or visual line can also require a larger window.
An additive, opt-in `MaxShapingCharacters` policy now bounds those requests before
allocation. Its default is zero for compatibility; a limit error suspends rendering
without truncating content. See [the contract](COMPATIBILITY.md#optional-shaping-limit).

## Coordinates and edits

Visible paragraphs contribute their text and one LF, except the final LF is not
exposed. Covered cells contribute no visible coordinates. IDs are stable;
paragraph split retains the first ID and allocates new IDs for following parts.
Paths use stable fractional sibling keys, ancestor keys and physical cell
coordinates. Inserting a sibling does not renumber unrelated paths. If repeated
insertions drive a label beyond 256 fractional bits, a documented full-index
rebase compacts the labels; saved snapshots retain their original valid paths. Subtree
lengths and the ID map are updated along affected paths. Empty containers retain
an empty paragraph. Cross-container replacement keeps the existing P1 semantics.

Built-in operations publish affected IDs and an old/new UTF-16 replacement range
internally. Arbitrary Execute snapshots, load, table structural operations and
undo/redo can signal a reset; no guessed delta is applied to an arbitrary record
graph. Revision-aware positions reject stale revisions rather than silently
interpreting an old offset against new text. History restores immutable indexed
snapshots. Phase 3 can build richer position rebasing on these contracts.

## Compatibility and migration

RichRun.Text is still an init-capable string property, with the same constructor,
deconstruction and value equality. Reading it materializes that run; assigning it
replaces its private rope. Runs retain their public immutable array type. Adjacent
equal-style runs join ropes rather than concatenate their strings. Slices share
bounded source chunks, so a tiny surviving slice cannot retain an enormous paste.
There is no mutable global append buffer, and saved snapshots never change.

FlowDocument.Blocks, Section.Blocks and TableCell.Paragraphs retain their public
immutable array types and with-expression behavior. Internal edits defer array
materialization; explicitly reading these compatibility arrays costs the size of
that collection. DocumentIndex.Paragraphs and Text remain complete compatibility
views. Range reads, lookup, search and normal editing use the persistent index.
Complete text is not permanently cached by history. Compatibility arrays memoize
their identity after explicit access; their possible allocation is charged
conservatively in the history estimate before materialization.
Native JSON uses current prerelease schema v4, with no persisted storage metadata.
Only that schema is supported; unused development versions have no migration support.

TextaloniaEditor defaults to eager Text synchronization. Opt into
SynchronizeText=false when binding Document: Text's Avalonia property then holds
the last synchronized/assigned value; use Document.Text or Session.Index.ReadText
for an explicit current read. A changed Text assignment still replaces the document.
Re-enabling synchronization immediately publishes current text. Automation value
requests explicitly read current document text in either mode. Existing bindings
need no migration, but their O(document-size) cost remains part of the benchmark.

## Layout and retention

Geometry separates persistent document identity, estimated subtree heights and
bounded shaped paragraphs. Height branches use additive subtree estimates and
materialize nodes on visited paths; row dependencies and numbered-list summaries
can still initialize their container metadata. Overscan is at most 160 DIP per side. The viewport plus overscan and explicit caret/hit-test
targets drive measurement. Prefix heights locate offscreen targets. Cell spans
depend on intersected rows; row height changes propagate to ancestor heights.
Painting, caret, selection and IME use the same measured paragraph geometry.
Width/font/theme changes invalidate shaping, and viewport corrections preserve a
text-line anchor, including inside a long paragraph. Table cells pass viewport
limits through to their paragraph contents. Cache eviction disposes TextLayout
instances independently of their line-break checkpoints. Visible shapes also
participate in the global LRU; drawing and geometry queries acquire short leases
and recreate evicted layouts. Oversized exact layouts are released at the end of
the lease, with their transient cost included in the peak estimate. This preserves default compatibility. Hosts can opt into a strict per-input cap and a typed rendering-limit error for indivisible contexts. Single-style edits reuse
the measured prefix and resume an unchanged suffix when line boundaries converge;
other formatting changes conservatively invalidate the paragraph's checkpoints.
One discarded, identical single-style window may be reused during prefix discovery
and is included in cache accounting. Exact first-time distant targets can require
discovering the intervening line breaks; this is not constant-time random access.

History has entry and byte limits. The byte count estimates objects retained
exclusively by undo/redo, using reference-counted shared storage graphs; current
document storage is excluded. It is an engine estimate, not process working set.
Oversized entries are evicted, including the only entry; coalescing does not
exempt a growing group. Load/reset clears both stacks. External snapshots remain
owned by their callers and are outside the session budget.

## Validation and rollout

Keep current-schema native fixture coverage. Extend (do not remove) the API baseline and package
consumer for new range, position, binding and budget APIs. Compare the full P1
control workloads, bound and unbound, on the same host, and capture scalable mode
separately. Include generated differential edits, cross-container replacement,
undo, graphemes across piece boundaries, export during edits, full-reference
geometry, cache disposal and sustained undo/redo eviction. Raw results and
operation counters decide acceptance. The [2026-09-26 completion decision](PERFORMANCE.md#perf-01-residual-latency-qualification) accepts residual latency qualification as PERF-01, superseding the original requirement that every latency miss keep Phase 2 incomplete. Numeric targets and measured failures remain unchanged.
