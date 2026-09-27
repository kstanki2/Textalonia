using System.Collections.Immutable;
using Avalonia.Media;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>A token range measured in UTF-16 units in the complete code block, including newline separators.</summary>
public sealed record CodeHighlightToken(int Start, int Length, string Kind);

/// <summary>Optional presentation overrides. Null properties retain the original code style.</summary>
public sealed record CodeHighlightStyle(string? Foreground = null, string? Background = null, bool? Bold = null, bool? Italic = null);

/// <summary>Host-supplied, background-thread-safe tokenizer and token palette. Unknown languages should return no tokens.</summary>
public interface ICodeHighlighter
{
    ValueTask<IReadOnlyList<CodeHighlightToken>> TokenizeAsync(string? language, string code, CancellationToken cancellationToken = default);
    CodeHighlightStyle? GetStyle(string? language, string tokenKind);
}

// This cache owns presentation data only. It never modifies the document used by editing, copying or serialization.
internal sealed class CodeHighlightCache
{
    private sealed record Span(int Start, int Length, CodeHighlightStyle Style);
    private sealed record Key(ICodeHighlighter Adapter, string? Language, string Text);
    private sealed record Entry(Key Key, ImmutableArray<Span> Spans, long Bytes);
    private readonly LinkedList<Entry> _entries = [];
    private readonly object _gate = new();
    private long _bytes, _generation;
    internal int Count { get { lock (_gate) return _entries.Count; } }
    internal long Bytes { get { lock (_gate) return _bytes; } }
    internal const int MaximumCodeLength = 131072;
    private const int MaximumTokens = 4096;
    private const long MaximumBytes = 1024 * 1024;

    public void Clear() { lock (_gate) { _entries.Clear(); _bytes = 0; _generation++; } }

    public async Task<FlowDocument> HighlightAsync(FlowDocument document, ICodeHighlighter adapter, CancellationToken cancellationToken)
    {
        async Task<ImmutableArray<Block>> Visit(ImmutableArray<Block> blocks)
        {
            var result = ImmutableArray.CreateBuilder<Block>(blocks.Length);
            foreach (var block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block is Section { Semantic: SectionSemantic.CodeBlock } code)
                    result.Add(await HighlightSection(code, adapter, cancellationToken).ConfigureAwait(false));
                else if (block is Section section)
                    result.Add(section with { Blocks = await Visit(section.Blocks).ConfigureAwait(false) });
                else result.Add(block);
            }
            return result.ToImmutable();
        }
        var presentation = document with { Blocks = await Visit(document.Blocks).ConfigureAwait(false) };
        return DocumentTree.ReuseSnapshot(document, presentation, cancellationToken);
    }

    private async Task<Section> HighlightSection(Section section, ICodeHighlighter adapter, CancellationToken token)
    {
        if (section.Blocks.Any(b => b is not Paragraph)) return section;
        var paragraphs = section.Blocks.Cast<Paragraph>().ToArray();
        var length = paragraphs.Sum(p => (long)p.Length) + Math.Max(0, paragraphs.Length - 1);
        if (length > MaximumCodeLength) return section;
        var text = string.Join('\n', paragraphs.Select(p => p.Text));
        var key = new Key(adapter, section.CodeLanguage, text);
        ImmutableArray<Span> spans = default;
        long generation;
        lock (_gate)
        {
            generation = _generation;
            var found = _entries.First;
            while (found is not null)
            {
                if (ReferenceEquals(found.Value.Key.Adapter, adapter) && found.Value.Key.Language == key.Language && found.Value.Key.Text == text)
                { spans = found.Value.Spans; _entries.Remove(found); _entries.AddFirst(found); break; }
                found = found.Next;
            }
        }
        if (spans.IsDefault)
        {
            var tokens = await adapter.TokenizeAsync(section.CodeLanguage, text, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The code highlighter returned null tokens.");
            token.ThrowIfCancellationRequested();
            if (tokens.Count > MaximumTokens) throw new InvalidOperationException("The code highlighter returned too many tokens.");
            var builder = ImmutableArray.CreateBuilder<Span>();
            var end = 0;
            foreach (var item in tokens)
            {
                token.ThrowIfCancellationRequested();
                if (item is null || item.Start < end || item.Length <= 0 || item.Start > text.Length || item.Length > text.Length - item.Start ||
                    string.IsNullOrEmpty(item.Kind) || item.Kind.Length > 128)
                    throw new InvalidOperationException("Code tokens must be ordered, non-overlapping ranges within the original code.");
                end = item.Start + item.Length;
                var style = adapter.GetStyle(section.CodeLanguage, item.Kind);
                if (style is null) continue;
                if (!ValidColor(style.Foreground) || !ValidColor(style.Background))
                    throw new InvalidOperationException("The code highlighter returned an invalid color.");
                builder.Add(new(item.Start, item.Length, style));
            }
            spans = builder.ToImmutable();
            var bytes = 2L * text.Length + spans.Length * 128L + 256;
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (generation == _generation && bytes <= MaximumBytes)
                {
                    _entries.AddFirst(new Entry(key, spans, bytes)); _bytes += bytes;
                    while (_entries.Count > 64 || _bytes > MaximumBytes)
                    { _bytes -= _entries.Last!.Value.Bytes; _entries.RemoveLast(); }
                }
            }
        }
        var blocks = ImmutableArray.CreateBuilder<Block>(paragraphs.Length);
        var offset = 0;
        foreach (var paragraph in paragraphs)
        {
            token.ThrowIfCancellationRequested();
            var runs = new List<RichRun>();
            var cursor = 0;
            foreach (var span in spans)
            {
                if (span.Start >= offset + paragraph.Length) break;
                if (span.Start + span.Length <= offset) continue;
                var start = Math.Max(0, span.Start - offset);
                var finish = Math.Min(paragraph.Length, span.Start + span.Length - offset);
                runs.AddRange(paragraph.Slice(cursor, start - cursor));
                runs.AddRange(paragraph.Slice(start, finish - start).Select(r => r with { Style = Apply(r.Style, span.Style) }));
                cursor = finish;
            }
            runs.AddRange(paragraph.Slice(cursor, paragraph.Length - cursor));
            blocks.Add(paragraph with { Runs = Paragraph.Normalize(runs) });
            offset += paragraph.Length + 1;
        }
        return section with { Blocks = blocks.ToImmutable() };
    }

    private static bool ValidColor(string? color) => color is null || color.Length <= 128 && Color.TryParse(color, out _);
    private static TextStyle Apply(TextStyle original, CodeHighlightStyle style) => original with
    {
        Foreground = style.Foreground ?? original.Foreground,
        Background = style.Background ?? original.Background,
        Bold = style.Bold ?? original.Bold,
        FontWeight = style.Bold is null ? original.FontWeight : null,
        Italic = style.Italic ?? original.Italic
    };
}
