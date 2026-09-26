using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Textalonia.Model;

namespace Textalonia.Editing;

/// <summary>UI-independent editing, selection, formatting, search, and bounded undo/redo.</summary>
public sealed class EditorSession
{
    private sealed record State(FlowDocument Document, DocumentIndex Index, TextSelection Selection, TextStyle TypingStyle) : IRetained
    {
        public long Bytes => 80;
        public IEnumerable<object> References { get { yield return Index.Tree; yield return TypingStyle; } }
    }
    private readonly List<State> _undo = [];
    private readonly List<State> _redo = [];
    private readonly RetentionGraph _retained = new();
    private readonly RetentionGraph _current = new();
    private long _historyByteLimit = 64 * 1024 * 1024;
    private readonly Guid _positionScope = Guid.NewGuid();
    private int _undoLimit = 100;
    private long _lastTypingTick;
    private bool _typingGroup;
    private bool _isReadOnly;

    public FlowDocument Document { get; private set; } = new();
    public DocumentIndex Index { get; private set; }
    public TextSelection Selection { get; private set; }
    private TextStyle _typingStyle = TextStyle.Default;
    public TextStyle TypingStyle
    {
        get => _typingStyle;
        private set
        {
            if (ReferenceEquals(value, _typingStyle)) return;
            _retained.Add(value); _current.Add(value);
            _retained.Remove(_typingStyle); _current.Remove(_typingStyle); _typingStyle = value;
        }
    }
    public bool CanUndo => !IsReadOnly && _undo.Count > 0;
    public bool CanRedo => !IsReadOnly && _redo.Count > 0;
    public string SelectedText => Index.ReadText(Selection.Start, Selection.Length);
    public int Revision { get; private set; }
    public event EventHandler? Changed;
    internal DocumentEdit? LastEdit { get; private set; }
    internal Func<long> Timestamp { get; set; } = Stopwatch.GetTimestamp;
    /// <summary>Estimated bytes owned exclusively by undo/redo, including shared storage only once.</summary>
    public long RetainedHistoryBytes => _retained.Bytes - _current.Bytes;
    /// <summary>History estimate budget, in bytes. Oversized entries are evicted; current document is excluded.</summary>
    public long HistoryByteLimit
    {
        get => _historyByteLimit;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _historyByteLimit = value; TrimUndo(); BreakUndoGroup(); OnChanged();
        }
    }
    public DocumentPosition CreatePosition(int offset)
    {
        var position = Index.At(Snap(offset));
        return new(Revision, position.Paragraph.Id, Snap(offset) - position.Start) { Scope = _positionScope };
    }
    public bool TryResolvePosition(DocumentPosition position, out int offset)
    {
        offset = 0;
        if (position.Scope != _positionScope || position.Revision != Revision || Index.Tree.Paths?.Find(position.ParagraphId) is null) return false;
        var entry = Index.Tree.Locate(position.ParagraphId);
        if (entry.Node.Source is not Paragraph paragraph || position.Offset < 0 || position.Offset > paragraph.Length) return false;
        offset = entry.Start + position.Offset; return true;
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set { if (_isReadOnly == value) return; _isReadOnly = value; BreakUndoGroup(); OnChanged(); }
    }

    public int UndoLimit
    {
        get => _undoLimit;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _undoLimit = value;
            ClearRedo(); TrimUndo(); BreakUndoGroup(); OnChanged();
        }
    }

    public EditorSession()
    {
        Index = new DocumentIndex(Document); _retained.Add(Index.Tree); _current.Add(Index.Tree);
        _retained.Add(TypingStyle); _current.Add(TypingStyle);
    }
    public EditorSession(FlowDocument document) : this() => Load(document);

    public void Load(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        ClearHistory(); SetDocument(document, new(document));
        Selection = default; TypingStyle = Index.At(0).Paragraph.StyleAt(0);
        BreakUndoGroup(); Revision++; LastEdit = new(Revision - 1, Revision, 0, 0, Index.Length, [], true); OnChanged();
    }

    public void Select(int anchor, int active)
    {
        Selection = new(Snap(anchor), Snap(active));
        var paragraph = Index.At(Selection.Active);
        var local = Selection.Active - paragraph.Start;
        TypingStyle = paragraph.Paragraph.StyleAt(Math.Max(0, local - (local > 0 ? 1 : 0)));
        TrimUndo(); BreakUndoGroup(); OnChanged();
    }

    public void SelectAll() => Select(0, Index.Length);

    public void InsertText(string text, bool coalesceTyping = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsReadOnly) return;
        text = FlowDocument.NormalizeNewlines(text).Replace("\0", "");
        if (text.Length == 0 && Selection.IsEmpty) return;
        var style = Index.At(Selection.Start).Paragraph.Style;
        var fragment = text.Split('\n').Select(s => new Paragraph(s, TypingStyle) { Style = style }).ToImmutableArray();
        var (document, caret) = ReplaceRange(Document, Selection, fragment);
        Commit(document, new(caret, caret), coalesceTyping && Selection.IsEmpty && !text.Contains('\n'), Selection);
    }

    public void InsertDocument(FlowDocument fragment)
    {
        if (IsReadOnly) return;
        fragment.Validate();
        var paragraphs = new DocumentIndex(fragment).Paragraphs.Select(p => p.Paragraph with { Id = Guid.NewGuid() }).ToImmutableArray();
        var (document, caret) = ReplaceRange(Document, Selection, paragraphs);
        Commit(document, new(caret, caret), editedRange: Selection);
    }

    public FlowDocument CopySelection()
    {
        if (Selection.IsEmpty) return new();
        var result = new List<Block>();
        foreach (var entry in Index.Enumerate(Selection.Start, Selection.End))
        {
            if (entry.Start > Selection.End || entry.End < Selection.Start) continue;
            if (entry.Start == Selection.End && entry.Start != Selection.Start) break;
            var start = Math.Max(0, Selection.Start - entry.Start);
            var end = Math.Min(entry.Paragraph.Length, Selection.End - entry.Start);
            result.Add(entry.Paragraph with
            {
                Id = Guid.NewGuid(), Runs = entry.Paragraph.Slice(start, Math.Max(0, end - start))
            });
        }
        // Preserve a selected trailing paragraph separator.
        if (Selection.End > 0 && Index.CharAt(Selection.End - 1) == '\n') result.Add(new Paragraph());
        return new FlowDocument(result);
    }

    public void DeleteBackward(bool word = false)
    {
        if (IsReadOnly) return;
        if (!Selection.IsEmpty) { InsertText(""); return; }
        if (Selection.Active == 0) return;
        var previous = word ? PreviousWord(Selection.Active) : PreviousCaret(Selection.Active);
        DeleteRange(new(previous, Selection.Active));
    }

    public void DeleteForward(bool word = false)
    {
        if (IsReadOnly) return;
        if (!Selection.IsEmpty) { InsertText(""); return; }
        if (Selection.Active == Index.Length) return;
        var next = word ? NextWord(Selection.Active) : NextCaret(Selection.Active);
        DeleteRange(new(Selection.Active, next));
    }

    private void DeleteRange(TextSelection range)
    {
        var paragraph = Index.At(range.Start).Paragraph;
        var (document, caret) = ReplaceRange(Document, range, [new Paragraph("", TypingStyle) { Style = paragraph.Style }]);
        Commit(document, new(caret, caret), editedRange: range);
    }

    public void InsertParagraph()
    {
        if (IsReadOnly) return;
        var paragraph = Index.At(Selection.Active).Paragraph;
        if (Selection.IsEmpty && paragraph.Length == 0 && paragraph.Style.List != ListKind.None)
            ApplyParagraphStyle(s => s with { List = ListKind.None, ListLevel = 0 });
        else InsertText("\n");
    }

    public void ApplyStyle(Func<TextStyle, TextStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (IsReadOnly) return;
        var newStyle = change(TypingStyle);
        new FlowDocument([new Paragraph("", newStyle)]).Validate();
        if (Selection.IsEmpty)
        {
            TypingStyle = newStyle; TrimUndo(); BreakUndoGroup(); OnChanged(); return;
        }
        var selection = Selection;
        var document = ChangeParagraphs(position =>
        {
            var p = position.Paragraph;
            var start = Math.Max(0, selection.Start - position.Start);
            var end = Math.Min(p.Length, selection.End - position.Start);
            return end > start ? p.Format(start, end - start, change) : p;
        });
        Commit(document, selection, editedRange: selection);
        TypingStyle = newStyle;
        TrimUndo();
        OnChanged();
    }

    public void ToggleBold() => ApplyStyle(s => s with { Bold = !TypingStyle.Bold });
    public void ToggleItalic() => ApplyStyle(s => s with { Italic = !TypingStyle.Italic });
    public void ToggleUnderline() => ApplyStyle(s => s with { Underline = !TypingStyle.Underline });
    public void ToggleStrikethrough() => ApplyStyle(s => s with { Strikethrough = !TypingStyle.Strikethrough });
    public void ClearFormatting() => ApplyStyle(_ => TextStyle.Default);

    public void ApplyParagraphStyle(Func<ParagraphStyle, ParagraphStyle> change)
    {
        if (IsReadOnly) return;
        var document = ChangeParagraphs(p => p.Paragraph with { Style = change(p.Paragraph.Style) });
        Commit(document, Selection, editedRange: Selection);
    }

    public void SetHeading(int level)
    {
        if (level is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(level));
        if (IsReadOnly) return;
        var size = level switch { 1 => 32, 2 => 26, 3 => 22, 4 => 20, 5 => 18, _ => 16 };
        TextStyle Change(TextStyle style) => style with { FontSize = size, Bold = level > 0 };
        var document = ChangeParagraphs(entry =>
        {
            var p = entry.Paragraph;
            return p.Format(0, p.Length, Change) with
            { Style = p.Style with { HeadingLevel = level, SpaceBefore = level > 0 ? 12 : 0 }, DefaultStyle = Change(p.DefaultStyle) };
        });
        Commit(document, Selection, editedRange: Selection);
        TypingStyle = Change(TypingStyle); TrimUndo(); OnChanged();
    }

    public void ToggleList(ListKind kind)
    {
        var active = Index.At(Selection.Active).Paragraph.Style.List;
        ApplyParagraphStyle(s => s with { List = active == kind ? ListKind.None : kind });
    }

    private FlowDocument ChangeParagraphs(Func<ParagraphPosition, Paragraph> change)
    {
        var replacements = new Dictionary<Guid, ImmutableArray<Paragraph>>();
        foreach (var entry in Index.Enumerate(Selection.Start, Selection.End))
        {
            if (!Selection.IsEmpty && entry.Start >= Selection.End) break;
            var paragraph = change(entry);
            if (ReferenceEquals(paragraph, entry.Paragraph)) continue;
            new FlowDocument([paragraph]).Validate();
            replacements.Add(paragraph.Id, [paragraph]);
        }
        return Index.Tree.Rewrite(Document, replacements);
    }

    public IEnumerable<TextSelection> FindAll(string query, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var found in Index.Find(query, comparison)) yield return new(found, found + query.Length);
    }

    public bool FindNext(string query, bool matchCase = false)
    {
        var matches = FindAll(query, matchCase).ToArray();
        if (matches.Length == 0) return false;
        var match = matches.FirstOrDefault(m => m.Start >= Selection.End);
        if (match.Length == 0) match = matches[0];
        Select(match.Anchor, match.Active);
        return true;
    }

    public int ReplaceAll(string query, string replacement, bool matchCase = false)
    {
        if (IsReadOnly) return 0;
        var matches = FindAll(query, matchCase).ToArray();
        if (matches.Length == 0) return 0;
        var document = Document;
        foreach (var match in matches.Reverse())
        {
            var current = new DocumentIndex(document).At(match.Start).Paragraph;
            var fragment = FlowDocument.FromText(replacement, current.StyleAt(match.Start - new DocumentIndex(document).At(match.Start).Start))
                .Blocks.Cast<Paragraph>().Select(p => p with { Style = current.Style }).ToImmutableArray();
            (document, _) = ReplaceRange(document, match, fragment);
        }
        Commit(document, new(0, 0));
        return matches.Length;
    }

    /// <summary>Records an application-defined immutable document operation in the normal undo history.</summary>
    public void Execute(Func<FlowDocument, FlowDocument> operation)
    {
        if (IsReadOnly) return;
        var document = operation(Document);
        document.Validate();
        Commit(document, Selection);
    }

    public void InsertTable(int rows = 2, int columns = 3)
    {
        if (IsReadOnly) return;
        var table = Table.Create(rows, columns);
        var blockId = Index.At(Selection.Active).TopLevelBlockId;
        var blockIndex = Document.Blocks.FindIndex(b => b.Id == blockId);
        var blocks = Document.Blocks.Insert(blockIndex + 1, table);
        if (blockIndex + 2 == blocks.Length) blocks = blocks.Add(new Paragraph());
        var document = Document with { Blocks = blocks };
        var caret = new DocumentIndex(document).Paragraphs.First(p => p.ContainerId == table.Rows[0][0].Id).Start;
        Commit(document, new(caret, caret));
    }

    public (Table Table, int Row, int Column)? CurrentCell()
    {
        var container = Index.At(Selection.Active).ContainerId;
        var path = Index.Tree.Paths?.Find(container)?.Value;
        if (path is null) return null;
        var node = Index.Tree.Root;
        Table? table = null;
        foreach (var key in path.Keys())
        {
            node = node.Children!.Find(key)!.Value;
            if (node.Source is Table t) table = t;
        }
        if (table is not null && node.Source is TableCell) return (table, node.Row, node.Column);
        return null;
    }

    public void UpdateCurrentTable(Func<Table, int, int, Table> update)
    {
        if (CurrentCell() is not { } cell) return;
        Execute(d => d.ReplaceBlock(cell.Table.Id, update(cell.Table, cell.Row, cell.Column)));
    }

    public void DeleteCurrentTable()
    {
        if (IsReadOnly || CurrentCell() is not { } current) return;
        ImmutableArray<Block> Remove(ImmutableArray<Block> blocks) => FlowDocument.EnsureBlocks(blocks
            .Where(b => b.Id != current.Table.Id)
            .Select(b => b is Section s ? s with { Blocks = Remove(s.Blocks) } : b).ToImmutableArray());
        Commit(Document with { Blocks = Remove(Document.Blocks) }, Selection);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var current = Capture(); _redo.Add(current); _retained.Add(current);
        var state = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        Restore(state); _retained.Remove(state); TrimUndo(); OnChanged();
    }
    public void Redo()
    {
        if (!CanRedo) return;
        var current = Capture(); _undo.Add(current); _retained.Add(current);
        var state = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        Restore(state); _retained.Remove(state); TrimUndo(); OnChanged();
    }

    public int PreviousCaret(int position) => Index.PreviousCaret(position);
    public int NextCaret(int position) => Index.NextCaret(position);
    public int PreviousWord(int position)
    {
        var i = Math.Clamp(position, 0, Index.Length);
        while (i > 0 && char.IsWhiteSpace(Index.CharAt(i - 1))) i = PreviousCaret(i);
        while (i > 0 && !char.IsWhiteSpace(Index.CharAt(i - 1))) i = PreviousCaret(i);
        return i;
    }
    public int NextWord(int position)
    {
        var i = Math.Clamp(position, 0, Index.Length);
        while (i < Index.Length && !char.IsWhiteSpace(Index.CharAt(i))) i = NextCaret(i);
        while (i < Index.Length && char.IsWhiteSpace(Index.CharAt(i))) i = NextCaret(i);
        return i;
    }

    public void BreakUndoGroup() => _typingGroup = false;
    private int Snap(int position) => Index.Snap(position);
    private State Capture() => new(Document, Index, Selection, TypingStyle);
    private void Restore(State state)
    {
        SetDocument(state.Document, state.Index); Selection = state.Selection;
        TypingStyle = state.TypingStyle; BreakUndoGroup(); Revision++;
        LastEdit = new(Revision - 1, Revision, 0, 0, Index.Length, [], true);
    }
    private void Commit(FlowDocument document, TextSelection selection, bool typing = false, TextSelection? editedRange = null)
    {
        var oldLength = Index.Length;
        var changed = editedRange is { } range ? Index.Enumerate(range.Start, range.End).Select(p => p.Paragraph.Id).ToArray() : [];
        var now = Timestamp();
        if (!(typing && _typingGroup && Stopwatch.GetElapsedTime(_lastTypingTick, now).TotalMilliseconds < 800))
        {
            if (UndoLimit > 0) { var state = Capture(); _undo.Add(state); _retained.Add(state); }
        }
        ClearRedo(); SetDocument(document, new(document)); TrimUndo();
        Selection = new(Math.Clamp(selection.Anchor, 0, Index.Length), Math.Clamp(selection.Active, 0, Index.Length));
        _typingGroup = typing && _undo.Count > 0; _lastTypingTick = now; Revision++;
        LastEdit = new(Revision - 1, Revision, editedRange?.Start ?? 0, editedRange?.Length ?? oldLength,
            editedRange is { } edit ? Index.Length - oldLength + edit.Length : Index.Length, changed, editedRange is null);
        OnChanged();
    }
    private void TrimUndo()
    {
        while (_undo.Count + _redo.Count > 0 &&
            (_undo.Count + _redo.Count > UndoLimit || RetainedHistoryBytes > HistoryByteLimit))
        {
            // Farthest undo first; when only redo remains, discard its farthest future.
            var list = _undo.Count > 0 ? _undo : _redo;
            _retained.Remove(list[0]); list.RemoveAt(0);
        }
    }
    private void SetDocument(FlowDocument document, DocumentIndex index)
    {
        _retained.Add(index.Tree); _current.Add(index.Tree);
        _retained.Remove(Index.Tree); _current.Remove(Index.Tree);
        Document = document; Index = index;
    }
    private void ClearRedo()
    { foreach (var state in _redo) _retained.Remove(state); _redo.Clear(); }
    private void ClearHistory()
    { ClearRedo(); foreach (var state in _undo) _retained.Remove(state); _undo.Clear(); }
    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static (FlowDocument Document, int Caret) ReplaceRange(
        FlowDocument document, TextSelection selection, ImmutableArray<Paragraph> fragment)
    {
        var index = new DocumentIndex(document);
        if (selection.Start == 0 && selection.End == index.Length && !selection.IsEmpty)
        {
            var replacement = new FlowDocument(fragment.Select(p => p with { Id = Guid.NewGuid() }));
            return (replacement, new DocumentIndex(replacement).Length);
        }
        var first = index.At(selection.Start);
        var last = index.At(selection.End);
        var prefix = first.Paragraph.Slice(0, selection.Start - first.Start);
        var suffix = last.Paragraph.Slice(selection.End - last.Start, last.End - selection.End);
        var sameContainer = first.ContainerId == last.ContainerId;
        var replacements = fragment.Select(p => p with { Id = Guid.NewGuid() }).ToArray();
        replacements[0] = replacements[0] with
        {
            Id = first.Paragraph.Id,
            Style = prefix.IsEmpty && first.Paragraph.Length == 0 ? replacements[0].Style : first.Paragraph.Style,
            Runs = Paragraph.Normalize(prefix.Concat(replacements[0].Runs))
        };
        var caretLocal = replacements[^1].Length;
        if (sameContainer)
            replacements[^1] = replacements[^1] with { Runs = Paragraph.Normalize(replacements[^1].Runs.Concat(suffix)) };
        var changes = new Dictionary<Guid, ImmutableArray<Paragraph>>();
        foreach (var entry in index.Enumerate(first.Start, last.Start))
        {
            var p = entry.Paragraph;
            changes[p.Id] = p.Id == first.Paragraph.Id ? replacements.ToImmutableArray() :
                !sameContainer && p.Id == last.Paragraph.Id ? [p with { Runs = suffix }] : [];
        }
        var changed = index.Tree.Rewrite(document, changes);
        var newEntry = new DocumentIndex(changed).ById(replacements[^1].Id);
        return (changed, newEntry.Start + caretLocal);
    }
}

internal static class ImmutableArrayExtensions
{
    public static int FindIndex<T>(this ImmutableArray<T> array, Func<T, bool> predicate)
    {
        for (var i = 0; i < array.Length; i++) if (predicate(array[i])) return i;
        return -1;
    }
}
