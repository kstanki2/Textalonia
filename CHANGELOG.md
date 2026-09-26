# Changelog

## 0.1.0-preview.1

- Added native schema v2 with explicit v1 migration; mixed-selection formatting state; identified multilevel lists with restart/continuation; richer typography and independent container borders/padding; persisted table sizing, merge-aware structural edits, and nested cell blocks. See `docs/DOCUMENT-MODEL.md` for migration and editing semantics.
- Established the Textalonia package, namespaces, editor/viewer/toolbar controls, and `.textalonia` native file extension (with legacy `.art` support).
- Added an Avalonia 12 control library targeting .NET 8.
- Added immutable document/model APIs, editing sessions, formatting, history, search, and tables.
- Added native layout/input, clipboard, composition, viewer mode, toolbar, and light/dark themes.
- Fixed distant caret scrolling to publish the measured extent before bringing the caret into view, preventing delayed scrolling and extra shaping on the next edit.
- Added windowed long-paragraph layout, line-break checkpoint reuse, viewport-limited table cells, and line-based scroll anchors; scaling contracts are documented in the Phase 2 report.
- Bounded retained glyph layouts even for dense visible content, added lazy height metadata and offscreen-query scroll anchoring, and reduced history/load bookkeeping. Added opt-in strict shaping limits with recoverable rendering errors, reduced temporary allocations, and added paired Windows qualification evidence; longer-run latency misses remain documented.
- Added native JSON and text/HTML/RTF/DOCX interchange codecs with documented limits.
- Added a desktop sample, regression/headless tests, NuGet packaging, CI, and package-consumer verification.
