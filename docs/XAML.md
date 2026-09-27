# Textalonia XAML data vocabulary

`DocumentFormats.Xaml` reads and writes **Textalonia XAML data version 1**. The namespace is `urn:textalonia:document:1`; the root is `Document` with `Version="1"`. `.txaml` is the recommended extension; `.xaml` also selects this codec through `DocumentFormats.ForPath`. Native JSON and its `.textalonia`, `.json`, and `.art` extensions remain supported independently.

This is an XML data format, implemented using the .NET XML reader and explicit model constructors in the main Textalonia package. It adds no dependency. It does not implement Avalonia XAML, WPF FlowDocument XAML, or another editor's vocabulary; those documents require an explicit converter and fixtures.

```xml
<Document xmlns="urn:textalonia:document:1" Version="1">
  <Resources>
    <Resource Key="logo" Kind="Host" MediaType="image/png" Location="app:logo" />
  </Resources>
  <Blocks>
    <Paragraph>
      <ParagraphStyle HeadingLevel="1" />
      <Runs>
        <Run Text="Welcome"><Style Bold="true" FontSize="28" /></Run>
      </Runs>
    </Paragraph>
    <Paragraph>
      <Runs>
        <Run>
          <Inline AltText="Application logo" Width="64" Height="64">
            <Image ResourceId="logo" />
          </Inline>
        </Run>
      </Runs>
    </Paragraph>
  </Blocks>
</Document>
```

```csharp
using var input = File.OpenRead("example.txaml");
var loaded = await DocumentFormats.Xaml.LoadWithReportAsync(input,
    new ConversionOptions { Mode = ConversionMode.Strict });
using var output = File.Create("copy.txaml");
await DocumentFormats.Xaml.SaveWithReportAsync(loaded.Document, output);
```

Streams remain caller-owned. `Parse` and `Serialize` are synchronous convenience methods; the stream API parses/encodes off the calling thread. The reporting extension methods provide tolerant/strict conversion and the explicit `PlainTextOnly` option shared with other codecs. Strict saves stage output before writing to the destination.

## Vocabulary

Names are case-sensitive and all elements use the namespace above. Attributes are unqualified. Container elements carry no attributes unless listed. Property/container order does not matter; collection order does. Singleton children cannot be repeated. No implicit style inheritance is performed: omitted values use model defaults. A missing `Id` gets a new GUID; export writes existing identities. Empty document/section/cell block collections receive an empty paragraph. An empty `MergeOriginalBlocks` collection remains empty.

| Element | Attributes | Children |
| --- | --- | --- |
| `Document` | `Version` (required, `1`) | `Resources`, `Blocks` |
| `Resources` | | zero or more `Resource` |
| `Resource` | `Key` (required), `Kind`, `MediaType`, `Location` | optional `Data` containing base64 bytes |
| `Blocks`, `MergeOriginalBlocks` | | ordered `Paragraph`, `Section`, `Table` |
| `Paragraph` | `Id` | `ParagraphStyle`, `DefaultStyle`, `Runs` |
| `Runs` | | ordered `Run` |
| `Run` | `Text` | `Style`, `Inline` |
| `Section` | `Id`, `Background`, `BorderColor`, `Padding`, `Semantic`, `CodeLanguage` | `PaddingEdges`, `Borders`, `Blocks` |
| `Table` | `Id` | `ColumnWidths`, `RowSizing`, `Rows` |
| `ColumnWidths` | | ordered `Column` with `Width` |
| `RowSizing` | | ordered `RowSize` with `Mode`, `Height` |
| `Rows` | | ordered `Row` |
| `Row` | | ordered `Cell` |
| `Cell` | `Id`, `ColumnSpan`, `RowSpan`, `Background` | `Padding`, `Borders`, `Blocks`, `MergeOriginalBlocks` |
| `Inline` | `Id`, `AltText`, `Width`, `Height` | exactly one `Image` or `Control` |
| `Image` | `ResourceId` (required) | |
| `Control` | `Type` (required) | zero or more `Property` |
| `Property` | `Name`, `Value` (both required) | |
| `Padding`, `PaddingEdges` | `Left`, `Top`, `Right`, `Bottom` | |
| `Borders` | | optional `Left`, `Top`, `Right`, `Bottom` |
| each border side | `Width`, `Color` | |

`DefaultStyle` and `Style` accept `FontFamily`, `FontSize`, `Bold`, `FontWeight`, `FontStretch`, `Italic`, `Underline`, `Strikethrough`, `Foreground`, `Background`, `Hyperlink`, `Baseline`, and `IsCode` attributes. Their values map directly to `TextStyle`.

`ParagraphStyle` accepts `Alignment`, `List`, `ListLevel`, `ListId`, `ListStart`, `ListRestart`, `HeadingLevel`, `SpaceBefore`, `SpaceAfter`, `Indent`, `RightIndent`, `FirstLineIndent`, `LineHeight`, `LetterSpacing`, and `RightToLeft`. Its optional `ListDefinition` child contains ordered `Level` elements with `Start`, `Kind`, `Marker`, `Text`, `Prefix`, `Suffix`, and `IncludeAncestors` attributes.

Numbers use invariant decimal syntax; booleans use `true`/`false` or `1`/`0`. Enum values use their exact public model names: for example resource kinds `Embedded`/`Local`/`Host`, section semantics `None`/`Quote`/`CodeBlock`, and row modes `Auto`/`AtLeast`/`Exact`. Null optional values are omitted; an explicit empty string is retained. Colors use `#RRGGBB` or `#AARRGGBB`. XML escaping applies to all strings; tabs and CR/LF in attributes are emitted as character references. `Run.Text` follows the model's no-hard-paragraph-break rule; a soft line break is U+2028. Strings must be representable in XML 1.0 (for example, NUL is not supported).

The codec preserves model IDs, all paragraph/character formatting, semantic quote/code sections and code language, list definitions, table geometry, hidden covered-cell contents and merge restoration history, inline descriptors, and unused as well as referenced resources. `Run.Text` for an inline is its U+FFFC atomic position; a conflicting supplied text value is discarded with a diagnostic. An unsupported/missing inline payload falls back to `AltText` and is diagnosed.

## Resources and execution boundaries

`Resource.Key` is the document dictionary key. `Image.ResourceId` refers to that key directly; it is not a markup extension. Embedded resources contain base64 `Data` and no `Location`. Local and host resources contain an opaque `Location` and no bytes. Parsing does not resolve paths, access the network, decode images, or load resources. The host's existing resource services and policy apply later when displaying the document. Hyperlinks are limited by model validation to absolute `http`, `https`, or `mailto` addresses.

`Control.Type` is an inert registration key and `Property` values are strings. Loading data never consults the inline presenter registry or instantiates a live control. A host may later map a key to an explicitly registered presenter. Text such as `{Binding Path=Name}` remains literal data in string fields: there is no markup-extension evaluation. Numeric/enum fields reject such text as invalid. There is no reflection, assembly/type activation, general XAML loader, event hookup, object provider, or `x:Class` implementation.

## Validation and diagnostics

Unknown elements (including elements from foreign namespaces) are omitted with their complete subtrees and `xaml.unsupported-element`. Unknown attributes, including event-like attributes and XAML directives, are ignored with `xaml.unsupported-attribute`. Unexpected element text and processing instructions are ignored and reported. Reports include XML line/column locations. Strict reporting imports reject these losses; legacy `Parse`/`LoadAsync` use the tolerant behavior without exposing a report. Comments and namespace declarations carry no document semantics and are ignored.

Malformed XML, incorrect root/namespace, duplicate singleton elements or resource/property keys, invalid scalar values, and model invariant violations fail in either mode. Future versions fail with `NotSupportedException`. DTDs and entity declarations are prohibited; the XML resolver is disabled. Parsing has a 32 Mi-character limit, XML reader depth limit of 256, and a 1,000,000-node reader limit, applied even to unsupported subtrees before allocating the XML tree. The XML depth allowance accommodates the model's full 32-level nesting with table/container wrappers. Stream imports also have the shared 32 MiB byte limit. Export rejects XML-unrepresentable text and output exceeding these bounds or 32 MiB in UTF-8. Model validation additionally limits nesting, identities, lists, table geometry, descriptor properties, and resource sizes (8 MiB per resource and 16 MiB embedded bytes per document).

See [XamlSerializationTests](../tests/Textalonia.Tests/XamlSerializationTests.cs) for strict snapshot roundtrips, stream ownership, hidden merge data, literal markup syntax, malicious element/directive/event inputs, external entity rejection, and parser bounds.

Named merge fields use the allowlisted `MergeField` inline payload with `Name`, optional
`Format` and `FallbackText` attributes. Cached display and descriptor data round-trip;
missing display text defaults to the field label. See [mail merge](MAIL-MERGE.md).

## Styles and themes

DX-01 adds optional `Styles`, `Defaults`, `Theme` and `Fonts` document elements.
These contain bounded typed JSON using the same allowlisted records as native v5.
Extended character/paragraph formatting uses a single `Data` child instead of legacy
attributes; combining both forms is rejected. Tables accept `StyleId` and a
`StyleOverrides` data element. Legacy XAML version 1 files remain readable. See
[style contracts](STYLES.md).
