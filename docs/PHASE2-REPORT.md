# Phase 2 implementation and evidence

**Status: retained viewport caches bounded; strict transient shaping and performance exit gates remain incomplete.** No P1 budget was widened. The final implementation passes 98 correctness tests, but both enforced performance comparisons still return nonzero.

The engine retains weighted persistent document/ID trees, shared bounded text pieces, local grapheme context, range reads, streaming search and byte-budgeted history. This follow-up closes the visible-content cache exception and removes eager height-metadata construction from ordinary first viewport and width changes. It preserves exact Unicode rendering, including the remaining large-context shaping paths.

See [ADR 002](ADR-002-SCALABLE-CORE.md), [migration and compatibility](COMPATIBILITY.md#phase-2-additive-api-migration), and [architecture/bounds](ARCHITECTURE.md#performance-boundaries). Existing Text bindings stay eager; Document consumers opt into `SynchronizeText=false`.

## Changes and correctness

- Glyph layouts use a central LRU limited to 256 layouts and a 16 MiB estimate. Visible/target layouts can be evicted; drawing and geometry queries acquire a short lease and recreate an evicted shape. Oversized exact layouts are released when the lease ends. Detach also clears shapes recreated by visuals whose paragraph metadata was evicted.
- Height branches begin with additive subtree estimates and materialize on visited paths. Table row dependencies and first-use numbered-list summaries may still enumerate their container metadata. Plain paragraphs avoid numbering-array allocations. Overscan is bounded to 160 DIP per side.
- Anchors include corrections caused by offscreen caret/IME queries. Painting does not discover offscreen caret geometry. Selection changes avoid unnecessary layout rebuilding. A final regression covers carets both above and below the viewport.
- One ownership table updates total/current history references in a single traversal. Load/reset clears discarded ownership without traversing the old graph to decrement every entry. The estimate, limits and undo/redo semantics are unchanged.
- Release build: zero warnings/errors. **98 tests passed, none failed or skipped.** Six generated seeds ran 2,000 operations each. The frozen full-measure oracle, window seams, styles, bidi, graphemes, table spans, long cells, width/theme changes, Home/End and IME checks pass. New regressions cover 900 visible paragraphs with a 256-layout cache, transient oversized layouts, lazy first-open metadata and offscreen-query anchoring.
- Native v1 and public API fixtures are unchanged. The independent package consumer is checked using a fresh workspace cache and offline dependency feed; execution status and loaded/packed library hashes are recorded in [verification](baselines/performance/windows-2026-09-26-phase2-cache/verification.json).

## Full control comparison

Same Windows host/CPU signature, .NET 8.0.31, SDK 10.0.204, Avalonia 12.1.3, headless Skia, Inter and 800 x 500 DIP as P1. The final capture uses the original two warmups/seven samples, preserves every one of the 138 compatibility cases, and separately records all 90 document-mode cases. Inputs, timing boundaries and adopted budgets are unchanged. Seven-sample p95 is a descriptive maximum, not a robust tail estimate.

| Workload / operation | P1 median ms | Previous windowed P2 median ms | Current median ms | Current p95 ms |
| --- | ---: | ---: | ---: | ---: |
| paragraphs-10000 / first-viewport | 776.10 | 73.74 | 32.28 | 66.72 |
| paragraphs-10000 / type-start-unbound | 24.87 | 4.97 | 5.68 | 14.04 |
| paragraphs-10000 / text-update-unbound | 1087.42 | 119.26 | 41.37 | 43.60 |
| paragraphs-10000 / text-update-bound | 924.86 | 125.31 | 75.15 | 89.22 |
| paragraphs-10000 / resize | 2795.89 | 29.57 | 7.39 | 10.10 |
| long-paragraph / first-viewport | 352.17 | 11.44 | 18.36 | 64.28 |
| long-paragraph / scroll | 19.67 | 18.11 | 7.11 | 7.58 |
| table-heavy / scroll | 3.42 | 10.89 | 5.22 | 6.50 |
| table-heavy / type-middle-unbound | 5.28 | 7.26 | 22.88 | 54.53 |

For 10,000 paragraphs, the final first viewport creates **34 geometry nodes and 21 glyph layouts**, rather than constructing all paragraph geometry. Its median managed allocation is 23.26 MiB (P1: 133.96 MiB; previous windowed P2: 40.50 MiB). Resize allocates 0.55 MiB at the median. Initial ingestion still constructs the document index and ownership graph, and eager Text materialization remains linear.

Long-paragraph first viewport shapes 4,094 UTF-16 units out of 90,000; its largest input is 2,047 units. The final corpus high-water observations are 150 resident layouts and 357,728 estimated resident shape bytes. The 900-paragraph regression separately exercises eviction beyond the count budget. These estimates do not measure native glyph allocations or process working set.

The [complete comparison](baselines/performance/windows-2026-09-26-phase2-cache/comparison.json) includes all cases and memory results, including regressions. Document-mode comparison entries explicitly identify the P1 compatibility comparator; they do not substitute for eager binding results.

## Budget outcome and remaining work

| Mode | Cases | Checks passed | Checks exceeded |
| --- | ---: | ---: | ---: |
| compatibility | 138 | 205 | 29 |
| document | 90 | 131 | 13 |

**All final allocation and retained-history checks pass; the failures are p95 latency. Both enforced budget commands exit 1.** Whole-text replacement at 10,000 paragraphs passes its 100 ms limit in the final seven-sample capture, but it fails in the separately preserved 30-sample diagnostic run. Local edits and some table/resize cases also remain above budget. No slow sample was excluded.

The [30-sample diagnostic](baselines/performance/windows-2026-09-26-phase2-cache/diagnostic-30/compatibility/results.json) has 52 compatibility and 20 document-mode failures, also latency only. It preceded the final offscreen-caret visibility guard and has separate hashes. CPU readings during that run reached 77% and 100%; the latter is [archived](baselines/performance/windows-2026-09-26-phase2-cache/diagnostic-30/host-load.json). GC counts accompany each sample. These observations show that the timings need controlled reruns; they do not prove that all misses are external or justify declaring acceptance. The final binary's separate capture above also fails.

P2.5 still needs a strict transient-work policy or a compatible bounded shaping implementation. Paragraph-wide bidi context and an indivisible grapheme/visual line can require a larger input than the normal 2,048-unit window. Such shapes no longer remain in an over-budget reusable cache, but their construction/active lease can exceed the estimate. `PeakLayoutBytes` discloses this high-water estimate. Checkpoints remain proportional to measured text, and first-time exact targets within a long paragraph may discover intervening line breaks. A fixed cap with a placeholder/rejection would change existing rendering behavior; no such compatibility change was made.

Acceptance therefore still requires resolving that shaping contract and demonstrating the unchanged budgets on a controlled reference run, then fixing any remaining measured misses. Native platform qualification remains separate.

## Evidence and reproduction

- [Final compatibility samples](baselines/performance/windows-2026-09-26-phase2-cache/compatibility/results.json), [budget checks](baselines/performance/windows-2026-09-26-phase2-cache/compatibility/budgets.json).
- [Final document-mode samples](baselines/performance/windows-2026-09-26-phase2-cache/document/results.json), [budget checks](baselines/performance/windows-2026-09-26-phase2-cache/document/budgets.json).
- [All comparisons](baselines/performance/windows-2026-09-26-phase2-cache/comparison.json), [source hashes](baselines/performance/windows-2026-09-26-phase2-cache/source-hashes.json), [verification](baselines/performance/windows-2026-09-26-phase2-cache/verification.json), [host notes](baselines/performance/windows-2026-09-26-phase2-cache/host-notes.txt).
- [Diagnostic compatibility samples](baselines/performance/windows-2026-09-26-phase2-cache/diagnostic-30/compatibility/results.json), [diagnostic document samples](baselines/performance/windows-2026-09-26-phase2-cache/diagnostic-30/document/results.json). These complete runs are retained separately and are not substituted case by case.
- [Previous windowed P2 evidence](baselines/performance/windows-2026-09-26-phase2-windows/verification.json), [initial P2 evidence](baselines/performance/windows-2026-09-26-phase2/verification.json), and [P1 results](baselines/performance/windows-2026-09-26/results.json) remain unchanged.

Use the commands in [PERFORMANCE](PERFORMANCE.md#phase-2-implementation-and-qualification). Set `TEXTALONIA_FUZZ_STEPS=2000` for the long corpus. The budget script ran with a process-local Windows PowerShell execution-policy override. Package restore uses a fresh workspace cache and the new local nupkg to avoid testing an older build with the same preview version.
