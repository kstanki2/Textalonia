using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Proofing;
using Xunit;

namespace Textalonia.Tests;

public sealed class SpellingTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);

    [Fact]
    public Task Mixed_languages_and_no_proof_use_resolved_run_boundaries() => Run(() =>
    {
        var english = TextStyle.Default with { Language = "en-US" };
        var french = TextStyle.Default with { Language = "fr-FR" };
        var excluded = french with { NoProof = true };
        var paragraph = new Paragraph([
            new RichRun("he", english), new RichRun("lo ", english),
            new RichRun("bonjor ", french), new RichRun("secret", excluded)]);
        var checker = new RecordingChecker();
        var editor = new TextaloniaEditor { Document = new FlowDocument([paragraph]), SpellChecker = checker };

        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());

        Assert.Null(editor.SpellCheckError);
        Assert.Equal(["helo", "bonjor"], editor.SpellingDiagnostics.Select(d => d.Word));
        Assert.Equal(["en-US", "fr-FR"], editor.SpellingDiagnostics.Select(d => d.Language));
        Assert.Equal([0, 5], editor.SpellingDiagnostics.Select(d => d.Start));
        Assert.Equal([("helo", "en-US"), ("bonjor", "fr-FR")], checker.Calls.ToArray());
    });

    [Fact]
    public Task Soft_hyphen_is_removed_for_dictionary_lookup_but_kept_in_source_span() => Run(() =>
    {
        var checker = new RecordingChecker();
        var editor = new TextaloniaEditor { Document = FlowDocument.FromText("co\u00ADoperate"), SpellChecker = checker };
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());

        var issue = Assert.Single(editor.SpellingDiagnostics);
        Assert.Equal("cooperate", issue.Word);
        Assert.Equal("co\u00ADoperate", issue.SourceText);
        Assert.Equal(0, issue.Start);
        Assert.Equal(10, issue.Length);
        Assert.Equal([("cooperate", (string?)null)], checker.Calls.ToArray());
        Assert.True(editor.ReplaceSpelling(issue, "cooperate"));
        Assert.Equal("cooperate", editor.Document.Text);
    });

    [Fact]
    public Task Right_click_suggestions_follow_clicked_word_inside_a_larger_selection() => Run(() =>
    {
        var editor = new TextaloniaEditor
        { Text = "bad wrng", ShowToolbar = false, SpellChecker = new RecordingChecker() };
        var window = new Window { Width = 600, Height = 300, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            editor.Session.Select(0, editor.Session.Index.Length);
            var click = surface.TranslatePoint(surface.Layout.Caret(1).Center, window)!.Value;
            window.MouseDown(click, MouseButton.Right);
            window.MouseUp(click, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            if (!surface.ContextMenu!.IsOpen) surface.ContextMenu.Open(surface);

            var items = ((IEnumerable<object>)surface.ContextMenu.ItemsSource!).OfType<MenuItem>().ToArray();
            Assert.Equal("bad!", items[0].Header);
            Assert.Equal("bad wrng", editor.SelectedText);
            surface.ContextMenu.Close();
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Keyboard_opened_context_menu_uses_the_current_caret() => Run(() =>
    {
        var editor = new TextaloniaEditor
        { Text = "bad wrng", ShowToolbar = false, SpellChecker = new RecordingChecker() };
        var window = new Window { Width = 600, Height = 300, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            editor.Session.Select(6, 6);
            surface.ContextMenu!.Open(surface);
            var items = ((IEnumerable<object>)surface.ContextMenu.ItemsSource!).OfType<MenuItem>().ToArray();
            Assert.Equal("wrng!", items[0].Header);
            surface.ContextMenu.Close();
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Old_checker_result_cannot_replace_new_revision_even_when_checker_ignores_cancellation() => Run(() =>
    {
        var checker = new DelayedChecker();
        var editor = new TextaloniaEditor { Document = FlowDocument.FromText("olde"), SpellChecker = checker };
        var oldPass = editor.WaitForSpellCheckAsync();
        MarkdownViewerTests.Pump(checker.Started.Task);

        editor.Document = FlowDocument.FromText("newer");
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
        Assert.Single(editor.SpellingDiagnostics);
        Assert.Equal("newer", editor.SpellingDiagnostics[0].Word);
        checker.Release.SetResult(new(false, ["old"]));
        MarkdownViewerTests.Pump(oldPass);

        Assert.Equal("newer", editor.Document.Text);
        Assert.Equal("newer", Assert.Single(editor.SpellingDiagnostics).Word);
        Assert.False(editor.IsSpellChecking);
    });

    [Fact]
    public Task Canceled_answer_after_dictionary_refresh_cannot_poison_new_cache() => Run(() =>
    {
        var checker = new RefreshRaceChecker();
        var editor = new TextaloniaEditor { Document = FlowDocument.FromText("teh"), SpellChecker = checker };
        var oldPass = editor.WaitForSpellCheckAsync();
        MarkdownViewerTests.Pump(checker.FirstStarted.Task);

        editor.RefreshSpelling();
        var newPass = editor.WaitForSpellCheckAsync();
        MarkdownViewerTests.Pump(checker.SecondStarted.Task);
        checker.FirstResult.SetResult(new(false, ["the"]));
        MarkdownViewerTests.Pump(oldPass);
        checker.SecondResult.SetResult(SpellingCheckResult.Correct);
        MarkdownViewerTests.Pump(newPass);
        Assert.Empty(editor.SpellingDiagnostics);

        editor.Session.Select(0, 0);
        editor.Session.InsertText("a ");
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
        Assert.Empty(editor.SpellingDiagnostics);
        Assert.Equal(2, checker.TehCalls);
    });

    [Fact]
    public Task An_edit_checks_its_paragraph_and_rebases_untouched_diagnostics() => Run(() =>
    {
        var checker = new RecordingChecker();
        var editor = new TextaloniaEditor { Document = FlowDocument.FromText("teh\nwrng"), SpellChecker = checker };
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
        Assert.Equal(1, checker.Calls.Count(call => call.Word == "wrng"));

        editor.Session.Select(0, 0);
        editor.Session.InsertText("very ");
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());

        Assert.Equal("very teh\nwrng", editor.Document.Text);
        Assert.Equal(9, editor.SpellingDiagnostics.Single(d => d.Word == "wrng").Start);
        Assert.Equal(1, checker.Calls.Count(call => call.Word == "wrng"));
        Assert.Equal(1, checker.Calls.Count(call => call.Word == "teh"));
        Assert.Equal(1, checker.Calls.Count(call => call.Word == "very"));
    });

    [Fact]
    public Task Typing_during_an_initial_pass_reuses_untouched_captures_and_completes_them() => Run(() =>
    {
        var checker = new PendingChecker();
        var source = "slow\n" + string.Join('\n', Enumerable.Range(0, 100).Select(i => $"term{i}"));
        var editor = new TextaloniaEditor { Document = FlowDocument.FromText(source), SpellChecker = checker };
        var oldPass = editor.WaitForSpellCheckAsync();
        MarkdownViewerTests.Pump(checker.Started.Task);

        editor.Session.Select(0, 0);
        editor.Session.InsertText("new ");
        var visited = editor.Session.Index.VisitedParagraphs;
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
        Assert.InRange(visited, 1, 30);
        Assert.Contains(editor.SpellingDiagnostics, d => d.Word == "term99");
        Assert.Contains(editor.SpellingDiagnostics, d => d.Word == "slow");
        checker.Release.SetResult(new(false, ["fast"]));
        MarkdownViewerTests.Pump(oldPass);
        Assert.Contains(editor.SpellingDiagnostics, d => d.Word == "term99");
    });

    [Fact]
    public Task Replace_ignore_and_add_to_host_dictionary_follow_normal_editing_workflow() => Run(() =>
    {
        var dictionary = new MemoryDictionary("user-en", "en-US", ["the"]);
        var checker = new DictionarySpellChecker([dictionary]);
        var editor = new TextaloniaEditor
        {
            Document = FlowDocument.FromText("teh teh", TextStyle.Default with { Language = "en-US" }),
            SpellChecker = checker
        };
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
        Assert.Equal(2, editor.SpellingDiagnostics.Count);
        Assert.All(editor.SpellingDiagnostics, d => Assert.Equal("user-en", d.DictionaryId));

        Assert.True(editor.SelectNextSpellingError());
        Assert.Equal("teh", editor.SelectedText);
        Assert.True(editor.ReplaceSpelling(editor.SpellingDiagnostics[0], "the"));
        Assert.Equal("the teh", editor.Document.Text);
        editor.Undo();
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
        Assert.Equal("teh teh", editor.Document.Text);

        Assert.True(editor.IgnoreSpelling(editor.SpellingDiagnostics[0]));
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
        Assert.Empty(editor.SpellingDiagnostics);
        editor.SpellChecker = null;
        editor.SpellChecker = checker;
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
        Assert.Equal(2, editor.SpellingDiagnostics.Count);

        MarkdownViewerTests.Pump(editor.AddSpellingToDictionaryAsync(editor.SpellingDiagnostics[0]));
        MarkdownViewerTests.Pump(editor.WaitForSpellCheckAsync());
        Assert.Contains("teh", dictionary.Terms);
        Assert.Empty(editor.SpellingDiagnostics);
    });

    [Fact]
    public async Task Dictionary_adapter_uses_exact_parent_then_fallback_with_source_identity()
    {
        var french = new MemoryDictionary("fr-ca", "fr-CA", ["bonjour"]);
        var chinese = new MemoryDictionary("zh-hant", "zh-Hant", ["文字"]);
        var fallback = new MemoryDictionary("default", null, ["other"]);
        var checker = new DictionarySpellChecker([french, chinese, fallback]);

        Assert.Equal("fr-ca", (await checker.CheckAsync("bonjor", "fr-CA")).DictionaryId);
        Assert.Equal("zh-hant", (await checker.CheckAsync("文字", "zh-Hant-TW")).DictionaryId);
        Assert.Equal("default", (await checker.CheckAsync("unknown", "de-DE")).DictionaryId);
        await checker.AddToDictionaryAsync("bonjor", "fr-CA");
        Assert.Contains("bonjor", french.Terms);
        Assert.DoesNotContain("bonjor", fallback.Terms);
    }

    private sealed class RecordingChecker : ISpellChecker
    {
        public ConcurrentQueue<(string Word, string? Language)> Calls { get; } = new();
        public ValueTask<SpellingCheckResult> CheckAsync(string word, string? language, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue((word, language));
            return ValueTask.FromResult(new SpellingCheckResult(false, [word + "!"], "recording"));
        }
    }

    private sealed class DelayedChecker : ISpellChecker
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SpellingCheckResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<SpellingCheckResult> CheckAsync(string word, string? language, CancellationToken cancellationToken = default)
        {
            if (word == "olde") { Started.TrySetResult(); return new(Release.Task); }
            return ValueTask.FromResult(new SpellingCheckResult(false, ["new"], "test"));
        }
    }

    private sealed class RefreshRaceChecker : ISpellChecker
    {
        private int _tehCalls;
        public int TehCalls => _tehCalls;
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SpellingCheckResult> FirstResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SpellingCheckResult> SecondResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SpellingCheckResult> CheckAsync(string word, string? language, CancellationToken cancellationToken = default)
        {
            if (word != "teh") return ValueTask.FromResult(SpellingCheckResult.Correct);
            return Interlocked.Increment(ref _tehCalls) switch
            {
                1 => First(),
                2 => Second(),
                _ => ValueTask.FromResult(SpellingCheckResult.Correct)
            };
        }

        private ValueTask<SpellingCheckResult> First()
        { FirstStarted.TrySetResult(); return new(FirstResult.Task); }
        private ValueTask<SpellingCheckResult> Second()
        { SecondStarted.TrySetResult(); return new(SecondResult.Task); }
    }

    private sealed class PendingChecker : ISpellChecker
    {
        private int _slowCalls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SpellingCheckResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<SpellingCheckResult> CheckAsync(string word, string? language, CancellationToken cancellationToken = default)
        {
            if (word == "slow" && Interlocked.Increment(ref _slowCalls) == 1)
            { Started.TrySetResult(); return new(Release.Task); }
            return ValueTask.FromResult(new SpellingCheckResult(false, ["fixed"]));
        }
    }

    private sealed class MemoryDictionary(string id, string? language, IEnumerable<string> terms) : ISpellingDictionary
    {
        private readonly HashSet<string> _terms = new(terms, StringComparer.OrdinalIgnoreCase);
        public string Id => id;
        public string? Language => language;
        public IReadOnlySet<string> Terms => _terms;
        public ValueTask<bool> ContainsAsync(string word, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_terms.Contains(word));
        public ValueTask<IReadOnlyList<string>> SuggestAsync(string word, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<string>>(["the"]);
        public ValueTask AddWordAsync(string word, CancellationToken cancellationToken = default)
        { _terms.Add(word); return ValueTask.CompletedTask; }
    }
}
