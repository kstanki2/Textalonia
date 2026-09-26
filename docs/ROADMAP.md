# Feature matrix and path to parity

The target is the feature set described by [Avalonia's editor announcement](https://avaloniaui.net/blog/rich-text-editor). This is an independently implemented preview with its own API, data format, and theme. The commercial editor is not a dependency.

| Area | Current preview | Remaining work |
| --- | --- | --- |
| Character formatting | Fonts, size, bold, italic, underline, strike, colors, baseline, links | Mixed-selection formatting indicators, more typographic controls |
| Document structure | Paragraphs, headings, list metadata, sections, tables | List restart/continuation model, richer block styles |
| Tables | Editable cell paragraphs, rectangular merges, split, backgrounds, add/remove rows/columns | Interactive resizing, border/padding UI, structural edits through existing merges, nested tables |
| Editing | Native text layout, keyboard, pointer selection, grapheme deletion, readonly, bounded history, find/replace | Visual bidi navigation, drag autoscroll refinement, drag/drop content, touch selection handles |
| IME | Composition client, transient preedit and committed text | Native Windows/macOS/Linux and mobile keyboard qualification |
| Clipboard | Native rich fragments, platform HTML adapter, plain text | Structured table/section fragments; native cross-application tests on every platform |
| Formats | Native JSON, text, supported HTML/RTF/DOCX subsets | Full RTF/DOCX round-trip fidelity, native XAML codec, import-loss diagnostics |
| Embedded content | Image alt text on HTML import | Inline images, arbitrary Avalonia controls, resource lifetime/serialization rules |
| Display | Viewer mode, light/dark, replaceable theme/toolbar, text highlights | Independent input components, complete accessibility text providers |
| Markdown | No dedicated implementation | Markdown codec/viewer and optional code highlighting |
| Scale | Cached paragraph shaping and viewport-clipped painting | True layout virtualization, incremental indexes, piece-table storage, byte-budgeted undo |
| Distribution | Local NuGet + symbols, docs, tests, CI workflow, package consumer smoke test | Ownership/license metadata and package ID availability, platform certification, public release |

## Suggested milestones

1. Harden the existing preview with native IME, clipboard, screen-reader, and bidirectional-text tests. Add document fuzzing and realistic performance measurements.
2. Introduce incremental storage/position/layout indexes before claiming large-document performance.
3. Extend the model with inline resources and separate input components without forcing file codecs to depend on live controls.
4. Expand conversion fidelity against a corpus of Word, LibreOffice, browser, and native documents. Add explicit diagnostics for unsupported content.
5. Add XAML/Markdown integrations and advanced table interactions, then stabilize and version the public API.

A fully featured clone requires these additional milestones. The current package is suitable for evaluating the API and continuing development; it is not a claim of production parity.
