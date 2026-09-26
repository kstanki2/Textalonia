# Phase 2: Scalable editing core

[Roadmap and dependencies](ROADMAP.md#implementation-phases)

**Outcome:** local edits and viewport work scale with affected content, with explicit memory bounds. **Entry:** Phase 1 correctness fixtures, measurements, and compatibility rules. **Status:** complete, with residual latency qualification deferred as [PERF-01](PERFORMANCE.md#perf-01-residual-latency-qualification). See the [completion decision and evidence](PHASE2-REPORT.md#completion-decision).

## Phase 1 starting points

- [FlowDocument and DocumentIndex](../src/Textalonia/Model/FlowDocument.cs): eager flattened text and whole-tree rewrites.
- [EditorSession](../src/Textalonia/Editing/EditorSession.cs): index rebuilding, whole-text grapheme scans, and entry-count history.
- [DocumentLayout](../src/Textalonia/Controls/DocumentLayout.cs), [DocumentSurface](../src/Textalonia/Controls/DocumentSurface.cs), and [TextaloniaEditor](../src/Textalonia/Controls/TextaloniaEditor.cs): complete geometry rebuilds and eager `Text` synchronization.

## Tasks

- [x] **P2.1 - Specify edit and position contracts.** Record an architecture decision for edit deltas, persistent tree paths, stable block IDs, revision-aware positions, and a slow fallback for arbitrary `Session.Execute` snapshots. Prototype the measured bottleneck before adopting a balanced piece table or equivalent persistent storage. Explicitly settle how the public `RichRun.Text`, immutable arrays, `DocumentIndex.Text`, and Avalonia `Text` binding remain compatible or acquire an opt-in scalable mode. **Done when:** the decision includes an end-to-end benchmark and migration plan; eager full-string notifications are not hidden behind a storage-only improvement.

- [x] **P2.2 - Introduce incremental document indexes.** Maintain subtree text lengths, paragraph/container lookup, ID-to-path mapping, and local grapheme boundaries. Update affected nodes from edit deltas, preserving LF and UTF-16 offsets. Provide range reads/search traversal without flattening the document, and materialize complete text only where the documented API requires it. **Done when:** randomized differential tests match the baseline index, including cross-container replacement and undo, and local edits do not enumerate unrelated paragraphs.

- [x] **P2.3 - Replace repeated text copying.** Implement the chosen shared text storage behind the P2.1 contract, with styled spans and immutable snapshot access. Handle split/join, insertion at piece boundaries, large paste, and reclamation/compaction without invalidating saved snapshots or grapheme segmentation across adjacent pieces. Update binding synchronization to match the chosen compatibility policy. **Done when:** both long-paragraph and many-paragraph workloads improve against P1.5, and background snapshot export remains stable during edits.

- [x] **P2.4 - Add incremental layout indexes.** Separate paragraph shaping, block height/position tracking, and geometry lookup. Invalidate changed blocks and necessary ancestors; maintain estimates and prefix-height queries for unmeasured content. Define table span/row measurement dependencies so an intersecting cell can invalidate its required rows without forcing unrelated blocks to remeasure. **Done when:** caret, selection, hit-testing, and IME rectangles use the same geometry and agree with a fully measured reference on test fixtures.

- [x] **P2.5 - Measure only the viewport and required targets.** Shape visible content plus bounded overscan; measure on demand for navigation, find results, automation ranges, and scrolling to a distant caret. Preserve a scroll anchor when estimated heights change. Bound layout caches and dispose evicted layouts. Include first-open, width change, theme/font changes, large tables, and offscreen edits. **Done when:** instrumentation shows initial layout does not shape every paragraph and viewport changes keep cache size bounded while selection and scroll positions remain correct.

- [x] **P2.6 - Bound retained history memory.** Add a configurable byte budget alongside the entry limit, accounting for shared text, snapshots, undo and redo retention, and later resource handles. Define the estimate's meaning, oversized-entry behavior, typing coalescing, and cleanup on load/history reset. **Done when:** sustained editing obeys the stated bound/tolerance, preserves exact undo/redo semantics, and reclaims data when history entries are evicted; shared data is not charged once per snapshot without explanation.

- [x] **P2.7 - Validate and document the engine change.** Run all Phase 1 correctness cases and compare raw performance results on the same workload/hardware. Document intentional O(document-size) operations, such as complete export and compatibility text materialization. Extend the package consumer for any changed public contract. **Done when:** correctness and package checks pass, budget outcomes are recorded with any accepted deferral explicitly tracked, and the architecture/performance docs describe the implementation that actually shipped. The [completion decision](PHASE2-REPORT.md#completion-decision) accepts residual latency qualification as a follow-up without changing numeric budgets.

## Implementation notes

- P2.1: [ADR 002](ADR-002-SCALABLE-CORE.md) records storage, positions, deltas,
  compatibility views, opt-in document binding, the measured prototype and migration.
- P2.2/P2.3: weighted persistent trees, ID paths, bounded string pieces, range reads,
  streaming search, local grapheme context and differential snapshot tests are in place.
  Dense sibling labels have an explicit full-index rebase at 256 fractional bits.
- P2.4: paragraph shaping and prefix heights are separate. Height nodes now
  initialize on visited paths from subtree estimates; ordinary first-open and
  width changes avoid constructing every paragraph's geometry metadata. Table
  row dependencies, shared caret/selection/hit-test/IME geometry, and a frozen full-measure oracle are covered.
- P2.5: viewport/overscan measurement, distant targets, cache eviction/disposal and
  scroll anchors are implemented. Distant caret requests publish refined extents
  before scrolling, so the first request reaches its measured target. Long paragraphs use bounded text windows,
  retained line-break checkpoints, suffix reuse and visible-line rendering. Large
  cells propagate viewport limits. Visible and target glyphs now share a central
  bounded LRU; short leases recreate evicted shapes, including in dense viewports.
  Hosts can now opt into `MaxShapingCharacters`, which checks bidi context,
  grapheme discovery, window growth and reshaping before allocation. A typed
  rendering-limit error suspends rendering without changing the document or
  history; changing the policy or content retries layout. Zero preserves default
  compatibility. Peak estimates include failed lookahead attempts. First-time
  exact distant targets still discover intervening line breaks with bounded inputs.
- P2.6: history has entry and byte limits, shared ownership accounting (including
  typing styles, covered cells and merge backups), oversized-entry eviction, redo
  accounting and deterministic coalescing-boundary tests. A single ownership
  table updates both total/current references, and load/reset drops the old table
  without an extra graph traversal. Reference visitors also eliminate per-node
  iterator allocations while preserving the same estimate and undo/redo semantics.
- P2.7: correctness, package-consumer and full-control performance evidence are
  recorded in the report. Paired Phase 1/2 captures use explicit setup isolation;
  all 234 compatibility and 144 strict document-mode checks pass without changing
  a budget or dropping a case in the standard seven-sample captures. The longer
  strict-mode confirmation still has table latency misses. Those misses, together
  with nonisolated and standalone startup misses, are preserved and tracked in
  [PERF-01](PERFORMANCE.md#perf-01-residual-latency-qualification); they do not block the Phase 3 handoff.

## Exit gate

- Differential tests preserve document text, structure, selection, formatting, and history across generated edit sequences.
- Measured local editing, first viewport, scrolling, and history are assessed against P1.5 budgets. The standard controlled captures pass; residual latency misses are an explicit exception under the [completion decision](PHASE2-REPORT.md#completion-decision), with targets unchanged. Cache/operation counters corroborate the intended scaling, independently of wall-clock measurements.
- Native JSON remains compatible with existing files; a private storage refactor must not accidentally change persisted data.
- Any unavoidable cost of a full `Text` binding is explicit and tested. Claims distinguish that mode from the scalable document mode.

## Handoff

Land P2.1, P2.2, and P2.3 in that order; layout and history can then proceed against the agreed contracts. Give Phase 3 reusable edit deltas and container traversal, and Phase 4 viewport/resource lifetime hooks. Do not expose every storage detail as permanent public API.
