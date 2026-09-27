# Textalonia Markdown dialect

`MarkdownDocumentFormat` is a dependency-free, bounded parser and writer for the **Textalonia Markdown dialect**. It is deliberately not a claim of full CommonMark or GitHub Flavored Markdown compatibility. The explicit parser keeps resource, cancellation, model, and diagnostic behavior under the same control as the other codecs; no additional parser or highlighter package is required.

Use `DocumentFormats.Markdown`, or extension selection with `.md` / `.markdown`. `Parse(string, CancellationToken)` supports cooperative cancellation. The shared `LoadWithReportAsync` / `SaveWithReportAsync` APIs return conversion diagnostics; strict mode rejects reported losses and stages exports before writing to the caller's stream.

## Supported vocabulary

| Source | Document meaning |
| --- | --- |
| `#` through `######`, followed by a space | Heading level 1–6; closing hashes are literal text. |
| Nonempty lines separated by blank lines | Paragraphs; adjacent ordinary source lines join with one space. |
| Two trailing spaces or a backslash before a continuation line | Soft line break (`U+2028`) within a paragraph. |
| Matched `*` / `_`, `**` / `__`, or `***` / `___` | Italic, bold, or both. Delimiters match exact run lengths; nested different delimiters are supported. Unlike CommonMark, this dialect does not apply intraword/flanking rules. |
| `[label](destination)` or `[label](<destination>)` | Inline link with recursively formatted label. Parentheses balance in unbracketed destinations; angle destinations can include spaces. Titles and reference definitions are unsupported. |
| `-`, `+`, `*`, or decimal `1.` / `1)` followed by a space | One paragraph per list item. Exactly two spaces add a nesting level, up to nine levels. Each explicit decimal marker sets that item's number. Multi-paragraph items, lazy continuations, and nested block containers inside items are outside the dialect. |
| `>` prefixed lines, including `>` blank lines | Quote sections; repeated prefixes form nested quotes. Every quote line must carry its prefix. |
| At least three backticks or tildes, then an optional language label | Code section containing one paragraph per original line. Closing fences use the same character and at least the opening length. Code text, blank lines, and language survive native storage and Markdown round trips. |
| Matched backtick runs | Inline code with explicit `TextStyle.IsCode`; interior backticks are allowed with a longer delimiter. A surrounding padding space is removed on both sides when the content is not all spaces. |
| `![literal alternative text](destination)` | Image descriptor plus embedded or host resource. Image labels are literal text with punctuation escapes, not nested formatting. |
| Backslash before ASCII punctuation | Literal punctuation. Export escapes literal syntax and chooses safe code delimiters. |

Code uses Avalonia's ordered font-family fallback list: Cascadia Code, Consolas, Menlo, DejaVu Sans Mono, Liberation Mono, then monospace. Fonts are supplied by the host operating system; the package does not redistribute a code font. The writer also accepts the earlier `monospace` code style.

The language label is at most 128 characters from ASCII letters/digits, `_`, `-`, `+`, `.`, and `#`. Unsupported fence info is diagnosed and omitted. An unfinished fence remains visible as code with `markdown.unclosed-fence`, useful during streamed updates.

Quotes use `Section.Semantic = Quote`. Fenced code uses `Section.Semantic = CodeBlock` and `Section.CodeLanguage`. No rendered color spans are inserted into the document by highlighting. Native JSON version 4 preserves these fields and inline code meaning; native versions 1–3 still import through their frozen vocabularies.

## Boundaries and diagnostics

Pipe tables and task lists are **disabled**. Pipe-table source stays literal paragraph text with `markdown.table`; task markers such as `[x]` stay literal list-item text with `markdown.task-list`. Raw HTML stays visible literal text, is diagnosed, and never executes. Thematic breaks, reference definitions, and indented code are diagnosed literal fallbacks. Setext headings, autolinks, entities, strikethrough, footnotes, and other extensions are outside this dialect; they have no special semantics.

Markdown export reports unsupported typography, layout, custom list definitions, image dimensions, generic sections, tables, host controls, unavailable images, unused resources, and merge history. Empty paragraph counts are not representable and are diagnosed. Newlines in inline alternative text become spaces with a diagnostic. Native JSON or the data-only XAML codec should be used for exact document storage.

## Links and resources

Active links follow the model policy: absolute `http`, `https`, and `mailto` only. Unsafe and relative links retain their visible labels with a diagnostic. The codec never opens a link.

Images accept raster `data:image/png|jpeg|gif|webp|bmp;base64,...` payloads, HTTP(S) references, or opaque `resource:` references such as `![Logo](resource:demo.logo)`. HTTP(S) and `resource:` references become `DocumentResourceKind.Host`; parsing does not download or decode them. The host resource resolver and viewer resource policy decide what may be loaded. Local paths, `file:` destinations, SVG, and unsupported data schemes fall back to alternative text. Explicit dimensions are outside the dialect; imported descriptors use the model default of 32×32.

Imports are bounded to 32 MB stream input / 32 million source characters, 100,000 lines/elements, 1,048,576 characters per line/paragraph, 32 levels of structural or inline nesting, nine list levels, a bounded parsing-work counter, and the existing model resource limits (4,096 descriptors, 8 MB per embedded resource, 16 MB embedded total). Cancellation is checked throughout block and inline scanning. These limits also bound malformed delimiter searches; no network or filesystem access occurs during parsing.
