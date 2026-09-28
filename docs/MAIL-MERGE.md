# Merge fields and mail merge

Textalonia supports named, atomic merge fields in paragraphs, sections and nested
tables. Use the editor toolbar's merge-field action to insert or edit a field, or
construct fields through the model/session APIs. The desktop demo's **Mail merge**
window keeps an editable template alongside recipient data and a read-only preview.
It can generate separate DOCX files in a ZIP, or one sectioned document for DOCX
and PDF export. Its invoice sample includes nested detail and tax rows.

## Author a template

```csharp
using Textalonia.Model;

var template = new FlowDocument([
    new Paragraph([
        new RichRun("Hello "),
        new RichRun(MergeFields.Create("FirstName", fallbackText: "friend")),
        new RichRun(", your balance is "),
        new RichRun(MergeFields.Create("Balance", format: "N2"),
            TextStyle.Default with { Bold = true }),
        new RichRun(".")
    ])
]);

// Also available on TextaloniaEditor; edits participate in undo/redo.
editor.Session.InsertMergeField("FirstName");
var current = editor.Session.CurrentMergeField;
if (current is not null)
    editor.Session.UpdateMergeField(current.Id, "DisplayName", fallbackText: "friend");
```

`MergeFieldInlinePayload` stores `Name`, optional .NET `Format`, and optional
literal `FallbackText`. `MergeFields.Create` creates an `InlineDescriptor` whose
initial display text is `«Name»`. A name is an exact, case-sensitive key, not a
property path or expression: `Customer.Name` requires that literal dictionary key.
Names are nonblank, at most 256 UTF-16 characters, and cannot contain controls.
Formats are at most 1,024 characters without controls; fallback text is at most
16,384 characters without NUL. Applications may discover unique names with
`MailMergeProcessor.GetFieldNames(template)` in first occurrence order.

Each field occupies one U+FFFC document position. Formatting, selection, deletion,
copy/paste and undo use the existing atomic-inline contract. Native clipboard paste
assigns a fresh field ID. The editor measures the displayed label using its run
font and style; labels have a maximum text width of 600 DIPs and display on one
line with ellipsis. Empty cached values remain empty labels. These visual limits
do not truncate the stored value or final merged text. Image/control descriptor
width and height behavior is unchanged. Managed accessibility describes the field
name and displayed value; native screen-reader qualification remains separate.

## Preview and generate documents

```csharp
using System.Globalization;
using Textalonia.MailMerge;

IReadOnlyDictionary<string, object?> recipient = new Dictionary<string, object?>
{
    ["FirstName"] = "Ada",
    ["Balance"] = 1234.5m
};
var options = new MailMergeOptions
{
    Culture = CultureInfo.GetCultureInfo("en-US"),
    MissingFieldBehavior = MissingFieldBehavior.Throw
};

var preview = MailMergeProcessor.Preview(template, recipient, options);
var completed = MailMergeProcessor.Merge(template, recipient, options);

IEnumerable<IReadOnlyDictionary<string, object?>> recipients = [recipient];
foreach (var document in MailMergeProcessor.MergeMany(template, recipients, options))
{
    // Save, print or collect one independent result per record.
}
```

These headless transforms leave the template unchanged. For templates without
repeating regions, preview updates field `AltText` while retaining definitions and
IDs, so another record can be previewed from that snapshot. For templates with
regions, preview expands the recipient's rows; preview each recipient from the
original template because the expanded snapshot no longer contains region markers.
Merge replaces resolved fields with ordinary text in the same run style. Paragraph,
section, table, list and resource data are preserved. The transform also visits
covered cells and cell merge backups, preventing a later table split from exposing
unresolved template data. `GetFieldNames` includes those retained fields too.

`MergeMany` validates the template/options immediately and enumerates recipients
lazily. Each returned document is separate. Cancellation is cooperative during
traversal and record enumeration. Culture is copied for deterministic formatting; the default is
invariant culture. Record values are read when that record is processed.

| Value | Behavior |
| --- | --- |
| Present non-null | Literal string/character/boolean or `IFormattable` value; optional .NET format uses the chosen culture |
| Null | Field fallback if supplied, otherwise empty text |
| Missing key with fallback | Literal fallback, including an explicitly empty fallback |
| Missing key without fallback | Throw by default; `KeepField` retains the field; `Empty` substitutes empty text |

Matching remains ordinal even if the supplied dictionary is case-insensitive.
No reflection, property traversal, script evaluation, SQL or data-source access is
performed. Hosts supply dictionaries and adapters for their chosen data source.
Unsupported value types or incompatible formats fail explicitly; a nonempty format on strings,
characters or booleans throws `FormatException`. Values are bounded to 16,384 UTF-16
characters and cannot contain NUL. Standard numeric precision above 1,024 is rejected
before formatting. Newlines in values become
soft line breaks, preserving the template's paragraph structure.

## Recipient sources, schema and selection

The dictionary methods above remain the simplest input. `IMailMergeDataSource`
adds a host-owned `Schema` and `GetRecipients(CancellationToken)` for larger batches.
`MailMergeSchema` lists scalar fields and named child-collection schemas. A
`DictionaryMailMergeDataSource` discovers the union of fields and re-readable
child lists from a finite `IReadOnlyList` of dictionaries. For a lazy source, pass
an explicit schema to its enumerable constructor; `DelegateMailMergeDataSource`
accepts an explicit schema and a host record factory. Schema discovery on a finite
collection reads that collection. It is not an implicit query against a database.

```csharp
IReadOnlyList<IReadOnlyDictionary<string, object?>> batch = [recipient];
var source = new DictionaryMailMergeDataSource(batch);
var selection = new MailMergeRecipientSelection
{
    SourceIndexes = [0], // zero-based positions in the original source
    Filter = record => record.Values.ContainsKey("FirstName")
};
foreach (var document in MailMergeProcessor.MergeFromSource(
    template, source, selection, options))
{
    // One independent document for each selected recipient.
}
```

`SortComparer` can also order selected records. Index selection runs first, then
`Filter`, then the stable sort. Without sorting, selected output stays lazy; sorting
buffers the selected recipients. A `MailMergeRecord` has scalar `Values` and named,
lazy `Children`. `ToMergeValues()` projects its children as
`IEnumerable<IReadOnlyDictionary<string, object?>>` for the dictionary merge API.
For example, a host can supply an invoice recipient with `Items` child records and
give each item its own `Taxes` collection. Empty or absent child collections produce
no repeated content. Hosts own opening and closing any database, file or service
connection; imported document connection information is never followed.

## Repeating regions

Insert atomic merge fields named `TableStart:Items` and `TableEnd:Items` around
blocks or table rows. The part after the colon is the exact, case-sensitive child
collection key. The `TableStart:` and `TableEnd:` prefixes accept any casing. A
paragraph region can contain ordinary paragraphs, nested sections and tables, and
other properly nested regions:

```csharp
var invoice = new FlowDocument([
    new Paragraph([new RichRun(MergeFields.Create("TableStart:Items"))]),
    new Paragraph([new RichRun("Item: "),
        new RichRun(MergeFields.Create("Description"))]),
    new Paragraph([new RichRun(MergeFields.Create("TableEnd:Items"))])
]);
IReadOnlyDictionary<string, object?>[] items =
[
    new Dictionary<string, object?> { ["Description"] = "Pencil" },
    new Dictionary<string, object?> { ["Description"] = "Notebook" }
];
var mergedInvoice = MailMergeProcessor.Merge(invoice,
    new Dictionary<string, object?> { ["Items"] = items });
```

Each paragraph boundary must consist of exactly one run containing the marker
field, with no neighboring text. Table-row boundaries use a dedicated row with
exactly one marker cell; its other cells each contain one empty paragraph. Marker
rows cannot be repeating header rows or merged cells, and table-row regions cannot
share a table with vertical cell merges. Start/end names must match and nest within
the same block list or table. Unmatched, crossing or misplaced markers are rejected
when the template is prepared, before a lazy recipient source is read.

A region value must be an `IEnumerable<IReadOnlyDictionary<string, object?>>`.
Missing, null or empty collections remove the region's content. If this leaves a
document, section or cell with no blocks, the model receives an empty paragraph;
a table with no remaining rows is omitted. Each repeated row sees its own fields
and inherited scalar ancestor values. A nested collection is read from its
immediate parent record, so a missing `Taxes` collection on one item does not reuse
another collection. Repeated blocks and rows get fresh identities, including
applicable bookmarks and field ranges; internal bookmark references are repaired.
An anchored range that crosses a region boundary is rejected during template preparation.
Repeating note references inside a region is unsupported. Expansion is limited to
100,000 child records per recipient.

`Preview` shows expanded regions for one recipient. To switch recipients, call it
again with the original template. The marker fields are consumed by expansion and
do not remain in the preview or generated document. `GetFieldNames` lists marker
names from an unexpanded template along with ordinary atomic field names; use the
data-source schema when presenting recipient data fields.

## General fields and lifecycle callbacks

Atomic merge fields are resolved by the merge methods. General `DocumentField`
instructions retain their cached results unless `MailMergeOptions.FieldOptions` is
set. With the default `FieldEvaluationOptions.Mode` (`Ordinary`), ordinary fields
are evaluated after region expansion, using the correct recipient or child-row
scope. This includes
nested `IF` and formula instructions, supported numeric/date/general switches, and
`MERGEFIELD` references inside other fields. See [Fields](FIELDS.md) for the exact
instruction and switch subset.

`FieldEvaluationOptions.DocumentVariableResolver` can return a rich
`FlowDocument` fragment for `DOCVARIABLE`. `PictureResourceResolver` can return a
`FieldPictureResult` containing a host-supplied image resource for
`INCLUDEPICTURE`; the legacy `PictureResolver` can return a rich fragment. Neither
field opens the path or URI in its instruction. `FieldResolveContext.MergeValues`
contains the values scoped to that field.

`MailMergeOptions.RecordStarting` and `RecordCompleted` identify the selected
recipient index and whether the operation is a preview. `RegionProgress` identifies
the child collection, item index, nesting depth and start/completion event.
`RecordDiagnostic` tags failures and field diagnostics with the recipient index;
`FieldDiagnostic` receives general-field diagnostics. Callback exceptions abort
the operation. A supplied cancellation token stops record enumeration and region
expansion cooperatively. The merge culture is cloned when processing starts, so
formatting does not drift if the caller later changes it.

## Combined document output

`MailMergeCombinedProcessor.Merge` accepts dictionaries or an
`IMailMergeDataSource` with the same selection options. It adds a next-page physical
section for each later recipient. `CombinedMailMergeOptions.PageNumberPolicy` can
restart at one (the default), continue, or retain the template setting.
`HeaderFooterPolicy` uses each recipient's header/footer stories by default, or
links later records to the previous section. Separate documents remain the default
output of `MailMergeProcessor.MergeMany` and `MergeFromSource`.

```csharp
using Textalonia.Layout;
using Textalonia.Model.Fields;

var combined = MailMergeCombinedProcessor.Merge(template, source, selection, options,
    new CombinedMailMergeOptions
    {
        PageNumberPolicy = CombinedPageNumberPolicy.RestartEachRecord,
        CompleteDocument = (document, token) =>
        {
            using var engine = new PaginationEngine();
            return engine.UpdateFields(document, new FieldEvaluationOptions
            {
                Culture = options.Culture,
                OnlyDirty = true,
                CancellationToken = token
            }).Document;
        }
    });
```

The completion callback runs once after all records have been expanded and joined.
Use it in a host with the required font/UI context to resolve page-dependent
fields against the final pagination. The combined document itself must be held in
memory; separate output can be streamed record by record. The desktop demo exposes
recipient preview and inclusion controls, an invoice sample with nested rows,
separate DOCX ZIP generation, and combined DOCX/PDF export.

## Storage and interchange

| Format | Field behavior |
| --- | --- |
| Native JSON schema 5, data XAML, native clipboard | Preserve definitions, formatting, fallback and cached display text |
| DOCX | Reads simple and complex `MERGEFIELD` instructions, including instructions split across runs; writes simple fields with cached display text |
| RTF | Reads/writes `MERGEFIELD` instructions and their cached result |
| HTML, Markdown, plain text | Export displayed text with a merge-field loss diagnostic; strict conversion rejects flattening |

DOCX/RTF preserve the basic atomic field name, cached display and representable run style.
The .NET `Format` and `FallbackText` options have no general equivalent in Word's
field language: exporting an unresolved field with those options reports loss.
Generate the final document first when formatted values must transfer faithfully.
General fields have their own persistent instructions and rich cached results;
their evaluated language and switch subset are described in [Fields](FIELDS.md).
Unknown instructions and switches retain cached results with diagnostics during
explicit evaluation. Unsupported interchange, malformed field boundaries and
unrepresentable cached result formatting are reported by conversion diagnostics,
with strict conversion available to reject loss. `NEXT`, `ASK` and `FILLIN` are
not activated as merge commands. Repeating regions use the named marker convention
above; exporting a completed merge writes its expanded content. No recipient data
source or linked Word database connection is fetched from an imported document.

Use `LoadWithReportAsync` / `SaveWithReportAsync` to inspect losses, or pass
`new ConversionOptions { Mode = ConversionMode.Strict }` to reject them. Basic
`LoadAsync` / `SaveAsync` retain the existing tolerant API contract.

Native schema v5 preserves the allowlisted `mergeField` payload and optional
properties. The reader also accepts v4 documents as explicit direct formatting. Older binaries without the field payload reject it rather than
silently converting it into text. There is no migration promise for unpublished
prerelease schemas.

## Verification and scope

Regression coverage includes typed fields, metadata rejection, clipboard/undo,
read-only editing, layout and accessibility, preview/batch behavior, cancellation,
formatting and missing values, nested regions, table rows and hidden cells,
adapter selection and schema discovery, combined sections, native/XAML round trips,
DOCX simple/complex fields, RTF fields and strict loss handling. The package consumer
also exercises insertion, preview and generation against the packed library.
Synthetic Office specimens verify codec behavior; they do not replace the open
Word/LibreOffice application qualification work in [the roadmap](ROADMAP.md).
