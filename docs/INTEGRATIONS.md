# XAML and Markdown integrations

Textalonia ships data-only XAML, a bounded Markdown dialect, and `MarkdownViewer`
in the main `Textalonia` package. These integrations add **no package dependencies**.
The core still depends only on Avalonia and the existing AngleSharp HTML parser.
Highlighting is an optional host adapter; no language engine is bundled or required.

- [XAML vocabulary, limits, and security](XAML.md)
- [Markdown dialect, fixtures, and loss policies](MARKDOWN.md)
- [Viewer updates, cancellation, resources, and highlighting](MARKDOWN-VIEWER.md)
- [Platform qualification and remaining gates](QUALIFICATION.md)

## Format selection and diagnostics

`DocumentFormats.Xaml` accepts `.txaml` and `.xaml`; `DocumentFormats.Markdown`
accepts `.md` and `.markdown`. Existing native `.textalonia`, `.json`, and `.art`
selections remain unchanged. XAML means Textalonia's own versioned data vocabulary;
it makes no compatibility claim with Avalonia, WPF, or another editor's XAML.

```csharp
using Textalonia.Serialization;

var result = await DocumentFormats.ForPath(path).LoadWithReportAsync(input,
    new ConversionOptions { Mode = ConversionMode.Strict }, cancellationToken);
var document = result.Document;
await DocumentFormats.Xaml.SaveWithReportAsync(document, output);
```

Use reporting APIs when fidelity matters. Tolerant conversions return diagnostics;
strict conversions reject reported losses before writing to the destination.
Codecs do not create controls, access local image paths, navigate links, or download
resources. Streams remain caller-owned. The shared 32 MiB stream limit applies.
The synchronous Parse/Serialize convenience APIs do not return reports.

## Viewer and host policy

```csharp
using Textalonia.Controls;

var viewer = new MarkdownViewer();
viewer.HyperlinkActivated += (_, e) => ShowLinkToUser(e.Uri);
viewer.InlineResourceResolver = myResourceResolver;
await viewer.UpdateMarkdownAsync("# Welcome\n\n**Text** and `code`.");
viewer.AppendMarkdown("\n\nMore content.");
await viewer.WaitForParsingAsync();
```

The viewer uses the existing theme, native document renderer, selection, resource
cache and accessibility contract. Only the host chooses whether a link is opened
or an opaque image resource is resolved. The default resource resolver only opens
embedded data. Image dimensions, encoded bytes, decoded pixels and cache counts
remain bounded by `InlineImageOptions`.

`ICodeHighlighter` supplies language tokens and token styles. Setting
`CodeHighlighter` to null uses ordinary code text. Syntax styling does not mutate
`Document`, copied text or serialized content. Hosts choosing a third-party engine
must review that engine's languages, maintenance and redistribution terms; this
package has selected no external engine. The demo's keyword adapter is illustrative
and is not a complete C# parser.

## Current native schema

Native JSON writes **version 5** and reads **versions 4 and 5**. Version 4 concrete
formatting loads as explicit overrides. Missing or other versions are
rejected before document decoding, and unknown members are rejected. The current
schema includes `Section.Semantic` (`None`, `Quote`, `CodeBlock`), nullable
`Section.CodeLanguage`, and `TextStyle.IsCode`. Their defaults are ordinary
sections and non-code text.
Code blocks contain ordinary paragraph lines, preserving selection coordinates and
plain text. XAML preserves these fields. HTML/RTF/DOCX exports report their loss;
Markdown preserves the supported semantics and reports unrepresentable formatting.

The project remains unpublished, with a single supported native schema. Loading a
document does not overwrite its source. The API snapshot and package consumer
cover the current preview APIs.

## Examples and verification

Run the demo and choose **Markdown / XAML**. The window provides editable Markdown,
a code-highlighting toggle, host-owned link handling, a custom image resolver,
data-XAML export/import, and a diagnostics tab. Main-window file dialogs include
both new formats automatically.

`tests/Textalonia.PackageSmoke` deliberately uses the packed NuGet package without
a project reference. It instantiates `MarkdownViewer` from compiled consumer XAML,
exercises both codecs, source appends, diagnostics, optional highlighting, selection
and the managed accessibility contract.

Run repeated update measurements with:

```powershell
dotnet run --project benchmarks/Textalonia.Benchmarks -c Release -- --markdown-probe artifacts/benchmarks/phase7-markdown
```

The probe measures source-to-render completion and UI dispatcher latency separately.
It does not replace native screen-reader, clipboard, mobile, or frame-pacing
qualification. Existing platform limitations remain recorded in Phase 4/6 reports.
