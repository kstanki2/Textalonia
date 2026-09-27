# Merge fields and mail merge

Textalonia supports named, atomic merge fields in paragraphs, sections and nested
tables. Use the editor toolbar's merge-field action to insert or edit a field, or
construct fields through the model/session APIs. The desktop demo's **Mail merge**
window keeps an editable template alongside recipient data and a read-only preview.
It can generate one document per record and export the results as DOCX files in a ZIP.

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

These headless transforms leave the template unchanged. Preview updates field
`AltText` while retaining definitions and IDs, so another record can be previewed.
Merge replaces resolved fields with ordinary text in the same run style. Paragraph,
section, table, list and resource data are preserved. The transform also visits
covered cells and cell merge backups, preventing a later table split from exposing
unresolved template data. `GetFieldNames` includes those retained fields too.

`MergeMany` validates the template/options immediately and enumerates recipients
lazily. Each returned document is separate; it does not concatenate letters into a
single paginated document. Cancellation is cooperative during traversal and record
enumeration. Culture is copied for deterministic formatting; the default is
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

## Storage and interchange

| Format | Field behavior |
| --- | --- |
| Native JSON schema 4, data XAML, native clipboard | Preserve definitions, formatting, fallback and cached display text |
| DOCX | Reads simple and complex `MERGEFIELD` instructions, including instructions split across runs; writes simple fields with cached display text |
| RTF | Reads/writes `MERGEFIELD` instructions and their cached result |
| HTML, Markdown, plain text | Export displayed text with a merge-field loss diagnostic; strict conversion rejects flattening |

DOCX/RTF preserve the basic field name, cached display and representable run style.
The .NET `Format` and `FallbackText` options have no general equivalent in Word's
field language: exporting an unresolved field with those options reports loss.
Generate the final document first when formatted values must transfer faithfully.
Unknown Word switches, nested/unsupported fields, malformed field boundaries and
unrepresentable cached result formatting are diagnosed, with strict conversion
available to reject loss. This is not a general Word field evaluator: `IF`, `NEXT`,
`NEXTIF`, `ASK`, `FILLIN`, repeating regions, numeric/date field switches and linked
Word recipient sources are outside the supported subset. No data source is fetched.

Use `LoadWithReportAsync` / `SaveWithReportAsync` to inspect losses, or pass
`new ConversionOptions { Mode = ConversionMode.Strict }` to reject them. Basic
`LoadAsync` / `SaveAsync` retain the existing tolerant API contract.

The unreleased native schema marker remains 4 under the current prerelease policy;
the allowlisted `mergeField` payload and optional properties are additive to the
current model. Older binaries without the field payload reject it rather than
silently converting it into text. There is no migration promise for unpublished
prerelease schemas.

## Verification and scope

Regression coverage includes typed fields, metadata rejection, clipboard/undo,
read-only editing, layout and accessibility, preview/batch behavior, cancellation,
formatting and missing values, nested/hidden cells, native/XAML round trips, DOCX
simple/complex fields, RTF fields and strict loss handling. The package consumer
also exercises insertion, preview and generation against the packed library.
Synthetic Office specimens verify codec behavior; they do not replace the open
Word/LibreOffice application qualification work in [the roadmap](ROADMAP.md).
