# Document semantics and schema v5

The document model is independent of Avalonia controls. Native JSON reads and
writes schema version 5. Version 4 remains readable and migrates concrete styles as
explicit direct formatting. Versions 1-3 were unused development formats and have no migration support. See [integration semantics](INTEGRATIONS.md)
for quote/code metadata and [inline content](INLINE-CONTENT.md) for descriptors,
resources and the coordinate/export contract. The version is checked before
interpreting the document; missing or unsupported versions fail with
`NotSupportedException`. The reader rejects unknown members and preserves IDs,
text, formatting, spans and hidden cells. Loading never rewrites the source file.

Model defaults include: null explicit weight uses
`Bold`, normal stretch is 5, paragraph tracking and extra indents are zero, line
height is automatic, list identity/definitions/start are absent, restart is false,
column widths are equal, and rows size automatically. Null container padding and
borders use the default geometry. Current-schema fixtures exercise these contracts.

See [named styles, themes and typography](STYLES.md) for the DX-01 cascade, sparse
overrides, font ownership, dialogs and format support.

## Selection formatting

`EditorSession.FormattingState` aggregates the selected runs and paragraphs,
including cells and sections, independently of selection direction. Each
`FormattingValue<T>` distinguishes a uniform value (including null) from
`IsMixed`. At a collapsed caret, character properties come from `TypingStyle`.
Generic `Text(selector)` and `Paragraph(selector)` access all model properties.

Formatting commands retain unrelated properties and commit one history entry.
Boolean toggles set mixed selections uniformly to true; a uniformly true selection
is cleared. A collapsed formatting change is undoable and affects subsequent
typing. Toolbar emphasis, font and list indicators use this same aggregation.

## Lists

`ParagraphStyle.ListId` identifies a list across intervening ordinary paragraphs,
sections and table cells. `ListDefinition.Levels` defines up to nine levels with
start values, decimal/letter/Roman/bullet markers, prefixes, suffixes and optional
ancestor numbers. `ListStart` assigns an explicit value to an item;
`ListRestart` restarts at the level's configured start. An increment at an outer
level resets deeper counters. Anonymous legacy lists retain their compatibility
numbering. `ListNumbering.Compute(document)` exposes markers without a viewport.

Use `SetList`, `RestartList`, `ContinueList` and `IndentList` on the session
to edit this metadata. Enter continues the identity and level while clearing a
restart on the new item. Enter in an empty list item exits the list. Indenting
changes the level while preserving identity. Deleting items recomputes numbering
from surviving metadata. Pasted fragments receive new list identities, preserving
relationships inside the fragment without joining a destination list accidentally.
Versioned fragments preserve clipped sections, nested/merged tables and resources. See [clipboard boundary and destination rules](INTERCHANGE.md).

## Typography and container styles

`TextStyle.FontWeight` is an optional weight from 1 through 1000 and takes
precedence over `Bold`; `EffectiveFontWeight` and `EffectiveBold` expose the
resolved value. The Bold command clears explicit weight and applies the shortcut.
`FontStretch` uses width classes 1 through 9. Italic remains the sole slant control.
Font availability determines which weight/stretch face the backend can resolve.

`ParagraphStyle` adds `RightIndent`, signed `FirstLineIndent`, optional absolute
`LineHeight`, and paragraph-wide `LetterSpacing`. These join the existing left
indent and before/after spacing in measurement, wrapping, drawing and hit testing.
The pinned [Avalonia 12.1.3 text backend](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.Base/Media/TextFormatting/TextLayout.cs)
accepts line height and letter spacing at paragraph level. Per-run letter spacing,
variable font axes and individual OpenType feature controls remain deferred.

`EdgeInsets` and `BlockBorders` represent independent sides. A section uses
`PaddingEdges` to override its legacy scalar `Padding`; a cell uses `Padding`.
Missing sides in an explicit `BlockBorders` mean no border. Null border colors
inherit the editor border color. All numeric styles validate finite ranges.

## Tables and nested blocks

`TableCell.Blocks` is the authoritative content, accepting paragraphs, sections
and nested tables. `MergeOriginalBlocks` retains the anchor's original content.
Legacy `Paragraphs` and `MergeOriginal` remain init-capable paragraph views over
the same storage and are omitted from native JSON. Use the block APIs when nested
content must be retained; assigning a legacy view replaces that cell's content.

`ColumnWidths` stores positive relative widths, normalized to the available table
width; an empty array keeps equal columns.
`RowSizing` stores `Auto`, `AtLeast` or `Exact` policies and heights. Nonempty sizing
arrays match the physical grid. Exact row heights clip overflowing content.
Cells independently retain background, padding
and borders, including hidden cells under a merge.

Structural operations follow these rules:

| Operation | Merged-cell behavior |
| --- | --- |
| Insert at a span's leading or trailing boundary | The new row/column stays outside the span. |
| Insert strictly inside a span | The span grows to cover the inserted cells. |
| Delete through a span | The span shrinks; completely deleted regions disappear. |
| Delete the anchor row/column with surviving span cells | The first surviving source cell becomes the anchor, keeping its ID and original styling while carrying the surviving merged content. The explicitly deleted anchor's styling is removed. |
| Delete from an unedited merge | The aggregate is rebuilt from surviving source cells; explicitly deleted source content is removed. |
| Delete from an edited merge | Edited aggregate content survives intact; explicitly deleted original cells are removed from split restoration data. |
| Split an unedited merge | Restore surviving source blocks and IDs. |
| Split an edited merge | Retain edited blocks in the anchor and restore other surviving original cells. |

Nested content is cloned recursively when merging, with fresh live block/cell IDs.
Hidden blocks and retained backups survive serialization and undo. The current
cell is always the innermost visible cell containing the caret, including through
an intervening section. Insert/update/delete table commands use this rule.
Validation bounds nesting to 32 levels and total structure to 100,000 elements,
with at most 1,000 rows and 100 columns per table. Backups share the depth/element
budget and have separate historical ID scopes.

The Phase 2 persistent edit/index paths and viewport shaping remain in use.
Structural operations and native import/export traverse their affected snapshots.
`ListNumbering.GetMarker` reuses numbering transitions on unchanged persistent
subtrees; `Compute` enumerates and caches the full marker map for a snapshot.
See the [current interchange support matrix](INTERCHANGE.md#supported-subset-and-diagnosed-losses) for supported mappings, loss diagnostics and application qualification gaps.

## Merge fields

`MergeFieldInlinePayload` is a named atomic inline with optional .NET value format and
literal missing/null fallback. It shares inline coordinates, clipboard identity remapping,
formatting and history. The pure mail-merge transforms include retained table cells and
merge backups. See [mail merge](MAIL-MERGE.md) for the complete contract.
