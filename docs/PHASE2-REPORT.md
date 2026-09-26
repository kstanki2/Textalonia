# Phase 2 implementation and evidence

**Status: implementation available; performance exit gate incomplete.** P2.5 remains open for work within long paragraphs and large cells. No P1 budget was widened.

The engine now has weighted persistent document/ID trees, shared bounded text pieces, local grapheme context, range reads and streaming search. Layout separates prefix heights from shaped paragraphs, measures the viewport and required targets, preserves scroll anchors, and evicts/disposes cached layouts. History has shared ownership accounting, including typing styles and hidden/merged content, with entry and byte limits.

See [ADR 002](ADR-002-SCALABLE-CORE.md), [migration and compatibility](COMPATIBILITY.md#phase-2-additive-api-migration), and [architecture/bounds](ARCHITECTURE.md#performance-boundaries). Existing Text bindings stay eager; Document consumers opt into `SynchronizeText=false`.

## Correctness and consumer checks

- Release build: zero warnings/errors. 83 tests passed, zero failures/skips; the P1 generated corpus ran 2,000 operations for each of six seeds (12,000 operations).
- New tests compare incremental coordinates against an independent tree walk through replacements, formatting, cross-container edits, Execute, undo and redo. They check Unicode across piece/style boundaries, dense-key rebasing, large paste, saved-snapshot export during editing, shared history, oversized entries, redo eviction, typing styles, reclamation and the 799/800 ms coalescing boundary.
- A frozen P1 full-measure layout oracle checks caret, hit-test and selection geometry on structured, mixed-script, emoji and long-paragraph fixtures. Headless tests cover IME, first viewport, width/theme changes, spans, cache disposal, distant targets and offscreen-edit/theme scroll anchors. The demo rendering was also inspected.
- The native v1 fixture is unchanged. The public surface baseline has additive APIs and no removed members. The independent package consumer exercises range/position APIs, document mode and the history budget as well as its previous input/formatting/JSON/render checks. **The packed consumer passed** from a fresh workspace cache; its loaded library hash matches the built/packed library. The execution result is recorded in verification.json.

## Full control comparison

Same Windows host/CPU signature, .NET 8.0.31, SDK 10.0.204 and Avalonia 12.1.3 as P1; headless Skia, Inter, 800 x 500 DIP. Two warmups and seven samples per case. Power state and background activity were not controlled, and seven-sample p95 is a descriptive maximum, not a robust tail estimate.

The compatibility capture preserves all 138 P1 cases. The separate document-mode capture has 90 cases; it does not substitute for eager binding results. First-open indexes a fresh wrapper so earlier fixture-size reporting cannot prebuild its document index. The input model/run objects are preconstructed in both phases.

| Workload / operation | P1 median ms | P2 median ms | P1 p95 ms | P2 p95 ms |
| --- | ---: | ---: | ---: | ---: |
| paragraphs-10000 / first-viewport | 776.10 | 66.33 | 923.50 | 109.06 |
| paragraphs-10000 / type-start-unbound | 24.87 | 4.30 | 42.66 | 11.29 |
| paragraphs-10000 / type-start-bound | 34.74 | 5.47 | 84.33 | 12.75 |
| paragraphs-10000 / caret-bound | 67.16 | 2.21 | 151.42 | 2.49 |
| paragraphs-10000 / resize | 2795.89 | 24.48 | 3782.81 | 70.99 |
| paragraphs-10000 / replace-all | 1881.21 | 25.94 | 1968.24 | 41.61 |
| paragraphs-10000 / text-update-bound | 924.86 | 124.03 | 1252.67 | 169.66 |
| long-paragraph / type-start-unbound | 381.62 | 261.30 | 586.34 | 311.36 |
| long-paragraph / resize | 948.41 | 870.60 | 1031.64 | 950.91 |
| table-heavy / scroll | 3.42 | 6.09 | 6.57 | 24.67 |

For 10,000 paragraphs, median first-viewport managed allocation fell from 133.96 MiB to 40.42 MiB. Initial shaping created 30 layouts, not 10,000. In document mode, start typing measured 3.02 ms median / 3.58 ms p95; the full raw capture also includes middle/end edits and scrolling.

| Workload | P1 median retained undo MiB | P2 median retained undo MiB |
| --- | ---: | ---: |
| paragraphs-100 | 0.119 | 0.196 |
| paragraphs-1000 | 0.798 | 0.245 |
| paragraphs-10000 | 7.596 | 0.299 |
| long-paragraph | 17.027 | 0.121 |
| table-heavy | 10.921 | 0.288 |
| run-heavy | 0.469 | 0.885 |

These retained values are the original forced-GC paired heap measurement, not `RetainedHistoryBytes`. The configurable budget enforces the separately documented shared-graph estimate; current storage, caller-held snapshots, weak-cache bookkeeping and UI/native caches are excluded. Sustained undo/redo/coalescing tests assert zero overshoot of that estimate and collection of evicted snapshots.

## Budget outcome and remaining work

- compatibility: 185 of 234 checks passed; 49 exceeded. Failures by metric: p95-ms: 37, p95-managed-allocated-bytes: 12.
- document: 120 of 144 checks passed; 24 exceeded. Failures by metric: p95-ms: 18, p95-managed-allocated-bytes: 6.

Both enforced budget commands returned nonzero, as expected for those misses. Long paragraphs still incur complete Avalonia shaping and drawing; this dominates local-edit latency and allocation. Whole-text replacement at 10,000 paragraphs remains above 100 ms, and some table/other control samples miss 16 ms. The next work is measurement within paragraphs/large cells, ingestion profiling and tail-latency qualification. Native platform qualification remains separate and pending; these results make no Linux/macOS/native compositor claim.

## Evidence and reproduction

- [Compatibility raw samples, counters and budgets](baselines/performance/windows-2026-09-26-phase2/compatibility/results.json), [budget checks](baselines/performance/windows-2026-09-26-phase2/compatibility/budgets.json).
- [Document-mode raw samples](baselines/performance/windows-2026-09-26-phase2/document/results.json), [budget checks](baselines/performance/windows-2026-09-26-phase2/document/budgets.json).
- [All P1/P2 comparisons](baselines/performance/windows-2026-09-26-phase2/comparison.json), [core prototype samples](baselines/performance/windows-2026-09-26-phase2/core-probe.json), [verification](baselines/performance/windows-2026-09-26-phase2/verification.json).
- The archive includes source hashes, runtime/SDK information and host notes. The core prototype models copying/flattening against persistent session edits; it is not a re-run of the historical P1 binary.

Use the commands in [PERFORMANCE](PERFORMANCE.md#phase-2-implementation-and-qualification). Set `TEXTALONIA_FUZZ_STEPS=2000` for the long correctness corpus. On this host the budget script used Windows PowerShell with a process-local execution-policy override because `pwsh` was unavailable. Consumer restore uses a fresh workspace package cache and local dependency feed to avoid accidentally testing an older package with the same preview version.
