# DevExpress WPF Rich Text Editor: scoped implementation plan

Research date: **2026-09-27**. Textalonia baseline: commit
`97e8573ef362197f33b97ae27667f5ee52b79508`, the unpublished `0.1.0-preview.1`
candidate. Status: **implementation plan; DX-01 through DX-07, DX-09 and DX-10 implementation and
qualification status is recorded below. Other workstreams remain proposed unless explicitly noted.**

The audit tables in sections 2-3 describe the baseline commit above; the DX-01
implementation entry and [style guide](STYLES.md) supersede those baseline findings
for delivered style/typography capabilities. The DX-02 implementation entry and
[pagination guide](PAGINATION.md) supersede baseline findings for physical pages
and document views. The DX-03 implementation entry and [story guide](STORIES.md)
supersede baseline findings for headers, footers and notes. The DX-04 implementation
entry and [output guide](OUTPUT.md) supersede baseline findings for print preview,
host printing and basic PDF export; conformance and native qualification remain open.
The DX-05 implementation entry and [field/navigation guide](FIELDS.md) supersede
baseline findings for bookmarks, general fields, contents and navigation.
The DX-06 implementation entry and [table/list guide](TABLES.md) supersede baseline
findings for table layout, conditional styles, table pagination and marker formatting.
The DX-07 implementation entry and [image/object guide](IMAGES.md) supersede baseline
findings for images, watermarks and OLE previews. The DX-09 implementation entry
and [forms/protection guide](FORMS.md) supersede baseline findings for protected
editing and structured form controls. The DX-10 implementation entry and
[proofing guide](PROOFING.md) supersede baseline findings for spelling,
AutoCorrect and hyphenation.

The intended reference is [DevExpress WPF Rich Text Editor / RichEditControl][dx-home].
Textalonia remains an independent Avalonia control. The goal is comparable behavior
for the selected document features, with its existing immutable model and public API
as the starting point. This corrects the earlier Avalonia commercial-editor target.
A WPF port or DevExpress binary/API compatibility is not assumed.

**Scope exclusions:** ActiveX, VBA/macros, charts, and drawing shapes are not required.
Shape tools include text boxes, connectors, grouping, drawing canvases and WordArt.
These features are excluded from implementation, dedicated preservation support,
UI, dependencies and release gates; they are not deferred milestones. DOCM/DOTM
support is also removed from the format backlog because macro support is excluded.

Comments/replies and tracked revisions are outside the current plan. DX-08 has been
removed, including its model, UI, preservation and release requirements. They can be
reconsidered in a future scope revision, but are not scheduled milestones or dependencies.

Images (including placement/cropping), text/image watermarks and OLE previews remain
in scope. They should use focused image/resource APIs without a general shape engine.
Importing excluded content should produce a clear unsupported-content diagnostic;
tolerant conversion may use an already available preview or omit it, while strict
conversion rejects the loss. No preview generator or round-trip guarantee is required
for excluded content. The scope applies to every workstream and source comparison below.

The largest gaps are a real page engine, multiple document stories, reusable styles,
general fields, printing/PDF, and protected forms. These require coordinated
model, editing, layout, serialization, and UI changes. Adding toolbar buttons alone
cannot close them.

## 1. Research scope and confidence

The comparison uses the official WPF documentation and its [feature matrix][dx-features],
as served during research under the **26.1** documentation selector. Individual topics
have different update dates, including older pages. Dedicated feature topics take
precedence over broad overview wording. Shared RichEdit API pages are used only where
the WPF documentation links to that API.

Repository findings come from source, existing tests, and the current contract guides.
This was a static audit, not a fresh runtime qualification or a side-by-side test of a
licensed DevExpress installation. Native interaction, Office rendering, and performance
claims still need the evidence described in [qualification](QUALIFICATION.md).

Status terminology:

| Status | Meaning |
| --- | --- |
| Existing foundation | Implemented behavior that should be retained and extended; no complete parity claim. |
| Partial | Some of the feature exists, but important semantics, UI, or interchange are absent. |
| Missing | No corresponding first-class feature was found in the current model/control/codecs. |
| Limited reference support | DevExpress itself supports only certain operations or requires another component. |

### Important boundaries in the reference product

- DevExpress exposes separate import/export, rendering, API, and UI capabilities.
  A preserved object is not necessarily editable. Equations are interchange-only;
  SmartArt, signature lines and Quick Parts are unsupported in the reference matrix.
  [Feature matrix][dx-features]
- Content controls are interactive, but DevExpress supplies no authoring toolbar or
  property dialogs for them. Its creation API covers six types; picture, repeating
  section, and building-block-gallery types have no creation method in that table.
  PDF export does not turn them into PDF forms. [Content controls][dx-controls]
- OLE objects display a preview; users cannot activate or edit their embedded files.
  [OLE][dx-ole]
- The overview separately identifies async document-server operations, document
  comparison, Word digital signing, and page-to-image export as Office File API
  capabilities. They are a possible later server/tooling track, not prerequisites
  for WPF control parity. Textalonia already has asynchronous codec APIs.
  [Product overview][dx-home]

## 2. What we already have

| Evidence | Current implementation and implication |
| --- | --- |
| [FlowDocument.cs](../src/Textalonia/Model/FlowDocument.cs), [Blocks.cs](../src/Textalonia/Model/Blocks.cs) | Immutable paragraphs, nested decorative sections and tables, stable block/cell IDs, inline resources. There are no page sections or independent header/note stories. |
| [TextStyle.cs](../src/Textalonia/Model/TextStyle.cs), [ListNumbering.cs](../src/Textalonia/Model/ListNumbering.cs) | Direct character/paragraph formatting, heading levels, bidi paragraphs, nine-level identified lists, starts/restarts and marker definitions. `TextStyle` and `ParagraphStyle` are value records, not a named document style catalog. |
| [EditorSession.cs](../src/Textalonia/Editing/EditorSession.cs), [DocumentTree.cs](../src/Textalonia/Model/DocumentTree.cs) | Persistent indexed edits, grapheme-safe UTF-16 selection, undo/redo, read-only, literal find/replace, structural editing. `CreatePosition` handles deliberately expire on revision changes; they cannot serve as durable bookmark anchors. |
| [DocumentLayout.cs](../src/Textalonia/Controls/DocumentLayout.cs), [ParagraphLayout.cs](../src/Textalonia/Controls/ParagraphLayout.cs) | Virtualized flow layout and bounded shaping caches. The internal `ParagraphLayout.Page` is a shaping window, not a sheet of paper. Existing navigation, hit testing and IME geometry are valuable foundations. |
| [TextaloniaEditor.Tables.cs](../src/Textalonia/Controls/TextaloniaEditor.Tables.cs), [DocumentLayout.Tables.cs](../src/Textalonia/Controls/DocumentLayout.Tables.cs) | Nested/merged tables, row policies, relative column widths, cell styling, rectangular selection and interactive resizing already exist. Preserve the covered-cell and merge-restoration rules. |
| [InlineContent.cs](../src/Textalonia/Model/InlineContent.cs), [inline guide](INLINE-CONTENT.md) | Atomic images and registered Avalonia controls, resources and bounded view lifecycles. Structured content controls, image placement and OLE previews still need dedicated semantics. |
| [MailMergeProcessor.cs](../src/Textalonia/MailMerge/MailMergeProcessor.cs), [mail merge contract](MAIL-MERGE.md) | Named atomic fields, preview, culture-aware text formatting, missing-value policies and lazy per-record output. This is a useful merge foundation, not a general field language or master-detail report engine. |
| [DocxDocumentFormat.cs](../src/Textalonia/Serialization/DocxDocumentFormat.cs), [interchange matrix](INTERCHANGE.md#supported-subset-and-diagnosed-losses) | Existing HTML/RTF/DOCX subsets and strict/tolerant reports. DOCX explicitly reports page-layout and revision losses; style inheritance is resolved into direct formatting. Native JSON v4 and data XAML preserve the current model. |
| [TextaloniaToolbar.cs](../src/Textalonia/Controls/TextaloniaToolbar.cs), [Generic.axaml](../src/Textalonia/Themes/Generic.axaml), [DocumentAccessibility.cs](../src/Textalonia/Controls/DocumentAccessibility.cs) | Formatting flyouts, basic commands, search, standard edit context menu, themes and managed text ranges. There is no paginated shell/ribbon, and `HasNativeTextPattern` is currently false. |

Existing regression evidence includes `RichFormattingLayoutTests`, `TableModelTests`,
`TableInteractionTests`, `BidiNavigationTests`, `ScalableCoreTests`, `ScalableLayoutTests`,
`DocxInterchangeTests`, `MergeFieldInterchangeTests`, `MailMergeTests`, and
`NativeSchemaTests` under [tests/Textalonia.Tests](../tests/Textalonia.Tests).
In particular, `DocxInterchangeTests.External_images_revisions_and_page_layout_are_reported_without_fetching`
records the current Office losses. Tests named `*Review*` are code-review regressions,
not an implemented document-review feature.

## 3. Feature gaps

Workstream IDs point to the implementation sections below. A row closes only at its
specified support level: model/API, UI, rendering and each advertised format are
separate acceptance dimensions.

Existing IDs are retained for traceability; G16 was removed with chart support,
and G17/G18 with comments and tracked revisions. Workstream DX-08 is also retired.

| Gap | Reference capability | Textalonia status and missing behavior | Workstreams |
| --- | --- | --- | --- |
| G01 | [Page/section layout][dx-sections] | **Missing.** Paper size, orientation, margins/gutter, mirrored pages, columns, page/column/section breaks and per-section numbering. Current `Section` is a nested content group. | DX-00, DX-02 |
| G02 | [Print, Draft and Simple views][dx-views]; [zoom API][dx-view-api] | **Partial.** Flow view exists; add page geometry, page-width draft view, zoom/fit and page navigation. | DX-02, DX-13 |
| G03 | [Headers and footers][dx-headers] | **Missing.** Separate editable content, first/odd/even variants, distances and linkage across sections. | DX-00, DX-03 |
| G04 | [Footnotes and endnotes][dx-notes] | **Missing.** Reference marks, note stories, numbering, placement and pagination. | DX-03 |
| G05 | [Print/preview][dx-print]; [PDF export][dx-pdf] | **Missing.** Output layout, print preview/dialog/service, PDF text/fonts/links and tagged/archival output profiles. | DX-04 |
| G06 | [Named and linked styles][dx-formatting] | **Partial.** Direct formatting and imported effective styles exist; no editable style definitions, inheritance graph, linked styles or live propagation. | DX-01 |
| G07 | [Advanced typography][dx-features]; [themes/embedded fonts][dx-formatting] | **Partial.** Missing rich underline types/colors, double strike, caps/small caps, run spacing/scaling/position/kerning controls, language/no-proof metadata, theme references and embedded-font handling. | DX-01 |
| G08 | [Paragraph/page settings][dx-features]; [tabs/rulers][dx-rulers] | **Partial.** Add tab stops/leaders, line-spacing modes, independent outline levels, contextual spacing, paragraph borders/shading, keep/widow/orphan rules, line numbering and East Asian grid settings. | DX-01, DX-02 |
| G09 | [Table layout/styles][dx-tables] | **Partial.** Add preferred widths/AutoFit, table alignment/indent/RTL, cell vertical alignment/text direction, styled borders, conditional styles, repeat headers, row splitting and positioned tables. | DX-06, DX-07 |
| G10 | [Lists and numbering][dx-features] | **Partial.** Numbering logic exists; extend marker formatting, marker/tab placement and style-linked list definitions rather than rebuilding it. | DX-01, DX-06 |
| G11 | [Bookmarks and internal links][dx-bookmarks] | **Partial.** External safe links exist; missing named ranges, internal destinations, bookmark navigation/conflict handling and richer hyperlink metadata. | DX-00, DX-05 |
| G12 | [Fields][dx-fields] and [field-code set][dx-field-codes] | **Partial.** Only basic `MERGEFIELD` semantics exist. Add nested code/results, switches, update/lock/display behavior, page/date/property/reference/formula fields and host-supplied dynamic content. | DX-05 |
| G13 | [TOC, captions and reference lists][dx-toc] | **Missing.** Build/update contents from headings/outline/TC entries; caption sequences and lists of figures/tables; linked entries and page numbers. | DX-01, DX-02, DX-05 |
| G14 | [Images and placement][dx-images] | **Partial.** Inline raster images exist; add image anchors/wrapping, crop/rotation, sizing and broader image-format support. | DX-07 |
| G15 | [Text/image watermarks][dx-watermarks] | **Missing.** Section/header-associated backgrounds and editing commands. | DX-03, DX-07 |
| G19 | [Editing restrictions and encryption][dx-protection] | **Partial.** Global read-only exists; missing capability policy, protected sections/ranges, user/group permissions, allowed-edit modes, password protection and encrypted Office files. | DX-09, DX-12 |
| G20 | [Structured content controls][dx-controls] and [legacy form fields][dx-features] | **Missing.** Typed form state, locks, placeholders and OOXML semantics; Avalonia host controls have a different purpose. | DX-09 |
| G21 | [Spell checking][dx-spelling], [AutoCorrect][dx-autocorrect], [hyphenation][dx-hyphenation] | **Missing.** Dictionary services, diagnostics/suggestions, committed-input correction and language-aware discretionary breaks. | DX-10 |
| G22 | [Advanced mail merge][dx-mailmerge] | **Partial.** Add nested data/regions, record selection, rich/image results, field rules, lifecycle callbacks and combined paginated output. Existing per-record generation remains supported. | DX-05, DX-11 |
| G23 | [Supported file formats][dx-formats] | **Partial.** DOCX/RTF/HTML fidelity needs expansion within the selected scope. Add DOC/DOT, DOTX, WordML, Flat OPC XML, ODT, MHTML and PDF export. Textalonia data XAML is unrelated to Word XML. | DX-04, DX-12 |
| G24 | [Document properties][dx-properties] and [custom XML][dx-custom-xml] | **Missing.** Typed built-in/custom metadata, retained XML parts and APIs. Application theme settings are not document themes. | DX-00, DX-01, DX-12 |
| G25 | [OLE previews and embedded data][dx-ole] | **Missing; limited reference support.** Preserve OLE data, relationships and supplied previews; insert/extract/remove objects without activating embedded applications. | DX-07, DX-12 |
| G26 | [Math interchange, frames and compatibility settings][dx-features] | **Missing.** Preserve equation data; implement paragraph frames and relevant text/table compatibility rules. Interchange-only support must be labelled separately from rendering. | DX-02, DX-12 |
| G27 | [Command UI, rulers and dialogs][dx-ui] | **Partial.** Add reusable command routing, contextual tools/menus, page/style/object/form dialogs, localization and interactive rulers. Existing toolbar/retemplating is retained. | DX-13, feature owners |
| G28 | [Find/replace panel][dx-search] | **Partial.** Literal APIs and toolbar find exist; add a reusable replace panel and navigation integrated with stories, bookmarks and page views. | DX-05, DX-13 |

Markdown, registered inline Avalonia controls, and cross-platform/mobile integrations
remain Textalonia capabilities. Their existing qualification backlog does not vanish
because the reference product is WPF.

## 4. Implementation workstreams

All new type and file names in this section are **proposals**. Existing paths are
implementation touch points, not evidence that the proposed feature already exists.
Use additive APIs where practical; document any changed contract explicitly.

### DX-00 — Document stories, anchors and transactions

**Priority: foundation. Size: XL. Dependencies: none.**

Touch `Model/FlowDocument.cs`, `DocumentIndex.cs`, `DocumentTree.cs`, `BlockOperations.cs`,
`Editing/EditorSession.cs`, `DocumentFragment.cs`, `HistoryMemory.cs`, and the native,
XAML and clipboard serializers. Add `Model/DocumentStory.cs`, `DocumentAnchor.cs`,
`DocumentSection.cs` and `Editing/DocumentTransaction.cs`.

1. Introduce immutable secondary stories for headers, footers and notes,
   owned by the document with a shared resource catalog. Keep `Blocks`
   and `FlowDocument.Text` as the main-body view. Add story-specific indexes and
   explicit story/range APIs; retain existing main-body integer APIs. The control
   routes input through an active-story facade, with one document-wide undo stack.
2. Add persisted anchors using story ID, paragraph ID, local UTF-16 offset and boundary
   affinity. Transform them through insert/delete/split/join/move operations. Specify
   deletion/collapse and copy/paste remapping for each owning feature. Existing
   revision-scoped `DocumentPosition` keeps its current invalidation contract.
3. Add transactions that return the new snapshot plus an edit/anchor map. Structural
   edits, table merges/splits and field expansion must use these maps. Keep the
   arbitrary `Session.Execute` API, but validate its full change set and derive a
   conservative mapping; reject ambiguous operations on anchored/protected content
   rather than silently corrupting ranges. Trusted document replacement remains an
   explicit load operation, distinct from an end-user edit.
4. Represent physical sections as an ordered, non-overlapping partition of the main
   story at valid paragraph boundaries. Keep today's nested `Section` content group
   intact. `DocumentSection` owns page settings, break type and story references;
   the next section start determines the previous section's end. Define how deleting
   a boundary combines section properties. Page/column break tokens remain separate
   from the existing soft-line-break character.
5. Extend validation, cloning, resource pruning and retention accounting to every
   story, anchored range, package part and hidden merge backup. Bound nesting, anchor
   counts and binary resources. Add document metadata and an opaque-part catalog
   with content type, owner and relationship IDs for in-scope codecs and payloads.
6. Establish a versioned schema decision before merging the model expansion. The
   current prerelease policy requires no historic v1-v3 migration. Recommend a new
   v5 envelope for the expanded model with an explicit v4 reader/converter to retain
   today's fixtures; record this as a new policy decision, not an existing promise.
   Decide XAML and clipboard versions independently; reject unsupported features
   and versions rather than silently flattening them.

**Exit evidence:** anchor edits across paragraph/table boundaries, backward selections,
undo/redo across stories, source/destination fragment remapping, resource/history
retention and schema round trips. Fuzz sequences must detect detached anchors and
duplicate IDs, including IDs in covered cells and restoration backups.

### DX-01 — Styles, typography, tabs and document themes

**Implementation status (2026-09-27):** named style catalog/resolution, sparse
overrides, theme references, document fonts, editing commands/dialogs, flow typography
and tab shaping, native v5/v4 reading, XAML/clipboard and DOCX mappings are implemented.
See [STYLES.md](STYLES.md) for the public contract and format boundaries. Page/keep/
widow and grid metadata are stored for DX-02. PDF geometry qualification depends on
DX-04; native UI and external Word/DevExpress comparisons remain DX-14 evidence gates.
The model and flow implementation do not close those separate milestones.

**Priority: foundation/core. Size: L. Dependencies: DX-00 contracts.**

Extend `Model/TextStyle.cs`, `BlockStyles.cs`, `Editing/SelectionFormattingState.cs`,
`Controls/InlineTextSource.cs`, `ParagraphLayout.cs` and `DocumentLayout.cs`. Add a
style catalog/resolver shared by editing, layout, accessibility and codecs.

1. Store named character, paragraph and table styles with IDs, parent links, linked
   character/paragraph styles and next-paragraph style. Resolve document defaults,
   theme values, base styles, applied styles and direct overrides in a documented
   order; detect inheritance cycles and invalidate affected layout when definitions
   change. Do not flatten imported styles at load time.
2. Add sparse override records with presence information, so explicit `false`, zero
   and cleared colors differ from inherited values. Merely adding a style ID beside
   today's fully populated defaults would suppress inheritance. Preserve old
   constructors/`ApplyStyle` behavior as explicit formatting, and provide new APIs
   for applying styles and clearing individual direct overrides.
3. Add underline kind/color/words-only, strike kind, caps/small caps, language/no-proof,
   run tracking, horizontal scale, numeric baseline position and kerning threshold.
   Add per-script/theme font references and theme colors/tints. Verify Avalonia's
   shaping support in a focused prototype; where necessary introduce a shaping
   adapter instead of pretending a stored property affects rendering.
4. Add tab alignment/position/leaders, line-spacing modes (multiple/exact/at-least),
   contextual spacing, paragraph borders/shading and outline levels 1-9 independent
   of the existing 1-6 heading shortcuts. Store page-break, keep and widow/orphan
   options now; DX-02 implements their page behavior. Model East Asian grid/snap
   options and test them with appropriate fonts and scripts.
5. Load embedded fonts into a document-scoped font service, respect embedding
   restrictions, and produce deterministic substitution diagnostics. Keep physical
   document colors/fonts independent of the application's light/dark chrome.
   Add font, paragraph, tabs and style-editor dialogs through DX-13.

**Exit evidence:** modifying one style updates all dependants; direct overrides survive;
mixed-selection formatting stays correct. DOCX style/theme/font round trips retain
identity. Glyph, tab, line-wrap, bidi and PDF geometry agree for new typography.

### DX-02 — Pagination, page setup and document views

**Priority: core. Size: XL. Dependencies: DX-00, DX-01.**

**Implementation status (2026-09-27):** the physical-section/page-settings model,
exact main-story page fragments and editor page views are implemented. The compact
toolbar now exposes page setup/numbering, page/column/section breaks, view selection,
zoom/fit, multiple-page arrangement and page navigation. Native v6 (with v4/v5
readers), data XAML and clipboard persistence carry the new model; unsupported
external page-layout mappings retain strict/tolerant loss diagnostics. See
[the pagination guide](PAGINATION.md) for the concrete APIs and supported behavior.

Headless model/layout/interaction/dialog tests and the reproducible pagination
probe provide local evidence. Explicit Unicode bidi controls have a confirmed
Avalonia 12.1.3 repeated-shaping defect, with retained regression reproducers and
qualification details in the pagination guide. Native IME, multiple monitor DPI, Office-rendered
comparisons and a licensed DevExpress reference comparison remain qualification
work; printing/PDF and secondary stories remain DX-03/04. This status does not close
those dependent workstreams or claim full DevExpress parity. The original scope
and exit criteria below remain the acceptance reference.

Extract reusable shaping/line measurement from `Controls/DocumentLayout*.cs` and
`ParagraphLayout.cs` into a proposed `Layout/` layer. Add `PageSettings`,
`PaginationEngine`, `PageLayoutSnapshot` and a paginated surface adapter.

1. Use explicit physical-unit conversion at import/export boundaries and consistent
   internal document units. Existing DIP-valued properties keep their meaning;
   zoom and monitor DPI are view transforms, not document edits.
2. Implement a single-column paginator first: page size, margins/gutter, orientation,
   explicit breaks, page backgrounds/borders and paragraph keep/widow/orphan rules.
   Produce fragments with story/range, page/column, baseline and clipping geometry.
   Never paginate from estimated viewport heights or screenshots.
3. Add section transitions, numbering restart/format, mirrored margins, unequal
   columns and column balancing, line numbering and document grids. Support
   continuous, next-page, odd-page and column starts documented by the reference;
   preserve additional imported break types until their layout is implemented.
   Treat legacy paragraph frames as paragraph-placement metadata, with separate
   compatibility fixtures; they do not require a general drawing-object model.
4. Share fragment geometry across drawing, hit testing, selection, caret, IME,
   accessibility and output. Split paragraphs and table cells across pages without
   splitting graphemes/atomic objects. Define guaranteed-progress handling for an
   oversize row/object and unsatisfiable keep chains. DX-06 adds full table policies.
5. Retain current flow layout as Simple view; add a page-width continuous Draft view
   and Print Layout view. Implement zoom, fit width/page, page gaps, multiple-page
   presentation and navigation without changing text offsets or undo history.
6. Cache page-break checkpoints by snapshot, section settings, font environment and
   field results. Invalidate from the earliest affected boundary and stop when later
   break state matches. Interactive layout may be incremental; printing and final
   page-count fields require complete exact layout. Run worker layout only after
   the chosen text backend's threading behavior is verified.

**Exit evidence:** reproducible page breaks for fixed fonts; stable caret/selection and
IME bounds at 50-200% zoom and multiple DPI settings; correct continuous/odd-page
transitions, columns and numbering. Benchmark edits near the start/end of large
documents and retained cache memory. Preserve existing Simple-view performance gates.

### DX-03 — Headers, footers, footnotes and endnotes

**Implementation status (2026-09-27):** immutable secondary stories with shared
resources/styles, active-story editing and document-wide undo, primary/first/even
header/footer references and link/unlink cloning, toolbar commands/dialogs and
page-region activation are implemented. Atomic notes support custom marks,
numbering/restarts, separators, placement, owning-line reservation, long-note
continuations and endnote flow. Repeated page context evaluates PAGE/NUMPAGES/
SECTIONPAGES without changing stored content. Native v7, data XAML v2, clipboard
v3 and supported DOCX/RTF mappings retain stories; other formats diagnose losses.
See [STORIES.md](STORIES.md) for API, rendering and interchange limits.

**Evidence:** `StoryModelTests`, `StoryEditorTests`, `StoryPaginationTests`,
`StoryInterchangeTests` and `RtfStoryInterchangeTests` cover linked editing/unlink,
backward selections, history/resource/clipboard ownership, rich secondary tables
and images, page field contexts, continuation/numbering, zoom/IME geometry and
codec round trips. Header overflow and oversized atomic notes are diagnosed;
notes suspend column balancing. External Office/DevExpress comparisons, native
platform qualification, general fields and print/PDF remain their own gates.
DX-00's broader anchor and transaction design is not claimed complete by this slice.

**Priority: core. Size: L. Dependencies: DX-00, DX-02; page fields use DX-05.**

Add story-aware editing commands, header/footer selection overlays and note layout
within the new paginator. Extend DOCX relationship handling and RTF destinations.

1. Store primary/first/even header and footer references with explicit link-to-previous
   semantics. Editing a linked story updates its users; unlinking clones content
   with remapped IDs. Support first-page/odd-even options and physical offsets.
2. Provide activation by commands and double-clicking the page region; retain the
   main-body selection on exit. Render repeated instances with a page context so
   the same header's PAGE field can show different values on different sheets.
3. Store note references as atomic inline markers linked to rich secondary stories.
   Implement custom marks, sequence formats, restart rules, separators and placement
   options; preserve all imported variants and diagnose any staged limitations.
4. Reserve footnote area during pagination, move the owning line when needed, split
   long notes with continuation handling and guard against pagination oscillation.
   Place endnotes according to document/section settings. Header/footer and note
   content must survive clipboard, undo, DOCX and RTF round trips.

**Exit evidence:** alternating headers across multiple sections; correct link/unlink
undo; a long footnote that forces repagination; independent note/body selection and
page-number output. Test fields, tables and images inside secondary stories.

### DX-04 — Print preview, printing and PDF

**Implementation status (2026-09-27):** shared exact-snapshot rendering, physical
page preview, output dialogs/commands, caller-owned stream export and host-native
print-service contracts are implemented. Printing validates ranges, copies,
collation and printer capabilities before submitting a captured snapshot.
`Textalonia.Pdf.Skia` is an optional backend using Avalonia.Skia/SkiaSharp for glyph,
vector and image output, links, metadata and physical page boxes. The core package
retains no WPF or PDF backend dependency. Registered inline controls require an
explicit output representation or diagnosed fallback. See [OUTPUT.md](OUTPUT.md)
for API usage, ownership, extraction limits and dependency/platform boundaries.

**Qualification boundary:** managed tests and PDF inspection provide local evidence;
native OS/printer qualification remains open. Tagged PDF, logical document
structure, bookmarks and PDF/A/PDF/UA profiles are not exposed or claimed. Their
external-validator exit criteria below remain future work, alongside broader
Unicode/font and native-platform qualification. This status records the basic
output implementation, not completion of every DX-04 conformance goal.

**Priority: core, then conformance. Size: L/XL. Dependencies: DX-02; integrate DX-03/05.**

Add proposed `Rendering/DocumentRenderer.cs`, `Export/IPagedDocumentExporter.cs`,
`PdfExporter.cs` and `Printing/IPrintService.cs`. Keep platform printing dependencies
behind host adapters; the default Avalonia library must not acquire a WPF dependency.

1. Render an immutable exact page-layout snapshot to screen preview, printer output
   and PDF through a shared drawing/semantic interface. Keep UI-only controls out
   of the export path; registered inline controls need a host-provided print
   representation or an explicit unsupported-content diagnostic.
2. Prototype a PDF backend against required glyph positioning, font subsetting,
   vector paths, images, links and structure tags before selecting a dependency.
   Verify its distribution terms and supported platforms. Use supplied glyphs and
   positions; an independent HTML-to-PDF reflow is not sufficient for shared layout.
3. Add preview, quick print and a host print dialog with page ranges, copies and
   printer capabilities. Snapshot before output, finalize relevant fields,
   report progress/cancellation, and handle output failures without
   mutating the live document or taking ownership of caller streams.
4. First ship selectable/searchable PDF text, images, links, metadata and page boxes.
   Then add logical reading order, heading/list/table structure, alt text, decorative
   artifacts, bookmarks and fonts for the documented tagged/PDF-UA/PDF-A profiles.
   Expose only profiles that pass an external conformance validator; a checkbox or
   raster image of each page is not sufficient.

**Exit evidence:** preview/PDF/print use identical fragment positions and page counts;
text extraction preserves Unicode; links work; cancellation and stream failures are
covered. Retain native print evidence and separate conformance-validator reports.

### DX-05 — Bookmarks, fields, contents and navigation

**Priority: core/automation. Size: XL. Dependencies: DX-00, DX-01; layout fields need DX-02/03.**

Extend `Model/MergeFields.cs`, `Serialization/MergeFieldInstructions.cs` and session
editing. Add `Model/Fields/`, `Editing/FieldOperations.cs`, `FieldEvaluator` and a
bookmark/outline service. Preserve the existing merge-field convenience API.

1. Implement bookmarks as named anchored ranges, including collapsed bookmarks,
   rename/delete/navigation and duplicate-name policies on import/paste. Represent
   internal link destinations separately from external URIs; retain the existing
   safe-URI policy for external links. Add tooltips and activation options.
2. General fields need an instruction AST, source code, switches, result content,
   lock/dirty state and explicit boundaries. TOCs and DOCVARIABLE results can span
   multiple runs/blocks; they cannot all be one U+FFFC label. Define code/result
   visibility and logical-coordinate projections before replacing the simple field
   representation. Provide a lossless adapter for today's atomic MERGEFIELD API.
3. Implement the documented field families in slices: MERGEFIELD and IF; dates/times
   and document metadata; PAGE/NUMPAGES/SECTIONPAGES; REF/SEQ/STYLEREF/SYMBOL;
   TC/TOC; DOCVARIABLE/INCLUDEPICTURE and arithmetic formulas. Track the exact
   [supported field list][dx-field-codes] plus the formula syntax documented in
   [Fields][dx-fields]. Do not infer support for NEXT, ASK, FILLIN or every Word
   field simply because they appear in existing out-of-scope notes.
4. Support general, numeric and date/time switches, nesting limits, dependency/cycle
   detection and deterministic culture/clock inputs. Preserve unknown codes and
   cached results with diagnostics. External data/images require explicit host
   resolvers; evaluating a document must not silently fetch arbitrary locations.
5. Separate ordinary evaluation from page-context evaluation. Recompute layout and
   page-dependent results until stable with a bounded iteration count and a useful
   failure diagnostic. Lock/update policies apply to explicit updates, load, save,
   print and merge; per-page header results are layout instances, not repeated edits.
6. Generate TOCs from styles, outline levels and TC entries, plus caption sequences
   and lists of figures/tables. Use generated bookmarks and real tab leaders.
   Add outline/bookmark/page navigation and a reusable find/replace panel; search
   must identify a story and revision, and replacement must be transactional.

**Exit evidence:** a nested conditional merge; rich DOCVARIABLE result; stable page
counts after a TOC changes length; cross-story fields; bookmark edits and paste
conflicts; locked/unknown/cyclic fields; native/DOCX/RTF field round trips.

**Implementation (2026-09-27):** delivered persistent story-local bookmark/field
ranges, internal destinations and activation, an instruction AST and rich cached
results, nested conditional/merge/date/property/reference/sequence/symbol/formula
and host-resolved fields, contents/captions, bounded physical-page re-evaluation,
story/revision-aware transactional search, outline/bookmark navigation, reusable
find/replace and toolbar authoring. Native JSON v8, XAML v3 and clipboard v4 preserve
the new model; DOCX/RTF preserve standard field/bookmark structures and rich caches.
Existing atomic merge and page-field APIs remain supported. See [FIELDS.md](FIELDS.md)
for the exact field families, update policies, coordinate contract and evidence.
Validation: Release solution build passed without warnings; 990 managed tests
passed with three existing skips, including two 180-step anchor fuzz seeds. The
fresh-cache independent NuGet consumer passed against the packaged build; offline
cached dependencies were used. This is managed/package evidence, not native or
external Office qualification.

Qualification boundaries remain explicit: full Word switch compatibility, advanced
formatted/nested page expressions in repeated stories, editable mixed code/result
views, typed property authoring and external Office/native accessibility qualification
are not claimed by this implementation. Code views currently use a read-only
projection; ordinary editing retains cached-result coordinates. DX-14 release gates
remain open.

### DX-06 — Table and list extensions

**Implementation status (2026-09-27):** table/cell preferred widths, Content/Window
AutoFit, alignment/indent and RTL grids; conditional table styles, cell alignment
and bidi direction, inside/outside and non-solid borders; repeated header instances,
row split policies and coordinated merged-cell fragments; positioned page tables
with rectangular body-text wrapping; and formatted, positioned, style-linked list
markers are implemented. Table properties and conditional shading are authorable
through the existing toolbar/dialogs, with undo and read-only guards. Native v9,
XAML v4 and clipboard v5 preserve the expanded model; DOCX/RTF/HTML map supported
settings or report explicit losses. See [table/list contracts](TABLES.md).

Managed validation: **1,041 tests passed, 3 existing native-bidi tests skipped**.
The public API baselines retain existing members and record the additive APIs and
clipboard schema version change.

Simple view places positioned tables inline; rotated vertical cell text is not
implemented. Positioned tables clip to their anchor column with diagnostics;
positioned tables with notes use inline flow. Nested-table header requests and
oversized unsplittable groups have explicit pagination fallbacks. Native UI and
external Word/DevExpress rendering qualification remain DX-14 gates. This
implementation does not claim those qualification results.

**Priority: core. Size: L. Dependencies: DX-01, DX-02.**

Extend `Model/Blocks.cs`, `TableOperations.cs`, `ListNumbering.cs`,
`Controls/DocumentLayout.Tables.cs`, `TablePointerController.cs`, toolbar/table UI
and all relevant codecs.

1. Retain current relative column widths as the legacy mode. Add table/cell preferred
   widths with explicit auto/absolute/percentage units, min/max-content measurement,
   AutoFit content/window, table alignment/indent and table-level RTL order.
   Add positioned tables through shared wrap-exclusion geometry with DX-07 images,
   without introducing a general drawing-object hierarchy.
2. Add cell vertical alignment/text direction, table and conditional styles,
   inside/outside border resolution and non-solid border kinds. Define precedence
   between table style, first/last/header/banded regions and direct cell overrides.
3. Add repeating header rows and allow/disallow row splitting. Split tall cell
   content into page fragments, coordinate spanning cells, and draw repeated header
   instances without duplicating model content. Define nested/merged-table behavior
   and handle rows taller than a page without a layout loop.
4. Extend list definitions with marker run formatting, indentation, suffix/tab
   position and named-style links. Reuse numbering identity/restart computation and
   clipboard remapping. Keep numbering layout independent of viewport visibility.
5. Add fixed-width/AutoFit, alignment, text direction, repeat-header and table-style
   controls to the existing table UI. Do not weaken current merge/split restoration
   semantics. Record table size limits independently of DevExpress's limits.

**Exit evidence:** multi-page tables with repeat headers, merged cells crossing page
boundaries, RTL tables, styled bands after row edits, content AutoFit and per-marker
formatting. DOCX/RTF preserve the supported settings or report precise losses.

### DX-07 — Images, watermarks and OLE previews

**Implementation status (2026-09-27):** image placement, aspect-aware sizing,
crop/rotation and square/top-bottom/behind/in-front/contour wrapping are implemented
with shared table exclusion geometry. Section text/image watermarks render behind
page content. Supplied OLE previews share image rendering while opaque package
resources support insertion, extraction, removal, clipboard and undo. Picture,
watermark and OLE controls are available through the toolbar. Native v10, data
XAML v5 and clipboard v6 retain the model; DOCX maps supported pictures, watermark
headers and OLE package/preview relationships. Other codecs report explicit losses.
See [image/object contracts](IMAGES.md).

Managed validation: **1,113 tests passed, 3 existing native-bidi tests skipped**.
Release packing and the isolated NuGet consumer passed, including DX-07 API
round trips and extraction/removal/undo. Public API baselines retain existing
members and record the additive APIs and clipboard schema increase.

The default decoder supports bounded raster images including ICO and a static SVG
subset. EMF/WMF originals can be retained with a supplied preview; native EMF/WMF
and TIFF decoding are not claimed. Contours use at most 64 conservative bands.
Simple view displays positioned images inline; table-cell, frame, secondary-story and Draft
floats have a diagnosed inline fallback. Page overflow clips with a diagnostic.
Continuous sections share the owning page's watermark. External Office/DevExpress
rendering, native interaction/DPI and native printing remain DX-14 qualification
gates, separate from managed implementation validation.
**Priority: core images, then OLE interchange. Size: L. Dependencies: DX-00/02/03.**

Extend `InlineContent.cs`, resource handling, surface interaction and DOCX picture
mappings. Add focused `ImagePlacement`, image editing controls and shared
`Layout/WrapExclusions.cs`; reuse existing resources and atomic-inline behavior.

1. Add image-only paragraph/page positioning, size/aspect ratio, crop/rotation and
   wrapping. Support square, top/bottom, behind/in-front and contour wrapping in
   slices. Share wrap geometry with positioned tables; specify collision ordering,
   incremental invalidation and bounded layout convergence. Keep inline images simple.
2. Provide image move/resize/crop controls and placement options. Store text/image
   watermarks as dedicated header-associated content with section-specific removal;
   render them as backgrounds without a shape-authoring framework.
3. Add SVG and broader image decoding through bounded resource adapters. Preserve
   EMF/WMF image originals where supported, with explicit preview/conversion capability.
   Retaining bytes and rendering an image are separate support claims.
4. Add OLE descriptors using a package resource and a supplied preview image/icon.
   Support insertion, extraction and removal through focused APIs and UI. Neither
   embedded-application activation nor a generic drawing engine is needed.

**Exit evidence:** image placement/cropping survives undo and serialization, wrapped
text paginates consistently, watermarks respect sections, and OLE previews/resources
round-trip and print correctly. Only in-scope image/OLE relationships are required.

### DX-09 — Capability policies, protected editing and forms

**Implementation status (2026-09-28):** implemented for the supported subset in
[FORMS.md](FORMS.md): host capabilities/identity, session-enforced atomic rejection,
read-only and permission ranges, document/section forms protection, anchored
plain/rich text and atomic checkbox/combo/dropdown/date controls, locks, navigation,
validation, keyboard/IME and value dialogs. Native JSON v11 and data XAML v6 retain
the model; DOCX maps supported SDTs, protection, permission markers and legacy form
imports. Bounded host-supplied XML binding evaluation is explicit. Picture/repeating/
gallery interaction, arbitrary cross-container Word SDTs, custom XML package data,
legacy password algorithms and encrypted I/O have explicit limits/diagnostics.
Managed model, policy, codec, control and output tests accompany the implementation;
external Office corpus and native platform qualification remain open.

**Managed validation:** 1,191 tests pass with three pre-existing pagination skips;
public API baselines and old-schema readers pass. The independent NuGet consumer
passes with the packaged assembly, including protected form fill and DOCX password
verification. Feature coverage is in `ContentControlTests`, `EditPolicyTests`,
`FormInterchangeTests` and `FormControlsUiTests`.

**Priority: forms/protection. Size: L/XL. Dependencies: DX-00.**

Add `Editing/EditPolicy.cs`, protection and content-control models, story-aware form
interaction and codec support. Extend command state through DX-13.

1. Centralize operation capabilities and editing permissions. Support hidden/disabled
   commands, read-only ranges, section form protection, user/group permission ranges,
   and a document mode that permits only form filling. Keep host
   identity resolution explicit; the editor does not authenticate users itself.
2. Enforce policies in the transaction/session layer, not just the toolbar. Cover
   paste/drop, replace-all, IME commit, table changes, inline updates, arbitrary
   `Execute` and undo/redo. Mixed permitted/forbidden edits need a
   documented atomic rejection or partition rule. Host load remains distinct.
3. Model plain/rich text, checkbox, combo box, dropdown and date controls with
   placeholders, tags, IDs, data, locks and ranges. Use inline values for atomic
   types and anchored block/range content for rich text. Existing registered
   `ControlInlinePayload` is not a substitute. Retain picture/repeating/gallery
   metadata and define their imported interaction limits explicitly.
4. Provide focus/tab navigation, validation, value editing and protected-form fill
   behavior. Add legacy checkbox/form-field support at its documented API level.
   Authoring/property UI for structured controls can be an optional Textalonia
   enhancement; reference parity requires code authoring and user interaction.
5. Persist `w:sdt`, locking, protection settings and permission markers. Add custom
   XML bindings where supported, with bounded XPath/data access. Separate edit
   protection password verification from actual file encryption. DX-12 supplies
   encrypted package I/O; secrets do not belong in persisted model snapshots.

**Exit evidence:** every edit route respects the same policy; denied changes leave
history unchanged; protected forms work with keyboard/IME and across save/load;
range permissions follow structural edits; exported PDF shows form values without
claiming interactive PDF-form support.

### DX-10 — Spelling, AutoCorrect and hyphenation

**Implementation status (2026-09-28):** implemented for the supported subset in
[PROOFING.md](PROOFING.md): host-supplied asynchronous spelling dictionaries,
language- and `NoProof`-aware diagnostics, cancellation/revision checks,
suggestions, ignore and add-to-dictionary workflows; committed-input
AutoCorrect with replacement tables, two-initial-capital correction, URL links,
host text/fragment/image replacements, policy checks and separate correction
undo; and host-supplied discretionary hyphenation shared by Simple and
paginated output, with source-offset mapping, soft hyphens, paragraph settings
and dictionary-revision invalidation. Native JSON, data XAML and the supported
DOCX paragraph settings retain hyphenation controls. Dictionaries and user
terms remain host-owned; external reference and native qualification remain
open.

**Priority: editing quality. Size: M/L. Dependencies: DX-01 language metadata and DX-00 transactions.**

Add `Proofing/ISpellChecker.cs`, `IAutoCorrectService.cs`, `IHyphenationService.cs`
and dictionary adapters. Connect to committed input and shared layout.

1. Check changed text ranges asynchronously with culture/no-proof metadata,
   cancellation and revision checks. Cache by text/language; show wavy underlines,
   suggestions, ignore/add-to-dictionary and a spelling workflow. Dictionary
   persistence belongs to a configured host provider.
2. Implement replacement tables, two-initial-capital correction and URL detection,
   plus host replacement callbacks returning text or a document fragment/image.
   Run after IME commit and at configured boundaries, never on transient preedit.
   Give correction a predictable undo step and obey edit permissions.
3. Insert discretionary hyphen opportunities through the line-breaking service,
   preserving source text/offsets. Honor soft hyphens, language dictionaries,
   per-paragraph suppression and capitals settings. Reuse the same decisions for
   pages, print and PDF; dictionary changes invalidate relevant layout checkpoints.

**Exit evidence:** stale spell results are discarded, mixed-language ranges use the
right dictionaries, correction undo is predictable, and hyphenated glyph/selection
positions agree in Simple and Print views. Retain dictionary provenance/terms.

### DX-11 — Rich mail merge and report generation

**Priority: automation. Size: L. Dependencies: DX-00/05; combined output needs DX-02/03.**

Extend `MailMerge/MailMergeOptions.cs`, `MailMergeProcessor.cs`, field evaluation
and the existing demo mail-merge workflow.

1. Introduce host-supplied recipient/data adapters with schema discovery, record
   selection/filter/sort and hierarchical child collections. Preserve dictionary
   input and lazy per-record output; do not silently resolve database connections
   stored in imported documents.
2. Parse/validate `TableStart`/`TableEnd` region pairs and nesting. Expand paragraphs
   and table-row regions with fresh block/inline/bookmark IDs, repaired internal
   references and correctly scoped numbering. Define empty-region behavior and
   reject unmatched or structurally invalid regions before generation.
3. Evaluate nested IF/formulas and Word-compatible formatting switches. Provide
   typed callbacks for DOCVARIABLE rich fragments and INCLUDEPICTURE resources,
   record/region lifecycle events, cancellation and per-record diagnostics.
4. Add a combined-document output mode with explicit section breaks, header/footer
   linkage and page-number restart policies. Keep separate-document output as the
   existing default. Run final field evaluation and pagination only after record
   expansion; stream outputs where possible without accumulating the whole batch.

**Exit evidence:** multi-level invoice rows, empty details, formatted/image results,
recipient previews, cancellation, deterministic culture, hidden merged-cell fields
and one combined document whose sections/page numbers survive DOCX/PDF output.

### DX-12 — Office fidelity, additional formats and package preservation

**Priority: continuous fidelity, then format breadth. Size: XL. Dependencies: DX-00 and each model feature.**

Refactor `Serialization/DocxDocumentFormat.cs` into package, relationship, style,
story, field, image and form readers/writers. Extend `DocumentFormats.cs`,
`DocumentConversion.cs`, RTF/HTML codecs and conversion reports.

1. Introduce a bounded package layer: content types, relationships, shared resources,
   owner-associated opaque parts, XML validation and diagnostics with part/range
   locations. Preserve only understood safe relationships; never write dangling
   IDs after editing or blindly copy unrelated ZIP members back into output.
   Apply the scope exclusions to opaque parts too; generic package preservation
   must not create an implicit requirement to retain excluded features.
2. Deliver DOCX mappings with their owning workstreams: real section properties,
   stories, named styles, numbering, fields, images, controls,
   protection, themes/fonts and core/custom properties. Preserve custom XML and
   compatibility settings. Resolve the current `Textalonia.Section` content-control
   encoding so decorative groups cannot be confused with actual Word form controls.
3. Extend RTF headers/notes/styles/fields/images and HTML resource/style
   handling. Keep strict versus tolerant conversion. Each feature/format combination
   has an explicit capability and loss code; adding a model feature must not make
   existing codecs silently lose it. Test strict rejection before writing output.
4. Add formats in increasing implementation scope:

   | Slice | Approach and completion boundary |
   | --- | --- |
   | DOTX | Reuse the OOXML package layer with correct template content types and loading semantics. |
   | Flat OPC XML, WordML | Separate codecs: Flat OPC packages OOXML parts in XML; Word 2003 XML has its own vocabulary. Neither maps to Textalonia XAML. |
   | MHTML | MIME/resource packaging over the HTML codec, with bounded parts, charset handling and resource identity. |
   | ODT | Implement package, style/list, page/master-page, table and object mappings; report losses by capability. |
   | DOC/DOT | Dedicated binary-format workstream: compound storage, text pieces, styles, numbering, fields and embedded objects. Evaluate a maintained compatible provider first; if unsuitable, implement/test the binary reader/writer separately. Renaming DOCX files is never support. |
   | Encrypted Office files | Add a password-provider/options API and standard/agile encryption adapters. Cover correct/incorrect/missing passwords and integrity failures without altering the active document. Include encrypted binary-format cases when DOC support exists. |

5. Track interchange-only equation payloads and OLE data separately from editable
   content. Define which edits invalidate opaque data. Existing digital
   signature parts cannot be carried over as if still valid after changing a package;
   report removal/invalidation. Creating signatures remains the optional server track.
6. Make backend choices through concrete compatibility prototypes. Keep the default
   core independently usable; optional providers must declare capabilities and fail
   clearly when unavailable. A format is qualified for the selected feature subset
   only after a tested provider or implementation ships, including its deployment
   and dependency requirements.

**Exit evidence:** Word/LibreOffice-generated import/edit/export/reopen corpus for each
advertised format, no repair prompts, expected metadata/parts intact, rendered page
comparisons, malformed/encrypted/package-limit cases and stable strict-mode reports.

### DX-13 — Commands, contextual UI, rulers and host integration

**Priority: incremental with every feature. Size: L. Dependencies: DX-00 contracts and feature APIs.**

Extend `TextaloniaEditor*.cs`, `TextaloniaToolbar.cs`, `DocumentSurface.cs`,
`Themes/Generic.axaml` and the desktop demo. Add command descriptors and reusable
Avalonia view models/templates, not a hard dependency on a commercial ribbon.

1. Define a command catalog with IDs, parameters, checked/mixed state, shortcuts,
   capability state and localization keys. Existing ICommand properties delegate
   to it. Route toolbar, keyboard, menus and automation through the same operations.
2. Provide an optional tabbed command surface with File/Home/Insert/Page Layout/
   References/Proofing/Mailings/View and contextual table/picture/header-footer tools.
   Keep the compact toolbar and host retemplating. Add dialogs alongside each model
   slice, rather than a late UI-only milestone with inaccessible functionality.
3. Add horizontal/vertical rulers tied to page, paragraph, tab, column and table
   metrics. One drag commits one transaction; preview/cancel leaves history correct.
   Apply the same zoom transforms used by content hit testing.
4. Add contextual menus, Paste Special choices using existing fragment/HTML/text
   paths, symbol insertion, document properties, reusable find/replace, outline and
   bookmark navigation, page/selection status, and view/zoom controls. Whole-word
   and bounded regex search may be useful extensions; treat them as separate API
   work until exact reference behavior is verified, not an assumed parity gap.
5. Expose service replacement for proofing, field resolution, print/export and dialogs
   with explicit ownership/cancellation contracts. Add load/save/layout completion
   and relevant editing-mode events only where consumers need them. Make all new
   UI keyboard accessible, localizable, and consistent with read-only/permission state.

**Exit evidence:** the demo can author and edit the milestone's features without host
code; commands agree across surfaces; dialogs restore focus/selection; rulers, menus
and contextual tabs work at multiple DPI values and with keyboard/screen readers.

### DX-14 — Qualification and release gates

**Priority: every milestone. Size: continuous. Dependencies: each delivered slice.**

Extend existing tests, fixtures, benchmark tools and independent NuGet consumers.
Do not replace the open [roadmap](ROADMAP.md) qualification tasks with this plan.

1. Maintain a capability matrix per feature and per format, including preservation,
   rendering, API editing and UI editing. Every gap ID needs fixtures and an explicit
   result; "DOCX supported" is too broad to be an acceptance criterion. Excluded
   content needs loss-diagnostic coverage, not preservation/rendering/editing parity.
2. Test invariants across all new stories and overlays: IDs, UTF-16/graphemes, directional
   selection, resource retention, undo, field/anchor mappings, protected edits,
   stream ownership and cancellation. Preserve public API baselines deliberately
   with migration examples and the independent package consumer.
3. Add a provenance-tracked corpus of letters, reports, legal documents,
   forms, mixed scripts, multi-page tables and templates. Record application version,
   fonts, expected semantics and rendering references. If a licensed DevExpress
   reference environment is available, record its version and differential results;
   lack of that environment must not be reported as a parity pass.
4. Compare semantic output plus fixed-font page images/PDF extraction. Use perceptual
   tolerances for rasterization while checking exact page count and layout invariants.
   Test native print, clipboard, IME, DPI, keyboard/touch and screen readers per claimed
   platform. Extend native accessibility bridges for stories, pages, tables and forms.
5. Retain existing PERF-01/PERF-06 gates and add first-page/full-pagination time,
   edit-to-visible-update p50/p95, export throughput, and peak/retained memory for
   documents with notes/positioned images/forms. Establish numerical budgets from reproducible
   baselines before optimization; do not invent a latency guarantee in the plan.
6. Update feature guides, release notes and support limits at each slice. Keep INT-01,
   INT-02, P4.6, NATIVE-06, MOB-01 and release/publication work open until their own
   evidence is complete. An editor milestone does not qualify mobile or publish a package.

## 5. Delivery order and acceptance milestones

Sizes are relative engineering scope, not calendar estimates: **M** is a contained
subsystem; **L** spans several layers; **XL** requires multiple milestones and carries
architecture/interoperability risk. Re-estimate after the foundational prototypes.

| Milestone | Work and dependency order | Demonstrable exit |
| --- | --- | --- |
| M0: contracts and prototypes | DX-00 contracts; DX-01 shaping/style prototype; DX-02 line-fragment prototype; DX-04 backend feasibility; DX-12 package design; DX-13 command skeleton. | Reviewed model/coordinate/schema decisions; old fixtures/API consumers retained; a page can be laid out and drawn by the proposed output path. No broad parity claim. |
| M1: editable page document | DX-00 implementation, DX-01 core styles/tabs, DX-02 single-column pagination, DX-03 headers/footers, DX-05 basic page fields, DX-04 basic PDF/print, accompanying DX-12/13. | Author, save, reopen and print a multi-section letter with different first-page header, page numbers, a style change and matching PDF pages. |
| M2: long documents | Complete columns/page rules, DX-03 notes, DX-06 tables/lists, DX-05 bookmarks/TOC/captions, DX-07 image placement/watermarks. | A report with columns, notes, a multi-page table, positioned images and a linked TOC updates consistently after edits. |
| M3: forms and proofing | DX-09 policies/forms, DX-10 proofing; supporting UI/codecs. Some model/proofing work can proceed after M0 without waiting for M2. | Protected fillable DOCX; suggestions/corrections; files retain semantics after reopening. |
| M4: document automation | Complete DX-05 field language and DX-11 regions/rich merge; properties/custom XML and DOTX templates from DX-12. | Generate a master-detail invoice batch and combined paginated output with images, computed values and cancellation. |
| M5: broader compatibility | DX-07 broader image formats/OLE, DX-12 remaining in-scope formats/encryption/compatibility, DX-01 advanced font/grid completion, DX-04 accessible/archival PDF. | Selected feature/API/UI/format claims pass their corpus and external validators; excluded content has clear loss diagnostics. |
| M6: qualified release | DX-14 native/platform/performance/package gates, documentation and existing release procedure. | Publishable candidate whose advertised scope has retained evidence; all unclosed parity rows remain visible. |

The first implementation PR should establish **story/anchor/transaction contracts and
tests**, not replace the toolbar. In parallel scheduling, the package reader and
command UI can progress against agreed contracts, but pagination must establish the
shared geometry before separate print, ruler, image-placement and note implementations
commit to incompatible layout assumptions.

### First reviewable implementation slices

1. Add story-aware ranges and transaction maps with tests for body-only documents;
   preserve current public position semantics and default rendering.
2. Add section metadata and a tiny exact paginator fixture (two paper sizes, explicit
   break, RTL text, one table); prove caret-to-page mapping and the drawing adapter.
3. Add named-style definitions and inheritance without flattening DOCX styles; prove
   explicit false/zero overrides and one style edit invalidating multiple paragraphs.
4. Add header/footer stories plus PAGE/NUMPAGES, with page-context evaluation.
5. Export/preview/print that same fixture from one layout snapshot, then expand the
   engine using M2 acceptance documents.

## 6. Decisions to resolve during implementation

These are design work items, not blockers to this planning document.

| Decision | Proposed default and required evidence |
| --- | --- |
| Native schema/API evolution | Adopt explicit v5 expansion and a v4 conversion path; review against the current unpublished-schema policy before implementation. Keep old model construction and main-body APIs usable. |
| Field coordinates | Approve storage-to-visible projection rules in DX-00/05 together; test selection/copy/IME before committing to representations. |
| Physical layout and shaping | Reuse Avalonia shaping through an adapter initially. Prove tabs, run typography, bidi, image/table wrapping and headless/thread behavior; replace internals only where the prototype shows a gap. |
| PDF/font/image/binary-format backends | Select using concrete format fixtures, platform availability, maintenance and dependency terms. No specific package is committed by this plan. Keep capabilities explicit if a backend is optional. |
| Pagination/field/image/footnote convergence | Set deterministic precedence and bounded retry policies, with diagnostic output and minimized failure fixtures. This is a major correctness risk, not just a rendering optimization. |
| Compatibility modes and opaque payloads | Preserve metadata for selected features first; implement behavior mode by mode with expected output. Excluded content needs loss diagnostics, and byte retention must never be advertised as edit/render parity. |
| Native scope | Keep Avalonia and existing desktop/mobile targets. Prioritize desktop editing/output while retaining explicit mobile/reader qualification status. |
| Optional server track | Consider document comparison, signing and page-image APIs only after core milestones; track separately from the WPF feature gaps and existing async serialization. |

Completion means closing the remaining capability rows with evidence for the selected
scope. The explicit exclusions do not block completion and must remain visible in
support documentation. Full DevExpress feature parity is not the project target.

## 7. Source index

All external feature claims above link to official DevExpress documentation, reviewed
on 2026-09-27. Implementation proposals, sequencing and size assessments are Textalonia
engineering recommendations based on the repository audit.

| Topic | Source |
| --- | --- |
| Product and support levels | [WPF overview][dx-home], [feature matrix][dx-features], [formats][dx-formats] |
| Page/document structure | [Sections][dx-sections], [headers/footers][dx-headers], [notes][dx-notes], [views][dx-views], [shared view API][dx-view-api] |
| Formatting and images | [Text formatting][dx-formatting], [tables][dx-tables], [image capabilities][dx-images], [watermarks][dx-watermarks] |
| Fields and automation | [Fields][dx-fields], [field codes][dx-field-codes], [bookmarks][dx-bookmarks], [TOC][dx-toc], [mail merge][dx-mailmerge] |
| Protection and forms | [Protection][dx-protection], [content controls][dx-controls] |
| Proofing and UI | [Spelling][dx-spelling], [AutoCorrect][dx-autocorrect], [hyphenation][dx-hyphenation], [visual elements][dx-ui], [rulers][dx-rulers], [dialogs][dx-dialogs], [find/replace][dx-search] |
| Output and package content | [Printing][dx-print], [PDF][dx-pdf], [properties][dx-properties], [custom XML][dx-custom-xml], [OLE][dx-ole] |

[dx-home]: https://docs.devexpress.com/WPF/8651/controls-and-libraries/rich-text-editor
[dx-features]: https://docs.devexpress.com/WPF/9532/controls-and-libraries/rich-text-editor/feature-overview
[dx-formats]: https://docs.devexpress.com/WPF/9113/controls-and-libraries/rich-text-editor/supported-formats
[dx-sections]: https://docs.devexpress.com/WPF/9116/controls-and-libraries/rich-text-editor/rich-edit-control-document/sections
[dx-headers]: https://docs.devexpress.com/WPF/9104/controls-and-libraries/rich-text-editor/rich-edit-control-document/headers-and-footers
[dx-notes]: https://docs.devexpress.com/WPF/401717/controls-and-libraries/rich-text-editor/rich-edit-control-document/footnotes-and-endnotes
[dx-views]: https://docs.devexpress.com/WPF/120518/controls-and-libraries/rich-text-editor/visual-elements/views
[dx-view-api]: https://docs.devexpress.com/OfficeFileAPI/DevExpress.XtraRichEdit.RichEditView._members
[dx-formatting]: https://docs.devexpress.com/WPF/118199/controls-and-libraries/rich-text-editor/text-formatting
[dx-tables]: https://docs.devexpress.com/WPF/9105/controls-and-libraries/rich-text-editor/rich-edit-control-document/tables
[dx-images]: https://docs.devexpress.com/WPF/11198/controls-and-libraries/rich-text-editor/rich-edit-control-document/shapes
[dx-watermarks]: https://docs.devexpress.com/WPF/403056/controls-and-libraries/rich-text-editor/rich-edit-control-document/watermarks
[dx-bookmarks]: https://docs.devexpress.com/WPF/9103/controls-and-libraries/rich-text-editor/rich-edit-control-document/hyperlinks-and-bookmarks
[dx-fields]: https://docs.devexpress.com/WPF/10296/controls-and-libraries/rich-text-editor/fields
[dx-field-codes]: https://docs.devexpress.com/WPF/17175/controls-and-libraries/rich-text-editor/fields/field-codes
[dx-toc]: https://docs.devexpress.com/WPF/9562/controls-and-libraries/rich-text-editor/page-layout/table-of-contents
[dx-protection]: https://docs.devexpress.com/WPF/9111/controls-and-libraries/rich-text-editor/restrictions-and-protection
[dx-controls]: https://docs.devexpress.com/WPF/404746/controls-and-libraries/rich-text-editor/rich-edit-control-document/content-controls
[dx-spelling]: https://docs.devexpress.com/WPF/8937/controls-and-libraries/spell-checker/examples/how-to-enable-spelling-check-as-you-type-for-the-rich-edit-control
[dx-autocorrect]: https://docs.devexpress.com/WPF/11019/controls-and-libraries/rich-text-editor/autocorrect
[dx-hyphenation]: https://docs.devexpress.com/WPF/401189/controls-and-libraries/rich-text-editor/hyphenation
[dx-mailmerge]: https://docs.devexpress.com/WPF/9110/controls-and-libraries/rich-text-editor/mail-merge
[dx-ui]: https://docs.devexpress.com/WPF/10316/controls-and-libraries/rich-text-editor/visual-elements
[dx-rulers]: https://docs.devexpress.com/WPF/400537/controls-and-libraries/rich-text-editor/visual-elements/rulers
[dx-dialogs]: https://docs.devexpress.com/WPF/10318/controls-and-libraries/rich-text-editor/visual-elements/dialogs
[dx-search]: https://docs.devexpress.com/WPF/10392/controls-and-libraries/rich-text-editor/visual-elements/dialogs/find-and-replace-panel
[dx-print]: https://docs.devexpress.com/WPF/11217/controls-and-libraries/rich-text-editor/printing
[dx-pdf]: https://docs.devexpress.com/WPF/116724/controls-and-libraries/rich-text-editor/examples/files/how-to-export-document-to-pdf-format
[dx-properties]: https://docs.devexpress.com/WPF/118378/controls-and-libraries/rich-text-editor/rich-edit-control-document/document-properties
[dx-custom-xml]: https://docs.devexpress.com/WPF/401647/controls-and-libraries/rich-text-editor/rich-edit-control-document/custom-xml-parts
[dx-ole]: https://docs.devexpress.com/WPF/402443/controls-and-libraries/rich-text-editor/rich-edit-control-document/ole-objects
