# Feature matrix and path to parity

The target is the feature set described by [Avalonia's editor announcement](https://avaloniaui.net/blog/rich-text-editor). This is an independently implemented preview with its own API, data format, and theme. The commercial editor is not a dependency.

| Area | Current preview | Remaining work |
| --- | --- | --- |
| Character formatting | Fonts, weight/stretch, size, emphasis, colors, baseline, links, paragraph tracking/line height, mixed-selection indicators | Per-run tracking and advanced font features |
| Document structure | Paragraphs, headings, identified multilevel lists with restart/continuation, independently styled sections, tables | Per-run typography and native interaction qualification |
| Tables | Nested cell blocks, merge-aware structural edits, split restoration, column widths, row sizing, interactive resize, rectangular selection, borders/padding UI | Native interaction qualification |
| Editing | Visual bidi navigation, timer-driven selection scrolling, structured drag/drop, touch gesture implementation, grapheme deletion, readonly, bounded history, find/replace | Native desktop and device touch qualification |
| IME | Composition client, transient preedit and committed text | Native Windows/macOS/Linux and mobile keyboard qualification |
| Clipboard | Versioned section/table/resource fragments, platform HTML adapter, plain text | Native cross-application qualification on every platform |
| Formats | Native JSON, data XAML, Markdown, text, expanded HTML/RTF/DOCX subsets with strict/tolerant diagnostics | Arbitrary RTF/DOCX fidelity, application-export corpus and native qualification |
| Embedded content | Atomic inline images, registered host controls, immutable resources, native save/load and bounded view caches | External image interchange fidelity; native qualification |
| Display | Viewer mode, light/dark, replaceable theme/toolbar/input components, text highlights, managed text-range contract | Native accessibility text-provider bridges and screen-reader evidence |
| Markdown | [Explicit bounded dialect, async viewer, optional host highlighting](INTEGRATIONS.md) | Broader dialects and native release qualification |
| Scale | Persistent indexes, shared text pieces, windowed viewport shaping, entry/byte-budgeted history, opt-in strict shaping limits | [Residual latency qualification (PERF-01)](PERFORMANCE.md#perf-01-residual-latency-qualification); native latency/working-set qualification |
| Distribution | MIT package metadata, API/schema contracts, source/symbol checks, isolated consumers, retained candidate bundles and explicit publication procedure | Authenticated package control, completed release OS matrix, native/device certification, public publication |

## Open work

The implementation plans and milestone reports have been retired. Current behavior
is documented in the feature guides above; these remaining tasks retain their
original IDs so unfinished qualification is not mistaken for completion.

| ID / original tasks | Remaining work and completion evidence | Responsible role |
| --- | --- | --- |
| PERF-01 / P2.7, P8.2 | Repeat the full editing corpus with 30+ samples across multiple processes, profile residual latency, and pass unchanged targets or record a reviewed support-scope decision. Include nonisolated/startup and native working-set limits. See [performance](PERFORMANCE.md#perf-01-residual-latency-qualification). | Performance/core maintainer |
| PERF-06 / P6 exit gate, P8.2 | Repeat/profile long-paragraph resize and sustained native gestures on a recorded idle host; retain all samples and existing targets. See [interaction latency](PERFORMANCE.md#perf-06-interaction-latency-qualification). | Performance/core maintainer |
| P4.6 | Implement the missing native text-provider/backend bridges, then execute text-range navigation, selection/caret, offscreen bounds, inline/table descriptions and screen-reader checks on each claimed platform. Managed coverage alone does not close [accessibility](ACCESSIBILITY.md). | Accessibility maintainer |
| INT-01 / P5.2, P8.2 | Collect redistributable or locally generated Word/LibreOffice/browser exports with exact application versions, provenance, expected semantics/losses and rendered comparisons. Synthetic specimens remain regression fixtures. | Interchange maintainer |
| INT-02 / P5.4, P5.6, P8.2 | Run N06 in both directions for every advertised [platform/application pair](QUALIFICATION.md#interchange-qualification), including DOCX opening without repair, Unicode clipboard offsets, images, fallback and atomic/stale cut/paste. All pairs remain unqualified. | Desktop/interchange QA |
| MOB-01 / P6.5, P6.6, P8.2 | Finish Android package/launch and iOS build/signing verification; run M01-M07 on emulator/simulator and physical devices with keyboards/readers. Record versions and resolve failures using [mobile procedures](MOBILE-QUALIFICATION.md). | Mobile QA |
| NATIVE-06 / P1.4, P6.6, P8.2 | Execute N01-N10 on each advertised desktop configuration: IME/reconversion, candidate placement after virtualization/resize, clipboard, keyboard/pointer/touch, screen readers and DPI/themes. Preserve explicit unsupported backend capabilities. See [native procedures](NATIVE-BASELINES.md) and [interaction checks](INTERACTIONS.md). | Desktop/accessibility QA |
| P8.2, P8.4 | Run the clean candidate's Windows/macOS/Linux build-test-package matrix, extended fuzz/corpus, workload probes and independent consumers; retain exact source/package/symbol hashes and resolve contradictory support claims. Local Windows results do not complete the matrix. | Release maintainer |
| P8.3 / D04-D06 | Confirm authenticated NuGet package control, contributor copyright/repository authority and dependency terms; select the immutable public version. MIT and package metadata are already present. | Project owner/release maintainer |
| P8.6 | Explicitly publish the reviewed artifacts, verify clean public-feed installation and source/symbol resolution, and record the released version and support scope. Follow [release procedure](RELEASE.md). The preview candidate is not yet published. | Release maintainer |

Per-run letter spacing/advanced font features, broader Markdown dialects, external
image interchange and arbitrary Office fidelity remain outside the documented
preview subset. Expand them only with explicit contracts and regression coverage.

## Completion and release policy

Changes must preserve immutable snapshots, directional UTF-16 selections,
grapheme-safe edits, read-only behavior, undo/redo, caller-owned streams and
cancellation. Deliberate contract changes need migration notes and independent
consumer verification. Model/schema changes need old-file fixtures and round trips;
resource changes need retention/disposal coverage; gestures need the actual control
and shared layout/hit-test geometry.

Headless tests do not qualify native IME, clipboard, touch or screen readers.
Every advertised capability needs evidence tied to its source and package. A
reduced-scope managed preview may defer native/device/application claims, but those
tasks remain open. [Qualification policy](QUALIFICATION.md), [API contracts](API-CONTRACTS.md)
and [release notes](RELEASE-NOTES.md) define the current support boundary.
