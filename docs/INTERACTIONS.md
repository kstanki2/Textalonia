# Editing interactions

The default keyboard and pointer components share the document's shaped geometry.
Applications replacing those components can retain these gestures by calling the
base implementations. Coordinates and hit targets use device-independent pixels.

## Keyboard and selection

Left/Right follow physical caret stops across mixed LTR/RTL runs. Home/End move to
the visual line edges; Up/Down and PageUp/PageDown retain the preferred visual X.
Shift extends the original logical anchor. A caret retains its shaped character
edge and line affinity at bidi and wrap boundaries; reflow resolves that affinity
against the current layout. Selection offsets remain logical UTF-16 coordinates,
and Backspace/Delete remove whole logical graphemes. Word movement keeps the
existing whitespace word policy while following visual direction.

Mouse drag selection scrolls on a 16 ms dispatcher clock. Speed increases with
pointer distance into/beyond a 24 DIP edge region, capped at 1200 DIP/s. Hit tests
are clamped to the current viewport, while the pointer is retained relative to
the viewport and the selection anchor stays fixed during virtualization. Release,
capture loss, focus loss, document edits, Escape, detach and component replacement
stop the gesture and release its resources. Read-only documents still allow selection.

## Tables and formatting

Hover a cell to show its resize edges. Drag an internal column boundary or a row's
bottom edge to resize. Column sizing follows the model's relative weights, sharing
width with the neighboring column; row sizing retains an existing Exact rule and
otherwise uses AtLeast. A preview changes layout without mutating the session or
history. Release commits once; Escape, focus/capture loss, readonly changes and
detach cancel. Intervening document revisions invalidate the preview.

Alt-drag selects a rectangle, expanding through every intersecting merged cell.
Alt+Shift+Arrow extends the same rectangle from the current cell. The active cell's
text caret remains available for ordinary typing/navigation. The selected table's
identity governs all commands, including nested tables. Copy extracts a structured
rectangle; Cut or Delete clears its cells and merge backups, retaining the table
structure. Failed or stale clipboard writes cannot clear cells.

The toolbar exposes merge/split, insertion/deletion, cell borders, padding,
background, column/row sizing, list restart/continuation, typography and paragraph
styles. Keyboard-accessible sizing controls use the same resize operation. Character
and paragraph formatting applies to all selected cells, including a discontiguous
column, and reports mixed values. Each operation commits one history entry.

Hosts can use `CellSelection`, `SelectTableCells`, `ExtendTableCellSelection`,
`BeginTableResize`, `PreviewTableResize`, `CommitTableResize`, `CancelTableResize`,
and the table/formatting methods on `TextaloniaEditor`. `FormattingState` reflects
rectangular selection; `Session.FormattingState` continues to describe logical text
selection. These additive APIs preserve existing document and fragment formats.

## Content drag/drop

Press within selected text and move at least 6 DIP to start dragging. A click without
a drag collapses the selection. A changed document or selection before the threshold
cancels the pending gesture. The insertion preview does not change selection/history.

| Transfer | Default | Modifiers |
| --- | --- | --- |
| Within one editor | Move | Ctrl/Meta, or Alt on macOS, copies |
| Between Textalonia editors | Copy | Shift moves; copy modifier takes priority |
| External applications | Copy | Source deletion is never authorized by an external effect |
| Read-only source | Copy | Target must remain editable |

Drops inside or at either edge of the source selection are no-ops. Native structured
fragments preserve nested containers and resources; external input falls back from
native to HTML to plain text. Unknown payloads are rejected. Source/target document
revision changes reject stale operations. Selection-only changes after native drag
start do not change the captured source range.

A move within one editor validates the deletion and insertion before publishing one
undo entry. For a cross-editor move, insertion succeeds before source removal. Each
editor owns its own history: undo in the destination removes the insertion; undo in
the source restores removed content. A source edited by a destination change callback
is retained and the transfer becomes a copy. Cancellation, failed insertion and native
backend failures preserve the source.

## Touch and qualification

Touch taps place the caret; long press selects a word and exposes context actions.
Caret/range handles use the same shaped geometry and support crossing. Motion before
the long-press threshold stays available for host scrolling; multi-touch, viewport
changes during a pending hold, focus changes and detach cancel pending ownership.
Handle positions track the visible viewport after keyboard opening and scale changes.
Embedded child controls retain their own focus/input.

Android/iOS qualification harnesses and device scripts are in
[MOBILE-QUALIFICATION.md](MOBILE-QUALIFICATION.md). Mouse/touch injection into the
headless backend is regression coverage, not device qualification. Native IME,
clipboard, drag/drop and screen-reader checks remain governed by the dated evidence
in [PHASE6-REPORT.md](PHASE6-REPORT.md); mobile support is not yet qualified.
