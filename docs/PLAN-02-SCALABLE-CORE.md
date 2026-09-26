# Phase 2: Scalable editing core

[Roadmap and dependencies](ROADMAP.md#implementation-phases)

**Outcome:** local edits and viewport work scale with affected content, with explicit memory bounds. **Entry:** Phase 1 correctness fixtures, measurements, and compatibility rules. **Status:** core implementation available; performance exit gate incomplete. See [implementation and evidence](PHASE2-REPORT.md).

## Phase 1 starting points

- [FlowDocument and DocumentIndex](../src/Textalonia/Model/FlowDocument.cs): eager flattened text and whole-tree rewrites.
- [EditorSession](../src/Textalonia/Editing/EditorSession.cs): index rebuilding, whole-text grapheme scans, and entry-count history.
- [DocumentLayout](../src/Textalonia/Controls/DocumentLayout.cs), [DocumentSurface](../src/Textalonia/Controls/DocumentSurface.cs), and [TextaloniaEditor](../src/Textalonia/Controls/TextaloniaEditor.cs): complete geometry rebuilds and eager `Text` synchronization.

## Tasks

- [x] **P2.1 - Specify edit and position contracts.** Record an architecture decision for edit deltas, persistent tree paths, stable block IDs, revision-aware positions, and a slow fallback for arbitrary `Session.Execute` snapshots. Prototype the measured bottleneck before adopting a balanced piece table or equivalent persistent storage. Explicitly settle how the public `RichRun.Text`, immutable arrays, `DocumentIndex.Text`, and Avalonia `Text` binding remain compatible or acquire an opt-in scalable mode. **Done when:** the decision includes an end-to-end benchmark and migration plan; eager full-string notifications are not hidden behind a storage-only improvement.

- [x] **P2.2 - Introduce incremental document indexes.** Maintain subtree text lengths, paragraph/container lookup, ID-to-path mapping, and local grapheme boundaries. Update affected nodes from edit deltas, preserving LF and UTF-16 offsets. Provide range reads/search traversal without flattening the document, and materialize complete text only where the documented API requires it. **Done when:** randomized differential tests match the baseline index, including cross-container replacement and undo, and local edits do not enumerate unrelated paragraphs.

- [x] **P2.3 - Replace repeated text copying.** Implement the chosen shared text storage behind the P2.1 contract, with styled spans and immutable snapshot access. Handle split/join, insertion at piece boundaries, large paste, and reclamation/compaction without invalidating saved snapshots or grapheme segmentation across adjacent pieces. Update binding synchronization to match the chosen compatibility policy. **Done when:** both long-paragraph and many-paragraph workloads improve against P1.5, and background snapshot export remains stable during edits.

- [x] **P2.4 - Add incremental layout indexes.** Separate paragraph shaping, block height/position tracking, and geometry lookup. Invalidate changed blocks and necessary ancestors; maintain estimates and prefix-height queries for unmeasured content. Define table span/row measurement dependencies so an intersecting cell can invalidate its required rows without forcing unrelated blocks to remeasure. **Done when:** caret, selection, hit-testing, and IME rectangles use the same geometry and agree with a fully measured reference on test fixtures.

- [ ] **P2.5 - Measure only the viewport and required targets.** Shape visible content plus bounded overscan; measure on demand for navigation, find results, automation ranges, and scrolling to a distant caret. Preserve a scroll anchor when estimated heights change. Bound layout caches and dispose evicted layouts. Include first-open, width change, theme/font changes, large tables, and offscreen edits. **Done when:** instrumentation shows initial layout does not shape every paragraph and viewport changes keep cache size bounded while selection and scroll positions remain correct.

- [x] **P2.6 - Bound retained history memory.** Add a configurable byte budget alongside the entry limit, accounting for shared text, snapshots, undo and redo retention, and later resource handles. Define the estimate's meaning, oversized-entry behavior, typing coalescing, and cleanup on load/history reset. **Done when:** sustained editing obeys the stated bound/tolerance, preserves exact undo/redo semantics, and reclaims data when history entries are evicted; shared data is not charged once per snapshot without explanation.

- [x] **P2.7 - Validate and document the engine change.** Run all Phase 1 correctness cases and compare raw performance results on the same workload/hardware. Document intentional O(document-size) operations, such as complete export and compatibility text materialization. Extend the package consumer for any changed public contract. **Done when:** adopted budgets pass or the phase remains explicitly incomplete, and the architecture/performance docs describe the implementation that actually shipped.

## Implementation notes

- P2.1: [ADR 002](ADR-002-SCALABLE-CORE.md) records storage, positions, deltas,
  compatibility views, opt-in document binding, the measured prototype and migration.
- P2.2/P2.3: weighted persistent trees, ID paths, bounded string pieces, range reads,
  streaming search, local grapheme context and differential snapshot tests are in place.
  Dense sibling labels have an explicit full-index rebase at 256 fractional bits.
- P2.4: paragraph shaping and prefix heights are separate. Table row dependencies,
  shared caret/selection/hit-test/IME geometry, and a frozen full-measure oracle are covered.
- P2.5: viewport/overscan measurement, distant targets, cache eviction/disposal and
  scroll anchors are implemented. Long paragraphs now use bounded text windows,
  retained line-break checkpoints, suffix reuse and visible-line rendering. Large
  cells propagate viewport limits. **This task remains open for strict bounds:**
  paragraph-wide bidi context uses full shaping, an indivisible grapheme/visual
  line may exceed the normal window size, and pinned working content can exceed
  the reusable cache budget. First-time exact distant targets discover intervening
  line breaks. The report records the remaining unchanged-budget failures.
- P2.6: history has entry and byte limits, shared ownership accounting (including
  typing styles, covered cells and merge backups), oversized-entry eviction, redo
  accounting and deterministic coalescing-boundary tests.
- P2.7: correctness, package-consumer and full-control performance evidence are
  recorded in the report. No existing budget was widened and no slow case was dropped.

## Exit gate

- Differential tests preserve document text, structure, selection, formatting, and history across generated edit sequences.
- Measured local editing, first viewport, scrolling, and history satisfy P1.5 budgets. Cache/operation counters corroborate the intended scaling, independently of noisy wall-clock measurements.
- Native JSON remains compatible with existing files; a private storage refactor must not accidentally change persisted data.
- Any unavoidable cost of a full `Text` binding is explicit and tested. Claims distinguish that mode from the scalable document mode.

## Handoff

Land P2.1, P2.2, and P2.3 in that order; layout and history can then proceed against the agreed contracts. Give Phase 3 reusable edit deltas and container traversal, and Phase 4 viewport/resource lifetime hooks. Do not expose every storage detail as permanent public API.
