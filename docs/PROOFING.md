# Proofing services

Spelling and dictionary-based hyphenation are opt-in. The editor does not include a system dictionary or store user terms. A host assigns `TextaloniaEditor.SpellChecker` and owns dictionary data, updates, and persistence. AutoCorrect has built-in capitalization and URL rules.

## Spelling

Implement `ISpellChecker.CheckAsync(word, language, cancellationToken)` to return a `SpellingCheckResult`. A false `IsCorrect` value supplies suggestions and an optional dictionary identity. `IWritableSpellChecker` additionally supports **Add to dictionary**. The built-in `DictionarySpellChecker` routes requests to host `ISpellingDictionary` instances using the run's BCP-47 language, then its neutral language, then a dictionary whose language is null. A missing dictionary leaves the word unchecked.

The editor resolves named styles before checking. It excludes `NoProof` text and inline objects, combines adjacent runs with the same language, and checks changed paragraphs asynchronously. A soft hyphen inside a word is omitted for dictionary lookup while its source span is retained for underlines and replacement. Results are cached by word and language. Each edit cancels the earlier pass, immediately removes affected underlines, and discards results from an older revision. Captured, untouched paragraphs from a pending pass are reused after typing. `RefreshSpelling()` clears cached answers after a host dictionary changes.

`SpellingDiagnostics` contains current-story UTF-16 offsets, words, languages, suggestions, and dictionary identities. The surface draws red wavy underlines in Simple and Print views. The context menu offers up to five suggestions, **Ignore all**, **Add to dictionary** when writable, and **Next spelling error**. Hosts can drive the same flow with `SelectNextSpellingError`, `ReplaceSpelling`, `IgnoreSpelling`, and `AddSpellingToDictionaryAsync`. Replacements use the normal edit policy and undo history. Ignore entries live only for the editor session.

```csharp
editor.SpellChecker = new DictionarySpellChecker(new ISpellingDictionary[]
{
    englishDictionary,
    frenchDictionary
});
await editor.WaitForSpellCheckAsync();
```

`ISpellingDictionary.Id` is copied into diagnostics so a host can show the source of a suggestion. A host dictionary implements `AddWordAsync` to persist accepted terms; Textalonia does not write a dictionary file.

## AutoCorrect

`TextaloniaEditor.AutoCorrectService` defaults to `AutoCorrectService`. Its
`Replacements` table matches completed tokens without case sensitivity.
`CorrectTwoInitialCapitals` changes a word like `THe` to `The`; `DetectUrls`
turns a safe `http`, `https`, or `www.` token into a hyperlink. Set the service
to null to disable correction. Hosts can supply an `IAutoCorrectService` or set
`AutoCorrectService.ReplacementRequested` to return `AutoCorrectText`,
`AutoCorrectFragment`, `AutoCorrectInline` (including an image resource), or
`AutoCorrectLink`.

```csharp
var corrections = new AutoCorrectService();
corrections.Replacements["teh"] = "the";
corrections.ReplacementRequested = context =>
    context.Text == "logo"
        ? new AutoCorrectInline(imageDescriptor, imageResource)
        : null;
editor.AutoCorrectService = corrections;
```

Correction runs after committed text input and after Enter or an inserted Tab.
`AutoCorrectBoundaries` configures the completing characters; the defaults are
space, tab, newline, period, comma, semicolon, colon, exclamation mark, and
question mark. IME preedit remains transient and cannot trigger correction.
The typed boundary remains after the replacement. Undo once restores the
original token and boundary; another undo removes the preceding typing.
Corrections use the session's edit policy and protected range checks.
`NoProof` runs do not auto-correct. Replacement callbacks run on the editor
UI thread and should finish promptly.

## Hyphenation

Assign `TextaloniaEditor.HyphenationService` to enable automatic dictionary
breaks. The service returns UTF-16 offsets within each word and a `Revision`
that changes whenever its decisions change. Raise `Changed` after incrementing
the revision so visible Simple, Draft, and Print layouts reflow. The editor does
not own or persist the service.

```csharp
var hyphenation = new DictionaryHyphenationService();
hyphenation.Register(new WordListHyphenationDictionary(
    "en-US", "my-english-dictionary", ["de\u00B7vel\u00B7op\u00B7ment"]));
editor.HyphenationService = hyphenation;
```

`DictionaryHyphenationService` is a language router. It selects the most
specific registered BCP-47 dictionary, walking parent tags toward the neutral
language. Its word-list adapter uses
U+00B7 middle dots to mark break positions and exposes `Source` and `Terms`
for provenance. A host can implement `IHyphenationDictionary` or
`IHyphenationService` for pattern-based dictionaries. No dictionary is bundled.

Automatic breaks use each run's `TextStyle.Language` and skip a word spanning
different languages. `ParagraphStyle.SuppressHyphenation` turns them off;
`HyphenateCaps` allows all-capital words to use them. Explicit U+00AD soft
hyphens remain available when automatic breaks are suppressed. The shaping
projection adds discretionary opportunities without changing document text or
UTF-16 caret and selection offsets. Inline object positions use the same
mapping. Page snapshots carry these shaped lines to preview, printing, and
PDF output. When a shaping limit is configured, automatic opportunities are
bounded by the remaining character budget for each shaping window. A page
snapshot caches queried word breaks per retained paragraph, so an evicted glyph
window re-shapes consistently after a dictionary update. This cache uses memory
in proportion to distinct words in the retained paragraphs and is released
with their measurements.

Native JSON and data XAML retain both paragraph settings. DOCX writes
`w:suppressAutoHyphens`; `HyphenateCaps` round-trips through a Textalonia
extension on `w:pPr`, which other Word processors may ignore. Dictionary
contents are host data and are not embedded in document files.
