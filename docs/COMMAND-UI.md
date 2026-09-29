# Commands and document UI

`TextaloniaEditor.Commands` is the per-editor command catalog. Each
`EditorCommandId` resolves to a live `TextaloniaCommand` with an `ICommand`, a
localization key and fallback label, optional parameter type and shortcut, current
`CommandCapability`, and checked or mixed state. The editor's existing command
properties, default keyboard shortcuts, compact toolbar and context menu use these
same editor operations. Hosts can bind the commands in their own Avalonia UI or
invoke them directly:

```csharp
var bold = editor.Commands[EditorCommandId.Bold];
if (bold.CanExecute(null))
    await bold.ExecuteAsync();

await editor.Commands.ExecuteAsync(EditorCommandId.SetZoom, 1.25);
await editor.Commands.ExecuteAsync(EditorCommandId.GoToPage, 2); // one-based
await editor.Commands.ExecuteAsync(EditorCommandId.PasteSpecial, PasteSpecialFormat.PlainText);
```

`CanExecute` and `Capability` reflect read-only mode, edit policy and the active
selection. `Hidden` commands can be removed from host UI; `Disabled` commands may
remain visible. Execute rechecks permissions, reports failures through
`LastError`/`OperationFailed`, and does not give UI controls ownership of the
document. The `Commands.Localize` delegate receives keys such as
`Textalonia.Commands.Bold`; return `null` to keep the English fallback. Commands
that are checked expose `IsChecked` and `IsMixed` for tri-state UI.

## Optional command surface

`TextaloniaTabbedCommandSurface` binds to an editor and presents File, Home,
Insert, Page Layout, References, Proofing, Mailings and View tabs. Table, Picture
and Header/Footer tabs appear for the corresponding selection or story. Its
buttons execute catalog commands. `HostActionRequested` asks the host to handle
New, Open, Save and Mail Merge because the editor does not own storage or merge
data. The surface disables these buttons until a listener is attached.

```csharp
var commands = new TextaloniaTabbedCommandSurface { Editor = editor };
commands.HostActionRequested += async (_, args) =>
{
    if (args.Action == TextaloniaHostAction.Save)
        await SaveCurrentDocumentAsync();
};
```

The host can set `TextaloniaTabbedCommandSurface.Localize` for tab, group and host
action labels using `Textalonia.CommandSurface.*` keys. The compact
`TextaloniaToolbar` remains available through `ShowToolbar`, and both surfaces can
be placed or retemplated independently. The desktop demo has a Compact/Tabs
switch. When the compact toolbar is hidden, Find, Replace and Navigation open
panels from the tabbed surface. Its navigation panel exposes the document outline
and bookmark actions with keyboard access.

## Rulers and view status

Set `ShowRulers="True"` or execute `EditorCommandId.ToggleRulers` to show the
horizontal and vertical `DocumentRuler` controls. They use the same page zoom and
scroll transform as the editor surface. Print Layout and Draft show page, column,
paragraph, tab and table markers; Simple view has no physical page ruler. Dragging
a marker previews its position and commits one undoable edit on release. Escape or
capture loss cancels the preview. A focused horizontal ruler uses Up/Down to
choose a marker and Left/Right to adjust it; the vertical ruler uses Left/Right
to choose and Up/Down to adjust. Shift reduces the keyboard adjustment to one
document unit. The compact View flyout exposes rulers, page number, caret or
selection status, zoom and page navigation.

## Editing commands

The context menu and tabbed surface offer `PasteSpecialAsync` with
`NativeFragment`, `Html` and `PlainText`. It uses only the selected clipboard
representation, and cancellation or a stale selection leaves the document
unchanged. Normal `PasteAsync` keeps its native, HTML, then text fallback order.
The find/replace panel remains reusable as `TextaloniaFindReplacePanel`; it
searches the requested stories and replaces current results transactionally.
Outline and bookmark navigation are available through the editor API and compact
navigation flyout. Hosts can also place `TextaloniaNavigationPanel` directly and
set its `Editor`; its buttons execute the matching catalog commands.

`InsertSymbol(string)` accepts one Unicode text element and uses normal text
editing permissions and undo. `ShowInsertSymbolDialogAsync` exposes common symbols
and a typed entry. `ShowDocumentPropertiesDialogAsync` edits built-in and typed
custom document properties. Hosts can use
`EditorSession.SetDocumentProperties(core, customProperties)` for one validated,
undoable metadata transaction.

The built-in dialog methods retain the selection while open and return focus to
the document when they close. The model APIs remain available in hosts with
custom UI. Headless tests cover command state, surface context, ruler transforms,
Paste Special, property transactions and dialog focus. Native DPI and screen
reader qualification is tracked separately in the parity plan.

## Host services and completion events

`DialogService` accepts an `IEditorDialogService`. The host owns the service;
the editor owns each created modal window and passes its Avalonia owner. The
service must finish only after the dialog closes, and must honor its cancellation
token without applying a pending edit. `CancelActiveDialogs()` requests
cancellation on the UI thread. Built-in formatting and output dialogs use this
service. Their normal close path restores editor focus; cancel discards the
pending selection edit.

`SpellChecker`, `HyphenationService`, `FieldOptions`, `PdfExporter` and
`PrintService` are replaceable host-owned services or policies. `FieldOptions`
provides default field resolvers for `UpdateFieldsWithLayout`; a call can override
it with its own `FieldEvaluationOptions`. Output can use a distinct
`OutputFieldOptions` snapshot policy. The editor does not dispose host services,
field delegate targets or output streams.

`LoadCompleted` and `SaveCompleted` fire only after successful editor
`LoadAsync`/`SaveAsync` operations and include the format, captured document and
an optional conversion report. `LayoutCompleted` reports revision, view and page
count after geometry completes; it is queued after the render pass so handlers
can update UI safely. `EditingModeChanged` reports read-only,
protection, policy and identity changes. These events run on the editor's UI
thread; hosts should avoid reentrant edits from their handlers.
