namespace Textalonia.Proofing;

/// <summary>A dictionary answer for one word. DictionaryId identifies the source that supplied the answer.</summary>
public sealed record SpellingCheckResult(bool IsCorrect, IReadOnlyList<string> Suggestions, string? DictionaryId = null)
{
    public static SpellingCheckResult Correct { get; } = new(true, []);
}

/// <summary>Host-supplied spell checker. Implementations may run on worker threads and should honor cancellation.</summary>
public interface ISpellChecker
{
    ValueTask<SpellingCheckResult> CheckAsync(string word, string? language, CancellationToken cancellationToken = default);
}

/// <summary>Optional host dictionary writer. The host owns persistence of added terms.</summary>
public interface IWritableSpellChecker : ISpellChecker
{
    ValueTask AddToDictionaryAsync(string word, string? language, CancellationToken cancellationToken = default);
}

/// <summary>A misspelled word in the current editing story, measured in UTF-16 document offsets.</summary>
public sealed record SpellingDiagnostic(int Start, int Length, string Word, string? Language,
    IReadOnlyList<string> Suggestions, string? DictionaryId)
{
    public int End => Start + Length;
    /// <summary>Exact source text, including any discretionary soft hyphens omitted from Word.</summary>
    public string SourceText { get; init; } = Word;
}

/// <summary>A language-specific dictionary supplied by the host. Null Language is the fallback dictionary.</summary>
public interface ISpellingDictionary
{
    string Id { get; }
    string? Language { get; }
    ValueTask<bool> ContainsAsync(string word, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<string>> SuggestAsync(string word, CancellationToken cancellationToken = default);
    ValueTask AddWordAsync(string word, CancellationToken cancellationToken = default);
}

/// <summary>Routes checks and additions to host dictionaries by BCP-47 language, then to a neutral fallback.</summary>
public sealed class DictionarySpellChecker : IWritableSpellChecker
{
    private readonly IReadOnlyList<ISpellingDictionary> _dictionaries;

    public DictionarySpellChecker(IEnumerable<ISpellingDictionary> dictionaries)
    {
        ArgumentNullException.ThrowIfNull(dictionaries);
        _dictionaries = dictionaries.ToArray();
        if (_dictionaries.Any(d => d is null || string.IsNullOrWhiteSpace(d.Id)))
            throw new ArgumentException("Dictionaries must have an identity.", nameof(dictionaries));
    }

    public async ValueTask<SpellingCheckResult> CheckAsync(string word, string? language, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(word);
        var dictionary = Select(language);
        if (dictionary is null) return SpellingCheckResult.Correct;
        if (await dictionary.ContainsAsync(word, cancellationToken).ConfigureAwait(false))
            return new(true, [], dictionary.Id);
        return new(false, await dictionary.SuggestAsync(word, cancellationToken).ConfigureAwait(false), dictionary.Id);
    }

    public ValueTask AddToDictionaryAsync(string word, string? language, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(word);
        return (Select(language) ?? throw new InvalidOperationException("No dictionary is available for this language."))
            .AddWordAsync(word, cancellationToken);
    }

    private ISpellingDictionary? Select(string? language)
    {
        if (language is not null)
        {
            var candidate = language;
            while (candidate.Length > 0)
            {
                var match = _dictionaries.FirstOrDefault(d => string.Equals(d.Language, candidate, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
                var separator = candidate.LastIndexOf('-');
                if (separator < 0) break;
                candidate = candidate[..separator];
            }
        }
        return _dictionaries.FirstOrDefault(d => d.Language is null);
    }
}
