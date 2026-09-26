# Phase 1: Qualification and regression baselines

[Roadmap and dependencies](ROADMAP.md#implementation-phases)

**Outcome:** a reproducible account of current behavior, known defects, and performance before changing the engine. **Entry:** none. **Status:** baseline implementation delivered; native execution pending by target (see [report](BASELINE-REPORT.md)).

## Starting points

- [Editing tests](../tests/Textalonia.Tests/EditingTests.cs), including the existing randomized plain-text replacement test.
- [Control tests](../tests/Textalonia.Tests/ControlTests.cs) and [serialization tests](../tests/Textalonia.Tests/SerializationTests.cs).
- [CI](../.github/workflows/ci.yml), [architecture](ARCHITECTURE.md), and [package metadata](../src/Textalonia/Textalonia.csproj).

## Tasks

- [x] **P1.1 - Record product and qualification targets.** Create `docs/QUALIFICATION.md` with desktop OS/backend versions, IMEs, source applications, screen readers, and planned mobile hosts. Record supported, experimental, and untested statuses separately. Add a decision register with responsible maintainer roles for support scope, license/ownership, repository URL, and package ID availability. Check release identity early; selecting project ownership/license remains a maintainer decision. **Done when:** each target has a reproducible test route and unresolved decisions have an owner role and required phase.

- [x] **P1.2 - Capture compatibility invariants.** Add `docs/COMPATIBILITY.md` covering immutable snapshots, unique IDs, UTF-16/LF/U+2028 coordinates, directional selection, graphemes, merge restoration, undo grouping, read-only rules, stream ownership, cancellation, and concurrent loads. Specify version-dispatched native readers, migration of v1 fixtures, and rejection of unsupported newer schemas. Capture the existing public surface before proposing replacements. **Done when:** each invariant points to an existing test or an explicit missing test, and schema changes have a documented migration procedure.

- [x] **P1.3 - Build deterministic correctness fixtures.** Add `tests/Textalonia.Tests/Fixtures/` for mixed scripts, emoji, long paragraphs, sections, styled runs, lists, and merged tables. Extend randomized edits to formatting, insert/delete, merge/split, serialization, and undo/redo; keep a simple independent text oracle where applicable. Record seed and operation log, validate every generated snapshot, and retain a small reproducer for each failure. **Done when:** CI repeats fixed seeds and verifies that undo restores both content and selection; opt-in longer runs produce replayable failures.

- [x] **P1.4 - Establish native behavior baselines.** Write executable/manual scripts for CJK composition, cancellation, candidate-window positioning after scroll, dead keys, mixed RTL/LTR navigation, native rich clipboard, and screen-reader value/selection announcements. Include font/DPI/theme settings and exact expected behavior. Execute on available desktop targets and record unavailable targets as pending with an execution owner; record missing text-range automation as a Phase 4 gap. **Done when:** every case has evidence or an explicit pending entry, and reproducible failures link to corrective tasks without being marked certified.

- [x] **P1.5 - Measure realistic workloads and set budgets.** Add a benchmark project under `benchmarks/` and `docs/PERFORMANCE.md`. Generate 100, 1,000, and 10,000 paragraph documents, a long single paragraph, and table/run-heavy documents within current validation limits. Measure first viewport, typing/deletion at start/middle/end, caret movement, bound/unbound `Text` updates, scroll/resize, replace-all, save/load, allocations, and retained undo memory. Capture environment, warmup, repetitions, median/p95, and raw results. **Done when:** measurements are reproducible and explicit latency/memory budgets are adopted for Phase 2; results are not advertised as universal hardware guarantees.

- [x] **P1.6 - Make evidence repeatable.** Extend CI artifact collection for fixture failures, native checklists, and benchmark output; run a bounded deterministic suite on every change and schedule/trigger longer corpus and performance runs separately. Fix regressions in already-supported behavior before changing architecture, and assign missing features to later task IDs. **Done when:** a clean checkout can build, test, pack, and run the independent package consumer using documented commands, with failures and pending platform evidence visible.

## Exit gate

- The existing suite and new deterministic checks pass on available runners; any newly exposed correctness defect has a reproducer and an explicit disposition.
- Baseline performance and adopted budgets exist, including the full control/binding path and history memory.
- Native results distinguish pass, fail, and pending. Known missing features such as full text automation do not block core work, but remain release blockers for any scope that advertises them.
- Support and schema decisions needed by Phase 2 are recorded. License and package ownership may remain open until Phase 8.

## Handoff

Implement P1.1-P1.3 first, then P1.5; P1.4 can proceed independently. Deliver the baseline report and fixtures to P2.1. Avoid adding new product features or replacing storage in this phase.
