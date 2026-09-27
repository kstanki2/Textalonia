# Textalonia

An independent, native rich text editor for **Avalonia 12** and **.NET 8+**, distributed as one NuGet package.

**Status: 0.1.0-preview.1 candidate; not yet published.** This repository contains a working editor, desktop demo, tests, and local NuGet packaging. It is not a feature-complete or API-compatible replacement for Avalonia's commercial editor. See [the feature matrix and roadmap](docs/ROADMAP.md) before adopting it.

## Run the demo

Install a .NET 8 or newer SDK, then open `Textalonia.sln` in Visual Studio/Rider, or run:

```sh
dotnet restore Textalonia.sln --configfile NuGet.Config
dotnet run --project samples/Textalonia.Demo
```

The demo includes editable sample content, light/dark themes, read-only mode, search, tables, and open/save dialogs. Use **Textalonia (.textalonia)** for lossless storage; the interchange formats support the subsets described below.

## Build, test, and pack

```sh
dotnet build Textalonia.sln -c Release --no-restore
dotnet test tests/Textalonia.Tests -c Release --no-build
dotnet pack src/Textalonia -c Release --no-build -o artifacts/packages
```

Verify the packed artifact through an independent consumer (after packing):

```sh
dotnet restore tests/Textalonia.PackageSmoke --configfile tests/Textalonia.PackageSmoke/NuGet.Config
dotnet run --project tests/Textalonia.PackageSmoke -c Release --no-restore
```

Output: `artifacts/packages/Textalonia.0.1.0-preview.1.nupkg`, plus a symbols package. Nothing is published automatically. The library's Avalonia dependency is bounded to **[12.1.3, 13.0.0)**. The demo and tests use 12.1.3, configured centrally in `Directory.Build.props`.

## Use the package in another app

Add the package output folder as a NuGet source alongside nuget.org, for example in your application's `NuGet.Config`:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="TextaloniaLocal" value="C:/path/to/Textalonia/artifacts/packages" />
  </packageSources>
</configuration>
```

Then, from your Avalonia 12 application directory:

```sh
dotnet add package Textalonia --version 0.1.0-preview.1
```

This preview has **not** been published to nuget.org. The command above requires the local feed.

Add the control theme after your application theme in `App.axaml`:

```xml
<Application.Styles>
  <FluentTheme />
  <StyleInclude Source="avares://Textalonia/Themes/Generic.axaml" />
</Application.Styles>
```

Drop the editor into a window:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:text="using:Textalonia.Controls">
  <text:TextaloniaEditor Text="Start writing here..."
                       ShowToolbar="True"
                       MinHeight="300" />
</Window>
```

The package contains no desktop platform dependency or application entry point. The host application chooses its Avalonia backend and theme.

## Structured documents and MVVM

`Document` supports two-way binding to a `FlowDocument` property on your view model:

```xml
<text:TextaloniaEditor Document="{Binding Document, Mode=TwoWay}" />
```

With compiled bindings enabled, set `x:DataType` on the enclosing view as usual.

```csharp
using Textalonia.Model;

var document = new FlowDocument([
    new Paragraph([
        new RichRun("Hello "),
        new RichRun("Avalonia", TextStyle.Default with { Bold = true }),
        new RichRun("!")
    ]),
    new Paragraph("A second paragraph")
]);

editor.Document = document;
```

Documents and their arrays are immutable snapshots. An edit publishes a new `Document`; unchanged paragraph objects and run strings can be shared with prior snapshots. In-place collection mutation is intentionally unsupported. Public records permit `with` expressions; use `Validate()` when constructing documents from external data. Each block and cell must have a unique nonempty ID.

`Text` is a plain-text convenience binding: **assigning Text replaces all document structure and formatting**. Bind either `Document` or `Text`, rather than both to competing sources.

For large structured documents, opt out of eager full-text synchronization:

```xml
<text:TextaloniaEditor Document="{Binding Document, Mode=TwoWay}"
                      SynchronizeText="False" />
```

In this mode `Text` retains its last published/assigned value. Read `Document.Text`
explicitly for complete current text, or `Session.Index.ReadText(start, length)`
for a range. Re-enabling synchronization immediately updates `Text`. The default
mode preserves existing text bindings and their linear materialization cost.
See [the engine decision](docs/ADR-002-SCALABLE-CORE.md) and
[performance limits](docs/PERFORMANCE.md) for windowed layout and qualification.

Hosts can also set `MaxShapingCharacters` (for example, `65536`) to cap each
document-paragraph shaping input. It defaults to zero for unrestricted exact
rendering. Oversized contexts suspend rendering and expose `LayoutError` without
changing the document or history. See [the optional limit contract](docs/COMPATIBILITY.md#optional-shaping-limit).


## Editing API

```csharp
editor.Session.Select(0, 5); // UTF-16 anchor and active/caret offsets
editor.ApplyStyle(s => s with { Bold = true, Foreground = "#356FBA" });
editor.Session.ToggleList(ListKind.Bullet);
editor.InsertTable(rows: 3, columns: 3);
editor.Undo();
editor.Redo();

editor.Session.UndoLimit = 100;
editor.Session.HistoryByteLimit = 64 * 1024 * 1024; // exclusive retained-history estimate
editor.FindNext("Avalonia");
editor.ReplaceAll("old", "new");

// Application-defined immutable changes participate in undo:
editor.Session.Execute(document => document with { /* replace Blocks here */ });
```

Available commands: `BoldCommand`, `ItalicCommand`, `UnderlineCommand`, `StrikethroughCommand`, `UndoCommand`, `RedoCommand`, `CutCommand`, `CopyCommand`, `PasteCommand`, and `SelectAllCommand`. Set `ShowToolbar="False"` to supply your own toolbar.

`EditorSession` can be used without creating any UI. `DocumentChanged`, `SelectionChanged`, and `Session.Changed` expose change notifications. UI controls and their sessions must be accessed on the UI thread; immutable documents can be passed to worker threads. `CreatePosition` returns a session/revision-scoped position; `TryResolvePosition` rejects it after edits, load, undo or redo. History byte limits can evict even a single oversized entry; `RetainedHistoryBytes` reports the estimate, excluding the current document and caller-owned snapshots.

Selection uses UTF-16 offsets in `Document.Text`, with one LF between visible paragraphs. Caret navigation and deletion respect .NET grapheme boundaries. A soft line break is U+2028. Drag, double-click word selection, triple-click paragraph selection, Shift selection, and standard Ctrl/Cmd editing shortcuts are supported. Shift+Enter inserts a soft break; Enter splits a paragraph. Tab moves between table cells or inserts a tab when `AcceptsTab` is enabled.

## Save and load

```csharp
using Textalonia.Serialization;

await using (var output = File.Create("notes.textalonia"))
    await editor.SaveAsync(output, DocumentFormats.Json);

await using (var input = File.OpenRead("notes.docx"))
    await editor.LoadAsync(input, DocumentFormats.Docx);

// Or operate directly on an immutable document without a control:
await DocumentFormats.Html.SaveAsync(document, outputStream);
```

Streams remain owned by the caller. Encoding/parsing runs on a worker thread; asynchronous stream I/O observes cancellation. A canceled operation may have written part of its output, so use a temporary file and rename for application-level atomic saves. Loading through the control rejects a result if the user edited while the file was being read.

| Format | Supported interchange |
| --- | --- |
| Textalonia / JSON | Versioned, lossless native model, including section styling and merge backups |
| Plain text | Visible text and paragraph separators |
| HTML | Identified/nested lists, start/restart, rich typography, safe links, styled sections, nested tables/spans/sizing, embedded raster images and a bounded inline CSS subset |
| RTF | Unicode and common typography, numbered/bullet lists, safe link fields, flow sections, rectangular tables/merges/sizing, embedded PNG/JPEG; nested tables and section/cell decoration have diagnosed losses |
| DOCX | Numbering definitions/restarts, inherited styles, safe links, nested tables/merge geometry/sizing/edges, section content groups and embedded raster images; page layout/revisions and section decoration have diagnosed losses |

HTML import never executes scripts or loads remote images/styles. Supported embedded images retain their data; unavailable images degrade to alternative text with diagnostics. DOCX parsing prohibits XML DTDs/external entities and limits package sizes. Only http, https, and mailto link targets are accepted. These converters do not guarantee arbitrary Word/browser document fidelity.

Use `format.LoadWithReportAsync` / `SaveWithReportAsync` (also available on the
editor) to receive stable diagnostic codes, severity, model/source locations and
the fallback taken. `ConversionOptions.Mode = ConversionMode.Strict` rejects
reported loss before writing export bytes; `PlainTextOnly = true` explicitly
requests text degradation. The original API remains compatible. Legacy custom
codecs report unknown fidelity; implement `IReportingDocumentFormat` to supply
reports. The demo displays reports and offers a **Conversion report** button for
clipboard notices. See [the full support and stream contracts](docs/INTERCHANGE.md)
and [platform qualification](docs/QUALIFICATION.md).
Implement `IDocumentFormat` to add a format and pass your instance to `LoadAsync`/`SaveAsync`. The native `.textalonia` format is a versioned JSON schema, **not Avalonia XAML**. The `.json` and legacy `.art` extensions remain supported.

## Viewer, themes, highlights, and links

Use `TextaloniaViewer` for an initially read-only, selectable display without a toolbar, or set `IsReadOnly="True"` on an editor.

The theme uses `TextaloniaBackground`, `TextaloniaForeground`, `TextaloniaToolbarBackground`, and `TextaloniaBorder` resources with light/dark variants. The visual template and optional `TextaloniaToolbar` can be replaced. Text colors that are null inherit the theme; explicit document colors remain explicit.

```csharp
editor.Highlights.Add(new TextHighlight(0, 5, Brushes.Gold));
editor.HyperlinkActivated += (_, e) => ShowLinkInYourApplication(e.Uri);
editor.OperationFailed += (_, e) => ShowError(e.Exception.Message);
```

Highlights use snapshot offsets: update or clear them after edits. Ctrl/Cmd-click activates links in editable mode; ordinary clicks activate them in read-only mode. The host decides how to open a link. Asynchronous command failures raise `OperationFailed` and set `LastError`.

## Table behavior

Table text participates in normal selection, formatting, and undo. Insert/delete rows and columns through merged spans, merge/split cells, and change cell backgrounds through the toolbar or model APIs. Cell `Blocks` can contain nested tables and sections; table commands target the innermost cell. Persisted column widths, row sizing, cell padding and independent borders are available through model APIs.

Merging retains original cells. Splitting an unedited merge restores them exactly. If a merged cell was edited, splitting keeps its edited blocks in the anchor cell and restores the other original cells. Undo always restores the exact previous state. See [document semantics](docs/DOCUMENT-MODEL.md) for structural deletion rules, schema v1-to-v2 migration, list restart/continuation, mixed-selection state and typography APIs.

Cross-cell text replacement preserves table structure; selecting and replacing the entire document clears its structure. Versioned rich clipboard fragments preserve sections, nested/merged tables and inline resources. Repeated paste remaps object/list identities and colliding resource keys; partial table selections clip unselected content. See [conversion and clipboard contracts](docs/INTERCHANGE.md) for boundary and destination merging rules.

## Editing gestures

The default components provide visual bidi navigation, stationary-pointer edge
scrolling, structured content drag/drop, table resize previews and rectangular cell
selection. Alt-drag or Alt+Shift+Arrow selects table cells; release commits a resize
once and Escape cancels. The toolbar exposes typography, table borders/padding and
list restart/continuation. See [interaction contracts](docs/INTERACTIONS.md) for
modifiers, undo ownership and the additive table APIs.

Touch gestures and isolated Android/iOS qualification hosts are implemented.
[Qualification status](docs/QUALIFICATION.md) keeps native desktop and mobile-device
qualification explicit; headless tests do not certify those integrations.

## Repository and release status

- `src/Textalonia`: packable control, model, editing, serializers, theme.
- `samples/Textalonia.Demo`: desktop application.
- `tests/Textalonia.Tests`: model, serializer, binding, headless input and rendering tests.
- `tests/Textalonia.PackageSmoke`: separate consumer that references the generated NuGet package.
- `docs/ARCHITECTURE.md`: design and extension points.
- `docs/ROADMAP.md`: remaining work toward the reference editor's feature set.
- `.github/workflows/ci.yml`: build/test/pack and consumer checks; no publishing.

Retained measurements are indexed in [benchmark baselines](docs/BENCHMARK-BASELINES.md), with
[qualification targets](docs/QUALIFICATION.md), [compatibility rules](docs/COMPATIBILITY.md),
[native procedures](docs/NATIVE-BASELINES.md), and [performance budgets](docs/PERFORMANCE.md).
Run the complete verification route with `pwsh -File scripts/Invoke-Baselines.ps1`.
Add `-LongCorpus` for 2,000 operations per seed and `-Performance` for full control benchmarks.
The bounded suite runs on every change; `.github/workflows/baselines.yml` schedules the
longer corpus and performance captures separately. Native pending records remain visible in CI artifacts.

Textalonia is [MIT licensed](LICENSE), attributed to Textalonia contributors.
[Dependency notices](THIRD-PARTY-NOTICES.md) retain upstream terms. The
[API contracts](docs/API-CONTRACTS.md),
[preview notes](docs/RELEASE-NOTES.md) and [release procedure](docs/RELEASE.md)
describe candidate validation and remaining owner/native qualification gates.
Run PowerShell 7+ with `scripts/Invoke-ReleaseCandidate.ps1 -LongCorpus -Performance`
to retain exact packages, source/symbol checks, clean consumer logs and workload
evidence. Authenticated NuGet package control, completed OS-matrix evidence and an
explicit publication action are still required; nothing is automatically published.

### Extensible input and inline content

Replace keyboard, pointer, caret or IME behavior independently through the editor's
component properties. Defaults preserve standard selection, clipboard and typing;
the demo includes an alternate keymap and caret. See [input contracts](docs/INPUT-COMPONENTS.md).

Insert immutable inline image/control descriptors with `Session.InsertInline`,
resize or update them with `Session.UpdateInline`, resolve external images through
`InlineResourceResolver`, and register explicit control factories through
`InlineControlFactories`. Native schema v4 preserves descriptors and encoded
resources without creating controls during save/load. See
[inline content and ownership](docs/INLINE-CONTENT.md).

`editor.Accessibility` exposes a tested text-range contract for host bridges.
Avalonia 12.1.3 does not expose a public native text-provider contract; full native
screen-reader text navigation remains [explicitly blocked](docs/ACCESSIBILITY.md).


## XAML and Markdown

Use `DocumentFormats.Xaml` for Textalonia's versioned, data-only `.txaml`/`.xaml`
vocabulary and `DocumentFormats.Markdown` for the documented `.md`/`.markdown`
dialect. `MarkdownViewer` adds asynchronous source updates and optional host-provided
code highlighting while reusing selection, themes, links and resource services.
No new dependencies are required. Native JSON writes schema v4 and still reads
v1-v3. See [integration boundaries and examples](docs/INTEGRATIONS.md),
[Markdown dialect](docs/MARKDOWN.md), and [XAML vocabulary](docs/XAML.md).
