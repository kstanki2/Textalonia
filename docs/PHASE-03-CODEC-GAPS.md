# Phase 3 codec gaps

> Historical Phase 3 snapshot. Phase 4 advanced native storage to schema 3; Phase 5 adds reporting and broader mappings. See the current [interchange contracts](INTERCHANGE.md) and [support matrix](PHASE5-REPORT.md).

Native JSON is the lossless format for the Phase 3 model. The writer emits schema 2; the reader supports schema 1 through an explicit, strict migration and supports schema 2 directly. External conversion improvements and structured clipboard diagnostics remain Phase 5 work.

| Model feature | Native JSON v2 | Current external formats / clipboard |
| --- | --- | --- |
| List identity, level definitions, starts, restart and continuation | Preserved, including hidden cells and backups | HTML and DOCX retain the older list kind/level subset. List IDs, custom markers, restart and continuation are not preserved. RTF and plain text flatten visible paragraphs and do not encode the new numbering semantics. |
| Explicit font weight and stretch | Preserved; explicit weight takes precedence over legacy Bold | HTML/RTF/DOCX use the legacy Bold flag and do not preserve explicit weights or stretch. A conflicting legacy flag can therefore export a different appearance. |
| Paragraph line height, letter spacing, right and first-line indents | Preserved | New properties are not mapped by HTML/RTF/DOCX. Their existing handling of left indents and spacing is unchanged. Letter spacing is currently paragraph-wide; individual run spacing is deferred because the pinned backend exposes it at paragraph layout level. |
| Independent section/cell borders and padding | Preserved, including legacy scalar section padding and BorderColor | External formats do not preserve the new independent edges. Existing section/cell color and legacy styling mappings remain limited. |
| Column width weights and Auto/AtLeast/Exact row policies | Preserved | Current HTML and DOCX table exporters do not encode these model properties. RTF and plain text flatten tables. |
| Nested cell blocks and merge restoration history | Full structure, IDs, physical covered cells, and nested backups preserved | HTML table import flattens cell blocks into paragraphs; DOCX reads the paragraph subset. HTML/DOCX export currently writes immediate cell paragraphs only, so child tables/sections are omitted. RTF/plain text retain visible nested text but flatten structure. None preserves merge backups or hidden-cell history. |
| Selection fragments | Native document save is lossless | Existing fragment extraction flattens sections/tables into paragraphs. Phase 5 defines a versioned structured fragment format and destination/list identity policy. |

Until conversion diagnostics are implemented, these losses are not reported by the current `IDocumentFormat` API. Applications requiring exact preservation must save `.textalonia` files. Phase 3 does not claim arbitrary HTML, RTF, or DOCX fidelity.

## Native migration defaults

Version 1 is read through frozen DTOs; properties added to the live model cannot accidentally become accepted v1 vocabulary. Every v1 cell's `paragraphs` becomes `blocks`, with each paragraph retaining its ID, runs and formatting. `mergeOriginal` becomes `mergeOriginalBlocks`, and physical covered cells are migrated as well. Backup IDs retain their historical identity scope while contributing to aggregate depth/element limits. Schema 2 accepts only the authoritative block collections; the ignored public compatibility adapters are rejected as wire members.

Absent new values preserve old behavior: explicit font weight is null (Bold selects 400/700), stretch is 5, extra letter spacing/right/first-line indents are zero, line height is automatic, list identity/definition/start are null and restart is false. Column widths and row sizing arrays are empty (equal columns and automatic row heights). Independent borders and padding are null, preserving the legacy section/cell appearance. Existing property defaults remain unchanged: 16 px font size, 8 px paragraph after-spacing and 12 px scalar section padding.

The frozen `native-v1.json` fixture is unchanged. `native-v2.json` contains all new persisted fields, nested cells, covered nested content, and a nested merge backup; both fixtures are tested through migration/current writing and reading. Writers never overwrite the caller's source file automatically, and schema 2 cannot be read by schema 1 readers. Unsupported versions are rejected before interpreting their document members.
