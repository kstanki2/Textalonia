# Named styles, themes and typography (DX-01)

A document owns `Styles`, `Defaults`, `Theme` and embedded `Fonts`. Named character,
paragraph and table styles have stable string IDs and base-style links. Paragraph
and character styles can be linked; paragraph definitions specify the next style
used when inserting a hard paragraph break. Inheritance cycles, missing references,
invalid typography, and excessive catalog/depth/resource sizes are rejected before
an editor accepts a snapshot.

## Cascade and direct formatting

`DocumentStyleResolver` is shared by editing, layout and export. Its cascade is:
document defaults, default named styles, base and applied paragraph styles,
base and applied character styles, then direct overrides. Theme colors/fonts are
resolved against the document theme. A concrete font/color override clears the
corresponding inherited theme reference. Themes describe physical document
appearance and are independent of application chrome.

Legacy `TextStyle` and `ParagraphStyle` constructors have `Overrides == null`:
all their concrete properties are explicit. Existing documents therefore keep
their appearance. Use `TextStyle.ForStyle(id)` / `ParagraphStyle.ForStyle(id)`
for inheritance. A sparse override's `StyleValue<T>.IsSet` distinguishes inherited
values from explicit `false`, zero and null. An explicitly null color clears the
inherited color. Set an override member to `default` to resume inheritance.

```csharp
var body = new ParagraphStyleDefinition
{
    Id = "Body", Name = "Body", NextStyle = "Body",
    TextFormatting = new() { FontSize = 18 },
    Formatting = new() { SpaceAfter = 12 }
};
session.SetStyles(session.Document.Styles with
{
    Paragraphs = session.Document.Styles.Paragraphs.SetItem(body.Id, body)
});
session.SelectAll();
session.ApplyNamedParagraphStyle("Body");
session.ChangeTextOverrides(s => s with { Bold = false }); // Explicit false.
session.ChangeTextOverrides(s => s with { Bold = default }); // Inherit again.
```

`SetStyles`, `SetDefaults`, `SetTheme`, named-style application and override changes
are validated, read-only aware, single undoable edits. Definition updates invalidate
all dependent layout, even when paragraphs themselves are unchanged. Existing
`ApplyStyle` / `ApplyParagraphStyle` callbacks see effective values; edits to sparse
content retain unrelated inheritance. Mixed selection state also uses effective
values. Old concrete formatting remains concrete.

Applying a named paragraph style clears direct paragraph and character formatting
by default; pass `clearDirectFormatting: false` to keep it. Character-style application
affects the selection or subsequent typing at a collapsed caret. Table-wide style
properties supply cell shading, padding and borders; explicit cell values take
precedence. Conditional table styles belong to DX-06.

## Typography and editing UI

Character records include underline kind/color/words-only, strike kind, caps/small
caps, language/no-proof, tracking, horizontal scale, numeric baseline offset,
kerning threshold, per-script fonts and theme font/color references. Paragraphs
include aligned tab stops and leaders, natural/multiple/exact/at-least line spacing,
contextual spacing, borders/shading, independent outline levels 1-9, page/keep/widow
rules and East Asian grid/snap metadata. All sizes and offsets are DIPs; scale 1
means 100 percent. `DefaultTabWidth = 0` preserves the backend's automatic legacy
tab interval; positive values use a fixed DIP interval after explicit stops.
Outline level zero means body text, independently of heading
shortcuts 1-6.

Small caps use a deterministic synthetic display: lowercase letters are uppercased
at 80 percent of the run size. Text storage and UTF-16 positions remain unchanged.
Per-script font choices are resolved before shaping; language/kerning settings are
passed to Avalonia. Page/grid options are preserved as metadata for DX-02.

The toolbar exposes Font, Paragraph, Tabs and Styles dialogs. Public
`ShowFontDialogAsync`, `ShowParagraphDialogAsync`, `ShowTabsDialogAsync` and
`ShowStylesDialogAsync` support host command surfaces. Dialogs defer changes until
Apply, preserve untouched mixed fields, reject stale selections, and restore editor
focus. Native keyboard/screen-reader qualification remains part of DX-13/14.

Page breaks, keep chains and widow/orphan pagination require DX-02. This flow editor
does not claim printed/PDF geometry qualification before DX-04 exists.

## Embedded fonts

`DocumentFontDefinition` references encoded font bytes in `Resources`.
`DocumentFontService` creates an isolated Avalonia font collection, never installs
fonts on the operating system, and releases it after dependent layouts. It reads
OpenType OS/2 embedding rights; malformed, restricted, bitmap-only, unsupported and
preview/print-only faces are not used for editing. Full fonts are retained; no
subsetting is performed. Missing/rejected embedded faces use the configured fallback with deterministic
diagnostics. Only single-face sfnt TTF/OTF containers are loaded. Legacy documents
without embedded fonts retain Avalonia's native font fallback behavior. Per-glyph
fallback depends on the platform and installed fonts. `FontEmbeddingPolicy` is also available to hosts.

## Persistence and clipboard

Native JSON writes schema v5 and reads v4/v5. V4 formatting remains explicit;
loading does not rewrite the source. XAML data preserves the same style metadata
through allowlisted typed JSON data elements, retaining its legacy formatting
attributes for ordinary styles. These elements never instantiate arbitrary types
or execute markup. Clipboard v2 reads v1/v2 and preserves catalogs/resources,
remapping colliding style IDs and their parent/link/next references. When pasting
into a nonempty document with incompatible defaults or theme, the imported copy
uses effective formatting to preserve appearance; the source remains unchanged.

DOCX preserves style IDs, inheritance, links/next styles, supported theme slots,
font resources and mapped typography. DOCX supports Office theme slot names;
custom/conflicting slots, conditional table styling, paragraph-local default tab
widths and East Asian grids receive diagnostics. DOCX integer-percent scale and
half-point baseline/kerning rounding also produce diagnostics. Legacy paragraph
letter spacing maps to equivalent run tracking when read from DOCX.
Format-specific losses are reported through
`SaveWithReportAsync` / `LoadWithReportAsync`; strict mode rejects those losses.
HTML, RTF and Markdown export effective supported formatting and diagnose lost
style identity/theme semantics and extended typography. They do not silently claim
lossless named-style interchange.

Regression evidence lives in `NamedStyleModelTests`, `NamedStyleEditingTests`,
`NamedStyleLayoutTests`, `TypographyLayoutTests`, DOCX style tests and font/dialog
tests. This is implementation evidence, not a licensed DevExpress comparison or
external Word/PDF visual qualification.
