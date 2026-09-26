using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Textalonia.Model;

namespace Textalonia.Editing;

/// <summary>UI-independent editing, selection, formatting, search, and bounded undo/redo.</summary>
public sealed class EditorSession
{
    private sealed record State(FlowDocument Document, TextSelection Selection, TextStyle TypingStyle);
    private readonly List<State> _undo = [];
    private readonly Stack<State> _redo = [];
    private int _undoLimit = 100;
    private long _lastTypingTick;
    private bool _typingGroup;
    private bool _isReadOnly;

    public FlowDocument Document { get; private set; } = new();
    public DocumentIndex Index { get; private set; }
    public TextSelection Selection { get; private set; }
    public TextStyle TypingStyle { get; private set; } = TextStyle.Default;
    public bool CanUndo => !IsReadOnly && _undo.Count > 0;
    public bool CanRedo => !IsReadOnly && _redo.Count > 0;
    public string SelectedText => Index.Text.Substring(Selection.Start, Selection.Length);
    public int Revision { get; private set; }
    public event EventHandler? Changed;

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
            TrimUndo(); _redo.Clear(); BreakUndoGroup(); OnChanged();
        }
    }

    public EditorSession() => Index = new DocumentIndex(Document);
    public EditorSession(FlowDocument document) : this() => Load(document);

    public void Load(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        Document = document; Index = new(document);
        Selection = default; TypingStyle = Index.At(0).Paragraph.StyleAt(0);
        _undo.Clear(); _redo.Clear(); BreakUndoGroup(); Revision++; OnChanged();
    }

    public void Select(int anchor, int active)
    {
        Selection = new(Snap(anchor), Snap(active));
        var paragraph = Index.At(Selection.Active);
        var local = Selection.Active - paragraph.Start;
        TypingStyle = paragraph.Paragraph.StyleAt(Math.Max(0, local - (local > 0 ? 1 : 0)));
        BreakUndoGroup(); OnChanged();
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
        Commit(document, new(caret, caret), coalesceTyping && Selection.IsEmpty && !text.Contains('\n'));
    }

    public void InsertDocument(FlowDocument fragment)
    {
        if (IsReadOnly) return;
        fragment.Validate();
        var paragraphs = new DocumentIndex(fragment).Paragraphs.Select(p => p.Paragraph with { Id = Guid.NewGuid() }).ToImmutableArray();
        var (document, caret) = ReplaceRange(Document, Selection, paragraphs);
        Commit(document, new(caret, caret));
    }

    public FlowDocument CopySelection()
    {
        if (Selection.IsEmpty) return new();
        var result = new List<Block>();
        foreach (var entry in Index.Paragraphs)
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
        if (Selection.End > 0 && Index.Text[Selection.End - 1] == '\n') result.Add(new Paragraph());
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
        Commit(document, new(caret, caret));
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
            TypingStyle = newStyle; BreakUndoGroup(); OnChanged(); return;
        }
        var selection = Selection;
        var positions = Index.Paragraphs.ToDictionary(x => x.Paragraph.Id);
        var document = Document.RewriteParagraphs(p =>
        {
            if (!positions.TryGetValue(p.Id, out var position)) return [p];
            var start = Math.Max(0, selection.Start - position.Start);
            var end = Math.Min(p.Length, selection.End - position.Start);
            return end > start ? [p.Format(start, end - start, change)] : [p];
        });
        document.Validate();
        Commit(document, selection);
        TypingStyle = newStyle;
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
        var selected = Index.Paragraphs.Where(p => p.End >= Selection.Start &&
            (Selection.IsEmpty ? p.Start <= Selection.End : p.Start < Selection.End)).Select(p => p.Paragraph.Id).ToHashSet();
        var document = Document.RewriteParagraphs(p => selected.Contains(p.Id) ? [p with { Style = change(p.Style) }] : [p]);
        document.Validate();
        Commit(document, Selection);
    }

    public void SetHeading(int level)
    {
        if (level is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(level));
        if (IsReadOnly) return;
        var selected = Index.Paragraphs.Where(p => p.End >= Selection.Start &&
            (Selection.IsEmpty ? p.Start <= Selection.End : p.Start < Selection.End)).Select(p => p.Paragraph.Id).ToHashSet();
        var size = level switch { 1 => 32, 2 => 26, 3 => 22, 4 => 20, 5 => 18, _ => 16 };
        TextStyle Change(TextStyle style) => style with { FontSize = size, Bold = level > 0 };
        var document = Document.RewriteParagraphs(p => selected.Contains(p.Id)
            ? [p.Format(0, p.Length, Change) with
            { Style = p.Style with { HeadingLevel = level, SpaceBefore = level > 0 ? 12 : 0 }, DefaultStyle = Change(p.DefaultStyle) }]
            : [p]);
        Commit(document, Selection);
        TypingStyle = Change(TypingStyle); OnChanged();
    }

    public void ToggleList(ListKind kind)
    {
        var active = Index.At(Selection.Active).Paragraph.Style.List;
        ApplyParagraphStyle(s => s with { List = active == kind ? ListKind.None : kind });
    }

    public IEnumerable<TextSelection> FindAll(string query, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var start = 0;
        while (start <= Index.Length - query.Length)
        {
            var found = Index.Text.IndexOf(query, start, comparison);
            if (found < 0) yield break;
            yield return new(found, found + query.Length);
            start = found + query.Length;
        }
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
        IEnumerable<Table> Tables(IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
                if (b is Table t) yield return t;
                else if (b is Section s)
                    foreach (var nested in Tables(s.Blocks)) yield return nested;
        }
        foreach (var table in Tables(Document.Blocks))
            for (var r = 0; r < table.Rows.Length; r++)
                for (var c = 0; c < table.ColumnCount; c++)
                    if (table.Rows[r][c].Id == container) return (table, r, c);
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
        _redo.Push(Capture());
        var state = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); Restore(state);
    }
    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Add(Capture()); TrimUndo(); Restore(_redo.Pop());
    }

    public int PreviousCaret(int position)
    {
        var elements = StringInfo.ParseCombiningCharacters(Index.Text);
        var i = Array.BinarySearch(elements, Math.Clamp(position, 0, Index.Length));
        i = i >= 0 ? i - 1 : ~i - 1;
        return i < 0 ? 0 : elements[i];
    }
    public int NextCaret(int position)
    {
        var elements = StringInfo.ParseCombiningCharacters(Index.Text);
        var i = Array.BinarySearch(elements, Math.Clamp(position, 0, Index.Length));
        i = i >= 0 ? i + 1 : ~i;
        return i >= elements.Length ? Index.Length : elements[i];
    }
    public int PreviousWord(int position)
    {
        var i = Math.Clamp(position, 0, Index.Length);
        while (i > 0 && char.IsWhiteSpace(Index.Text[i - 1])) i = PreviousCaret(i);
        while (i > 0 && !char.IsWhiteSpace(Index.Text[i - 1])) i = PreviousCaret(i);
        return i;
    }
    public int NextWord(int position)
    {
        var i = Math.Clamp(position, 0, Index.Length);
        while (i < Index.Length && !char.IsWhiteSpace(Index.Text[i])) i = NextCaret(i);
        while (i < Index.Length && char.IsWhiteSpace(Index.Text[i])) i = NextCaret(i);
        return i;
    }

    public void BreakUndoGroup() => _typingGroup = false;
    private int Snap(int position)
    {
        position = Math.Clamp(position, 0, Index.Length);
        if (position == Index.Length || position == 0) return position;
        var elements = StringInfo.ParseCombiningCharacters(Index.Text);
        var i = Array.BinarySearch(elements, position);
        return i >= 0 ? position : elements[Math.Max(0, ~i - 1)];
    }
    private State Capture() => new(Document, Selection, TypingStyle);
    private void Restore(State state)
    {
        Document = state.Document; Index = new(Document); Selection = state.Selection;
        TypingStyle = state.TypingStyle; BreakUndoGroup(); Revision++; OnChanged();
    }
    private void Commit(FlowDocument document, TextSelection selection, bool typing = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!(typing && _typingGroup && Stopwatch.GetElapsedTime(_lastTypingTick, now).TotalMilliseconds < 800))
        {
            if (UndoLimit > 0) _undo.Add(Capture());
            TrimUndo();
        }
        _redo.Clear();
        Document = document; Index = new(Document);
        Selection = new(Math.Clamp(selection.Anchor, 0, Index.Length), Math.Clamp(selection.Active, 0, Index.Length));
        _typingGroup = typing; _lastTypingTick = now; Revision++; OnChanged();
    }
    private void TrimUndo()
    {
        if (_undo.Count > UndoLimit) _undo.RemoveRange(0, _undo.Count - UndoLimit);
    }
    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static (FlowDocument Document, int Caret) ReplaceRange(
        FlowDocument document, TextSelection selection, ImmutableArray<Paragraph> fragment)
    {
        var index = new DocumentIndex(document);
        if (selection.Start == 0 && selection.End == index.Length && !selection.IsEmpty)
        {
            var replacement = new FlowDocument(fragment.Select(p => p with { Id = Guid.NewGuid() }));
            return (replacement, replacement.Text.Length);
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
        var selectedIds = index.Paragraphs.Where(p => p.Start >= first.Start && p.Start <= last.Start)
            .Select(p => p.Paragraph.Id).ToHashSet();
        var changed = document.RewriteParagraphs(p =>
        {
            if (p.Id == first.Paragraph.Id) return replacements;
            if (!selectedIds.Contains(p.Id)) return [p];
            if (!sameContainer && p.Id == last.Paragraph.Id) return [p with { Runs = suffix }];
            return [];
        });
        var newEntry = new DocumentIndex(changed).Paragraphs.First(p => p.Paragraph.Id == replacements[^1].Id);
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
