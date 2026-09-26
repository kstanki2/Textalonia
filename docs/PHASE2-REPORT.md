# Phase 2 implementation and evidence

**Status: windowed layout implemented; performance exit gate incomplete.** Ordinary long paragraphs and large cells now use viewport-limited text windows. P2.5 remains open for the bidi/indivisible-line shaping fallbacks and strict working-set bounds described below. No P1 budget was widened.

The engine has weighted persistent document/ID trees, shared bounded text pieces, local grapheme context, range reads and streaming search. Layout separates prefix heights and line-break checkpoints from glyph layouts. It shapes ordinary long paragraphs in windows of up to 2,048 UTF-16 units, commits complete lines with lookahead, reuses converged single-style suffixes, and draws only visible lines. Large cells pass viewport limits to their contents. Text-line anchors preserve scrolling within long paragraphs; Home/End, hit-testing, selection and IME use the same window offsets. History has shared ownership accounting, including typing styles and hidden/merged content, with entry and byte limits.

See [ADR 002](ADR-002-SCALABLE-CORE.md), [migration and compatibility](COMPATIBILITY.md#phase-2-additive-api-migration), and [architecture/bounds](ARCHITECTURE.md#performance-boundaries). Existing Text bindings stay eager; Document consumers opt into `SynchronizeText=false`.

## Correctness and consumer checks

- Release build: zero warnings/errors. 95 tests passed, zero failures/skips; the P1 generated corpus ran 2,000 operations for each of six seeds (12,000 operations).
- New tests compare incremental coordinates against an independent tree walk through replacements, formatting, cross-container edits, Execute, undo and redo. They check Unicode across piece/style boundaries, dense-key rebasing, large paste, saved-snapshot export during editing, shared history, oversized entries, redo eviction, typing styles, reclamation and the 799/800 ms coalescing boundary.
- A frozen P1 full-measure layout oracle checks caret, hit-test and selection geometry on structured, mixed-script, emoji and long-paragraph fixtures. New regressions cover window seams with all alignments, mixed styles, graphemes and soft breaks; tiny-font paragraph height; bidi context introduced by an edit; large cells with many paragraphs; Home/End and IME in the real control; and anchors inside a paragraph across edits, width and theme changes. A 720,000-character paragraph exercises eviction across more than 256 windows and exact caret checkpoints after eviction. Existing headless rendering, table spans and scroll-anchor cases also pass.
- The native v1 fixture is unchanged. The public surface baseline has additive APIs and no removed members. The independent package consumer exercises range/position APIs, document mode and the history budget as well as its previous input/formatting/JSON/render checks. **The packed consumer passed** from a fresh workspace cache; its loaded library hash matches the built/packed library. The execution result is recorded in verification.json.

## Full control comparison

Same Windows host/CPU signature, .NET 8.0.31, SDK 10.0.204 and Avalonia 12.1.3 as P1; headless Skia, Inter, 800 x 500 DIP. Two warmups and seven samples per case. Power state and background activity were not controlled, and seven-sample p95 is a descriptive maximum, not a robust tail estimate.

The compatibility capture preserves all 138 P1 cases. The separate document-mode capture has 90 cases; it does not substitute for eager binding results. First-open indexes a fresh wrapper so earlier fixture-size reporting cannot prebuild its document index. The input model/run objects are preconstructed in both phases.

| Workload / operation | P1 median ms | P2 median ms | P1 p95 ms | P2 p95 ms |
| --- | ---: | ---: | ---: | ---: |
| paragraphs-10000 / first-viewport | 776.10 | 73.74 | 923.50 | 116.97 |
| paragraphs-10000 / type-start-unbound | 24.87 | 4.97 | 42.66 | 15.05 |
| paragraphs-10000 / type-start-bound | 34.74 | 6.16 | 84.33 | 17.77 |
| paragraphs-10000 / caret-bound | 67.16 | 2.36 | 151.42 | 2.87 |
| paragraphs-10000 / resize | 2795.89 | 29.57 | 3782.81 | 43.75 |
| paragraphs-10000 / replace-all | 1881.21 | 23.91 | 1968.24 | 54.65 |
| paragraphs-10000 / text-update-bound | 924.86 | 125.31 | 1252.67 | 169.33 |
| long-paragraph / type-start-unbound | 381.62 | 4.97 | 586.34 | 12.80 |
| long-paragraph / resize | 948.41 | 18.01 | 1031.64 | 27.31 |
| table-heavy / scroll | 3.42 | 10.89 | 6.57 | 26.81 |

For 10,000 paragraphs, median first-viewport managed allocation fell from 133.96 MiB to 40.50 MiB. Initial shaping created 30 layouts, not 10,000. In document mode, start typing measured 3.57 ms median / 3.91 ms p95; the full raw capture also includes middle/end edits and scrolling.

Compared with the initial Phase 2 implementation, long-paragraph first viewport fell from 271.54 to 11.44 ms median, start typing from 261.30 to 4.97 ms, and resize from 870.60 to 18.01 ms. First viewport shapes 4,094 units in two windows out of 90,000 units, with a maximum input of 2,047 units. Its managed allocation fell from 37.59 to 3.92 MiB; start typing fell from 34.94 to 0.82 MiB. First distant scroll is still 18.11 ms median / 28.88 ms p95 in compatibility mode (11.34 / 21.21 ms in document mode). It discovers intervening line breaks, reuses identical discarded windows where formatting agrees, and retains only the required glyphs. The archive includes all initial-P2 and P1 comparisons, including regressions.

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

- compatibility: 214 of 234 checks passed; 20 exceeded, all p95 latency.
- document: 133 of 144 checks passed; 11 exceeded, all p95 latency.
- All allocation and retained-history checks passed in both modes. The initial Phase 2 captures had 49 and 24 total failures, respectively.

Both enforced budget commands returned nonzero. Whole-text replacement at 10,000 paragraphs remains above 100 ms, and some editing, scrolling, table and resize samples miss their latency budgets. Further ingestion and tail-latency work is required; these samples do not justify declaring the exit gate passed.

The window size is not a universal hard limit. Paragraph-wide bidi context retains complete Avalonia shaping to preserve ordering across embeddings and windows. An indivisible grapheme or visual line may require a larger input. Exact first-time distant targets can require linear prefix discovery. The reusable cache is bounded to 256 paragraph checkpoint sets, 256 layouts and a 16 MiB shape estimate; pinned visible/target content can exceed those limits. Compact checkpoint memory grows with measured text and is separate from the glyph estimate. These exceptions keep P2.5 explicitly open. Native platform qualification also remains separate and pending; these results make no Linux/macOS/native compositor claim.

## Evidence and reproduction

- [Compatibility raw samples, counters and budgets](baselines/performance/windows-2026-09-26-phase2-windows/compatibility/results.json), [budget checks](baselines/performance/windows-2026-09-26-phase2-windows/compatibility/budgets.json).
- [Document-mode raw samples](baselines/performance/windows-2026-09-26-phase2-windows/document/results.json), [budget checks](baselines/performance/windows-2026-09-26-phase2-windows/document/budgets.json).
- [All P1/initial-P2/windowed comparisons](baselines/performance/windows-2026-09-26-phase2-windows/comparison.json), [verification](baselines/performance/windows-2026-09-26-phase2-windows/verification.json).
- [Initial Phase 2 evidence](baselines/performance/windows-2026-09-26-phase2/verification.json) and [core prototype samples](baselines/performance/windows-2026-09-26-phase2/core-probe.json) remain unchanged.
- The archive includes source hashes, runtime/SDK information and host notes. The core prototype models copying/flattening against persistent session edits; it is not a re-run of the historical P1 binary.

Use the commands in [PERFORMANCE](PERFORMANCE.md#phase-2-implementation-and-qualification). Set `TEXTALONIA_FUZZ_STEPS=2000` for the long correctness corpus. On this host the budget script used Windows PowerShell with a process-local execution-policy override because `pwsh` was unavailable. Consumer restore uses a fresh workspace package cache and local dependency feed to avoid accidentally testing an older package with the same preview version.
