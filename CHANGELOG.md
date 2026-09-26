# Changelog

## 0.1.0-preview.1

- Established the Textalonia package, namespaces, editor/viewer/toolbar controls, and `.textalonia` native file extension (with legacy `.art` support).
- Added an Avalonia 12 control library targeting .NET 8.
- Added immutable document/model APIs, editing sessions, formatting, history, search, and tables.
- Added native layout/input, clipboard, composition, viewer mode, toolbar, and light/dark themes.
- Added windowed long-paragraph layout, line-break checkpoint reuse, viewport-limited table cells, and line-based scroll anchors; remaining scalability limits are tracked in the Phase 2 report.
- Bounded retained glyph layouts even for dense visible content, added lazy height metadata and offscreen-query scroll anchoring, and reduced history/load bookkeeping. Strict transient Unicode shaping and latency qualification remain open.
- Added native JSON and text/HTML/RTF/DOCX interchange codecs with documented limits.
- Added a desktop sample, regression/headless tests, NuGet packaging, CI, and package-consumer verification.
