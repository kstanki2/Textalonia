# Phase 3 implementation and verification

Phase 3 is complete. [Document semantics](DOCUMENT-MODEL.md) specifies the public
contracts, compatibility defaults and structural deletion rules. The six plan
tasks are covered as follows:

| Task | Implementation | Regression evidence |
| --- | --- | --- |
| P3.1 | Schema v2 writer, strict frozen v1 DTO migration, version-first dispatch, authoritative cell blocks | `SchemaEvolutionTests`, unchanged `native-v1.json`, new `native-v2.json`, legacy serializer/corpus tests |
| P3.2 | UI-independent uniform/mixed property aggregation and collapsed typing state; uniform toggles and one undoable command | `SelectionAndListTests`, `SelectionToolbarTests` |
| P3.3 | Identified multilevel lists, definitions, start/restart/continue commands, split/exit/indent/paste/delete policies; model numbering with persistent subtree caches | Numbering, native merge restoration and 4,096-item locality tests in `SelectionAndListTests` |
| P3.4 | Weight/stretch, paragraph tracking/line height/indents, independent container borders/padding, legacy defaults and explicit precedence | `RichFormattingLayoutTests`, `RichFormattingValidationTests`, selection and schema tests |
| P3.5 | Persisted column weights and row policies; merge-aware insertion, deletion, shrinking and promotion | 36 operation-matrix combinations plus collapse, promotion, repeated-edit and exact undo/redo tests in `TableModelTests` |
| P3.6 | Recursive cell blocks and backups, cloning, traversal, validation, index/history retention, innermost-cell session commands and layout | Nested edit/merge/split/structural deletion/native/undo tests, including edited children inside merged anchors and retained nested backups |

## Verification on 2026-09-26

Windows, .NET SDK 10.0.204 targeting net8.0, pinned Avalonia 12.1.3:

- Release solution build: zero warnings and errors.
- Full test suite: **201 passed, zero failed or skipped** (including the original
  107-test baseline and all Phase 2 regression tests).
- NuGet and symbols packages produced successfully.
- Independent package consumer passed compiled XAML, themes, input, formatting,
  schema v2, nested/merged tables, exact undo, range/position APIs, document mode,
  history budgets, shaping limits and rendering. It restored the new package into
  a separate cache using a workspace-local feed of dependency archives.
- Public API snapshot reviewed: additive changes only; existing members remain.
- Frozen v1 fixture is byte-for-byte unchanged. SHA-256:
  `C4ABA5F01389F66C8FE1F8ED33DD733D2D28DBE10011CC6557619A6B75A7FB36`.
- Wider `document-semantics` fixture added to the deterministic corpus; existing
  fixtures were retained. It includes every new wire field and hidden nested data.
- Phase 2 locality checks pass. A new 10,000-paragraph nested-cell case preserves
  existing shapes on an offscreen edit and creates at most 12 geometry nodes.
  A warmed 4,096-item list edit requires at most 60 subtree transition evaluations.

The local build disables Avalonia build telemetry because its default log path is
outside the sandbox; single-node MSBuild avoids shared-worker file contention:

```powershell
dotnet build Textalonia.sln -c Release --no-restore -p:UsedAvaloniaProducts= -m:1 -nr:false
dotnet test tests/Textalonia.Tests -c Release --no-build --no-restore
dotnet pack src/Textalonia -c Release --no-build --no-restore -o artifacts/packages
```

This verifies deterministic correctness, locality and package consumption. It is
not a new native platform or p95 latency qualification run. Existing Phase 2
[PERF-01 follow-up](PERFORMANCE.md#perf-01-residual-latency-qualification) remains.

## Handoff

The pinned text backend supports paragraph-wide letter spacing and line height;
per-run tracking, variable font axes and individual OpenType feature controls are
explicitly deferred. Rich table/style interaction UI belongs to Phase 6.

Native JSON is lossless for the new model. [External codec gaps](PHASE-03-CODEC-GAPS.md)
record current HTML/RTF/DOCX losses, including omitted nested blocks in HTML/DOCX
table export. Structured clipboard extraction remains paragraph-based; Phase 5
owns structural fragment preservation and conversion diagnostics. Native OS
IME/clipboard/accessibility qualification is unchanged by these headless tests.
