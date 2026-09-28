using System.Collections.Immutable;
using Avalonia;
using Avalonia.Threading;
using Textalonia.Editing;
using Textalonia.Proofing;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    public static readonly DirectProperty<TextaloniaEditor, IReadOnlyList<SpellingDiagnostic>> SpellingDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<TextaloniaEditor, IReadOnlyList<SpellingDiagnostic>>(
            nameof(SpellingDiagnostics), editor => editor.SpellingDiagnostics);
    public static readonly DirectProperty<TextaloniaEditor, bool> IsSpellCheckingProperty =
        AvaloniaProperty.RegisterDirect<TextaloniaEditor, bool>(nameof(IsSpellChecking), editor => editor.IsSpellChecking);
    public static readonly DirectProperty<TextaloniaEditor, Exception?> SpellCheckErrorProperty =
        AvaloniaProperty.RegisterDirect<TextaloniaEditor, Exception?>(nameof(SpellCheckError), editor => editor.SpellCheckError);

    private ISpellChecker? _spellChecker;
    private SpellingResultCache _spellingCache = new();
    private readonly HashSet<string> _ignoredSpellingWords = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<Guid, ImmutableArray<LocalSpellingDiagnostic>> _spellingByParagraph = [];
    private Dictionary<Guid, ParagraphSpellingInput> _pendingSpellingInputs = [];
    private IReadOnlyList<SpellingDiagnostic> _spellingDiagnostics = [];
    private bool _isSpellChecking;
    private Exception? _spellCheckError;
    private CancellationTokenSource? _spellingCancellation;
    private Task _spellingTask = Task.CompletedTask;
    private int _spellingGeneration, _observedSpellingRevision = -1;
    private Guid _observedSpellingStory;

    /// <summary>Optional host spell checker. Assigning a checker starts a pass over the active story.</summary>
    public ISpellChecker? SpellChecker
    {
        get => _spellChecker;
        set
        {
            if (ReferenceEquals(_spellChecker, value)) return;
            _spellChecker = value;
            _spellingCache = new();
            _ignoredSpellingWords.Clear();
            QueueSpelling(full: true);
        }
    }

    /// <summary>Current misspellings in the active story. Offsets follow the current session revision.</summary>
    public IReadOnlyList<SpellingDiagnostic> SpellingDiagnostics => _spellingDiagnostics;
    public bool IsSpellChecking => _isSpellChecking;
    public Exception? SpellCheckError => _spellCheckError;

    /// <summary>Wait for the current pass. Canceling the wait leaves the editor pass running.</summary>
    public Task WaitForSpellCheckAsync(CancellationToken cancellationToken = default) =>
        _spellingTask.WaitAsync(cancellationToken);

    /// <summary>Clear cached checker answers after an external dictionary change and check the active story again.</summary>
    public void RefreshSpelling()
    {
        _spellingCache = new();
        QueueSpelling(full: true);
    }

    private void OnProofingSessionChanged()
    {
        if (_observedSpellingRevision != Session.Revision || _observedSpellingStory != Session.ActiveStoryId)
            QueueSpelling(full: _observedSpellingStory != Session.ActiveStoryId);
    }

    private void QueueSpelling(bool full)
    {
        var generation = ++_spellingGeneration;
        _spellingCancellation?.Cancel();
        _spellingCancellation?.Dispose();
        _spellingCancellation = new();
        var token = _spellingCancellation.Token;
        var revision = Session.Revision;
        var storyId = Session.ActiveStoryId;
        var checker = SpellChecker;
        var cache = _spellingCache;
        var previousRevision = _observedSpellingRevision;
        _observedSpellingRevision = revision;
        _observedSpellingStory = storyId;
        SetAndRaise(SpellCheckErrorProperty, ref _spellCheckError, null);
        if (checker is null)
        {
            _spellingByParagraph.Clear();
            _pendingSpellingInputs.Clear();
            PublishSpellingDiagnostics();
            SetAndRaise(IsSpellCheckingProperty, ref _isSpellChecking, false);
            _spellingTask = Task.CompletedTask;
            return;
        }

        var index = Session.Index;
        var edit = Session.LastEdit;
        full |= previousRevision < 0 || edit is null || edit.Reset || edit.BeforeRevision != previousRevision;
        var working = full ? new Dictionary<Guid, ImmutableArray<LocalSpellingDiagnostic>>() : new(_spellingByParagraph);
        var pending = full ? new Dictionary<Guid, ParagraphSpellingInput>() : new(_pendingSpellingInputs);
        var paragraphs = full ? index.Enumerate(0, index.Length).ToArray() :
            index.Enumerate(Math.Max(0, edit!.Start - 1),
                Math.Min(index.Length, edit.Start + Math.Max(1, edit.InsertedLength) + 1)).ToArray();
        if (!full)
            foreach (var changedId in edit!.ChangedParagraphs)
            { working.Remove(changedId); pending.Remove(changedId); }
        foreach (var paragraph in paragraphs)
        { working.Remove(paragraph.Paragraph.Id); pending.Remove(paragraph.Paragraph.Id); }
        _spellingByParagraph = working;
        PublishSpellingDiagnostics();
        var resolver = new Model.DocumentStyleResolver(Session.Document);
        foreach (var paragraph in paragraphs)
            pending[paragraph.Paragraph.Id] = SpellingScanner.Capture(paragraph.Paragraph, resolver);
        foreach (var id in pending.Keys.Where(id => index.Tree.Paths?.Find(id) is null).ToArray()) pending.Remove(id);
        _pendingSpellingInputs = pending;
        var inputs = pending.Values.ToArray();
        var ignored = new HashSet<string>(_ignoredSpellingWords, StringComparer.OrdinalIgnoreCase);
        SetAndRaise(IsSpellCheckingProperty, ref _isSpellChecking, true);
        _spellingTask = ScanSpellingAsync(inputs, new(working), ignored, checker, cache, revision, storyId, generation, token);
    }

    private async Task ScanSpellingAsync(ParagraphSpellingInput[] inputs,
        Dictionary<Guid, ImmutableArray<LocalSpellingDiagnostic>> working, IReadOnlySet<string> ignored,
        ISpellChecker checker, SpellingResultCache cache, int revision, Guid storyId, int generation, CancellationToken token)
    {
        try
        {
            var completed = await Task.Run(async () =>
            {
                foreach (var input in inputs)
                {
                    token.ThrowIfCancellationRequested();
                    var found = await SpellingScanner.CheckAsync(input, checker, cache, ignored, token).ConfigureAwait(false);
                    if (!found.IsEmpty) working[input.ParagraphId] = found;
                }
                return working;
            }, token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrent()) return;
                _spellingByParagraph = completed;
                _pendingSpellingInputs.Clear();
                PublishSpellingDiagnostics();
                SetAndRaise(IsSpellCheckingProperty, ref _isSpellChecking, false);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrent()) return;
                SetAndRaise(SpellCheckErrorProperty, ref _spellCheckError, error);
                SetAndRaise(IsSpellCheckingProperty, ref _isSpellChecking, false);
            });
        }

        bool IsCurrent() => generation == _spellingGeneration && !token.IsCancellationRequested &&
            ReferenceEquals(checker, SpellChecker) && Session.Revision == revision && Session.ActiveStoryId == storyId;
    }

    private void PublishSpellingDiagnostics()
    {
        var index = Session.Index;
        var diagnostics = new List<SpellingDiagnostic>();
        foreach (var (paragraphId, local) in _spellingByParagraph)
        {
            if (index.Tree.Paths?.Find(paragraphId) is null) continue;
            var entry = index.ById(paragraphId);
            foreach (var item in local)
                if (item.Start >= 0 && item.Length > 0 && item.Start <= entry.Paragraph.Length - item.Length &&
                    !_ignoredSpellingWords.Contains(SpellingScanner.IgnoreKey(item.Word, item.Language)))
                    diagnostics.Add(new(entry.Start + item.Start, item.Length, item.Word, item.Language,
                        item.Suggestions, item.DictionaryId) { SourceText = item.SourceText });
        }
        diagnostics.Sort((a, b) => a.Start.CompareTo(b.Start));
        SetAndRaise(SpellingDiagnosticsProperty, ref _spellingDiagnostics, diagnostics.ToImmutableArray());
        _surface?.InvalidateVisual();
        _surface?.RefreshProofingContextMenu();
    }

    /// <summary>Find a diagnostic at a current-story UTF-16 offset.</summary>
    public SpellingDiagnostic? SpellingDiagnosticAt(int position) =>
        SpellingDiagnostics.FirstOrDefault(item => position >= item.Start && position < item.End) ??
        SpellingDiagnostics.FirstOrDefault(item => position == item.End);

    /// <summary>Select the next misspelling, wrapping once at the end of the story.</summary>
    public bool SelectNextSpellingError()
    {
        var item = SpellingDiagnostics.FirstOrDefault(d => d.Start >= Session.Selection.End) ??
            SpellingDiagnostics.FirstOrDefault();
        if (item is null) return false;
        Session.Select(item.Start, item.End);
        FocusDocument();
        return true;
    }

    /// <summary>Replace one current misspelling through the normal edit and undo policy.</summary>
    public bool ReplaceSpelling(SpellingDiagnostic diagnostic, string replacement)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!IsCurrentDiagnostic(diagnostic) || Session.IsReadOnly) return false;
        Session.Select(diagnostic.Start, diagnostic.End);
        var revision = Session.Revision;
        Session.InsertText(replacement);
        return Session.Revision != revision;
    }

    /// <summary>Ignore this word for the current editor session and language.</summary>
    public bool IgnoreSpelling(SpellingDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!IsCurrentDiagnostic(diagnostic)) return false;
        _ignoredSpellingWords.Add(SpellingScanner.IgnoreKey(diagnostic.Word, diagnostic.Language));
        QueueSpelling(full: true);
        return true;
    }

    /// <summary>Ask the configured host dictionary to retain a term, then refresh cached answers.</summary>
    public async Task<bool> AddSpellingToDictionaryAsync(SpellingDiagnostic diagnostic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!IsCurrentDiagnostic(diagnostic) || SpellChecker is not IWritableSpellChecker writable) return false;
        await writable.AddToDictionaryAsync(diagnostic.Word, diagnostic.Language, cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(RefreshSpelling);
        return true;
    }

    private bool IsCurrentDiagnostic(SpellingDiagnostic diagnostic) =>
        SpellingDiagnostics.Contains(diagnostic) && diagnostic.Start >= 0 && diagnostic.Length > 0 &&
        diagnostic.Start <= Session.Index.Length - diagnostic.Length &&
        Session.Index.ReadText(diagnostic.Start, diagnostic.Length) == diagnostic.SourceText;
}
