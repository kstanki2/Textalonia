# Feature matrix and path to parity

The target is the feature set described by [Avalonia's editor announcement](https://avaloniaui.net/blog/rich-text-editor). This is an independently implemented preview with its own API, data format, and theme. The commercial editor is not a dependency.

| Area | Current preview | Remaining work |
| --- | --- | --- |
| Character formatting | Fonts, weight/stretch, size, emphasis, colors, baseline, links, paragraph tracking/line height, mixed-selection indicators | Richer toolbar controls, per-run tracking and advanced font features |
| Document structure | Paragraphs, headings, identified multilevel lists with restart/continuation, independently styled sections, tables | Inline resources and richer interaction controls |
| Tables | Nested cell blocks, merge-aware structural edits, split restoration, column widths, row sizing, independent cell borders/padding | Interactive resizing and border/padding UI |
| Editing | Native text layout, keyboard, pointer selection, grapheme deletion, readonly, bounded history, find/replace | Visual bidi navigation, drag autoscroll refinement, drag/drop content, touch selection handles |
| IME | Composition client, transient preedit and committed text | Native Windows/macOS/Linux and mobile keyboard qualification |
| Clipboard | Native rich fragments, platform HTML adapter, plain text | Structured table/section fragments; native cross-application tests on every platform |
| Formats | Native JSON, text, supported HTML/RTF/DOCX subsets | Full RTF/DOCX round-trip fidelity, native XAML codec, import-loss diagnostics |
| Embedded content | Atomic inline images, registered host controls, immutable resources, native save/load and bounded view caches | External image interchange fidelity; native qualification |
| Display | Viewer mode, light/dark, replaceable theme/toolbar/input components, text highlights, managed text-range contract | Native accessibility text-provider bridges and screen-reader evidence |
| Markdown | No dedicated implementation | Markdown codec/viewer and optional code highlighting |
| Scale | Persistent indexes, shared text pieces, windowed viewport shaping, entry/byte-budgeted history, opt-in strict shaping limits | [Residual latency qualification (PERF-01)](PERFORMANCE.md#perf-01-residual-latency-qualification); native latency/working-set qualification |
| Distribution | Local NuGet + symbols, docs, tests, CI workflow, package consumer smoke test | Ownership/license metadata and package ID availability, platform certification, public release |

## Implementation phases

The plans below turn the remaining work into ordered deliverables. Phase 1 baseline tooling, fixtures and local automated measurements are implemented; [its report](BASELINE-REPORT.md) records passing checks, budget gaps and pending native evidence. Phase 2 is **complete**, with [residual latency qualification accepted as a follow-up](PHASE2-REPORT.md#completion-decision). Phase 3 is **complete**, with [schema, semantics and verification evidence](PHASE3-REPORT.md); Phase 4 has [implemented input and inline content](PHASE4-REPORT.md), with its native text-accessibility bridge still blocked; phases 5-8 remain **planned**. Task IDs are stable references for future issues and implementation requests; a task may need several focused pull requests. There are no delivery-date commitments until the baseline measurements and design decisions are complete.

| Phase | Outcome and detailed plan | Prerequisites |
| --- | --- | --- |
| 1 | [Qualification and regression baselines](PLAN-01-BASELINES.md): reproducible native checks, document corpus, fuzzing, and performance budgets | None; start here |
| 2 | [Scalable editing core](PLAN-02-SCALABLE-CORE.md): incremental positions, shared text storage, virtual layout, bounded history memory | Phase 1 automated baseline and measured workloads |
| 3 | [Document semantics and tables](PLAN-03-DOCUMENT-MODEL.md): selection formatting state, list identity, typography, nested tables, merge-aware structural edits | Phase 2 edit/index contracts; schema policy from P1.2 |
| 4 | [Extensible input and inline resources](PLAN-04-EXTENSIBILITY.md): replaceable input, serializable resources, images/controls, text accessibility | Phases 2-3 model/layout contracts |
| 5 | [Conversion fidelity and structured clipboard](PLAN-05-INTERCHANGE.md): diagnostics, corpus-driven codecs, structural copy/paste | Phases 3-4 model/resource contracts; corpus work can start in Phase 1 |
| 6 | [Editing and table interactions](PLAN-06-INTERACTIONS.md): visual bidi navigation, resize/style UI, drag/drop, touch, native input qualification | Phases 2-4; structured drop also requires P5.5 |
| 7 | [XAML and Markdown integrations](PLAN-07-INTEGRATIONS.md): data-only XAML, Markdown codec/viewer, optional highlighting | Phases 3-4 and P5.1 diagnostics; can run alongside Phase 6 |
| 8 | [API stabilization and release](PLAN-08-RELEASE.md): compatibility, platform evidence, package metadata, public release | Phases 1-7 exit gates for the advertised release scope |

The default implementation order is 1 through 8. Dependencies permit earlier corpus collection, codec design, and release-metadata work; input polish and integrations can proceed independently once their stated contracts exist. Implementation should follow dependencies, not require every platform investigation to finish before unrelated core work begins.

## Review findings that determine the order

- **Measure the complete editing path.** Phase 1 exposed whole-document indexes, grapheme scans, eager text synchronization and complete shaping. Phase 2 adds persistent indexing/storage and windowed viewport measurement while keeping eager `Text` compatibility explicit. Both compatibility and opt-in document modes have full-control measurements; the optional strict shaping policy and paired performance qualification are documented in the Phase 2 report. Default unlimited rendering and nonisolated timing results remain explicit.
- **Decide schema evolution before extending the model.** The version-1 JSON reader rejects unknown members and other versions. Lists, cell blocks, typography, and resources need explicit reader migration, writer-version, and old-file fixtures. Existing native files must remain readable; older readers must not silently misread newer files.
- **Finish structural semantics before expanding codecs.** Phase 3 adds nested blocks, sizing and merge transformations; rich fragments still flatten structure. Inline resource descriptors follow in Phase 4. Phase 5 can now preserve the richer structures, with current [codec gaps](PHASE-03-CODEC-GAPS.md) explicitly recorded.
- **Extract input behind existing behavior tests.** Keyboard, pointer, caret and IME defaults now run through replaceable components, with the original control behavior tests retained. Native automation remains value-only pending a text-provider bridge.
- **Separate evidence from claims.** The existing CI matrix and headless tests are useful, but do not certify native IME, clipboard, touch, or screen readers. Conversion fidelity must be demonstrated against named fixtures and applications. Full arbitrary RTF/DOCX fidelity remains an aspiration beyond any declared subset; known losses cannot be counted as completed parity.

## Coverage of the remaining work

| Roadmap area | Implementation tasks |
| --- | --- |
| Character formatting | P3.2 mixed selection; P3.4 typography; P6.2 UI |
| Document structure | P3.3 list restart/continuation; P3.4 block styles |
| Tables | P3.5 merge-aware edits; P3.6 nesting; P6.2 sizing/borders/padding |
| Editing | P6.1 bidi; P6.3 drag autoscroll; P6.4 drag/drop; P6.5 touch |
| IME | P1.4 native baseline; P4.2 composition component; P6.6 qualification |
| Clipboard | P5.5 structured fragments; P5.6 native cross-application checks |
| Formats | P5.1 diagnostics; P5.3-P5.4 HTML/RTF/DOCX; P7.1 XAML |
| Embedded content | P4.3 resource rules; P4.4 images; P4.5 host controls |
| Display | P4.1-P4.2 independent input; P4.6 text accessibility; P6.6 native checks |
| Markdown | P7.2 codec; P7.3 viewer; P7.4 optional highlighting |
| Scale | P2.2 indexes; P2.3 storage; P2.4-P2.5 layout; P2.6 history |
| Distribution | P1.1 ownership decisions; P8.1-P8.6 API, metadata, certification, packaging, release |

## Shared completion rules

Each implementation task needs observable behavior, focused regression coverage, and updated user-facing documentation for any changed contract. Preserve immutable snapshots, directional UTF-16 selections, grapheme-safe editing, read-only behavior, undo/redo, caller-owned streams, cancellation, and model use without controls. A deliberate public-contract change needs a migration note and consumer verification.

Model and schema changes must include native round trips and old-version fixtures in the same change. Resource-bearing changes must cover history retention and disposal. Interactive changes must exercise the actual control and shared layout/hit-test geometry. Run the existing build/test/package-consumer checks for code changes; add native evidence and measured performance comparisons where the task requires them. Do not substitute a headless pass for a native check.

Initial qualification targets are Windows, macOS, and Linux, reflecting the current demo and CI. Android/iOS hosts, keyboards, and touch checks remain explicit work in Phases 6 and 8. P1.1 records the intended support tiers; a platform or feature that is deferred must stay visible as an uncompleted roadmap item.

## First implementation batch

1. Complete **P1.1-P1.2**: record support targets, compatibility invariants, and the native schema policy.
2. Complete **P1.3**: add deterministic fixtures and extend the existing randomized replacement test to structured operations.
3. Complete **P1.5**: measure editing, binding, layout, serialization, and history on those fixtures; adopt explicit budgets.
4. Run **P1.4** native scripts and land **P1.6** reporting/CI changes. Track newly discovered defects against the phase that fixes them.
5. Start **P2.1** using that evidence; do not select a storage replacement solely from a benchmark of an isolated data structure.

Phases 1-7 produce reviewable preview milestones. Phase 8 separates release-candidate qualification from public publication. The current package remains suitable for evaluating the API and continuing development; it is not a claim of production parity.
