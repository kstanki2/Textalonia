using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Textalonia.Model;

namespace Textalonia.Proofing;

internal sealed record SpellingWord(int Start, int Length, string Word, string SourceText, string? Language);
internal sealed record ParagraphSpellingInput(Guid ParagraphId, ImmutableArray<SpellingWord> Words);
internal sealed record LocalSpellingDiagnostic(int Start, int Length, string Word, string SourceText, string? Language,
    IReadOnlyList<string> Suggestions, string? DictionaryId);

internal static class SpellingScanner
{
    internal static ParagraphSpellingInput Capture(Paragraph paragraph, DocumentStyleResolver resolver)
    {
        var words = ImmutableArray.CreateBuilder<SpellingWord>();
        var zone = new StringBuilder();
        var zoneStart = 0;
        string? zoneLanguage = null;
        var position = 0;

        void Flush()
        {
            if (zone.Length == 0) return;
            AddWords(zone.ToString(), zoneStart, zoneLanguage, words);
            zone.Clear();
        }

        foreach (var run in paragraph.Runs)
        {
            var style = resolver.ResolveText(paragraph, run.Style);
            if (run.Inline is not null || style.NoProof)
            {
                Flush();
                position += run.Storage.Length;
                continue;
            }
            if (zone.Length > 0 && !string.Equals(zoneLanguage, style.Language, StringComparison.OrdinalIgnoreCase)) Flush();
            if (zone.Length == 0) { zoneStart = position; zoneLanguage = style.Language; }
            zone.Append(run.Text);
            position += run.Storage.Length;
        }
        Flush();
        return new(paragraph.Id, words.ToImmutable());
    }

    private static void AddWords(string text, int start, string? language, ImmutableArray<SpellingWord>.Builder words)
    {
        var position = 0;
        while (position < text.Length)
        {
            if (!ReadRune(text, position, out var first, out var width) || !Rune.IsLetter(first))
            { position += width; continue; }
            var wordStart = position;
            position += width;
            while (position < text.Length && ReadRune(text, position, out var next, out width))
            {
                var category = Rune.GetUnicodeCategory(next);
                if (Rune.IsLetterOrDigit(next) || category is UnicodeCategory.NonSpacingMark or
                    UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                { position += width; continue; }
                if (next.Value == 0xAD && position + width < text.Length &&
                    ReadRune(text, position + width, out var afterSoftHyphen, out _) && Rune.IsLetter(afterSoftHyphen))
                { position += width; continue; }
                if (next.Value is '\'' or '\u2019' && position + width < text.Length &&
                    ReadRune(text, position + width, out var after, out _) && Rune.IsLetter(after))
                { position += width; continue; }
                break;
            }
            var length = position - wordStart;
            if (length <= 256)
            {
                var source = text.Substring(wordStart, length);
                var word = source.Replace("\u00AD", "", StringComparison.Ordinal);
                if (word.Length <= 128) words.Add(new(start + wordStart, length, word, source, language));
            }
        }
    }

    private static bool ReadRune(string text, int position, out Rune rune, out int width)
    {
        var status = Rune.DecodeFromUtf16(text.AsSpan(position), out rune, out width);
        if (status == OperationStatus.Done) return true;
        rune = default;
        width = 1;
        return false;
    }

    internal static async Task<ImmutableArray<LocalSpellingDiagnostic>> CheckAsync(
        ParagraphSpellingInput input, ISpellChecker checker, SpellingResultCache cache,
        IReadOnlySet<string> ignored, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<LocalSpellingDiagnostic>();
        foreach (var word in input.Words)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ignored.Contains(IgnoreKey(word.Word, word.Language))) continue;
            if (!cache.TryGet(word.Word, word.Language, out var result))
            {
                result = await checker.CheckAsync(word.Word, word.Language, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The spell checker returned no result.");
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Suggestions is null || result.Suggestions.Count > 32 ||
                    result.Suggestions.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 128))
                    throw new InvalidOperationException("The spell checker returned invalid suggestions.");
                result = result with { Suggestions = result.Suggestions.ToArray() };
                cache.Store(word.Word, word.Language, result);
            }
            if (!result.IsCorrect)
                diagnostics.Add(new(word.Start, word.Length, word.Word, word.SourceText, word.Language,
                    result.Suggestions, result.DictionaryId));
        }
        return diagnostics.ToImmutable();
    }

    internal static string IgnoreKey(string word, string? language) => (language ?? "") + "\0" + word;
}

internal sealed class SpellingResultCache
{
    private readonly Dictionary<(string Word, string? Language), SpellingCheckResult> _values = [];
    private readonly Queue<(string Word, string? Language)> _order = [];
    private readonly object _gate = new();
    private const int MaximumEntries = 4096;

    internal bool TryGet(string word, string? language, out SpellingCheckResult result)
    {
        lock (_gate) return _values.TryGetValue((word, language), out result!);
    }

    internal void Store(string word, string? language, SpellingCheckResult result)
    {
        lock (_gate)
        {
            var key = (word, language);
            if (_values.ContainsKey(key)) return;
            _values.Add(key, result);
            _order.Enqueue(key);
            while (_order.Count > MaximumEntries) _values.Remove(_order.Dequeue());
        }
    }

    internal void Clear()
    {
        lock (_gate) { _values.Clear(); _order.Clear(); }
    }
}
