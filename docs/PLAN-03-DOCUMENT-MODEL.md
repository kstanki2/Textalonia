# Phase 3: Document semantics and tables

[Roadmap and dependencies](ROADMAP.md#implementation-phases)

**Outcome:** a model that can represent and edit the intended structure before codecs and UI expose it. **Entry:** Phase 2 edit/index contracts and P1.2 schema policy. **Status:** complete; see [implementation and verification report](PHASE3-REPORT.md) and [document semantics](DOCUMENT-MODEL.md).

## Starting points

- [Blocks](../src/Textalonia/Model/Blocks.cs), [styles](../src/Textalonia/Model/TextStyle.cs), and [table operations](../src/Textalonia/Model/TableOperations.cs).
- [Validation/traversal](../src/Textalonia/Model/FlowDocument.cs), [editing commands](../src/Textalonia/Editing/EditorSession.cs), and [native format](../src/Textalonia/Serialization/DocumentFormats.cs).
- [Layout](../src/Textalonia/Controls/DocumentLayout.cs) and [toolbar state](../src/Textalonia/Controls/TextaloniaToolbar.cs).

## Tasks

- [x] **P3.1 - Implement native schema evolution.** Add version-dispatched readers and explicit migrations from saved v1 fixtures. Select a new schema version for incompatible data, define defaults for absent properties, and keep unsupported-version errors clear. Maintain one authoritative representation during model transitions; avoid divergent `Paragraphs` and `Blocks` collections. **Done when:** v1 documents, including hidden merged cells, load unchanged and new documents round-trip all introduced fields. Apply this gate to each later model change as it lands.

- [x] **P3.2 - Expose selection formatting state.** Add UI-independent aggregation for text and paragraph properties, distinguishing uniform values, mixed values, and a collapsed caret's typing style. Define command behavior for mixed selections: applying a chosen value affects the selection uniformly without changing unrelated properties. Update basic toolbar indicators to consume that state. **Done when:** cross-run/paragraph/cell, reverse, and empty selections produce correct indicators and one undoable formatting operation.

- [x] **P3.3 - Model list identity and numbering.** Add list identity, level definitions, start values, explicit restart, and continuation across intervening paragraphs/sections. Define numbering when Enter splits an item, an empty item exits, items are indented, fragments are pasted, or list items are removed. Move numbering semantics out of per-layout local counters. **Done when:** visible numbering, edit commands, native round trips, and undo agree on restart/continuation examples, including multilevel lists.

- [x] **P3.4 - Specify and implement richer formatting.** Start with explicit font weight/stretch, letter spacing, line height, and paragraph spacing/indents; define precedence between legacy bold/italic shortcuts and richer style values. Extend section/block styling with independent borders and padding, retaining legacy defaults. Verify the pinned text backend's capabilities before promising individual typographic controls; keep unsupported controls explicitly deferred. **Done when:** supported properties affect both measurement and drawing, validate finite/ranged values, survive native round trips, and have inheritance/mixed-selection tests.

- [x] **P3.5 - Transform merged tables safely.** Add persisted column widths, row sizing policy, per-cell borders/padding, and merge-aware insert/delete rules. Define insertion inside/outside spans, deletion through an anchor, span shrinking, and restoration data after structural edits. Preserve surviving content, relocate an anchor when required, and document which explicitly deleted row/column content is removed. **Done when:** a table-driven operation matrix covers every boundary and undo restores exact content, spans, styling, IDs, and merge backups.

- [x] **P3.6 - Support nested table blocks.** Extend cell content to blocks and generalize merge backups, validation, cloning, traversal, current-cell lookup, replacement, and deletion for nested containers. Make layout and indexes handle child tables/sections, with explicit depth/element limits and an unambiguous innermost-cell editing rule. **Done when:** nested tables can be inserted, edited, merged/split where allowed, selected, serialized, and undone through model/session APIs without duplicating IDs or losing hidden content.

## Exit gate

- Every new field has validation, a native round-trip fixture, and an old-document compatibility case.
- Mixed formatting and list behavior are independent of the toolbar or a particular viewport.
- Merge/nesting tests include structural edits after a merged cell has been edited, nested content in retained merge backups, and full undo/redo restoration.
- Phase 2 locality/performance checks still hold. Wider-feature fixtures are added to the baseline set rather than replacing earlier cases.

## Handoff

Complete schema infrastructure first. Formatting/list work and table work can be separate changes using that infrastructure. Rich interaction controls follow in Phase 6; conversion of these structures follows in Phase 5. Codec gaps introduced by the new model must be documented until diagnostics land.
