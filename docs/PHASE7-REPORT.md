# Phase 7 integration report

Phase 7 adds the Textalonia data XAML codec, the explicit Textalonia Markdown
dialect, an asynchronous Markdown viewer, optional presentation-only code
highlighting, and executable host/consumer examples. All integrations remain in
the main package without new parser, XAML loader, or highlighting dependencies.

## Delivered contracts

- **P7.1:** `XamlDocumentFormat` uses `urn:textalonia:document:1`, `.txaml`/`.xaml`,
  explicit whitelisted model mappings, XML size/depth/node bounds and prohibited
  DTD processing. Resources, descriptors, styles, lists, nested/merged tables and
  restoration history round-trip as data. XML 1.0 character restrictions are
  documented; this is not another editor's XAML vocabulary. See [XAML](XAML.md).
- **P7.2:** `MarkdownDocumentFormat` supports documented headings, paragraphs,
  emphasis, safe links, nested single-paragraph lists, quotes, inline/fenced code,
  and supported image references. Tables/task lists are explicitly disabled with
  diagnosed literal fallbacks. Code language and quote/inline-code annotations
  persist in native JSON v4; frozen v1-v3 readers remain supported. See the
  [dialect and fixture](MARKDOWN.md).
- **P7.3:** `MarkdownViewer` exposes source, parsing state, errors and reports,
  coalesces bursts, parses on workers, and rejects stale results. It retains
  selection and viewport anchors, immutable blocks and measured index branches.
  Host link/resource services and the existing managed accessibility contract are
  inherited. See [viewer lifecycle](MARKDOWN-VIEWER.md).
- **P7.4:** `ICodeHighlighter` maps bounded UTF-16 tokens to optional style overrides
  in a private presentation snapshot, with a bounded cache and separate errors.
  Unknown languages need no adapter, malformed adapters leave code readable, and
  copied/exported text remains canonical. No third-party language engine was
  selected, so no additional redistribution dependency is introduced.
- **P7.5:** the demo's **Markdown / XAML** window includes editable source, XAML
  import/export, highlighting, diagnostics, and a restricted host image resolver.
  Main file dialogs include both formats. The independent NuGet consumer uses
  compiled Markdown viewer XAML, both codecs, diagnostics, selection, appends,
  accessibility text and a host-provided highlighting adapter.

## Verification

On 2026-09-27 the Release solution build passed with zero warnings/errors; the
full test suite passed **502 tests**, with zero failures/skips. Local NuGet and
symbol packing passed, and the independent package consumer restored into a fresh
cache and completed all checks. The restore required normal NuGet user-config
access outside the sandbox; code tests/builds ran with Avalonia telemetry disabled.
See the [command and artifact receipt](baselines/phase7-verification.json).

Final source-to-frame p95 values were **33.02/41.45 ms** for 100-paragraph
edits/appends and **51.62/38.87 ms** for 1,000 paragraphs, against 100 ms.
Dispatcher-delay p95 stayed below **1.99 ms**, against 16 ms. All measured scroll
drift was zero and both revision bursts preserved the latest content/selection.
The [rendered integration demo](baselines/performance/windows-2026-09-27-phase7-markdown/integration-demo.png)
was inspected after checking its actual host-resolved image and code font.

The automated checks cover strict rejection before output, resource and link
policies, inert XAML control/markup data, maximum nesting, escaping/fences,
headings and code typography, legacy schemas, stale asynchronous completion,
selection, viewport retention, adapter failures/cache limits, and the integration
demo's actual rendered frame. The public API snapshot includes the new types,
properties and native metadata.

The [dated update probe](baselines/performance/windows-2026-09-27-phase7-markdown/README.md)
records all samples and source identity. It measures 100/1,000 body paragraphs plus
a heading, 40 updates each for suffix edits and streamed appends, and 30-revision
bursts. Adopted p95 budgets are 100 ms source-to-frame and 16 ms independent UI
callback delay, with at most 1 DIP scroll drift. These are bounded managed
workload claims; they do not replace native compositor or device qualification.

During implementation, the first regression run caught selection-boundary and
scroll-estimate failures. The fixes preserve surviving selection suffixes and
reuse immutable index branches instead of restoring a forced pixel offset.
Review also identified list identity reconciliation, reshaped-cell coordinate
reuse, and highlighted-snapshot reuse cases; dedicated regressions cover them.

## Phase 8 handoff and limits

See [integration boundaries and migration](INTEGRATIONS.md) for examples,
threading/cancellation behavior, native v4 migration and dependency policy.
The public snapshot is `tests/Textalonia.Tests/Fixtures/public-api.txt`, and the
clean consumer remains `tests/Textalonia.PackageSmoke`. The core package continues
to function with the optional adapter unset.

The Markdown parser is a documented bounded dialect, not full CommonMark/GFM.
Native screen-reader text-provider limitations, native clipboard/IME/mobile
qualification and earlier PERF-01/PERF-06 tasks remain open in their existing
reports. This integration work does not claim to complete those release gates.
