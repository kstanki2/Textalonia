# Phase 5 implementation report

The conversion/reporting API, expanded codec subsets, structured fragments, demo
reports, and automated interchange corpus are implemented. Native application
qualification remains open; this report does not claim arbitrary Office/browser
fidelity or completion of the application evidence gate.

| Task | Implemented result | Evidence / remaining gate |
| --- | --- | --- |
| P5.1 | Additive result/options/report API, tolerant/strict modes, explicit plain text, legacy custom codec compatibility, host/demo reports, staged strict export | ConversionDiagnosticsTests; original IDocumentFormat signatures retained |
| P5.2 | Locally authored browser-, Writer-, Word-shaped and native specimens with provenance, expected semantics/loss codes, malformed cases | InterchangeCorpusTests and dedicated codec tests; actual versioned Word/LibreOffice/browser exports and rendered comparisons remain INT-01 |
| P5.3 | Rich HTML lists/styles/nesting/resources and expanded RTF lists/links/sections/tables/images, with bounded parsing and deterministic losses | HtmlInterchangeTests, RtfInterchangeTests, SerializationTests; supported subset below |
| P5.4 | DOCX numbering/overrides/inheritance, section groups, nested tables/merges/sizing/edges, image relationships and typography | DocxInterchangeTests; real Word/LibreOffice opening without repair remains INT-02 |
| P5.5 | Versioned fragments, clipped containers and merged rectangles, identity/list/resource remapping, native/HTML/text fallback, stale cut/paste protection and atomic undo | ClipboardFragmentTests, ClipboardControlTests, existing input/selection/editing suites |
| P5.6 | CF_HTML byte offsets, rejection/fallback reports, resource transfer and host events tested; support matrix and qualification records updated | Automated adapter evidence only; all native platform/application pairs remain INT-02 |

## Supported subset and diagnosed losses

All external formats omit hidden physical cells and merge restoration backups with
`conversion.merge-history`. Resources without visible image references produce
`conversion.unused-resource`. Native JSON schema 3 preserves the full current model,
including these values; frozen schema 1 and 2 inputs still migrate explicitly.
Plain text retains visible text/paragraph separators and inline alternative text;
its `text.*` diagnostics describe discarded formatting, containers and resources.

| Feature | HTML | RTF | DOCX |
| --- | --- | --- | --- |
| Lists | Nested lists, identity, levels, start/restart/continuation; model definition metadata | Standard list tables/overrides and ordinary identity/continuation; restart or format changes use a new instance with `rtf.list-instance` | Abstract definitions, instances/overrides, standard formats, starts, restart/continuation; definition/kind changes preserve counters through start overrides |
| Custom markers | Model metadata retained; browser presentation approximations report `html.list-marker` | Supported literal prefixes/suffixes and ancestor slots; unsupported patterns/number formats reported | Supported prefixes/suffixes/ancestor slots; unusual patterns, restart rules, marker fonts/indentation reported |
| Typography | Numeric weights/stretch, font family, baseline, colors, paragraph tracking/line height/indents; inline CSS subset | Common font/color/emphasis/baseline/stretch and paragraph metrics; numeric weights, color alpha and precision have diagnostics | Common inherited/default/character styles, effective bold, baseline/colors and paragraph metrics; numeric weights/stretch, theme-only styles, per-run spacing and precision have diagnostics |
| Sections | Styled nested sections with edges/padding | Flow section groups; nested sections and arbitrary mixed section/root grouping have diagnosed normalization; decoration omitted with report | Section content groups encoded as block content controls; page sections/layout/header/footer and decoration reported |
| Tables | Nested cell blocks, spans, relative columns, rows, cell edges/padding/background | Rectangular grids, horizontal/vertical merges, relative widths, row policies/background; nested table and cell decoration losses reported | Nested tables, grid/vMerge geometry, relative columns, row policies, cell edges/padding/background; table style/layout/position and unsupported cell properties reported |
| Images | Bounded data-URI PNG/JPEG/GIF/BMP/WebP raster data; dimensions/alt text and deduplication | Embedded PNG/JPEG; alternative text is not retained by standard picture data and is reported | Supported embedded PNG/JPEG/GIF/BMP/TIFF relationships; dimensions/alt text and deduplication; cropping/rotation/floating placement reported |
| Links | Safe absolute http/https/mailto | Safe HYPERLINK field results | Safe external hyperlink relationships |
| Unsafe/unavailable content | Scripts, unknown elements/CSS, relative or unsafe links, remote/unsupported images produce notices; no fetch | Unknown controls/destinations, unsafe fields, unavailable/unsupported images produce notices; no fetch | Revision history, dynamic fields, drawings outside subset, unsupported XML properties, unsafe/external images produce notices; no fetch |

HTML metadata preserves model semantics that CSS cannot exactly render, notably
custom markers and exact row-height policy. These are still reported as browser
presentation approximations. RTF/DOCX dimensions can quantize to twips, half points,
or format-specific units; changed values are reported. Generated model IDs and
resource keys are not external-format identity guarantees.

Tolerant conversion is bounded, not a promise to parse every valid extension of
these formats. Malformed geometry, invalid encoding/base64, excessive nesting or
resource sizes, and malformed packages/XML remain errors. UTF-8 text input rejects
invalid byte sequences; RTF honors declared supported code pages and groups adjacent
encoded bytes before decoding. No parser executes embedded content. See
[the API and clipboard contracts](INTERCHANGE.md) for stream ownership, cancellation,
strict rejection, boundary merging, destination shells and fallback order.

## Corpus coverage

The [manifest](../tests/Textalonia.Tests/Fixtures/Interchange/manifest.json) defines
positive and negative locally authored specimens. The adjacent provenance record
identifies generator version 1 and leaves source application/version null. No
third-party document content is embedded. Historical native fixtures and dedicated
codec/fragment cases add coverage as follows:

| Area | Positive assertions | Negative / loss assertions |
| --- | --- | --- |
| Lists | Shared identity, restart/continuation, multiple levels, formatted markers, starts | Missing definitions, unsupported patterns/formats, orphan markers, invalid counters, Word numbering changes |
| Sections/tables | Nested blocks, merge geometry, restored native backups, relative columns, row policies, independent edges | Malformed/overflowing grids, clipping of unselected hidden contents, unsupported nested/decoration mappings |
| Typography/links/whitespace | Inherited/default styles, safe targets, soft breaks, preserved whitespace and collapsed browser spans, multiple encodings | Unknown CSS/properties, unsafe links, numeric range/precision, theme-only styles, malformed multibyte encodings |
| Images/resources | Embedded bytes, dimensions, deduplication, native descriptors, collision-safe repeated paste | External/missing/unsupported sources, invalid base64, limits, ZIP size lies, host-control alternative text |
| Parser/report policy | Native strict round trips, legacy and reporting custom codecs, independent concurrent reports | Cancellation, malformed native/RTF/XML, strict output untouched, I/O partial-write propagation |
| Clipboard | Whole/partial containers, rectangular merge closure, nested resource transfer, one-step undo | Duplicate/future payload rejection, UTF-8 CF_HTML offsets, stale/failed cut and stale paste |

Semantic comparisons are separate from native byte-equivalence and application
visual comparison. Exact application-produced corpus coverage is intentionally not
inferred from these synthetic specimens.

## Open qualification work

- **INT-01 — Application corpus:** collect redistributable or locally generated
  exports from exact installed Word, LibreOffice and browser versions; attach
  provenance, expected model/loss reports, and application-rendered comparisons.
  Owner: interchange maintainer; gates P5.2 and release scope P8.2.
- **INT-02 — Native interoperability:** run N06 both directions for every advertised
  Windows/macOS/Linux pair, including DOCX opening without repair, native/HTML/text
  clipboard priority, non-ASCII offsets, images, fallback and stale/failed cut.
  Owner: desktop QA; gates P5.4/P5.6 and P8.2. All pairs remain explicitly unqualified
  in [QUALIFICATION.md](QUALIFICATION.md).

These gaps do not block unrelated Phase 6 model/interaction work. There is no claim
of full arbitrary RTF/DOCX compatibility, and no fabricated application version,
native execution result, or repair-free opening result.

## Verification

Verification on 2026-09-26 in the Windows workspace:

- Release solution build: **passed**, zero warnings/errors.
- Complete Release suite: **368 passed**, zero failures/skips. Receipt:
  `artifacts/test-results/phase5-release.trx`.
- NuGet and symbols packages: created in `artifacts/packages`.
- Independent package consumer: **passed**, restored into the fresh
  `artifacts/phase5-consumer-cache` using the new local package and an offline
  dependency feed assembled from the existing cache. Exercises strict native
  conversion, loss reports and repeated structural fragments in addition to
  compiled XAML/theme, editing, rendering and prior preview contracts.
- Public/protected API baseline: reviewed and updated additively; **zero removed
  declarations**. Native schema and frozen v1/v2 fixtures are unchanged.
- Whitespace/diff checks: passed.

Build/test commands use `-p:UsedAvaloniaProducts=` to avoid Avalonia telemetry writes
outside the workspace. The independent restore used a workspace APPDATA/CLI
profile and explicit offline NuGet configuration; NuGet audit was disabled for
that offline restore. These local verification settings do not alter the package
or shipped project settings. The SDK is 10.0.204 targeting .NET 8 and Avalonia
12.1.3. No native desktop/application pair was qualified by these checks.
