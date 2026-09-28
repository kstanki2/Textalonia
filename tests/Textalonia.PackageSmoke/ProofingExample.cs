using System.Globalization;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Proofing;
using Textalonia.Serialization;

namespace Textalonia.PackageSmoke;

internal static class ProofingExample
{
    internal static async Task VerifyAsync(TextaloniaEditor editor)
    {
        var corrections = new AutoCorrectService();
        corrections.Replacements["teh"] = "the";
        var replacement = corrections.GetReplacement(new AutoCorrectContext(
            "teh", CultureInfo.GetCultureInfo("en-US"), Guid.Empty, 0, 3, ' ', TextStyle.Default));
        var session = new EditorSession(FlowDocument.FromText("teh "));
        session.Select(4, 4);
        if (replacement is not AutoCorrectText || !session.TryApplyAutoCorrect(0, 3, replacement) ||
            session.Document.Text != "the ")
            throw new InvalidOperationException("Packaged AutoCorrect API failed.");
        session.Undo();
        if (session.Document.Text != "teh ")
            throw new InvalidOperationException("Packaged AutoCorrect undo failed.");

        var hyphenation = new DictionaryHyphenationService();
        hyphenation.Register(new WordListHyphenationDictionary(
            "en-US", "package-word-list", ["de·vel·op·ment"]));
        if (!hyphenation.BreakPositions("development", CultureInfo.GetCultureInfo("en-US")).SequenceEqual([2, 5, 7]))
            throw new InvalidOperationException("Packaged hyphenation dictionary failed.");
        var paragraph = new Paragraph("development")
        { Style = new() { SuppressHyphenation = true, HyphenateCaps = true } };
        var reopened = DocumentFormats.Json.Parse(DocumentFormats.Json.Serialize(new FlowDocument([paragraph])));
        if (reopened.Blocks[0] is not Paragraph retained || !retained.Style.SuppressHyphenation ||
            !retained.Style.HyphenateCaps)
            throw new InvalidOperationException("Packaged hyphenation settings round trip failed.");

        editor.HyphenationService = hyphenation;
        editor.SpellChecker = new DictionarySpellChecker([new PackageDictionary()]);
        editor.Document = FlowDocument.FromText("wrng development", new TextStyle { Language = "en-US" });
        await editor.WaitForSpellCheckAsync();
        var diagnostic = editor.SpellingDiagnostics.Single();
        if (diagnostic.Word != "wrng" || diagnostic.DictionaryId != "package-dictionary" ||
            !diagnostic.Suggestions.SequenceEqual(["right"]) ||
            !await editor.AddSpellingToDictionaryAsync(diagnostic))
            throw new InvalidOperationException("Packaged spelling diagnostics failed.");
        await editor.WaitForSpellCheckAsync();
        if (editor.SpellingDiagnostics.Count != 0)
            throw new InvalidOperationException("Packaged add-to-dictionary did not refresh spelling.");
    }

    private sealed class PackageDictionary : ISpellingDictionary
    {
        private bool _accepted;
        public string Id => "package-dictionary";
        public string Language => "en-US";
        public ValueTask<bool> ContainsAsync(string word, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(word != "wrng" || _accepted);
        public ValueTask<IReadOnlyList<string>> SuggestAsync(string word, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>(["right"]);
        public ValueTask AddWordAsync(string word, CancellationToken cancellationToken = default)
        { _accepted = true; return ValueTask.CompletedTask; }
    }
}