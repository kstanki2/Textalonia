# Bookmarks, fields, contents and navigation

DX-05 adds persistent story ranges, general field instructions and rich cached
results, bookmark/internal-link navigation, outline navigation and a reusable
find/replace panel. These are independent Textalonia APIs, not DevExpress API
compatibility. The implementation preserves the existing atomic merge-field and
page-field convenience APIs.

## Coordinates and editing

`FlowDocument.Bookmarks` and `Fields` own `DocumentAnchor` boundaries. An anchor
identifies a story, paragraph, UTF-16 offset and insertion affinity. Body story ID
is `Guid.Empty`. Boundaries must be on grapheme boundaries. General field source
code is metadata; its cached result consists of ordinary rich runs and blocks in
the story between `Start` and `End`. Multi-paragraph and table results therefore
participate in selection, editing, search, rendering and copying normally.

`EditorSession` transforms ranges during edits and records them in document-wide
undo/redo. Bookmark names are ordinal and unique. Nonempty selection bookmarks
include insertions at their start/end; collapsed bookmarks follow inserted text.
Deleting bookmarked text collapses the bookmark. `RemoveField` explicitly removes
the definition, optionally retaining its cached content. Locking prevents field
updates; it is not a protected editing range.

Complete copied ranges retain metadata and receive fresh identities on paste.
Partial bookmark/field copies carry only their visible content. Duplicate bookmark
names receive `_2`, `_3`, etc.; internal destinations and parsed REF/PAGEREF/
HYPERLINK references and bare formula bookmark operands are remapped with them. Covered cells and note stories retain
only valid owned ranges. Arbitrary `Execute` edits use paragraph identities and a
conservative change mapping; invalid or crossing field boundaries are rejected.
Revision-scoped `DocumentPosition` retains its existing invalidation behavior.

```csharp
session.Select(12, 28);
session.AddBookmark("summary");
session.RenameBookmark("summary", "overview");
session.NavigateToBookmark("overview"); // also activates the bookmark's story
editor.SetInternalLink("overview", tooltip: "Go to overview");
```

`TextStyle.InternalLink` is an `InternalLinkDestination`, separate from external
`Hyperlink` URIs. External links still require http, https or mailto. An internal
link can use modifier-click or ordinary click; read-only documents activate on
click. A dangling destination does not launch an external application.

## Authoring and evaluation

The toolbar's **Fields** menu edits source instructions, inserts fields, contents,
lists of figures/tables and captions, updates fields and changes lock state.
`FieldOperations` provides pure immutable operations; session methods provide undo.
The parser retains source and produces an AST of literal/nested arguments and
switches. Unknown instructions can be preserved without executing them.

```csharp
session.InsertField("IF { MERGEFIELD Amount } > 100 \"Large\" \"Small\"",
    FlowDocument.FromText("Pending"));
var result = session.UpdateFields(new FieldEvaluationOptions
{
    Culture = CultureInfo.InvariantCulture,
    Clock = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
    MergeValues = new Dictionary<string, object?> { ["Amount"] = 125 }
});
```

Types are in `Textalonia.Model`, `Textalonia.Model.Fields`, and
`Textalonia.Editing`. The core default clock is Unix epoch and culture is invariant;
interactive toolbar updates explicitly supply the current clock and UI culture.
`DocumentVariableResolver` and `PictureResolver` are explicit host callbacks that
return rich `FlowDocument` results. Evaluation performs no implicit file/network
fetch. Callbacks run synchronously and can inspect the cancellation token supplied
through their hosting operation; hosts remain responsible for their resolver I/O.

Evaluation has bounded instruction/dependency depth and result size. Locked fields
retain caches. Unknown codes/switches, missing properties/resolvers, cycles and
invalid formulas retain cached results and return diagnostics. Instruction nesting
and range nesting are separate: code nesting does not require adding invisible
characters to the document. `OnlyDirty` is opt-in; normal explicit updates inspect
all unlocked fields.

The supported reference field families are tracked against the official
[DevExpress field list](https://docs.devexpress.com/WPF/17175/controls-and-libraries/rich-text-editor/fields/field-codes):

| Family | Implemented behavior |
| --- | --- |
| MERGEFIELD, IF | Record values, nested instructions, numeric/text comparisons, conditional text results |
| DATE, TIME, CREATEDATE, SAVEDATE, PRINTDATE | Explicit clock for current date/time; supplied document properties for historical dates |
| DOCPROPERTY, AUTHOR, COMMENTS, KEYWORDS, LASTSAVEDBY, REVNUM, SUBJECT, TEMPLATE, TITLE | String document properties; missing values diagnosed |
| HYPERLINK | Safe external URI or separate internal bookmark destination |
| PAGE, NUMPAGES, SECTIONPAGES | Explicit page context and bounded layout updates; repeated basic page fields use layout instances |
| REF, SEQ, STYLEREF, SYMBOL | Bookmark content dependencies, caption sequences/restarts, style text and Unicode symbols |
| TC, TOC | Heading/style/outline/TC entries, generated bookmarks, linked entries, actual tab stops/leaders and page numbers |
| DOCVARIABLE, INCLUDEPICTURE | Explicit host-provided rich content/images, with result validation |
| `=` | Arithmetic and comparisons with nested numeric fields |

FILENAME, NUMWORDS, NUMCHARS and PAGEREF are Textalonia extensions to that reference
list. Formula operators follow the documented `+ - * / ^ = < <= > >= <>` set;
bounded numeric functions are additional extensions: `ABS`, `INT`, `SIGN`, `ROUND`,
`MOD`, `SUM`, `PRODUCT`, `COUNT`, `AVERAGE`, `MIN`, `MAX`, `IF`, `AND`, `OR` and `NOT`.
Formula literals use invariant decimal syntax; output uses the evaluation culture.
Bare bookmark operands, parentheses, percentages and `TRUE`/`FALSE` constants are
supported. Exponentiation binds before unary negation; exponent chains associate
to the right. `ROUND` accepts 0 to 15 decimal places and rounds midpoint values away
from zero. Formula functions are numeric and eagerly evaluated.

Scalar-result fields support `\* UPPER`, `LOWER`, `CAPS`, `FIRSTCAP`, `ROMAN`/`roman`,
`ALPHABETIC`/`alphabetic`, `ARABIC`, `MERGEFORMAT` and `CHARFORMAT`. The last two keep
the current result's starting character formatting. `\#` uses .NET numeric formats;
`\@` uses .NET date/time formats with Word's `AM/PM` mapped to `tt`. Numeric/date
switches require compatible values. Word-specific numeric-picture behavior beyond
those formats is not implemented. Rich host, contents and hyperlink results use
their own formatting rather than applying scalar formatting to every rich run.

The field-specific switch subset is:

| Fields | Evaluated switches |
| --- | --- |
| MERGEFIELD | `\b` prefix and `\f` suffix for nonempty values |
| SEQ | `\r` reset, `\c` repeat, `\n` next, `\h` hide, `\s` restart after a heading level |
| STYLEREF | `\l` selects the last matching paragraph in its story; ordinary selection uses the preceding matching paragraph or first following match |
| SYMBOL | `\f` font, `\s` size, `\u` Unicode interpretation |
| TOC | `\o` heading range, `\u` outline levels, `\t` style/level pairs, `\f` TC identifier, `\l` entry-level range, `\c` caption sequence, `\h` links, `\n` omit page numbers, `\p` separator |
| TC | `\f` identifier, `\l` level and `\n` omit this entry's page number; the marker itself has an empty visible result |
| REF | `\h` makes the copied rich result link to its bookmark |
| PAGEREF | `\h` makes the page-number result link to its bookmark |
| HYPERLINK | `\l` internal destination; `\o` tooltip for internal destinations |
| INCLUDEPICTURE | `\d` is accepted; the host resolver determines image storage and retrieval |

REF combined with scalar formatting produces text rather than preserving its
source's full rich formatting. External
HYPERLINK tooltip/target switches are diagnosed instead of fetching or navigating
their values. Other unsupported switch names/variants are diagnosed. This is not
a claim that every Word switch or switch combination is implemented. NEXT, ASK
and FILLIN are not activated as supported fields.

`FieldOperations.AdaptMergeField` converts an existing atomic merge field to a
range while retaining its format/fallback metadata. Existing atomic merge APIs and
their default processing semantics remain available.

## Layout and update policies

`FieldEvaluator.Update` separates ordinary and page-dependent evaluation.
`PaginationEngine.UpdateFields` and `editor.UpdateFieldsWithLayout` run ordinary
updates, paginate, and update page-dependent fields until both content and layout
stabilize. The default bound is eight iterations; non-convergence is reported as
`field.pagination-not-converged`. Session/editor updates commit one undo transaction.
TOC insertion can increase page count; a subsequent iteration recalculates links'
page numbers from the new physical layout.

Basic repeated header/footer PAGE/NUMPAGES/SECTIONPAGES results can use the existing
page-field inline rendering instances, so each sheet has its own value. Advanced
formatted or nested page expressions in repeated stories still require a host
projection; they must not be described as fully qualified per-page expressions.

Low-level load/save APIs retain cached results. Reporting load/save operations
accept `ConversionOptions.FieldOptions` for explicit updates and merge diagnostics
into their conversion report. Strict failed updates leave staged save destinations
untouched. `MailMergeOptions.FieldOptions` opts general fields into per-record
processing; the legacy merge behavior is unchanged by default.
`editor.OutputFieldOptions` opts print/preview/PDF into updating a detached snapshot;
null prints cached results, and non-converging output updates are rejected. All
these paths honor locks. External callbacks are never supplied implicitly.

`FieldCodeProjection.Create` provides a read-only code view and maps its display
positions to stored result boundaries. `ShowCode` persists a display preference;
normal document editing, IME, search and clipboard coordinates remain result
coordinates. In-place editing of a mixed code/result projection is not advertised.

## Navigation and persistence

**Navigate** exposes bookmarks and the outline. **Find** and **Replace** use
`TextaloniaFindReplacePanel`; Ctrl/Cmd+F and Ctrl/Cmd+H open them. Hosts can embed
the panel and replace command handling. Search matches identify session, story and
revision, do not split graphemes, and reject stale/foreign/overlapping replacement
requests. All-story replacement is one transaction. This slice implements literal
search; regex and whole-word search remain separate future work.

Native JSON v9 (reading v4-v9), data XAML v4 (reading v1-v4), and clipboard v5
(reading v1-v4) preserve ranges, source instructions, lock/dirty/display state,
internal links and properties. DOCX/RTF map standard field/bookmark markup and
rich cached results, including nested source instructions and secondary stories.
Some Textalonia-specific metadata uses ignorable extensions. Unknown fields retain
source and cache with diagnostics. See [the format matrix](INTERCHANGE.md) for
format-specific limits; HTML/Markdown/plain text do not preserve general field
semantics and report that loss through reporting APIs.

Evidence includes `BookmarkEditingTests`, `AnchoredFragmentTests`, `AnchoredRangeEditingTests`,
`AnchoredFuzzTests`, `GeneralFieldPageLayoutTests`,
`AnchoredFuzzTests` (two deterministic seeds, 180 edits each, validating anchors,
identities and grapheme boundaries after mutations and undo/redo),
`GeneralFieldTests`, `FieldLifecycleTests`, `FieldPaginationTests`,
`GeneralFieldPageLayoutTests`,
`FieldsInterchangeTests`, `DocumentNavigationTests` and `RtfInternalLinkTests`.
Native keyboard/screen-reader and external Office rendering qualification remain
under the existing release gates. Typed property authoring, complete Word switch
semantics, and editable mixed code/result projections are not completion claims.
