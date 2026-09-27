using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>Dependency-free Textalonia Markdown dialect. Parsing never executes HTML or resolves resources.</summary>
public sealed class MarkdownDocumentFormat : TextDocumentFormat
{
    public override string Name => "Markdown";
    public override IReadOnlyList<string> Extensions => [".md", ".markdown"];
    private const int MaximumCharacters = 32 * 1024 * 1024;
    private static readonly Regex ListPattern = new(@"^( *)(?:([-+*])|([0-9]{1,7})[.)]) +(.*)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex HeadingPattern = new(@"^ {0,3}(#{1,6})(?: +(.*)|$)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex TablePattern = new(@"^\s*\|?\s*:?-{3,}:?\s*\|", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    // Avalonia parses comma-separated family fallbacks; generic "monospace" alone
    // can resolve to the platform default proportional typeface.
    private const string CodeFontFamily = "Cascadia Code, Consolas, Menlo, DejaVu Sans Mono, Liberation Mono, monospace";
    private static readonly TextStyle CodeStyle = TextStyle.Default with { FontFamily = CodeFontFamily };

    public override FlowDocument Parse(string text) => Parse(text, CancellationToken.None);

    /// <summary>Parses a snapshot with cooperative cancellation during block and inline processing.</summary>
    public FlowDocument Parse(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumCharacters) throw new FormatException("Markdown exceeds the 32 MB character limit.");
        cancellationToken.ThrowIfCancellationRequested();
        var parser = new Parser(cancellationToken);
        var lines = FlowDocument.NormalizeNewlines(text).Split('\n');
        if (lines.Length > 100_000 || lines.Any(line => line.Length > 1_048_576)) throw new FormatException("Markdown exceeds the line limits.");
        var document = new FlowDocument(parser.Blocks(lines, 0)) { Resources = parser.Resources.ToImmutableDictionary(StringComparer.Ordinal) };
        cancellationToken.ThrowIfCancellationRequested();
        document.Validate();
        return document;
    }

    public override async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        return await Task.Run(() =>
        {
            return Parse(DocumentFormats.DecodeText(bytes), cancellationToken);
        }, cancellationToken);
    }

    public override string Serialize(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        return WriteBlocks(document.Blocks, document, ListNumbering.Compute(document));
    }

    private sealed class Parser(CancellationToken token)
    {
        internal readonly Dictionary<string, DocumentResource> Resources = new(StringComparer.Ordinal);
        private int _nodes;
        private long _embeddedBytes;
        private long _work;
        private void Step(int amount = 1)
        {
            token.ThrowIfCancellationRequested();
            if ((_work += amount) > 128L * 1024 * 1024) throw new FormatException("Markdown parsing exceeds the work limit.");
        }
        private void Node() { Step(); if (++_nodes > 100_000) throw new FormatException("Markdown contains too many elements."); }
        internal ImmutableArray<Block> Blocks(IReadOnlyList<string> lines, int depth, int sourceOffset = 0)
        {
            if (depth > 32) throw new FormatException("Markdown block nesting exceeds 32 levels.");
            var blocks = ImmutableArray.CreateBuilder<Block>();
            Guid? listId = null;
            var previousListLevel = 0;
            for (var i = 0; i < lines.Count;)
            {
                Step();
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
                Node();
                if (TryFence(line, out var fenceChar, out var fenceLength, out var info))
                {
                    var sourceLine = ++i + sourceOffset;
                    var code = ImmutableArray.CreateBuilder<Block>();
                    while (i < lines.Count && !IsClosingFence(lines[i], fenceChar, fenceLength))
                    { Node(); code.Add(new Paragraph(lines[i++], CodeStyle) { Style = ParagraphStyle.Default with { SpaceAfter = 0 } }); }
                    if (i == lines.Count)
                        Report("markdown.unclosed-fence", "Unclosed code fence", "Remaining lines are retained as code.", sourceLine);
                    else i++;
                    var language = info.Trim();
                    if (language.Length > 128 || language.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '+' or '.' or '#')))
                    {
                        Report("markdown.code-info", "Unsupported code fence info", "The code is retained without a language label.", sourceLine);
                        language = "";
                    }
                    blocks.Add(new Section { Semantic = SectionSemantic.CodeBlock, CodeLanguage = language.Length == 0 ? null : language,
                        Blocks = code.Count == 0 ? [new Paragraph("", CodeStyle) { Style = ParagraphStyle.Default with { SpaceAfter = 0 } }] : code.ToImmutable() });
                    listId = null;
                    continue;
                }
                if (TryQuote(line, out _))
                {
                    var quoteStart = i;
                    var quoted = new List<string>();
                    while (i < lines.Count && TryQuote(lines[i], out var content)) { quoted.Add(content); i++; }
                    blocks.Add(new Section { Semantic = SectionSemantic.Quote, Blocks = Blocks(quoted, depth + 1, sourceOffset + quoteStart),
                        Borders = new(new(3, "#808080"), null, null, null) });
                    listId = null;
                    continue;
                }
                var heading = HeadingPattern.Match(line);
                if (heading.Success)
                {
                    var level = heading.Groups[1].Length;
                    var headingStyle = HeadingStyle(level);
                    blocks.Add(new Paragraph(Inlines(heading.Groups[2].Value, headingStyle, 0, sourceOffset + i + 1))
                    { DefaultStyle = headingStyle, Style = ParagraphStyle.Default with { HeadingLevel = level, SpaceBefore = 12 } });
                    i++; listId = null;
                    continue;
                }
                var list = ListPattern.Match(line);
                if (list.Success)
                {
                    var indent = list.Groups[1].Length;
                    var level = indent / 2;
                    if (indent % 2 != 0 || level > 8 || listId is null && level != 0 || listId is not null && level > previousListLevel + 1)
                    {
                        Report("markdown.list-indent", "Unsupported list indentation", "List indentation is bounded to consecutive two-space levels.", sourceOffset + i + 1);
                        level = Math.Clamp(level, 0, listId is null ? 0 : Math.Min(8, previousListLevel + 1));
                    }
                    listId ??= Guid.NewGuid(); previousListLevel = level;
                    var numbered = list.Groups[3].Success;
                    int? start = numbered ? int.Parse(list.Groups[3].Value, CultureInfo.InvariantCulture) : null;
                    if (start is < 1 or > 1_000_000) { Report("markdown.list-number", "Unsupported list number", "The item starts at one.", sourceOffset + i + 1); start = 1; }
                    var body = list.Groups[4].Value;
                    if (body.StartsWith("[ ] ", StringComparison.Ordinal) || body.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase))
                        Report("markdown.task-list", "Task-list extension", "The checkbox marker is retained as literal list-item text.", sourceOffset + i + 1);
                    blocks.Add(new Paragraph(Inlines(body, TextStyle.Default, 0, sourceOffset + i + 1))
                    { Style = ParagraphStyle.Default with { List = numbered ? ListKind.Numbered : ListKind.Bullet, ListLevel = level, ListId = listId, ListStart = start } });
                    i++;
                    continue;
                }
                listId = null;
                var firstLine = sourceOffset + i + 1;
                var paragraph = new StringBuilder();
                do
                {
                    line = lines[i++];
                    InspectUnsupported(line, sourceOffset + i);
                    var hardBreak = line.EndsWith("  ", StringComparison.Ordinal) || EndsWithUnescapedBackslash(line);
                    paragraph.Append(hardBreak ? line.EndsWith("  ", StringComparison.Ordinal) ? line[..^2] : line[..^1] : line);
                    if (i >= lines.Count || string.IsNullOrWhiteSpace(lines[i]) || StartsBlock(lines[i])) break;
                    paragraph.Append(hardBreak ? '\u2028' : ' ');
                    if (paragraph.Length > 1_048_576) throw new FormatException("Markdown paragraph exceeds the size limit.");
                } while (true);
                blocks.Add(new Paragraph(Inlines(paragraph.ToString(), TextStyle.Default, 0, firstLine)));
            }
            return blocks.Count == 0 ? [new Paragraph()] : blocks.ToImmutable();
        }

        private void InspectUnsupported(string line, int number)
        {
            if (TablePattern.IsMatch(line)) Report("markdown.table", "Pipe-table extension", "Table source is retained as literal paragraph text.", number);
            if (line.TrimStart().StartsWith("<", StringComparison.Ordinal))
                Report("markdown.raw-html", "Raw HTML", "HTML is retained as literal text and is never executed.", number);
            if (line.Trim() is "---" or "***" or "___" || line.TrimStart().StartsWith("[", StringComparison.Ordinal) && line.Contains("]:", StringComparison.Ordinal))
                Report("markdown.extension", "Thematic break or reference-link extension", "Source is retained as literal text.", number);
            if (line.StartsWith("    ", StringComparison.Ordinal))
                Report("markdown.indented-code", "Indented code", "Use a fenced code block; source remains paragraph text.", number);
        }

        private ImmutableArray<RichRun> Inlines(string text, TextStyle style, int depth, int sourceLine)
        {
            if (depth > 32) throw new FormatException("Markdown inline nesting exceeds 32 levels.");
            var runs = new List<RichRun>();
            var literal = new StringBuilder();
            void Flush() { if (literal.Length != 0) { Node(); runs.Add(new(literal.ToString(), style)); literal.Clear(); } }
            for (var i = 0; i < text.Length;)
            {
                Step();
                var ch = text[i];
                if (ch == '\\' && i + 1 < text.Length && IsPunctuation(text[i + 1])) { literal.Append(text[i + 1]); i += 2; continue; }
                if (ch == '`')
                {
                    var length = Count(text, i, '`');
                    var end = FindDelimiter(text, i + length, '`', length);
                    if (end >= 0)
                    {
                        Flush(); var content = text.Substring(i + length, end - i - length).Replace('\u2028', ' ');
                        if (content.Length > 1 && content[0] == ' ' && content[^1] == ' ' && content.Any(c => c != ' ')) content = content[1..^1];
                        Node(); runs.Add(new(content, style with { IsCode = true, FontFamily = CodeFontFamily })); i = end + length; continue;
                    }
                    literal.Append('`', length); i += length; continue;
                }
                var image = ch == '!' && i + 1 < text.Length && text[i + 1] == '[';
                if (ch == '[' || image)
                {
                    var labelStart = i + (image ? 2 : 1);
                    var close = FindClosingBracket(text, labelStart);
                    if (close >= 0 && close + 1 < text.Length && text[close + 1] == '(' && TryDestination(text, close + 2, out var destination, out var end))
                    {
                        Flush();
                        var label = text.Substring(labelStart, close - labelStart);
                        if (image) runs.AddRange(Image(label, destination, style, sourceLine));
                        else
                        {
                            if (!FlowDocument.IsSafeHyperlink(destination))
                                Report("markdown.unsafe-link", "Unsafe or relative link destination", "The label is retained without an active link.", sourceLine);
                            runs.AddRange(Inlines(label, style with { Hyperlink = FlowDocument.IsSafeHyperlink(destination) ? destination : null }, depth + 1, sourceLine));
                        }
                        i = end; continue;
                    }
                }
                if (ch is '*' or '_')
                {
                    var length = Math.Min(3, Count(text, i, ch));
                    var end = FindDelimiter(text, i + length, ch, length);
                    if (end > i + length)
                    {
                        Flush(); runs.AddRange(Inlines(text.Substring(i + length, end - i - length), style with
                        { Bold = style.Bold || length >= 2, Italic = style.Italic || length is 1 or 3 }, depth + 1, sourceLine));
                        i = end + length; continue;
                    }
                    literal.Append(ch, length); i += length; continue;
                }
                if (ch == '<' && i + 1 < text.Length && (char.IsAsciiLetter(text[i + 1]) || text[i + 1] is '/' or '!'))
                    Report("markdown.raw-html", "Raw HTML", "HTML is retained as literal text and is never executed.", sourceLine);
                literal.Append(ch); i++;
            }
            Flush();
            return Paragraph.Normalize(runs);
        }
        private int FindDelimiter(string text, int start, char delimiter, int length)
        {
            for (var i = start; i < text.Length; i++)
            {
                Step();
                if (text[i] == '\\' && delimiter != '`') { i++; continue; }
                if (text[i] == '`' && delimiter != '`')
                {
                    var codeLength = Count(text, i, '`');
                    var codeEnd = FindDelimiter(text, i + codeLength, '`', codeLength);
                    if (codeEnd >= 0) { i = codeEnd + codeLength - 1; continue; }
                }
                if (text[i] != delimiter) continue;
                var count = Count(text, i, delimiter);
                if (count == length) return i;
                i += count - 1;
            }
            return -1;
        }
        private int FindClosingBracket(string text, int start)
        {
            var nesting = 0;
            for (var i = start; i < text.Length; i++)
            {
                Step();
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '`')
                {
                    var codeLength = Count(text, i, '`');
                    var codeEnd = FindDelimiter(text, i + codeLength, '`', codeLength);
                    if (codeEnd >= 0) { i = codeEnd + codeLength - 1; continue; }
                }
                if (text[i] == '[') nesting++;
                if (text[i] == ']' && nesting-- == 0) return i;
            }
            return -1;
        }
        private bool TryDestination(string text, int start, out string destination, out int end)
        {
            var builder = new StringBuilder();
            var angle = start < text.Length && text[start] == '<';
            var nesting = 0;
            for (var i = start + (angle ? 1 : 0); i < text.Length; i++)
            {
                Step(); var ch = text[i];
                if (ch == '\\' && i + 1 < text.Length) { builder.Append(text[++i]); continue; }
                if (angle && ch == '>' && i + 1 < text.Length && text[i + 1] == ')' || !angle && ch == ')' && nesting == 0)
                { destination = builder.ToString(); end = i + (angle ? 2 : 1); return true; }
                if (!angle && char.IsWhiteSpace(ch) || ch is '\r' or '\n' or '\u2028' || angle && ch == '<' || builder.Length > 12 * 1024 * 1024) break;
                if (!angle && ch == '(') nesting++;
                if (!angle && ch == ')') nesting--;
                builder.Append(ch);
            }
            destination = ""; end = start; return false;
        }
        private IEnumerable<RichRun> Image(string label, string destination, TextStyle style, int sourceLine)
        {
            var alt = Unescape(label);
            DocumentResource? resource = null;
            if (destination.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var separator = destination.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
                if (separator > 5 && RasterType(destination[5..separator]))
                {
                    try
                    {
                        if (destination.Length - separator - 8 > (DocumentResource.MaximumEmbeddedBytes + 2L) / 3 * 4) throw new FormatException();
                        var bytes = Convert.FromBase64String(destination[(separator + 8)..]);
                        if (bytes.Length > DocumentResource.MaximumEmbeddedBytes || _embeddedBytes + bytes.Length > DocumentResource.MaximumDocumentEmbeddedBytes) throw new FormatException();
                        resource = new() { Data = ImmutableArray.CreateRange(bytes), MediaType = destination[5..separator].ToLowerInvariant() };
                        _embeddedBytes += bytes.Length;
                    }
                    catch (FormatException) { }
                }
            }
            else if (SafeImageLocation(destination))
                resource = new() { Kind = DocumentResourceKind.Host, Location = destination, MediaType = "application/octet-stream" };
            if (resource is null || alt.Length > 16_384 || Resources.Count >= 4096)
            {
                Report("markdown.image", "Unsupported, unsafe, or oversized image", "Alternative text is retained; no resource is resolved.", sourceLine);
                return [new RichRun(alt, style)];
            }
            var id = "markdown-image-" + Resources.Count.ToString(CultureInfo.InvariantCulture);
            Resources.Add(id, resource); Node();
            return [new RichRun(new InlineDescriptor { AltText = alt, Payload = new ImageInlinePayload(id) }, style)];
        }
    }

    private static string WriteBlocks(IEnumerable<Block> blocks, FlowDocument document, IReadOnlyDictionary<Guid, ListMarker> markers)
    {
        var builder = new StringBuilder();
        var previousList = false;
        foreach (var block in blocks)
        {
            var list = block is Paragraph { Style.List: not ListKind.None };
            if (builder.Length != 0) builder.Append(previousList && list ? "\n" : "\n\n");
            switch (block)
            {
                case Paragraph p:
                    ReportParagraph(p);
                    if (p.Style.List != ListKind.None)
                    {
                        builder.Append(' ', p.Style.ListLevel * 2);
                        builder.Append(p.Style.List == ListKind.Bullet ? "- " : markers[p.Id].Number.ToString(CultureInfo.InvariantCulture) + ". ");
                    }
                    if (p.Style.HeadingLevel > 0)
                    {
                        if (list) ConversionDiagnostics.Report("markdown.list-heading", "Heading inside a list item", "The heading level is omitted.", p.Id);
                        else builder.Append('#', p.Style.HeadingLevel).Append(' ');
                    }
                    var content = WriteRuns(p, document);
                    if (content.Contains('\n') && (list || p.Style.HeadingLevel > 0 || content.Split('\n').Any(string.IsNullOrWhiteSpace)))
                        ConversionDiagnostics.Report("markdown.soft-break", "Soft line break outside an ordinary paragraph continuation",
                            "Leading, trailing, or consecutive breaks can be omitted; heading and list continuations become separate paragraphs.", p.Id);
                    builder.Append(content);
                    break;
                case Section s when s.Semantic == SectionSemantic.CodeBlock:
                    var code = string.Join("\n", new DocumentIndex(new FlowDocument(s.Blocks)).Paragraphs.Select(p => p.Paragraph.PlainText));
                    if (s.Blocks.Any(b => b is not Paragraph) || s.Blocks.OfType<Paragraph>().Any(p => p.Runs.Any(r => r.Inline is not null)))
                        ConversionDiagnostics.Report("markdown.code-structure", "Non-text content in a code section", "Visible text is flattened into the code fence.", s.Id);
                    foreach (var p in s.Blocks.OfType<Paragraph>())
                    {
                        ReportStyle(p.DefaultStyle, p.Id, true);
                        if (p.Style != (ParagraphStyle.Default with { SpaceAfter = 0 }))
                            ConversionDiagnostics.Report("markdown.code-formatting", "Code paragraph formatting", "Code retains its original text without paragraph formatting.", p.Id);
                        foreach (var r in p.Runs) ReportStyle(r.Style, p.Id, true);
                    }
                    var fence = new string('`', Math.Max(3, LongestRun(code, '`') + 1));
                    builder.Append(fence).Append(s.CodeLanguage).Append('\n').Append(code).Append('\n').Append(fence);
                    ReportSectionDecoration(s);
                    break;
                case Section s when s.Semantic == SectionSemantic.Quote:
                    var quote = WriteBlocks(s.Blocks, document, markers);
                    builder.Append(string.Join("\n", quote.Split('\n').Select(line => "> " + line)));
                    ReportSectionDecoration(s);
                    break;
                case Section s:
                    ConversionDiagnostics.Report("markdown.section", "Generic section structure", "Visible blocks are retained without the section boundary.", s.Id);
                    builder.Append(WriteBlocks(s.Blocks, document, markers));
                    break;
                case Table t:
                    ConversionDiagnostics.Report("markdown.table", "Tables", "Visible cell text is exported as paragraphs in row order.", t.Id);
                    var cells = new List<Block>();
                    for (var row = 0; row < t.Rows.Length; row++)
                        for (var col = 0; col < t.ColumnCount; col++)
                            if (!t.IsCovered(row, col)) cells.AddRange(t.Rows[row][col].Blocks);
                    builder.Append(WriteBlocks(cells, document, markers));
                    break;
            }
            previousList = list;
        }
        return builder.ToString();
    }

    private static string WriteRuns(Paragraph paragraph, FlowDocument document)
    {
        var builder = new StringBuilder();
        var active = new List<(string Key, string Open, string Close)>();
        foreach (var run in paragraph.Runs)
        {
            var next = new List<(string Key, string Open, string Close)>();
            if (run.Style.Hyperlink is { } link)
            {
                if (link.Any(c => char.IsControl(c) || c == '\u2028'))
                    ConversionDiagnostics.Report("markdown.link-destination", "Control character in link destination", "The label is retained without an active link.", paragraph.Id);
                else next.Add(("link:" + link, "[", "](<" + EscapeDestination(link) + ">)"));
            }
            if (run.Style.EffectiveBold && paragraph.Style.HeadingLevel == 0) next.Add(("bold", "__", "__"));
            if (run.Style.Italic) next.Add(("italic", "*", "*"));
            var common = 0;
            while (common < active.Count && common < next.Count && active[common].Key == next[common].Key) common++;
            for (var i = active.Count - 1; i >= common; i--) builder.Append(active[i].Close);
            for (var i = common; i < next.Count; i++) builder.Append(next[i].Open);
            builder.Append(WriteRun(run, document, paragraph));
            active = next;
        }
        for (var i = active.Count - 1; i >= 0; i--) builder.Append(active[i].Close);
        return builder.ToString();
    }

    private static string WriteRun(RichRun run, FlowDocument document, Paragraph paragraph)
    {
        var paragraphId = paragraph.Id;
        ReportStyle(run.Style, paragraphId, false, HeadingStyle(paragraph.Style.HeadingLevel));
        string value;
        if (run.Inline is { } inline)
        {
            var alt = inline.AltText;
            if (alt.IndexOfAny(['\r', '\n', '\u2028']) >= 0)
            {
                ConversionDiagnostics.Report("markdown.image-alt-break", "Line breaks in inline alternative text", "Alternative-text line breaks become spaces.", inline.Id);
                alt = FlowDocument.NormalizeNewlines(alt).Replace('\n', ' ').Replace('\u2028', ' ');
            }
            string? destination = null;
            if (inline.Payload is ImageInlinePayload image && document.Resources.TryGetValue(image.ResourceId, out var resource))
            {
                if (resource.Kind == DocumentResourceKind.Embedded && RasterType(resource.MediaType))
                    destination = "data:" + resource.MediaType.ToLowerInvariant() + ";base64," + Convert.ToBase64String(resource.Data.AsSpan());
                else if (resource.Kind == DocumentResourceKind.Host && resource.Location is { } location && SafeImageLocation(location)) destination = location;
            }
            if (destination is null)
            {
                if (inline.Payload is not MergeFieldInlinePayload)
                    ConversionDiagnostics.Report("markdown.inline", "Unrepresentable inline object or image resource", "Alternative text is retained; no resource is resolved.", inline.Id);
                value = Escape(alt);
            }
            else
            {
                value = "![" + Escape(alt) + "](<" + EscapeDestination(destination) + ">)";
                if (inline.Width != 32 || inline.Height != 32)
                    ConversionDiagnostics.Report("markdown.image-size", "Explicit image dimensions", "The imported image uses the default dimensions.", inline.Id);
            }
        }
        else if (run.Style.IsCode)
        {
            var code = run.Text.Replace('\u2028', ' ');
            if (code != run.Text) ConversionDiagnostics.Report("markdown.code-break", "Soft line break in inline code", "The line break becomes a space.", paragraphId);
            var delimiter = new string('`', Math.Max(1, LongestRun(code, '`') + 1));
            var pad = code.StartsWith('`') || code.EndsWith('`') || code.Length > 1 && code[0] == ' ' && code[^1] == ' ' && code.Any(c => c != ' ');
            value = delimiter + (pad ? " " : "") + code + (pad ? " " : "") + delimiter;
        }
        else value = Escape(run.Text);
        return value;
    }
    private static void ReportParagraph(Paragraph p)
    {
        var plain = p.Style with { HeadingLevel = 0, List = ListKind.None, ListId = null, ListLevel = 0, ListStart = null, ListRestart = false, ListDefinition = null,
            SpaceBefore = p.Style.HeadingLevel > 0 && p.Style.SpaceBefore == 12 ? 0 : p.Style.SpaceBefore };
        if (plain != ParagraphStyle.Default)
            ConversionDiagnostics.Report("markdown.paragraph-formatting", "Paragraph layout formatting", "Text and supported heading/list meaning are retained.", p.Id);
        if (p.Style.ListDefinition is not null)
            ConversionDiagnostics.Report("markdown.list-definition", "Custom list numbering definition", "Standard decimal or bullet markers are used.", p.Id);
        if (p.Runs.IsEmpty) ConversionDiagnostics.Report("markdown.empty-paragraph", "Empty paragraph", "Markdown blank lines separate blocks and do not retain empty paragraph counts.", p.Id);
        ReportStyle(p.DefaultStyle, p.Id, false, HeadingStyle(p.Style.HeadingLevel));
    }
    private static TextStyle HeadingStyle(int level) => level == 0 ? TextStyle.Default : TextStyle.Default with
    { Bold = true, FontSize = level switch { 1 => 32, 2 => 26, 3 => 22, 4 => 20, 5 => 18, _ => 16 } };

    private static void ReportStyle(TextStyle style, Guid id, bool code, TextStyle? basis = null)
    {
        basis ??= TextStyle.Default;
        var representable = style with { Bold = false, Italic = false, Hyperlink = null, IsCode = false, FontSize = TextStyle.Default.FontSize,
            FontFamily = (code || style.IsCode) && style.FontFamily is CodeFontFamily or "monospace" ? null : style.FontFamily };
        if (representable != TextStyle.Default || style.FontSize != basis.FontSize || basis.Bold && !style.EffectiveBold ||
            code && (style.Bold || style.Italic || style.Hyperlink is not null || style.IsCode))
            ConversionDiagnostics.Report("markdown.character-formatting", "Character formatting without a Markdown representation", "Supported emphasis, links, code, and text are retained.", id);
    }
    private static void ReportSectionDecoration(Section section)
    {
        var quoteBorder = section.Semantic == SectionSemantic.Quote ? new BlockBorders(new(3, "#808080"), null, null, null) : null;
        if (section.Background is not null || section.BorderColor is not null || section.Padding != 12 || section.PaddingEdges is not null || section.Borders != quoteBorder)
            ConversionDiagnostics.Report("markdown.section-formatting", "Section decoration", "Quote or code meaning is retained without custom decoration.", section.Id);
    }
    private static bool TryFence(string line, out char character, out int length, out string info)
    {
        var trimmed = line.TrimStart(' '); var indent = line.Length - trimmed.Length;
        character = trimmed.Length == 0 ? '\0' : trimmed[0];
        length = character is '`' or '~' ? Count(trimmed, 0, character) : 0;
        info = length > 0 ? trimmed[length..] : "";
        return indent <= 3 && length >= 3 && (character != '`' || !info.Contains('`'));
    }
    private static bool IsClosingFence(string line, char character, int length) =>
        TryFence(line, out var actual, out var count, out var info) && actual == character && count >= length && string.IsNullOrWhiteSpace(info);
    private static bool TryQuote(string line, out string content)
    {
        var trimmed = line.TrimStart(' ');
        if (line.Length - trimmed.Length <= 3 && trimmed.StartsWith('>'))
        { content = trimmed.Length > 1 && trimmed[1] == ' ' ? trimmed[2..] : trimmed[1..]; return true; }
        content = ""; return false;
    }
    private static bool StartsBlock(string line) => TryFence(line, out _, out _, out _) || TryQuote(line, out _) || HeadingPattern.IsMatch(line) || ListPattern.IsMatch(line);
    private static bool EndsWithUnescapedBackslash(string line)
    { var count = 0; for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; i--) count++; return count % 2 != 0; }
    private static int Count(string text, int start, char value)
    { var end = start; while (end < text.Length && text[end] == value) end++; return end - start; }
    private static int LongestRun(string text, char value)
    { var result = 0; for (var i = 0; i < text.Length; i++) if (text[i] == value) { var length = Count(text, i, value); result = Math.Max(result, length); i += length - 1; } return result; }
    private static bool IsPunctuation(char ch) => char.IsAscii(ch) && char.IsPunctuation(ch) || ch is '+' or '<' or '>' or '=' or '~' or '^' or '$' or '|' or '`';
    private static string Escape(string text)
    {
        var builder = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch == '\u2028') builder.Append("  \n");
            else { if (IsPunctuation(ch)) builder.Append('\\'); builder.Append(ch); }
        }
        return builder.ToString();
    }
    private static string Unescape(string text)
    { var builder = new StringBuilder(); for (var i = 0; i < text.Length; i++) { if (text[i] == '\\' && i + 1 < text.Length && IsPunctuation(text[i + 1])) i++; builder.Append(text[i]); } return builder.ToString(); }
    private static string EscapeDestination(string value) => value.Replace("\\", "\\\\").Replace("<", "\\<").Replace(">", "\\>");
    private static bool SafeImageLocation(string value) => value.Length <= 4096 && !value.Any(char.IsControl) && !value.Any(char.IsWhiteSpace) &&
        (value.StartsWith("resource:", StringComparison.Ordinal) && value.Length > 9 || Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http");
    private static bool RasterType(string value) => value.ToLowerInvariant() is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp";
    private static void Report(string code, string feature, string fallback, int line) =>
        ConversionDiagnostics.Report(code, feature, fallback, sourceLocation: "line " + line.ToString(CultureInfo.InvariantCulture));
}
