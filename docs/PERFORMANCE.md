# Performance baseline and Phase 2 budgets

The benchmark project measures the current engine and the full headless editor path before changing storage. Measurements describe one environment, not universal hardware guarantees. Native compositor/input latency, device power states and fallback fonts require separate platform runs.

## Reproduce

```sh
dotnet restore Textalonia.sln --configfile NuGet.Config
dotnet build Textalonia.sln -c Release --no-restore
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release --no-build -- --warmups 2 --repetitions 7 --output artifacts/benchmarks/local
```

Set `TEXTALONIA_REVISION` to the tested commit (and note uncommitted changes) and `TEXTALONIA_SDK` to `dotnet --version`. Archive `dotnet --info` too. Run on an otherwise idle machine, plugged in, with a fixed power plan; record any deviations. `--workload paragraphs-10000` selects one workload for focused comparisons. Default is all six. CLI warmup range is 1-100, repetitions 3-200. Scheduled/manual CI uses 3 warmups/15 repetitions; the initial local run uses 2/7. Seven observations provide a descriptive p95 (the maximum), not a reliable tail-latency estimate; use 30+ and multiple process runs for performance decisions.

Output includes `environment.json`, **every raw sample** in `results.json`, median/p95 and allocations in `summary.csv`, and `status.json`. Results are rewritten after each case, so an interrupted run retains partial measurements; only `status=complete` represents a complete workload set. A nonzero runner exit is a harness/workload failure. Exceeding an adopted budget is reported in results and does not turn an initial baseline capture into a failed job. Phase 2 must assess both latency and the memory budgets below; the runner's `WithinLatencyBudget` field covers latency only.

## Workloads and measurements

All inputs use the same deterministic generator as the correctness fixtures, validate before measurement, and are rejected if encoded native JSON exceeds the current 32 MiB import limit.

| Workload | Shape |
| --- | --- |
| `paragraphs-100`, `paragraphs-1000`, `paragraphs-10000` | 100/1,000/10,000 paragraphs, about 100 UTF-16 units each, Latin/Chinese/accented text, stable IDs. Replace-all matches `NEEDLE` in every 100th paragraph (1/10/100 matches). |
| `long-paragraph` | One paragraph, 3,000 repeats of `Long paragraph café 中文 👩‍💻. `; replace-all has 3,000 matches. |
| `table-heavy` | One 100 x 10 table with alternating cell backgrounds, one paragraph per cell and a trailing paragraph; replace-all has 1,000 matches. |
| `run-heavy` | 100 paragraphs x 100 alternating bold/italic/size runs; replace-all has 100 matches. |

The output records exact visible paragraph count, UTF-16 length and native byte size. Maximum table dimensions and overall element/depth limits stay within `FlowDocument.Validate`.

Each workload measures 23 operations:

- First viewport: a fresh editor/window, template/toolbar, load, layout and captured rendered frame. This is a warm-process first viewport; app/platform bootstrap and cold JIT/font startup are excluded after warmup.
- Single-character typing and deletion at start/middle/end, caret movement, and whole `Text` assignment, each **unbound and two-way bound** to a notifying view model. Bound typing includes control-to-view-model synchronization; bound assignment measures view-model-to-control replacement. Position setup and initial layout are outside the timer. Start deletion uses Delete, middle/end use Backspace; movement dispatches Right through the focused real surface. Every timed action drains dispatcher work, updates layout and captures a Skia frame. Default toolbar is included.
- Scroll to the midpoint of the actual `ScrollViewer`, width resize from 800 to 640 DIP, and replace-all through the editor, all including layout/frame work. The window starts at 800 x 500 DIP, Fluent Light, Inter 16 DIP, scale 1. OS font fallback may differ across machines.
- Native `SaveAsync` and `LoadAsync` through caller-owned memory streams. Document generation/encoded input preparation are outside the timer; validation, worker scheduling, encoding/parsing and memory-stream I/O are inside. Disk I/O is intentionally not measured.
- 100 non-coalesced edits alternating start/middle/end on an `EditorSession`, followed by a forced-GC history-retention comparison. It reports live managed bytes with undo minus the same session after setting `UndoLimit=0` (which also clears redo). Current document/index stay alive. The batch latency includes GC and is **not** a keystroke latency. This is an approximate managed undo-retention measure, not a native-memory/resource census or a UI history leak test.

Allocation samples use process-wide `GC.GetTotalAllocatedBytes(true)` deltas so background encoding work is included; unrelated runtime activity can add noise. Setup and cleanup are excluded. Heap retention is signed and may be noisy; do not clamp a negative sample into apparent certainty. Medians average the two central latency samples; p95 uses nearest rank `ceil(.95 * n)`. Allocation and retained-memory summaries use the middle ordered observation. No microbenchmark-only speedup may stand in for the control and binding results.

## Phase 2 budgets

Adopted in D03 for this corpus, evaluated on the same Windows reference host/configuration (or a documented replacement measured before/after). These are engineering acceptance targets for P2.7, not existing preview promises. Both bound/unbound cases count; full-text materialization costs must remain explicit in P2.1.

| Operation | Required p95 latency |
| --- | ---: |
| Single type/delete, caret, scroll | 16 ms |
| First viewport | 250 ms |
| Width resize, whole `Text` update | 100 ms |
| Replace-all, native save/load | 2,000 ms |

| Memory metric | Required budget |
| --- | --- |
| Managed allocation per local type/delete/caret/scroll | p95 at most 4 MiB + 4 x visible UTF-16 length bytes, including eager `Text` compatibility materialization |
| Managed first viewport allocation | p95 at most 64 MiB |
| Incremental retained undo for 100 non-coalesced edits | Median at most 64 MiB, with all raw samples disclosed; future configurable byte-budget accounting within 10% of the chosen budget under sustained edits |
| History eviction/reclamation | P2.6 must add sustained over-budget eviction and redo-retention cases. The Phase 1 paired heap estimate alone cannot prove a resource lifetime or eviction bound. |

Native caches, total working set and mobile device budgets remain unmeasured and cannot be advertised. A Phase 2 budget miss needs either a fix or a recorded maintainer decision changing the target; do not silently widen budgets or drop slow workloads.

Run `pwsh -File scripts/Compare-BaselineBudgets.ps1 -Results artifacts/benchmarks/local` to generate `budgets.json` from the raw observations. It assesses both memory and latency against this table. Add `-Enforce` for the Phase 2 acceptance gate; baseline CI deliberately reports current gaps without treating them as new regressions.

## Initial evidence

See the [baseline report](BASELINE-REPORT.md) and [raw Windows archive](baselines/performance/windows-2026-09-26). Environment: Windows build 26100 x64, .NET runtime 8.0.31, SDK 10.0.204, Avalonia 12.1.3, 24 logical processors. The archive is the authoritative measurement record. Linux/macOS performance is pending execution by their QA/performance maintainers; the Windows result is not a portability claim.

Compare medians, tails, allocations and retained history using the same input sizes and harness revision. Report source hash changes when evolving the harness, and repeat both old/new engine versions if measurement semantics change. Whole-document index construction, UTF-16 grapheme scans, tree/layout rebuilds and eager text synchronization are the P2.1 candidates; no storage replacement is selected by this phase.


## Phase 2 implementation and qualification

[The Phase 2 report](PHASE2-REPORT.md) compares all six workloads against a
remeasured Phase 1 reference with matching setup isolation. The unchanged budgets
pass in the standard seven-sample captures using the fixed full-corpus order on the Windows reference host. Longer-run table latency misses keep the Phase 2 exit gate open.
Original and nonisolated captures, including misses, remain archived.

The default harness still measures the 23 P1 operations per workload, including
both eager unbound and two-way Text-bound controls. `--text-mode document` captures
15 cases per workload separately, using `SynchronizeText=false`; its local edit
names end in `-document`, and it does not claim to support an eager Text binding.
Each raw UI sample additionally records shapes created, resident cached paragraphs,
updated document index nodes and the retained-history estimate. Window-layout
captures add shaped UTF-16 units, the largest shaping input, resident window count
and estimated resident shape bytes. Follow-up captures also record the peak shape
estimate and per-sample generation 0/1/2 collection counts. Collection counters are
read outside the timed interval; the workloads and budget rules are unchanged. The legacy `ShapedParagraphs` counter counts
TextLayout creations (now windows); `CachedParagraphs` counts paragraph checkpoint
sets. `ShapedCharacters` is an operation delta, while `LargestShapingWindow` is the
surface's high-water input length. Observation occurs
after the timed/allocation interval. First-open uses a fresh document wrapper so
fixture size reporting cannot warm the document index outside the timer.

```sh
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release --no-build -- --isolate-samples true --output artifacts/phase2/compatibility
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release --no-build -- --isolate-samples true --text-mode document --max-shaping-characters 4096 --output artifacts/phase2/document
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release --no-build -- --core-probe artifacts/phase2/probe
pwsh -File scripts/Compare-BaselineBudgets.ps1 -Results artifacts/phase2/compatibility -Enforce
pwsh -File scripts/Compare-BaselineBudgets.ps1 -Results artifacts/phase2/document -Enforce
```

The core probe separately measures 100-edit batches with a copying/grapheme-scan
prototype, the persistent session, and that session plus eager Text reads. Its
copying comparator models the bottleneck; it is not the historical P1 binary.
Only full control captures are compared with the P1 release budgets.

Intentional linear operations include ingestion/validation, arbitrary Execute
snapshots, structural table changes, dense-label rebasing, complete exports and
compatibility text/array materialization. Height metadata initializes lazily from
subtree estimates for ordinary text.
Table row dependencies and first-use numbered-list summaries can enumerate their
container's metadata. First viewport shaping uses visible text windows and at most
160 DIP of overscan on each side. `GeometryNodes` records nodes created since the
current height index was initialized; it is not an operation delta or a live heap count.
Exact distant targets may discover previously unmeasured line breaks. Default
compatibility rendering retains larger inputs for paragraph-wide bidi context and
oversized graphemes/visual lines. Hosts can set `MaxShapingCharacters` to reject
those inputs before allocation, with a typed rendering-limit error. Visible and
target layouts share the bounded cache; peak counters include construction and
active-lease estimates, including discarded lookahead attempts. See the
[architecture](ARCHITECTURE.md#performance-boundaries) for exact history/cache
estimate meanings and ownership exclusions.

### Isolating setup collections

The optional `--isolate-samples true` switch finishes pending GC/finalizer work
with two blocking full collections after preparation and before each timer.
Each sample creates a fresh editor/window during preparation and closes it during cleanup. A background
collection started there can otherwise suspend the next timed action. The runner
now records `GcPauseMilliseconds` as well as generation counts: a collection can
pause an action even when its start was outside the sample's generation-count
interval. No pause is subtracted from elapsed time, and collections triggered by
the measured operation are still counted and timed.

Isolation changes the heap state, so comparisons must use the same setting.
The final Phase 2 qualification remeasures Phase 1 from commit `ccae784`, applying
only the same setup-isolation switch and additive GC observations to its harness.
Original captures and nonisolated diagnostics are preserved. This controlled
comparison uses the original full-corpus order and two warmups per case. Standalone
workload startup and nonisolated results are disclosed separately; it does not qualify sustained
native input latency or eliminate the need for application workload measurements.

Use `--max-shaping-characters 4096` with document mode to exercise the strict
shaping policy on the full corpus. It is recorded in `environment.json`; zero
(the default) keeps compatibility rendering. Neither flag changes corpus sizes,
operations, budget thresholds or the requirement to disclose all samples.
