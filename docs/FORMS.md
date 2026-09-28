# Protected editing and forms

DX-09 adds immutable structured controls, session-enforced editing policies, and
protected form interaction. Host code supplies user/group identity; Textalonia does
not authenticate users. Editing protection is distinct from file encryption.

## Authoring and filling

```csharp
var session = new EditorSession(FlowDocument.FromText("Name: "));
session.Select(6, 6);
var name = session.InsertContentControl(new DocumentContentControl
{
    Kind = ContentControlKind.PlainText,
    Value = "Guest", Placeholder = "Enter your name",
    Tag = "customer-name", Title = "Customer name", LockControl = true
});
session.Protect(new DocumentProtection { Mode = DocumentProtectionMode.FormsOnly }, "password");
session.SetContentControlValue(name!.Id, "Ada");
```

Plain and rich text use persistent story/paragraph anchors. Plain text accepts one
paragraph; rich text supports paragraph content. Checkbox, combo box, dropdown and
date controls occupy one U+FFFC position with `FormControlInlinePayload`. Their
values, list items, tags, title, placeholder, locks and bounded application data
belong to `DocumentContentControl`, independent of registered Avalonia controls.
Checkbox values are `true`/`false`; dates use ISO `yyyy-MM-dd`. Dropdown values must
match an item; combo boxes accept other text. Empty values are allowed. DateFormat
affects display; stored dates remain ISO values.
`ValidateValue` returns a validation message; `SetContentControlValue` returns false
for invalid, locked or denied updates. Ordinary typing synchronizes text values.
Atomic placeholders appear inline. Empty plain/rich text placeholders appear in the
Forms value editor; zero-length text ranges do not reserve document layout space.

`LockContents` forbids filling and content changes; `LockControl` prevents removing
or reconfiguring the wrapper while allowing filling. Picture, repeating-section and
building-block-gallery controls preserve imported content and metadata but have no
interactive replacement, repetition or gallery authoring. `IsLegacyFormField`
identifies imported legacy fields; the same value APIs edit supported legacy text,
checkbox and dropdown forms. Export converts these to structured controls with an
explicit legacy-encoding diagnostic.

Use `SelectContentControl`, `SelectNextContentControl`, `ToggleContentControl`, and
`RemoveContentControl` for code interaction. Tab/Shift+Tab cycle through supported unlocked
controls across stories; Space toggles a selected checkbox and Enter opens the
list/date value editor. Click toggles a checkbox; double-click opens other atomic
values. The toolbar Forms menu opens the same validated editor.
Plain/rich text uses normal text input and IME commit. The demo includes a protected
form example. Rendering, print preview and PDF show the current values as static
content; exported PDFs do not contain interactive form fields.

## Permissions and atomic changes

`EditorSession.EditPolicy.Commands` maps `EditOperation` to `Enabled`, `Disabled` or
`Hidden`. Both disabled and hidden capabilities reject editing. `GetCapability`
combines host command state, read-only mode and the current selection. The toolbar
uses that result; session transactions independently inspect the complete proposed
snapshot. `Identity` contains the host-resolved user and group names and is never
persisted with the document.

`DocumentProtection` supports `ReadOnly` and `FormsOnly`. Empty
`ProtectedSectionIds` applies to the document; a nonempty list applies to the named
physical sections, with secondary stories remaining protected. A
`DocumentPermissionRange` with `IsReadOnly=true` is always read-only. Otherwise it
is an exception for the named user/group; an unspecified identity or `everyone`
grants all hosts access to that range. Explicit read-only ranges take precedence.
Permission boundaries follow insertion, deletion and paragraph splitting.

Text input, IME commit, paste/drop, replace-all, table and inline changes, field
updates, arbitrary `Execute`, and undo/redo share the transaction check. A mixed
permitted/forbidden edit is rejected in full, retaining document, revision,
selection and history. Undo/redo use the current identity and policy; an edit that introduces a new lock
can therefore create a history boundary that cannot be undone while locked. Ambiguous
structural edits conservatively require permission for the affected story;
ordinary paragraph splitting before protected content retains its anchors.
Changing global styles/resources is conservatively rejected when it could change
protected content. Cross-editor moves from a protected source become copies.

`Load` remains an explicit trusted host replacement. User `Execute` cannot erase
protection or existing permission locks. `Protect` and `TryUnprotect` are explicit
protection transitions and clear history to prevent undo from removing a password
requirement. Only salted verifiers are stored. New passwords use the Office SHA-512
write-protection hash; SHA-1/256/384/512 Office verifiers and native PBKDF2-SHA256
verification are supported, with bounded iteration counts. Unknown/legacy verifier
algorithms cannot unlock a document. This does not encrypt content; encrypted
package I/O remains DX-12. The hash implementation follows Microsoft's
[ISO write-protection method](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-offcrypto/1357ea58-646e-4483-92ef-95d718079d6f).

## Interchange and binding boundaries

Native JSON v11 (v4-v11 readable), data XAML v6 (v1-v6 readable), and structured
clipboard retain form and protection metadata. Paste clones control, inline and
permission IDs and remaps anchors. Clipboard insertion retains the destination's
protection settings. Old snapshots load with no form controls or restrictions.

DOCX reads/writes standard `w:sdt` controls, locks, choices, dates, permission
markers and document/section form protection. Inline ranges and complete paragraph
ranges become ordinary Word SDTs. Partial cross-paragraph or cross-container ranges
retain exact native extension markers and report that Word displays ordinary
content. Native read-only spans and arbitrary host groups retain metadata with a
loss diagnostic because Word permission exceptions cannot express those semantics.
Supported Office password verifiers map to DOCX protection attributes.

Bindings retain `storeItemID`, XPath and namespace mappings. Explicit
`ContentControlBinding.TryReadValue` or `ApplyContentControlBinding` evaluates only
host-supplied XML. It accepts absolute named paths, numeric indexes and a final
text or attribute selection, within 1,048,576-character, 10,000-element and 32-depth bounds. It does
not resolve external data, execute arbitrary XPath functions, or allow DTDs.
Custom XML package parts are not loaded/exported and receive a binding diagnostic.

Formats without these semantics report content-control/protection losses; strict
conversion rejects before writing. Native, DOCX, policy, headless interaction and
static output regressions cover the supported subset. Word/LibreOffice-generated
corpus qualification, native OS IME/accessibility and encrypted-file qualification
remain separate release gates; managed tests do not constitute those passes.
