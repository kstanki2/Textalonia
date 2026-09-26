# Architecture

## Layers

`Model` contains immutable documents: FlowDocument -> Paragraph / Section / Table -> RichRun + TextStyle. It has no controls or live visual objects. Tables retain physical cells behind merged spans; visible text comes from anchor cells.

`DocumentIndex` maps a snapshot to a linear UTF-16 string and paragraph positions. Paragraph separators count as one position. It supplies selection, clipboard, search, and IME coordinates. Cell/section container IDs keep text editing from accidentally destroying structure.

`Editing.EditorSession` owns the current snapshot, directional selection, insertion style, and bounded undo/redo stacks. Each edit replaces affected paragraph content and publishes a snapshot. Adjacent typed characters coalesce until navigation, formatting, another operation, or an 800 ms pause breaks the group. Application operations enter the same history through Execute.

`Serialization.IDocumentFormat` operates on snapshots and caller-owned streams. The native version-1 JSON format preserves the entire model. External formats intentionally map only supported features. Codecs parse data; they do not instantiate XAML or execute document code.

`Controls.TextaloniaEditor` exposes Avalonia styled properties, binding, commands, clipboard and notifications. Its template composes TextaloniaToolbar, ScrollViewer, and DocumentSurface. TextaloniaViewer starts the same control in read-only mode.

`DocumentSurface` handles input and draws shaped native Avalonia TextLayouts. Its TextInputMethodClient displays transient composition without changing the committed document. DocumentLayout caches layouts for unchanged paragraphs, positions sections/tables, and uses the same geometry for painting, hit tests, carets, and selection.

## Performance boundaries

Paragraph shaping is cached. Painting skips content outside the effective viewport. Undo shares immutable paragraph/run objects and has an entry limit. Exports capture a snapshot before background encoding.

**This is not full document virtualization.** Text indexing, snapshot tree rebuilding, and geometric layout still visit the document. Large replacements can copy substantial text. The first layout shapes every visible-model paragraph, and a width change reshapes them all. Undo is bounded by entry count, not bytes. Extremely large documents need a rope/piece table, incremental position index, viewport-driven measurement, and a byte-budgeted history before production use.

Table ownership maps are cached by table object identity with a weak cache. This avoids repeated scans of the entire table for every cell without changing immutable record equality.

## Public extension points

- Bind Document or Text, and style/retemplate TextaloniaEditor.
- Supply an independent toolbar through public ICommand properties and Session methods.
- Apply custom immutable operations with Session.Execute.
- Supply serialization through IDocumentFormat.
- Draw offset-based TextHighlights and handle HyperlinkActivated.
- Use EditorSession without UI, or immutable snapshots without an editing session.

The keyboard/mouse implementation is currently in DocumentSurface; a swappable input-component system is future work. The automation peer exposes a value provider, not full text-range automation.

## Verification

Tests cover formatting-preserving edits, Unicode graphemes, newline semantics, selection restoration, history, readonly behavior, search, random replacements against a string reference, table merges, codecs, invalid documents, and XML entity rejection. Avalonia headless tests exercise bindings, real keyboard/pointer dispatch, clipboard, composition and Skia rendering.

Screenshots are generated under artifacts when tests run. CI is configured for Windows, Linux, and macOS; local verification only demonstrates the environment on which it actually ran. Native operating-system clipboard/IME/touch/screen-reader behavior still needs platform testing.
