# Markdown viewer and optional highlighting

`Textalonia.Controls.MarkdownViewer` ships in the main Textalonia package and derives from `TextaloniaViewer`. It uses the existing editor theme, shaped document surface, read-only selection/copy, accessibility bridge, link activation, inline descriptors, and resource services. Include `avares://Textalonia/Themes/Generic.axaml` as for the other controls. No Markdown or highlighter dependency is required.

```xml
<textalonia:MarkdownViewer Markdown="{Binding MarkdownSource}" />
```

```csharp
var viewer = new MarkdownViewer();
viewer.HyperlinkActivated += (_, link) => ShowLinkConfirmation(link.Uri);
viewer.InlineResourceResolver = applicationResourceResolver;
await viewer.UpdateMarkdownAsync("# Results\n\nSelect **this text**.");
viewer.AppendMarkdown("\n\nAnother streamed paragraph.");
await viewer.WaitForParsingAsync();
if (viewer.ParseError is { } error) ShowError(error);
foreach (var diagnostic in viewer.ParseReport.Diagnostics) ShowDiagnostic(diagnostic);
```

The codec's supported dialect and import/export limits are documented in [MARKDOWN.md](MARKDOWN.md). Parsing does not open links or fetch image resources. `HyperlinkActivated`, `InlineResourceResolver`, `InlineImageOptions`, and `InlineControlFactories` remain host-controlled inherited services. Network images retain the existing resolver's opt-in network and size policies.

## Source updates and state

Set the styled `Markdown` property on the Avalonia UI thread. Source changes and changes to the styled `ConversionOptions` property schedule a parse on a worker thread after a 15 ms quiet period. Immediate bursts coalesce, cancellation is checked while parsing, and a source revision guard rejects late completions even if a parser ignores cancellation. A concurrent host replacement of `Document` also prevents the pending parse from replacing that document.

The read-only Avalonia properties `IsParsing`, `ParseError`, and `ParseReport` support bindings. The last successful document remains visible while parsing or after failure. Strict conversion failures expose their conversion report through `ParseReport`. Source updates clear the preceding parse error/report before starting. `UpdateMarkdownAsync` and `WaitForParsingAsync` complete after their captured parse attempt finishes; inspect `ParseError` for failures. If that attempt is superseded, wait again for the latest source. Canceling the caller's wait does not cancel the bound source update.

`AppendMarkdown` concatenates source; it reparses the complete bounded source rather than incrementally interpreting partial syntax. An unfinished code fence or emphasis delimiter may therefore change the interpretation of preceding source when later chunks arrive. Updates reuse unchanged immutable blocks and index branches. Selection is mapped through the common rendered-text prefix/suffix; positions within replaced text are clamped to the replacement. The current selection and viewport at application time are respected, including interaction during parsing. The existing layout anchor preserves visible text when content is inserted above it. Appending does not automatically scroll to the bottom.

The control cancels pending parsing/highlighting and releases its highlighting cache when detached. Changes made while detached are parsed on reattachment. A never-attached control can still parse for headless consumers. The protected `ParseMarkdownAsync` hook runs on a worker thread; overrides must be thread-safe, avoid UI access, honor cancellation, and return a valid `DocumentLoadResult`.

The headless `--markdown-probe` benchmark exercises repeated small edits and streamed appends on 100 and 1,000 paragraphs, includes rendering, observes UI dispatcher latency independently, and verifies selection/viewport retention and superseded updates. Its adopted p95 budgets are 100 ms source-to-frame and 16 ms dispatcher delay; see [the dated benchmark report](BENCHMARK-BASELINES.md#interaction-and-markdown-probes) for the recorded machine-specific measurements. Very large sources still require a full parse and document-index validation; these measurements do not promise the same latency for arbitrary input sizes.

## Optional code highlighting

Set the styled `CodeHighlighter` property to an `ICodeHighlighter`. The adapter receives the language label and original complete code block, with `\n` between code-line paragraphs. `CodeHighlightToken` ranges use UTF-16 offsets. Return tokens ordered by start, without overlap, using positive lengths contained in the original text. `GetStyle(language, tokenKind)` maps token kinds to optional `CodeHighlightStyle` foreground/background color strings and bold/italic overrides. Null style properties retain the code's ordinary appearance.

```csharp
sealed class TinyHighlighter : ICodeHighlighter
{
    public ValueTask<IReadOnlyList<CodeHighlightToken>> TokenizeAsync(
        string? language, string code, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CodeHighlightToken> tokens =
            language == "example" && code.StartsWith("let ", StringComparison.Ordinal)
                ? new[] { new CodeHighlightToken(0, 3, "keyword") }
                : Array.Empty<CodeHighlightToken>();
        return ValueTask.FromResult(tokens);
    }

    public CodeHighlightStyle? GetStyle(string? language, string tokenKind) =>
        tokenKind == "keyword" ? new(Foreground: "#A23BCE", Bold: true) : null;
}
```

Both adapter methods run on worker threads and may overlap across canceled revisions; implementations must be thread-safe. Unknown languages should return an empty token list. Highlighting starts after the canonical document is applied and has separate `IsHighlighting` and `HighlightError` properties. Await `WaitForParsingAsync()` followed by `WaitForHighlightingAsync()` to await both current passes. Exceptions, malformed ranges, invalid colors, and excessive token counts leave the code readable without highlighting. Late or canceled results never replace the current presentation.

A private presentation snapshot holds the styled runs. `Document`, `Session`, selected/copied text, accessibility text, and all codec exports continue to use the canonical original code. Setting `CodeHighlighter` to null removes the presentation overrides. A host replacement of `Document` cancels old highlighting and recomputes for the new snapshot.

The per-viewer LRU cache is keyed by adapter identity, language, and exact code text. It retains at most 64 entries and an estimated 1 MiB of text/styles. Code blocks larger than 131,072 UTF-16 units remain unhighlighted, and an adapter result may contain at most 4,096 tokens. Replacing the adapter clears the cache; update an adapter's palette by replacing the adapter or clearing/reassigning the property. Colors and token kinds are bounded and validated. Source changes reuse cached tokens only for unchanged code and language.

The integration deliberately supplies an adapter contract and small demonstration adapter rather than selecting a third-party tokenizer. There is no mandatory highlighter package, supported-language claim, or additional redistribution license. Hosts choosing an external tokenizer own its language coverage, maintenance and license review.
