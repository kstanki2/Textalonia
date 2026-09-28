# Changelog

## 0.1.0-preview.1

- Added DX-07 image placement/crop/rotation and wrapping, section text/image watermarks, bounded image decoding, OLE package/preview APIs and picture editing UI. Native v10, XAML v5 and clipboard v6 retain the model; DOCX maps supported objects and other formats diagnose losses. See [image contracts](docs/IMAGES.md) for support and qualification boundaries.

- Added DX-06 preferred table/cell widths, AutoFit, RTL grids, conditional styles and border kinds, repeating headers and row split policies, positioned table wrapping, formatted/style-linked list markers, and table-property UI. Native v9, XAML v4 and clipboard v5 retain the model; external formats preserve mapped settings or report losses. See `docs/TABLES.md` for behavior and qualification boundaries.

- Added DX-05 anchored bookmarks and internal links, rich general fields and deterministic evaluation, TOCs/captions with bounded pagination, outline/bookmark navigation and story-aware transactional find/replace. Native v8, XAML v3 and clipboard v4 plus DOCX/RTF field interchange; see `docs/FIELDS.md` for supported families and qualification limits.
- Added DX-04 exact-snapshot output rendering, print preview and output commands, validated host print-service jobs with ranges/copies/collation, and optional `Textalonia.Pdf.Skia` PDF export. Output preserves caller-owned streams and reports cancellation/fallbacks. Native printing and tagged/archival/accessibility PDF qualification remain open; see `docs/OUTPUT.md`.
- Added DX-03 secondary stories: linked first/even/primary headers and footers, rich footnotes/endnotes, story editing and shared undo, page-context fields, continuation pagination, toolbar commands and DOCX/RTF interchange. Native v7, data XAML v2 and clipboard v3 preserve stories. See `docs/STORIES.md` for scope and qualification limits.
- Added physical page sections, page setup and numbering dialogs, explicit page/column/section breaks, exact page fragments, Simple/Draft/Print Layout views, zoom/fit and page navigation. Native schema v6 retains page metadata and reads v4/v5; unsupported external page-layout mappings report conversion losses. See `docs/PAGINATION.md` for the implemented scope and remaining native, Office and output qualification.

- Added atomic named merge fields with toolbar editing, clipboard/history/native/XAML persistence, measured labels and accessibility descriptions; immutable preview, lazy per-record mail merge, culture/format/fallback policies, basic DOCX/RTF field preservation and a desktop merge workflow. See `docs/MAIL-MERGE.md` for supported field syntax and diagnosed limits.

- Added additive tolerant/strict conversion reports; expanded HTML/RTF/DOCX list, typography, table and embedded-image mappings; versioned structural clipboard fragments with identity/resource remapping and stale cut/paste protection; a locally authored interchange corpus and explicit application qualification gaps. See `docs/INTERCHANGE.md#supported-subset-and-diagnosed-losses`.

- Added replaceable input components; atomic inline images/host controls with immutable resources, native persistence and bounded view ownership; managed text accessibility ranges with the native Avalonia bridge limitation documented. See `docs/INPUT-COMPONENTS.md` and `docs/INLINE-CONTENT.md`.
- Added mixed-selection formatting state; identified multilevel lists with restart/continuation; richer typography and independent container borders/padding; persisted table sizing, merge-aware structural edits, and nested cell blocks. See `docs/DOCUMENT-MODEL.md` for editing and persistence semantics.
- Established the Textalonia package, namespaces, editor/viewer/toolbar controls, and `.textalonia` native file extension (with legacy `.art` support).
- Added an Avalonia 12 control library targeting .NET 8.
- Added immutable document/model APIs, editing sessions, formatting, history, search, and tables.
- Added native layout/input, clipboard, composition, viewer mode, toolbar, and light/dark themes.
- Fixed distant caret scrolling to publish the measured extent before bringing the caret into view, preventing delayed scrolling and extra shaping on the next edit.
- Added windowed long-paragraph layout, line-break checkpoint reuse, viewport-limited table cells, and line-based scroll anchors; scaling contracts are documented in `docs/ARCHITECTURE.md` and `docs/PERFORMANCE.md`.
- Bounded retained glyph layouts even for dense visible content, added lazy height metadata and offscreen-query scroll anchoring, and reduced history/load bookkeeping. Added opt-in strict shaping limits with recoverable rendering errors, reduced temporary allocations, and added paired Windows qualification evidence; longer-run latency misses remain documented.
- Added native JSON and text/HTML/RTF/DOCX interchange codecs with documented limits.
- Added a desktop sample, regression/headless tests, NuGet packaging, CI, and package-consumer verification.


### XAML and Markdown integrations

- Added the versioned Textalonia data XAML codec and bounded Markdown dialect with
  strict/tolerant diagnostics, safe resource mapping, and extension selection.
- Added `MarkdownViewer` with asynchronous revision-checked updates and optional
  presentation-only token highlighting; no new package dependency.
- Native schema v4 preserves quote/code semantics and code language. This is the
  only supported prerelease schema; unused development schemas have no migration
  support. See `docs/INTEGRATIONS.md` for persistence and dialect limits.
- Added integration demo, independent package-consumer coverage, and update probe.

### Release preparation

- Added MIT licensing, repository/source/symbol metadata and dependency notices.
- Preserved public signatures and added nullable/attribute/modifier baselines,
  current-schema round-trip, rejection and stream-failure coverage, and executable extension examples.
- Added isolated consumer/package inspection, checksum-bound candidate receipts,
  retained OS-matrix CI artifacts and explicit publication/public verification tools.
- Published preview API/support/schema policies and upgrade/correction procedures.
  Native qualification, authenticated package ownership and public release remain open.
