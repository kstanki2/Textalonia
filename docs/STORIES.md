# Headers, footers and notes

DX-03 adds editable secondary stories, repeated page regions, footnotes and
endnotes. These share the document's styles, fonts, resources and undo history.
The existing body APIs keep their storage coordinates; a note reference and a
page field each occupy one UTF-16 U+FFFC position.

## Editing

The demo and optional editor toolbar expose **Stories** with primary/first/even
header and footer commands, link/unlink, first-page and odd/even options,
distances, page fields, note insertion/deletion, and note settings. Double-click
a page's top/bottom margin or an existing note region to edit it. Double-click
a note marker to open its note. **Return to document** or Escape restores the
saved body selection, including backward selections.

Selection, typing, formatting, table editing, clipboard operations and IME input
use the active story. A dashed rectangle identifies the active repeated region.
A variant that has no visible page instance, such as an even header in a one-page
document, opens on a continuous editing surface. Enable its first/even option to
use it on matching pages. Simple and Draft normally show the body; an explicitly
activated secondary story can be edited there independently.

```csharp
editor.EditHeader();
editor.Session.InsertText("Quarterly report");
editor.InsertPageField(PageFieldKind.Page);
editor.CloseStory();

editor.Session.Select(12, 12); // main-body coordinates
editor.InsertFootnote();
editor.Session.InsertText("Supporting information.");
editor.CloseStory();
```

`Session.Document` and `editor.Document` always return the complete document;
`FlowDocument.Blocks`, `Text`, `PlainText` and `editor.Text` remain body-only.
`Session.ActiveStoryId` is `Guid.Empty` for the body. `ActiveDocument`, `Index`,
`Selection`, search and ordinary edit operations address the active story.
`GetStoryDocument(id)` and `GetStoryIndex(id)` provide explicit read views.
`SwitchStory(id)` changes the editing context without an undo entry; it invalidates
revision-scoped positions and text ranges. Undo/redo restore the document, active
story and selection of the saved history state. Read-only permits navigation and
copying but prevents creating or editing stories.

## Model and ownership

`FlowDocument.Stories` owns immutable `DocumentStory` records with stable IDs,
kind and rich blocks. Header/footer stories can be shared by multiple sections.
`DocumentSection.HeaderFooter` contains six `StoryReference` values, first/even
flags and physical distances in DIP. A reference with `LinkToPrevious = true`
inherits the same variant from the preceding section. An unlinked null reference
is explicitly empty. First-page selection takes precedence over even-page
selection; even-page selection uses the physical sheet index.

Editing a linked header changes the shared story. `SetHeaderFooterLink(...,
linked: false)` clones its rich blocks and remaps descendant identities;
relinking changes the reference. These operations are undoable. Creating the
first header/footer also creates an explicit default physical section.

`DocumentNote` links a main-body `NoteInlinePayload` to a Footnote or Endnote
story. Custom marks are independent of automatic counters. Notes cannot contain
other note references; header/footer notes are also rejected. Deleting a
reference removes its note/story when no live or retained merge content owns it.
Removing a note through the session also removes its marker. Resources used only
by deleted stories are pruned and remain available to undo.

`FootnoteSettings` and `EndnoteSettings` configure decimal/Roman/letter numbering,
start, continuous/section restart, footnote page restart, placement and separator
text. Footnotes support page bottom or below text; endnotes support document or
section end. Note settings are document-wide. `DocumentNoteNumbering.GetMark`
uses reference order; callers must supply a page map for physical page restarts.
Simple/Draft use continuous numbering when no physical page map exists.

Validation covers story kinds/references, globally unique live IDs, notes,
secondary rich content and shared resources. Merge restoration backups retain
the existing historical-ID exception. Clipboard v3 remaps story/note/inline and
block identities, copies referenced resources, and materializes inherited headers
when a copied section no longer has its preceding section. Copying ordinary
secondary text creates an ordinary fragment. Pasting nested notes is rejected.
Mail merge discovers and expands fields inside secondary stories too.

## Layout

Print Layout measures rich header/footer content at page width and places it at
the configured distances. `PageLayoutSnapshot.StoryRegions` describes each
repeated instance or note continuation; `StoryFragments` supplies its shaped
lines and story-local coordinates. `PageContext` exposes page number, total and
section page count. `HitTestStory`, story-aware caret/selection APIs and the editor
use this same geometry. Body-only geometry APIs retain their meaning.

Footnote reservation moves an owning line when necessary, reduces the available
body area, and splits long notes at complete line bands, including table content.
Owning-line reservation takes precedence over widow/orphan rules in paragraphs
containing references; that fallback is diagnosed. Unsplittable adjacent table
lines that cross a reference band can be clipped with a diagnostic.
Continuation separators identify subsequent portions. Endnotes consume flow
space at section/document end. Progress is forward and bounded: an oversized
atomic line is consumed once and clipped with a layout diagnostic. Continuations
finish before a section changes paper size; column balancing is suspended for
documents with notes. Draft remains one continuous body surface and does not
place secondary page regions.

`PAGE`, `NUMPAGES` and `SECTIONPAGES` are focused atomic page fields. Their labels
are evaluated per physical instance after pagination in body, headers, footers
and notes. Stored content is unchanged. Fixed-width inline slots prevent page
count or note-number changes from causing pagination oscillation; long labels
can be clipped by the configured descriptor width. General fields, switches,
TOCs and field update/lock policies remain DX-05.

A header/footer taller than its configured page margin is clipped and reported
in `PageLayoutSnapshot.LayoutDiagnostics`; it does not grow the body margin.
The same list reports oversized note content and the column-balancing fallback.
Blank parity sheets omit repeated headers and footers.
The engine remains synchronous on the Avalonia shaping thread.

## Persistence and format boundaries

Native JSON writes v9 and reads v4-v9. Data XAML writes v4 and reads v1/v2/v3/v4 in the
existing data namespace. Clipboard writes v5 and reads v1/v2/v3/v4/v5. Native/XAML
retain the complete supported story model, including dormant variants.

DOCX supports rich header/footer parts and references, linkage, first/even
options, distances, footnote/endnote parts and markers, custom marks, numbering,
placement, text separators, page fields and story-local image/link relationships.
Physical section geometry and numbering are mapped alongside story ownership.
RTF supports its primary/first/left/right header/footer destinations, footnote
and endnote destinations, page fields, custom marks, numbering and placement.
Its existing rich-content restrictions still apply to these destinations.

Use reporting conversion APIs and strict mode when loss is unacceptable. Formats
that cannot retain stories diagnose their omission. Unsupported Office note
numbering/placement variants, rich separator formatting, per-section note
settings and unsupported section properties produce precise diagnostics. Word's
global odd/even and mirror settings cannot represent differing per-section
flags without normalization. An imported section beginning directly with a table
gets a diagnosed empty boundary paragraph. Unreferenced stories and malformed or
external story relationships are also diagnosed; no external content is fetched.

## Verification and qualification

`StoryModelTests`, `StoryEditorTests`, `StoryPaginationTests`,
`StoryInterchangeTests` and `RtfStoryInterchangeTests` cover ownership, link/unlink,
clipboard/history/resources, typing/IME, zoomed geometry, repeated fields, rich
tables/images, long-note continuation, restarts, package relationships and strict
loss reporting. Headless page images are generated in `artifacts/stories`.
The complete managed suite passed 860 tests with 3 existing skips on 2026-09-27;
the independent Release package consumer also exercises story editing,
serialization and page-region rendering.

This implementation does not qualify Word/LibreOffice rendering equivalence,
licensed DevExpress comparisons, native IME/accessibility on every platform,
or DX-00's broader durable-anchor/transaction framework. [DX-04 output](OUTPUT.md)
uses the same story page geometry and retains its own native print and PDF
conformance qualification gates.
