# Phase 2 implementation and evidence

**Status: implementation complete; performance exit gate remains open.**
Both standard seven-sample controlled captures pass, but the longer strict-mode
confirmation still exceeds table latency budgets.
The optional strict shaping policy closes P2.5 without changing default rendering.
No adopted budget was widened, no workload was removed, and no sample was discarded.
The 30-sample confirmation and package verification are recorded in the linked evidence.

The core provides persistent document/ID indexes, shared text pieces, local grapheme
context, range reads, streaming search, viewport layout and byte-budgeted history.
Existing `Text` bindings remain eager. `SynchronizeText=false` avoids those full-text
notifications; `MaxShapingCharacters` independently enables strict shaping inputs.
See [ADR 002](ADR-002-SCALABLE-CORE.md), [migration](COMPATIBILITY.md#phase-2-additive-api-migration)
and [bounds](ARCHITECTURE.md#performance-boundaries).

## Remaining implementation completed

- `MaxShapingCharacters` defaults to zero for exact compatibility rendering.
  Positive values of at least 2,048 bound text reads, grapheme discovery and every
  shaping attempt, including bidi context, window growth and recreation after
  eviction. A request exceeding the limit produces `ShapingLimitExceededException`.
  The surface releases partial layouts, exposes `LayoutError`, reports
  `OperationFailed`, and displays an explicit rendering-limit message. It preserves
  the document, logical selection, undo/redo, copy and export. Successful layout
  after changing the policy/content clears the error. Geometry-dependent input
  is suspended while exact geometry is unavailable; IME receives an empty caret
  rectangle. Text edits and undo remain available.
- Reusable glyph ownership remains limited to 256 layouts and a 16 MiB estimate.
  With limit L, construction or one active lease adds at most `256 + 32 * L`
  estimated bytes. Peak observations now include discarded lookahead attempts.
  Default unlimited rendering retains its explicitly documented transient exception.
- History reference visitors reuse callbacks instead of allocating iterators per
  node. Single-run text reads avoid a weak-table lookup. Paragraph length reads,
  geometry path traversal and paragraph-cache eviction avoid temporary enumeration
  and sorting allocations. Empty preedit cancellation no longer rebuilds layout.
  The retained-history estimate and undo/redo policy are unchanged.

- Distant caret navigation now publishes a refined document extent before asking
  the scroll viewer to bring the caret into view. Previously that request could
  be clamped against the old estimate, leaving the caret offscreen until the next
  edit. A failing visibility regression established the bug; insertion and deletion
  now both keep the caret visible and reshape only the changed final window.
## Correctness and package contract

Release build: **zero warnings/errors; 107 tests passed, none failed or skipped**.
Six generated seeds each ran 2,000 operations. Tests include the frozen full-measure
geometry oracle, window seams, styles, bidi, graphemes, table spans, long cells,
width/theme changes, Home/End, IME, offscreen anchoring, 900 visible paragraphs with
cache eviction, sustained history limits and snapshot export during edits.

Closing-control coverage confirms every cached glyph layout is released.
New regressions cover rejected bidi/grapheme/visual-line inputs before oversized
shaping, partial-layout disposal, exact geometry within the allowance, an offscreen
target, error notification deduplication, retained content/history and recovery via
policy changes, load and undo/redo. The benchmark fails if any frame reports a
rendering-limit error, so the strict corpus cannot pass by displaying an error message.

The native-v1 fixture is byte-for-byte unchanged. The API snapshot adds only the
shaping property, observable error and exception type. The independent package
consumer exercises those APIs, including error/recovery, through a fresh workspace
package cache and offline dependency feed. Exact results and loaded/packed assembly
hashes are in [verification](baselines/performance/windows-2026-09-26-phase2-final/verification.json).

## Paired performance qualification

The Windows host, .NET 8.0.31, SDK 10.0.204, Avalonia 12.1.3, headless Skia, Inter,
800 x 500 DIP, corpus, operations and thresholds are unchanged. Both engines were
remeasured with two warmups and seven samples. All 138 compatibility cases and
90 document-mode cases are present, with identical input sizes to the reference.
Document mode uses `SynchronizeText=false` and `MaxShapingCharacters=4096`.

The harness now offers explicit **setup isolation**. Each sample creates a fresh
editor/window during preparation. Diagnostic GC-pause observations showed that a
collection started there could suspend the next timed action for 50–85 ms even
when no collection started inside that action's generation-count interval.
`--isolate-samples true` settles GC/finalizers after preparation, before timing.
No pause is subtracted from elapsed time; GC caused by the action remains timed.
This is a changed heap preparation method, so Phase 1 was rebuilt from `ccae784`
and run with exactly the same isolation and additive counters. The
[small reference-harness patch](baselines/performance/windows-2026-09-26-phase2-final/p1-harness.patch)
changes no engine code or workload. Its application was checked against the original.

| Workload / operation | Paired P1 median ms | P2 median ms | P2 p95 ms |
| --- | ---: | ---: | ---: |
| paragraphs-10000 / first-viewport | 627.04 | 15.65 | 16.82 |
| paragraphs-10000 / type-middle-unbound | 14.09 | 3.95 | 4.11 |
| paragraphs-10000 / text-update-unbound | 643.30 | 15.72 | 17.19 |
| paragraphs-10000 / text-update-bound | 643.37 | 15.38 | 19.10 |
| paragraphs-10000 / resize | 1925.92 | 7.52 | 8.26 |
| long-paragraph / first-viewport | 228.83 | 12.66 | 13.19 |
| long-paragraph / type-middle-unbound | 232.06 | 5.37 | 6.08 |
| long-paragraph / scroll | 16.24 | 8.05 | 8.42 |
| table-heavy / type-middle-bound | 4.35 | 6.63 | 7.81 |
| table-heavy / scroll | 3.04 | 5.92 | 6.72 |

Some table geometry operations are slower than P1 while meeting their budgets.
The compatibility history batch also measures 217.53 ms versus P1's 139.18 ms;
that batch includes forced GC and has a memory gate, not a keystroke-latency gate.
The [complete comparison](baselines/performance/windows-2026-09-26-phase2-final/comparison.json)
includes every case and the regressions. Document-mode entries explicitly identify
the P1 unbound compatibility comparator rather than replacing eager binding evidence.

For 10,000 paragraphs, first viewport creates **34 geometry nodes and 21 glyph
layouts**. Median managed allocation is **13.93 MiB**, versus P1's 133.97 MiB.
A middle edit with eager text allocates 2,032,864 bytes; full Text materialization
remains intentionally linear. Long-paragraph first viewport shapes 4,094 of 90,000
UTF-16 units in two inputs, each at most 2,047 units. The compatibility corpus peaks
at 150 resident layouts, 328,800 estimated resident shape bytes and 394,560 estimated
peak shape bytes. Dense-viewport tests independently exercise the 256-layout limit.
These counters do not measure native glyph allocations or process working set.

## Budget outcome and scope

| Capture | Cases | Checks passed | Checks exceeded |
| --- | ---: | ---: | ---: |
| Paired P1 reference | 138 | 175 | 59 |
| P2 compatibility, seven samples | 138 | 234 | 0 |
| P2 strict document mode, seven samples | 90 | 144 | 0 |
| P2 strict document mode, 30 samples | 90 | 142 | 2 |

Both P2 `Compare-BaselineBudgets.ps1 -Enforce` commands return **0**. This covers
latency, allocation and retained history. Seven-sample p95 is a descriptive maximum;
[30-sample confirmation](baselines/performance/windows-2026-09-26-phase2-final/confirmation-30)
repeats the complete strict document-mode corpus after the scroll fix. No budgets or slow cases are suppressed in either run.

The longer strict confirmation returns a nonzero enforcement result:
`table-heavy/delete-middle-document` is **19.84 ms p95**, and
`table-heavy/caret-document` is **16.33 ms p95**, against **16 ms**. All its
allocation and history checks pass. These two latency misses keep P2.7 and the
Phase 2 exit gate open. The slow samples record no GC pause; a host CPU sample
was 17.56% with other applications active. That observation does not establish
load as the sole cause. An idle-host rerun can separate host variance from
remaining engine latency before choosing another implementation change.
A [focused CPU probe](baselines/performance/windows-2026-09-26-phase2-final/diagnostics/cpu-probe)
did not reproduce either miss across 64 samples per operation, so it does not
establish a cause or replace the failed full-corpus confirmation.
The [nonisolated diagnostics](baselines/performance/windows-2026-09-26-phase2-final/diagnostics)
remain archived: the starting engine has 40 misses, the first allocation stage 20,
and the next stage 21, all latency. Their GC pauses remain in their elapsed times.
The [final binary without setup isolation](baselines/performance/windows-2026-09-26-phase2-final/nonisolated-final)
has 23 latency misses and is retained separately. Controlled acceptance uses the fixed full-corpus order
and two warmups per case on this host; it does not promise the same tail latency during
continuous allocation, arbitrary application activity or native compositor/input.

The [first candidate](baselines/performance/windows-2026-09-26-phase2-final/diagnostics/first-candidate)
passed both standard captures and its complete compatibility 30-sample run. Its
strict 30-sample run was stopped after 77 complete cases when end deletion in the
long paragraph reached 16.6832 ms p95. The caret regression established that the
old extent clamped the initial scroll; the first edit then finished scrolling and
rebuilt an extra window. The fix reduces that edit from 2,941 shaped UTF-16 units
to 894 and passes both visibility regressions. End deletion is 8.42 ms p95 in the
new full-corpus 30-sample confirmation. All completed samples are preserved.

The final binary's [standalone long-paragraph probes](baselines/performance/windows-2026-09-26-phase2-final/diagnostics/standalone-long-paragraph)
also remain disclosed: first insertion exceeds 16 ms when that workload starts the
process (17.88 ms in document mode, 20.96 ms unbound). These probes use a different
workload order and do not replace the paired full-corpus qualification. Startup
and application workload latency need their own measurements.

Strictness is opt-in: default rendering may shape a whole bidi paragraph or an
indivisible long grapheme/line. Enabling the cap chooses an explicit error over
unbounded exact shaping. Checkpoints and height metadata remain proportional to
measured text/model nodes, and first-time distant targets can discover preceding
line breaks. Ingestion, validation, arbitrary snapshots, structural table changes,
complete exports and compatibility text/array reads retain documented linear costs.
Native platform qualification and native-memory/working-set budgets remain separate.

## Evidence and reproduction

- [Compatibility samples](baselines/performance/windows-2026-09-26-phase2-final/compatibility/results.json)
  and [enforced budgets](baselines/performance/windows-2026-09-26-phase2-final/compatibility/budgets.json).
- [Strict document-mode samples](baselines/performance/windows-2026-09-26-phase2-final/document/results.json)
  and [enforced budgets](baselines/performance/windows-2026-09-26-phase2-final/document/budgets.json).
- [Remeasured P1](baselines/performance/windows-2026-09-26-phase2-final/reference/results.json),
  [source hashes](baselines/performance/windows-2026-09-26-phase2-final/source-hashes.json),
  [host notes](baselines/performance/windows-2026-09-26-phase2-final/host-notes.txt),
  and [reproduction commands](baselines/performance/windows-2026-09-26-phase2-final/README.md).
- The [original P1](baselines/performance/windows-2026-09-26/results.json),
  [initial P2](baselines/performance/windows-2026-09-26-phase2/verification.json),
  [windowed P2](baselines/performance/windows-2026-09-26-phase2-windows/verification.json),
  and [cache follow-up](baselines/performance/windows-2026-09-26-phase2-cache/verification.json)
  remain unchanged, including their earlier failures.

Use the commands in [PERFORMANCE](PERFORMANCE.md#phase-2-implementation-and-qualification).
Set `TEXTALONIA_FUZZ_STEPS=2000` for the long correctness corpus. The budget script
uses a process-local PowerShell execution-policy override on this Windows host.
