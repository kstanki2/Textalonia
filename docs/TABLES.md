# Table and list extensions (DX-06)

Textalonia tables retain the immutable grid, covered cells, merge backups and
relative column widths. The additions below apply to the model, editor and output
layout. These are supported Textalonia behaviors; native Office/DevExpress visual
comparison remains an open qualification gate.

## Table widths and direction

`Table.PreferredWidth` and `TableCell.PreferredWidth` use `TablePreferredWidth`
with `TableWidthUnit.Auto`, `Absolute` (DIP, 96 per inch), or `Percentage` (0-100).
Non-auto widths must be positive. Table width is relative to the available column
or cell area; cell percentages are relative to the table width. Cell preferences
are constraints on the column grid, including merged spans, rather than separate
cell rectangles.

`Table.AutoFit` selects `Legacy`, `Fixed`, `Content` or `Window`. Legacy preserves
the existing relative `ColumnWidths` behavior. Content measures unbroken words and
unwrapped rich text, including inline dimensions, to distribute widths between
minimum and maximum content requirements. Window fills the available area.
Unbreakable content can make a Content table wider than its container. Explicit
row heights still apply. A manual resize freezes an AutoFit table's measured grid
into fixed widths and clears conflicting visible-cell width preferences.

`Alignment` and `Indent` place the table within its container. `RightToLeft`
reverses visual column order while model indexes, storage text, selection and
numbering remain in logical order. Resize handles and drag deltas follow that
visual order. `TableCell.TextDirection` independently selects inherited, LTR or
RTL paragraph direction; it does not rotate text. `VerticalAlignment` supports
Top, Center and Bottom. `StyleOverrides` can explicitly override a style with Top
or inherited text direction.

## Styles and borders

Named `TableStyleDefinition` entries retain inheritance. `Conditions` maps
`TableStyleRegion` to sparse `TableStyleOverrides`. Resolution follows this order:

1. Default and applied named table formatting, including ancestors.
2. Odd/even column bands, odd/even body row bands, first/last column, first/last row,
   then header rows. Later matching regions win; body row banding restarts after
   the designated header rows.
3. Direct table overrides, direct cell properties, then sparse cell overrides.

Bands are resolved from the current row/column positions, so row edits recalculate
banding. Merged cells use their owning cell and complete span to determine edge
regions. `OutsideBorders`, `InsideHorizontal` and `InsideVertical` distinguish
outer edges and grid separators; direct cell borders take precedence.
`BorderSide.Kind` supports Solid, Dashed, Dotted, Double and None.

The existing Styles dialog can create and edit table shading and header/odd/even
row shading. The Table properties dialog applies saved table styles and edits
layout, selected cells and selected rows in one undo transaction. APIs include
`SetTableAutoFit`, `SetTablePreferredWidth`, `SetTableAlignment`,
`SetTableRightToLeft`, `SetTableRepeatHeaderRows`, `SetTableRowsAllowSplit`,
`SetTableCellPreferredWidth`, `SetTableCellVerticalAlignment`,
`SetTableCellTextDirection` and `ApplyNamedTableStyle`.

## Pagination and placement

`RepeatHeaderRows` identifies consecutive leading rows. Continuation pages and
columns draw additional instances of those cells without cloning model content.
They map back to the original text for hit testing; canonical caret and navigation
positions use the original instance. A header boundary through a vertical merge
expands to the end of that merged group. Headers that leave no space for body
content are suppressed with a layout diagnostic.

`TableRowSizing.AllowSplit` defaults to true. Unsplittable rows or connected merged
row groups move to the next column when they fit there. Groups taller than a full
column are split with a diagnostic to guarantee progress. Cell text splits on
shaped line boundaries, and spanning cells share coordinated page cuts. Exact
row heights clip content to their bounds. Oversized atomic content is consumed
once. Nested tables fragment inside their parent cells; their own repeated-header
requests are diagnosed where independent repetition is unavailable.

`Table.Position = new TablePosition(x, y, distance)` anchors a table relative to
the current page column. The table's preferred width determines its width. The
shared rectangular wrap helper reserves its bounds plus the text-wrap distance
for following body paragraphs. Positioned tables are confined to their anchor
column, with a diagnostic for clipped oversized content. Positioned tables with notes use
inline flow with a diagnostic. Simple view displays
these tables inline. This focused rectangle geometry is available for the DX-07
image work; it introduces no drawing-object hierarchy. Wrapping takes precedence
over paragraph keep/widow rules, with layout diagnostics for that fallback.

Tables support 1-1000 rows and 1-100 columns independently of another editor's
limits. Nested content also obeys the document's existing depth/resource limits.

## List markers

`ListLevelDefinition` adds `MarkerFormatting` (sparse character formatting),
`CharacterStyleId`, `ParagraphStyleId`, `TextIndent`, `MarkerIndent`, `TabPosition`
and `FollowCharacter` (Tab, Space or Nothing). Positions use DIP. Defaults retain
the original marker layout. `Suffix` remains the literal suffix within the marker;
`FollowCharacter` determines separation from the text.

`ListDefinition.CreateParagraphStyle(level, listId)` creates paragraph list
formatting linked to that level's named paragraph style. Marker character styles
and direct marker formatting are resolved separately from body runs. Effective
level definitions continue through the existing identified-list numbering cache;
restarts, ancestor numbering and off-screen preceding items keep their existing
semantics. Clipboard paste remaps list identities as before.

DOCX maps marker run properties and character/paragraph style links, indentation,
tabs and follow characters. RTF maps core marker font/size/color/emphasis and
placement; unsupported marker typography gets `rtf.list-marker-typography`.
Style links flattened for another format get `conversion.list-style-links`.
HTML retains list metadata but reports browser marker appearance differences as
`html.list-marker-appearance`. Combined direct paragraph indent and list-level
indent has an external precedence diagnostic (`docx.numbering-paragraph-indent`
or `rtf.list-paragraph-indent`). Named paragraph links apply through
`CreateParagraphStyle`; changing a level alone does not change the paragraph style.

## Persistence and evidence

Native JSON v9 (reading v4-v9), data XAML v4 (reading v1-v4), and clipboard v5
(reading v1-v5) preserve these properties and hidden merge backups. Cropping a
clipboard table adjusts its header count to the leading header rows actually
included in the copied range.

| Format | Table settings retained | Diagnosed boundaries |
| --- | --- | --- |
| DOCX | Preferred widths, fixed/content fit, alignment/indent/RTL grid, vertical alignment, headers, row splitting, eight band/edge regions, border kinds | Window AutoFit, positioned geometry, cell-wide bidi override, HeaderRow-only style region, sparse cell overrides |
| RTF | Preferred widths, fixed/content fit, alignment/indent/RTL grid, vertical alignment, headers, row splitting | Window AutoFit, positioned geometry, cell-wide bidi override, cell borders/padding, named-style identity |
| HTML | Preferred widths, fit/alignment metadata, indent/RTL, vertical alignment, cell bidi direction, headers, row splitting, border kinds | Positioned geometry and named-style identity; browser page layout is not guaranteed |

Diagnostics include `docx.table-autofit-window` / `rtf.table-autofit-window`,
`<format>.positioned-table`, `docx.cell-text-direction` / `rtf.cell-text-direction`,
`docx.table-header-style`, and existing `rtf.cell-decoration` and
`conversion.named-styles`. Strict conversion rejects reported losses. Use native storage for a complete editable
snapshot; see [interchange](INTERCHANGE.md) for the conversion-report contract.

Regression evidence is in `TableFormattingTests`, `TablePaginationPolicyTests`,
`TablePropertiesTests`, `TableExtensionInterchangeTests`, `ListExtensionTests` and the existing merged-table,
clipboard, pagination and numbering suites. Native DPI, keyboard/screen-reader
and external rendering comparisons remain tracked in [qualification](QUALIFICATION.md).
