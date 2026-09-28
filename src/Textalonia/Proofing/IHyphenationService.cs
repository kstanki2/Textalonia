using System.Collections.Immutable;
using System.Globalization;

namespace Textalonia.Proofing;

/// <summary>Supplies discretionary UTF-16 break offsets within a word. The host owns dictionary loading and persistence.</summary>
public interface IHyphenationService
{
    /// <summary>Increment this value whenever dictionary decisions can change.</summary>
    long Revision { get; }
    /// <summary>Raised after Revision changes so an editor can reflow its visible pages.</summary>
    event EventHandler? Changed;
    /// <summary>Returns offsets between characters of word. Invalid offsets are ignored by layout.</summary>
    IReadOnlyList<int> BreakPositions(string word, CultureInfo culture);
}

/// <summary>A host supplied, language identified dictionary. Source identifies its provenance.</summary>
public interface IHyphenationDictionary
{
    string Language { get; }
    string Source { get; }
    IReadOnlyList<int> BreakPositions(string word);
}

/// <summary>Routes language-specific requests to host dictionaries, including neutral-language fallback.</summary>
public sealed class DictionaryHyphenationService : IHyphenationService
{
    private ImmutableDictionary<string, IHyphenationDictionary> _dictionaries =
        ImmutableDictionary.Create<string, IHyphenationDictionary>(StringComparer.OrdinalIgnoreCase);
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);
    public event EventHandler? Changed;
    public IReadOnlyCollection<IHyphenationDictionary> Dictionaries => _dictionaries.Values.ToArray();

    public void Register(IHyphenationDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        var language = CultureInfo.GetCultureInfo(dictionary.Language).Name;
        ImmutableInterlocked.AddOrUpdate(ref _dictionaries, language, dictionary, (_, _) => dictionary);
        Interlocked.Increment(ref _revision);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var removed = ImmutableInterlocked.TryRemove(ref _dictionaries, CultureInfo.GetCultureInfo(language).Name, out _);
        if (!removed) return false;
        Interlocked.Increment(ref _revision);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public IReadOnlyList<int> BreakPositions(string word, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(culture);
        var dictionaries = _dictionaries;
        for (var candidate = culture; candidate.Name.Length > 0; candidate = candidate.Parent)
            if (dictionaries.TryGetValue(candidate.Name, out var dictionary)) return dictionary.BreakPositions(word);
        return [];
    }
}

/// <summary>An explicit word-list adapter. Entries use a middle dot to mark each allowed break.</summary>
public sealed class WordListHyphenationDictionary : IHyphenationDictionary
{
    private readonly ImmutableDictionary<string, ImmutableArray<int>> _terms;
    public string Language { get; }
    public string Source { get; }
    public IReadOnlyCollection<string> Terms => _terms.Keys.ToArray();

    public WordListHyphenationDictionary(string language, string source, IEnumerable<string> terms)
    {
        Language = CultureInfo.GetCultureInfo(language).Name;
        Source = !string.IsNullOrWhiteSpace(source) ? source : throw new ArgumentException("Dictionary source is required.", nameof(source));
        ArgumentNullException.ThrowIfNull(terms);
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in terms)
        {
            if (string.IsNullOrWhiteSpace(entry)) throw new ArgumentException("Dictionary terms must not be empty.", nameof(terms));
            var positions = ImmutableArray.CreateBuilder<int>();
            var word = new System.Text.StringBuilder(entry.Length);
            foreach (var ch in entry)
            {
                if (ch == '\u00B7')
                {
                    if (word.Length == 0 || positions.Count > 0 && positions[^1] == word.Length)
                        throw new ArgumentException("Invalid dictionary break position.", nameof(terms));
                    positions.Add(word.Length);
                }
                else word.Append(ch);
            }
            if (positions.Count == 0 || positions[^1] == word.Length || builder.ContainsKey(word.ToString()))
                throw new ArgumentException("Dictionary term needs unique, internal break positions.", nameof(terms));
            builder.Add(word.ToString(), positions.ToImmutable());
        }
        _terms = builder.ToImmutable();
    }

    public IReadOnlyList<int> BreakPositions(string word) => _terms.TryGetValue(word, out var positions) ? positions : [];
}
