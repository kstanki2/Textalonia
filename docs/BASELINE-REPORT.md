# Phase 1 baseline report

Recorded 2026-09-26 against base commit `050c7adff2c612acb6de2a9e226ad6020720f7fe` plus the Phase 1 working tree. The engine and native writer remain unchanged. The automated baseline is ready for P2.1; native certification, mobile hosts, release ownership and licensing remain pending.

## Delivered evidence

| Task | Result |
| --- | --- |
| P1.1 | [Qualification matrix and decisions D01-D07](QUALIFICATION.md): separate preview contracts/native tiers, platform routes and responsible roles, public repository/NuGet identity checks. License and package ownership remain maintainer decisions for P8.3. |
| P1.2 | [Compatibility invariants](COMPATIBILITY.md), frozen native v1 fixture, public/protected API snapshot and regression check. Future version-dispatched readers/migration policy documented; no schema bump. |
| P1.3 | Deterministic mixed-script/emoji/long/structured fixtures; six fixed seed/mode combinations with operation logs and replay. Every committed snapshot, serialized copy, undo and redo validated; text oracle independent of editor replacement code. |
| P1.4 | Seven [native scripts](NATIVE-BASELINES.md), generated demo fixtures, source HTML, and 21 explicit pending case/target records. Windows host exists but native input/operator access is unavailable; macOS/Linux hosts unavailable. Named QA roles own execution. No native pass or certification is inferred from headless tests. |
| P1.5 | Six realistic workloads x 23 operations = **138 completed benchmark cases**, 2 warmups and 7 recorded repetitions each. Full rendered control and both `Text` binding directions included, plus async native save/load and managed history retention. [Raw archive](baselines/performance/windows-2026-09-26) and [adopted Phase 2 budgets](PERFORMANCE.md#phase-2-budgets). |
| P1.6 | Bounded corpus/API/v1 tests on every CI change; scheduled/manual extended corpus and performance jobs; artifacts for failures, screenshots, native pending records and benchmark samples. [Verification script](../scripts/Invoke-Baselines.ps1) builds, tests, packs and restores an independent consumer through a fresh cache. |

## Automated validation

Local Windows x64, .NET runtime 8.0.31, SDK 10.0.204, Avalonia 12.1.3:

- Release solution build: zero warnings/errors.
- Full suite: **60 passed**, zero failed/skipped, including all existing tests and new contracts.
- Extended corpus: **11 passed** (fixture tests plus all six seed/mode sequences), 2,000 operations per sequence, **12,000 generated operations**. No new product correctness failure was exposed, so there is no discarded failing reproducer.
- Benchmark: **138/138 cases completed**, all input documents valid and below 32 MiB native import size.
- PowerShell evidence generator and budget reporter exercised locally. NuGet package/symbol output and independent consumer are covered by the verification route; final local outcomes are recorded in [verification.json](baselines/verification.json).

Raw TRX and screenshots are generated under `artifacts/` and uploaded by CI; local runs used `artifacts/baselines/tests` and `artifacts/baselines/long-corpus`. These are generated artifacts rather than source fixtures. CI configuration exists for Windows/Linux/macOS; this local report does not claim those remote jobs have executed.

Restricted local build environment: NuGet restore needed approved network access. A fresh `--artifacts-path artifacts/p1-build` and `-p:UseSharedCompilation=false` avoided a compiler-server access error. `-p:UsedAvaloniaProducts=` suppressed an Avalonia build telemetry write outside the workspace; it does not change editor behavior. The clean-checkout commands in README/`Invoke-Baselines.ps1` use the normal output layout. Exact performance sources are recorded by [SHA-256](baselines/performance/windows-2026-09-26/source-hashes.json).

## Measured behavior and disposition

Reference machine: AMD Ryzen 9 5900X, 24 logical processors, approximately 64 GiB available to the runtime, Windows build 26100, High performance power plan. Headless Skia rendering, Inter 16 DIP with OS fallback, Fluent Light, 800 x 500 DIP, scale 1. This was a development workstation, not an isolated lab; OS background activity and ongoing lightweight file/document work were uncontrolled. No test/build run overlapped the full capture. See [environment](baselines/performance/windows-2026-09-26/environment.json) and [measurement caveats](PERFORMANCE.md).

Numbers below are **median / p95 milliseconds**, with seven samples per case. p95 is the largest sample at this sample count. Retained undo is the median paired managed-heap estimate for 100 non-coalesced edits.

| Workload (UTF-16 units) | First viewport | Type in middle, bound | Whole Text update, bound | Resize | Undo MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| 100 paragraphs (9,706) | 80.55 / 91.84 | 5.99 / 7.60 | 9.19 / 9.88 | 30.19 / 37.53 | 0.12 |
| 1,000 paragraphs (97,069) | 83.70 / 93.27 | 7.15 / 27.49 | 65.57 / 91.35 | 189.33 / 220.22 | 0.80 |
| 10,000 paragraphs (970,699) | 776.10 / 923.50 | 46.21 / 72.97 | 924.86 / 1,252.67 | 2,795.89 / 3,782.81 | 7.60 |
| One long paragraph (90,000) | 352.17 / 641.39 | 405.83 / 463.96 | 364.46 / 409.21 | 948.41 / 1,031.64 | 17.03 |
| Table-heavy (20,003) | 58.20 / 64.95 | 15.14 / 32.59 | 37.80 / 71.15 | 140.44 / 175.79 | 10.92 |
| Run-heavy (110,099) | 143.50 / 153.21 | 5.60 / 7.25 | 59.15 / 85.30 | 315.52 / 413.24 | 0.47 |

All start/middle/end, bound/unbound, caret, scroll, replace-all, save/load, allocation and history observations remain in [results.json](baselines/performance/windows-2026-09-26/results.json); the table is not a substitute for slower cases. Of 234 [budget checks](baselines/performance/windows-2026-09-26/budgets.json), **102 exceed targets**: 75/132 latency checks and 27/96 allocation checks. All six median undo-retention observations are below 64 MiB; this does not prove a sustained history byte bound. For example, the 10,000-paragraph first viewport allocates about 134 MiB (median) and middle bound typing about 22 MiB, despite modest retained history.

| Finding | Disposition / owner |
| --- | --- |
| Whole-index/grapheme/tree work and eager Text synchronization scale with document size | P2.1-P2.3, core maintainer. Prototype against the full binding cases before selecting storage. |
| First viewport/resize and long-paragraph edits exceed budgets | P2.4-P2.5, layout maintainer. Retain identical geometry/input fixtures while introducing incremental/viewport work. |
| History is bounded by entry count only; managed estimate omits native resources and future inline content | P2.6, core maintainer. Add sustained eviction, redo retention and resource accounting, then remeasure. |
| Visual bidi navigation is missing | P6.1, input maintainer. N05 native results stay pending; do not call logical caret movement visual support. |
| Full text-range automation and selection announcements are missing | P4.6, accessibility maintainer. Native N07 needs separate value/selection evidence and backend assessment. |
| Native clipboard/IME/dead-key behavior has no execution evidence; rich fragments flatten structure | P4.2/P5.5/P5.6/P6.6, respective platform/input/interchange maintainers; 21 pending native records. |

P2.1 may proceed using D01-D03, the frozen contracts and raw measurements. Phase 2 completion must compare the same workloads and pass adopted budgets (or document an explicit maintainer revision), while native evidence and release decisions remain visible release gates for any advertised scope.
