# Conversion and clipboard contracts

Phase 5 adds a reporting API without changing `IDocumentFormat`. Existing custom
codecs and calls to `Parse`, `Serialize`, `LoadAsync`, and `SaveAsync` remain valid.
Use the reporting methods when an application must show losses or reject them:

```csharp
IDocumentFormat format = DocumentFormats.Docx;
var loaded = await format.LoadWithReportAsync(input, cancellationToken: token);
foreach (var diagnostic in loaded.Report.Diagnostics)
    Console.WriteLine($"{diagnostic.Code}: {diagnostic.UnsupportedFeature}. {diagnostic.Fallback}");

var saved = await format.SaveWithReportAsync(document, output,
    new ConversionOptions { Mode = ConversionMode.Strict }, token);
```

`ConversionDiagnostic` carries a stable code, severity, unsupported feature,
fallback, and a model ID or source location when available. Warnings and errors
count as loss. Reports have deterministic encounter order, deduplicate identical
notices, and cap distinct notices at 1024 with an explicit truncation warning.
Concurrent conversions have independent reports.

Tolerant conversion returns supported content and its report. Strict conversion
throws `DocumentConversionException` with the report whenever fidelity is lost or
unknown. It does not reinterpret parser failures as loss notices: invalid native
models, malformed RTF/packages/XML, resource limits and unsafe package geometry
remain errors. HTML uses the HTML parser's normal tolerant tag repair; HTML is not
validated as XML. Scripts, unsupported elements/styles and remote resources are
reported when omitted. No format executes content or implicitly fetches resources.
Cancellation propagates as `OperationCanceledException`, including cancellation
after a custom codec finishes parsing.

`PlainTextOnly = true` explicitly flattens a successfully parsed document or export
snapshot before further encoding. It preserves visible text and inline alternative
text and reports discarded structure, styles and resources. Combining it with
strict mode rejects actual losses; the informational request notice alone is not
a loss. Native JSON otherwise preserves resource descriptors and embedded bytes,
including references in covered cells and merge backups.

Legacy custom codecs receive `conversion.diagnostics-unavailable` through the
reporting extensions; they still work in tolerant mode and with the original API.
A custom codec can implement `IReportingDocumentFormat` to return its own reports.
Hosts can call the extensions on `IDocumentFormat` to apply the shared strict,
plain-text, cancellation and output-staging policy. Implementers invoking their
reporting methods directly are responsible for honoring the options contract.

Streams remain caller-owned. Reporting exports stage encoding in memory before
copying to the destination, so parser/encoding errors and strict rejection leave
the destination untouched. An I/O failure or cancellation during that final copy
can leave a partial write. For transactional saves, write a temporary file, close
it, then replace the destination. Neither save API truncates a caller's existing
stream automatically. The demo owns and truncates its selected output stream.
A control load checks the captured document revision before replacement, including
reporting loads; a delayed load cannot overwrite intervening edits.

`TextaloniaEditor.LoadWithReportAsync` and `SaveWithReportAsync` expose the same
results. `LastConversionReport` and `ConversionCompleted` surface completed host
and clipboard operations. The demo shows conversion reports after file operations.

## Supported subset and diagnosed losses

All external formats omit hidden physical cells and merge restoration backups with
`conversion.merge-history`. Resources without supported references produce
`conversion.unused-resource` (DOCX also recognizes embedded-font references). Native
JSON v10 preserves the full model and reads v4/v5/v6/v7/v8/v9/v10; earlier versions remain rejected.
Plain text retains visible text/paragraph separators and inline alternative text;
its `text.*` diagnostics describe discarded formatting, containers and resources.

| Feature | HTML | RTF | DOCX |
| --- | --- | --- | --- |
| Lists | Nested lists, identity, levels, start/restart/continuation; model definition metadata | Standard list tables/overrides and ordinary identity/continuation; restart or format changes use a new instance with `rtf.list-instance` | Abstract definitions, instances/overrides, standard formats, starts, restart/continuation; definition/kind changes preserve counters through start overrides |
| Custom markers | Model metadata retained; browser presentation approximations report `html.list-marker` | Supported literal prefixes/suffixes and ancestor slots; unsupported patterns/number formats reported | Supported prefixes/suffixes/ancestor slots; unusual patterns, restart rules, marker fonts/indentation reported |
| Typography | Numeric weights/stretch, font family, baseline, colors, paragraph tracking/line height/indents; inline CSS subset | Common font/color/emphasis/baseline/stretch and paragraph metrics; numeric weights, color alpha and precision have diagnostics | Named/default/character styles, theme references, run tracking/scale/baseline, underline/strike/caps/language, tabs/leaders, outline and paragraph rules; unsupported variants and precision have diagnostics |
| Named styles/themes/fonts | Effective supported appearance; lost identity, overrides, themes and embedded fonts diagnosed | Effective supported appearance; lost identity, overrides, themes and embedded fonts diagnosed | Preserves named style definitions/inheritance/links/next, Office theme slots and permitted embedded TTF/OTF fonts; see [style limits](STYLES.md) |
| Sections | Styled nested sections with edges/padding | Flow section groups; nested sections and arbitrary mixed section/root grouping have diagnosed normalization; decoration omitted with report | Decorative section groups encoded as block content controls; physical sections and header/footer relationships are mapped; unsupported decoration is reported |
| Physical page sections | Native page settings and physical sections have diagnosed losses | Native page settings and physical sections have diagnosed losses | Paper, orientation, margins/gutter, columns, breaks and page numbering; unsupported section properties are diagnosed. See [stories](STORIES.md) |
| Tables | Nested cell blocks, spans, relative columns, rows, cell edges/padding/background | Rectangular grids, horizontal/vertical merges, relative widths, row policies/background; nested table and cell decoration losses reported | Nested tables, grid/vMerge geometry, relative columns, row policies, cell edges/padding/background; named table shading/padding/borders; conditional styles/layout/position and unsupported cell properties reported |
| Images | Bounded data-URI PNG/JPEG/GIF/BMP/WebP raster data; dimensions/alt text and deduplication | Embedded PNG/JPEG; alternative text is not retained by standard picture data and is reported | Supported embedded PNG/JPEG/GIF/BMP/TIFF relationships; dimensions/alt text and deduplication; cropping/rotation/floating placement reported |
| Headers, footers and notes | Story omission diagnosed | Primary/first/even header/footer destinations, footnotes/endnotes, custom marks/settings and page fields; rich-content and section limitations diagnosed | Rich relationship-scoped stories, linked variants, note markers/settings, separators and page fields; unsupported variants diagnosed. See [STORIES.md](STORIES.md) |
| Merge fields | Display text with loss diagnostic | Atomic MERGEFIELD compatibility plus general rich result ranges; unsupported atomic switches and native formatting/fallback options diagnosed | Simple/complex atomic MERGEFIELD compatibility plus general rich result ranges; unsupported atomic switches and linked recipient metadata diagnosed |
| General fields | Cached rich results with loss diagnostic | Standard field instruction/result groups, nested instruction expressions and rich ranges, lock/dirty state and secondary stories; unknown/malformed instructions retained with diagnostics | Simple/complex field import, complex-marker export, nested instruction expressions and rich ranges across paragraphs/stories; unknown/malformed instructions retained with diagnostics |
| Bookmarks | Range omission diagnosed | Standard bookmark start/end destinations; duplicate names renamed with diagnostics | Standard bookmark start/end markers; duplicate names renamed with diagnostics |
| Document properties | Metadata omission diagnosed | String property catalog retained in a Textalonia ignorable destination | String property catalog in standard custom-properties part; typed/core property mapping remains outside this subset |
| Links | Safe absolute http/https/mailto; internal destination omission diagnosed | Safe external HYPERLINK fields and internal bookmark targets with tooltip; activation preference retained in ignorable metadata | Safe external hyperlink relationships and internal bookmark anchors with tooltip; activation preference retained in ignorable metadata |
| Unsafe/unavailable content | Scripts, unknown elements/CSS, relative or unsafe links, remote/unsupported images produce notices; no fetch | Unknown controls/destinations, unsafe fields, unavailable/unsupported images produce notices; no fetch | Revision history, unsupported field evaluation codes, drawings outside subset, unsupported XML properties, unsafe/external images produce notices; no fetch |

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

## Anchored fields, bookmarks and internal links

Native JSON v10 and data XAML v5 retain range identities, story/paragraph anchors,
boundary affinity, instructions, lock/dirty/show-code flags, internal-link metadata
and the string property catalog. Data XAML also reads v1/v2. Existing atomic
`MERGEFIELD` and page-field descriptors remain supported.

A general field's cached result is ordinary rich story content. DOCX and RTF emit
standard field/bookmark markup, including child fields inside nested instructions, so another reader can display cached content and
recognize field instructions and destinations. Textalonia extension metadata retains
range IDs, boundary affinity, native field options and link activation preferences.
DOCX declares these attributes with markup-compatibility `Ignorable`; RTF uses
ignorable destinations. Other applications may drop that metadata. Preservation
in Textalonia does not establish another application's support for every field code.

Import never evaluates fields or fetches external data. Unknown or malformed codes
retain instructions and cached results with `<format>.unsupported-field` diagnostics;
empty instructions retain cached content with a diagnostic and no field metadata.
Unmatched bookmark markers are omitted with a diagnostic. Field nesting is bounded
at 32 and instructions at 16,384 UTF-16 units. Invalid anchors, crossed field ranges,
invalid extension metadata and size limits are rejected during parsing/validation.
Evaluation policies and host resolvers are described in [FIELDS.md](FIELDS.md).

Legacy single atomic MERGEFIELD/page imports retain their earlier result-formatting
limits and diagnostics. General ranged fields preserve multiple run styles and
inline results. RTF foreign merge results containing unsupported block controls
continue to report their legacy fallback. DOCX/RTF property persistence currently
covers string values; it is not a typed built-in/custom-property API.

## Corpus and comparison

The checked-in [manifest](../tests/Textalonia.Tests/Fixtures/Interchange/manifest.json)
and [provenance](../tests/Textalonia.Tests/Fixtures/Interchange/provenance.json)
record locally authored browser-, Writer- and Word-shaped specimens and native
schema 4 input. DOCX tests package the checked-in XML rather than relying on an
opaque binary archive. These specimens were not exported by those applications;
application versions are null and native qualification is explicitly pending.

`InterchangeCorpusTests` asserts exact visible text, declared tree/list/style/link/
image properties, expected diagnostic codes and malformed-input errors. Dedicated
HTML, RTF, DOCX, conversion and fragment tests extend those fixtures to rich lists,
spans, styles, whitespace, image bytes, unsafe inputs and clipping. Native fixtures
cover the current schema; unsupported development versions have rejection tests.

Semantic comparison ignores generated block/cell/inline IDs and ZIP byte order.
It compares list grouping and markers, visible block order, spans/nesting, supported
formatting and resource content. Native save/load additionally requires canonical
native serialization equality, including identities and hidden merge state.
Application-rendered comparison is a separate gate: open exported files without a
repair prompt, compare layout and text, and exercise clipboard transfer in both
directions in a recorded application/version. A semantic test pass is not visual
or desktop interoperability evidence.

## Fragment transfer

`DocumentFragment` version 6 (readers also accept v1/v2/v3/v4/v5) contains a native document and paragraph boundary
flags. `EditorSession.CopyFragment`, `CopyCells`, and `InsertFragment` work without
a control, and can also be used by a future drag/drop adapter. `CopySelection` and
`InsertDocument` remain available as document-based compatibility APIs.

Text selections retain section shells and clip first/last paragraph runs. Table
selection bounds expand to a rectangle and then to include every intersecting
merged cell. `CopyCells` takes explicit table coordinates and expands across merged
owners. Whole selected cells retain nested blocks and merge restoration data;
partially selected merged contents discard unselected hidden text and backups.
Unused resources are pruned from the resulting fragment. Bookmarks clip to the copied
range; a general field is copied only when its complete cached result is included.
Paste remaps range and content identities, resolves bookmark-name conflicts, and
rewrites internal links and reference-field instructions for renamed destinations.

Insertion targets the innermost destination block container. It clones block,
cell, inline and list identities, including hidden cells/backups. Each paste gets
new list identities; incoming lists do not implicitly continue a destination list.
Resource-key collisions with different content receive new keys, while identical
resources can be shared. Inside a destination paragraph, complete incoming paragraph
edges remain separate and clipped edges merge with the existing text. At destination
paragraph boundaries, edges merge for compatibility. `InsertDocument` and legacy
native payloads retain the previous paragraph merging policy. Structural blocks get
an editable paragraph after insertion when needed. Each paste and cut commits one
undo operation. Selection replacement and cut retain the established deletion policy:
selections covering only part of the document keep destination table/section shells,
including empty cells, even when all text in a container is selected. Replacing the full
document replaces its complete block tree without adding an outer paragraph; the caret lands in its final visible paragraph. A subsequent paste therefore targets that final paragraph's container, which can be a nested cell.

The clipboard writes a versioned native flavor, a legacy native document flavor,
HTML and text. Reading tries versioned native, legacy native, HTML, then text.
Rejected flavors contribute `clipboard.*` diagnostics and fall through; unsupported
future fragments cannot partially load. Older document envelopes receive default
boundary behavior. Copy reports describe losses in the HTML fallback flavor; their
fallback messages begin with `HTML fallback:`. These notices do not mean the native
fragment lost the corresponding data. CF_HTML offsets count UTF-8 bytes, including non-ASCII text;
invalid offsets/encoding are rejected before HTML import.

Asynchronous clipboard reads/writes retain document revision and selection checks.
Cut deletes only after the clipboard write succeeds and the original revision and
selection still match. Paste similarly refuses a stale target. Clipboard failures
propagate from direct asynchronous calls; command-bound failures raise `OperationFailed`.
Completed conversion and fallback reports are available through `LastConversionReport`
and `ConversionCompleted`.

## Native qualification

No Word, LibreOffice or browser/application pair is promoted by the synthetic
corpus. Run N06 in [native procedures](NATIVE-BASELINES.md) with exact OS/application
versions, copied native/HTML/text flavors, non-ASCII CF_HTML, images, rejected native
payloads, paste fallback, and atomic undo/cut cases. Record both directions and
export opening/repair results. [Qualification](QUALIFICATION.md) lists the still
unqualified platform/application pairs. Full arbitrary HTML/RTF/DOCX compatibility
is outside the declared subset.

DX-06 table/list format mappings and precise loss boundaries are listed in the
[table/list guide](TABLES.md#persistence-and-evidence).

## DX-07 pictures, watermarks and OLE

Native v10, XAML v5 and clipboard v6 preserve placement, crop/rotation, supplied
previews, package resources and section watermarks. DOCX maps image anchors/wraps,
crop/rotation and OLE image/package relationships; section watermarks are visible
VML header content with Textalonia metadata for reconstruction. Original image
bytes and separately supplied previews are retained independently.

Unsupported external features report `conversion.image-placement`,
`conversion.image-preview`, `conversion.watermark` and `conversion.ole`. Positioned
OLE metadata survives DOCX but its external preview displays inline, reported as
`docx.ole-placement`. Unsupported import positioning/mirroring/wrap distances also
produce specific `docx.image-*` diagnostics. Strict conversion rejects diagnosed
loss before writing. See [image support and limits](IMAGES.md).
