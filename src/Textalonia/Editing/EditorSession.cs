using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Textalonia.Model;

namespace Textalonia.Editing;

/// <summary>UI-independent editing, selection, formatting, search, and bounded undo/redo.</summary>
public sealed partial class EditorSession
{
    private sealed record State(FlowDocument Document, DocumentIndex Index, TextSelection Selection, TextStyle TypingStyle) : IRetained
    {
        public long Bytes => 80;
        public void VisitReferences(Action<object> visit) { visit(Index.Tree); visit(Document.Resources); visit(Document.Styles); visit(Document.Defaults); visit(Document.Theme);
            if (!Document.Fonts.IsEmpty) visit(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(Document.Fonts)!);
            visit(TypingStyle); }
    }
    private readonly List<State> _undo = [];
    private readonly List<State> _redo = [];
    private readonly RetentionGraph _retained = new();
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
            _retained.Add(value, current: true);
            _retained.Remove(_typingStyle, current: true); _typingStyle = value;
        }
    }
    public bool CanUndo => !IsReadOnly && _undo.Count > 0;
    public bool CanRedo => !IsReadOnly && _redo.Count > 0;
    public string SelectedText => Index.ReadPlainText(Selection.Start, Selection.Length);
    public SelectionFormattingState FormattingState => new(Index, Selection, TypingStyle, new(Document));
    public int Revision { get; private set; }
    public event EventHandler? Changed;
    internal DocumentEdit? LastEdit { get; private set; }
    internal Func<long> Timestamp { get; set; } = Stopwatch.GetTimestamp;
    /// <summary>Estimated bytes owned exclusively by undo/redo, including shared storage only once.</summary>
    public long RetainedHistoryBytes => _retained.HistoryBytes;
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
        Index = new DocumentIndex(Document); _retained.Add(Index.Tree, current: true);
        _retained.Add(Document.Resources, current: true);
        RetainFormatting(Document, true);
        _retained.Add(TypingStyle, current: true);
    }
    public EditorSession(FlowDocument document) : this() => Load(document);

    public void Load(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        // Loading releases every previous root. Clearing the ownership table
        // avoids walking a graph solely to decrement all its counts to zero.
        var index = new DocumentIndex(document);
        _undo.Clear(); _redo.Clear(); _retained.Clear();
        _retained.EnsureCapacity(index.ParagraphCount * 6 + index.Tree.UpdatedNodes * 2);
        Document = document; Index = index;
        Selection = default; _typingStyle = Index.At(0).Paragraph.StyleAt(0);
        _retained.Add(Index.Tree, current: true); _retained.Add(Document.Resources, current: true);
        RetainFormatting(Document, true); _retained.Add(TypingStyle, current: true);
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
        var fragment = text.Split('\n').Select((s, i) =>
        {
            if (i > 0) style = FollowingParagraphStyle(style);
            return new Paragraph(s, TypingStyle) { Style = style };
        }).ToImmutableArray();
        var (document, caret) = ReplaceRange(Document, Selection, fragment);
        Commit(document, new(caret, caret), coalesceTyping && Selection.IsEmpty && !text.Contains('\n'), Selection);
    }

    /// <summary>Inserts an atomic inline descriptor and optionally owns its encoded image resource.</summary>
    public void InsertInline(InlineDescriptor descriptor, DocumentResource? resource = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (IsReadOnly) return;
        descriptor.Validate();
        var source = Document;
        if (resource is not null)
        {
            resource.Validate();
            if (descriptor.Payload is not ImageInlinePayload image)
                throw new ArgumentException("Only image descriptors reference encoded resources.", nameof(resource));
            if (source.Resources.TryGetValue(image.ResourceId, out var existing) && existing != resource)
                throw new ArgumentException("The resource identifier already belongs to different data.", nameof(resource));
            source = source with { Resources = source.Resources.SetItem(image.ResourceId, resource) };
        }
        var paragraph = new Paragraph([new RichRun(descriptor, TypingStyle)]) { Style = Index.At(Selection.Start).Paragraph.Style };
        var (document, caret) = ReplaceRange(source, Selection, [paragraph]);
        document = document.PruneUnusedResources();
        document.Validate();
        Commit(document, new(caret, caret), editedRange: Selection);
    }

    /// <summary>Changes inline data, including dimensions, as a single undoable edit, preserving its identity.</summary>
    public void UpdateInline(Guid id, Func<InlineDescriptor, InlineDescriptor> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (IsReadOnly) return;
        foreach (var entry in Index.Enumerate(0, Index.Length))
        {
            var offset = entry.Start;
            for (var i = 0; i < entry.Paragraph.Runs.Length; i++)
            {
                var run = entry.Paragraph.Runs[i];
                if (run.Inline is { } inline && inline.Id == id)
                {
                    var replacement = update(inline) ?? throw new ArgumentException("Inline updates cannot return null.", nameof(update));
                    replacement = replacement with { Id = id };
                    replacement.Validate();
                    var paragraph = entry.Paragraph with { Runs = entry.Paragraph.Runs.SetItem(i, run with { Inline = replacement }) };
                    var document = Index.Tree.Rewrite(Document, new Dictionary<Guid, ImmutableArray<Paragraph>> { [paragraph.Id] = [paragraph] });
                    Commit(document, Selection, editedRange: new(offset, offset + 1));
                    return;
                }
                offset += run.Storage.Length;
            }
        }
        throw new ArgumentException("The inline descriptor does not exist in the visible document.", nameof(id));
    }

    /// <summary>Inserts a document as one undoable structured fragment in the active block container.</summary>
    public void InsertDocument(FlowDocument fragment) => InsertFragment(new DocumentFragment { Document = fragment, StartsInsideParagraph = true, EndsInsideParagraph = true });

    /// <summary>Preserves containers and resources and gives every pasted element and list fresh identities.</summary>
    public void InsertFragment(DocumentFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (IsReadOnly) return;
        var (document, caret) = BuildFragmentInsertion(Document, Selection, fragment);
        Commit(document, new(caret, caret), editedRange: Selection);
    }

    private static (FlowDocument Document, int Caret) BuildFragmentInsertion(FlowDocument destination, TextSelection selection, DocumentFragment fragment)
    {
        fragment.Validate();
        var prepared = DocumentFragments.Prepare(fragment.Document, destination);
        var index = new DocumentIndex(destination);
        FlowDocument document; int caret;
        if (selection.Start == 0 && selection.End == index.Length &&
            (!selection.IsEmpty || destination.Blocks is [Paragraph { Length: 0 }]))
        {
            document = prepared.PruneUnusedResources();
            caret = new DocumentIndex(document).Length;
        }
        else
        {
            var offset = selection.Start;
            if (!selection.IsEmpty)
                (destination, offset) = ReplaceRange(destination, selection, [new Paragraph()]);
            (document, caret) = DocumentFragments.Insert(destination, offset, prepared, fragment.StartsInsideParagraph, fragment.EndsInsideParagraph);
            document = document.PruneUnusedResources();
        }
        document.Validate();
        return (document, caret);
    }

    /// <summary>Copies the selected text, retaining enclosing sections and intersected table geometry.</summary>
    public FlowDocument CopySelection() => CopyFragment().Document;

    /// <summary>Clips paragraph boundaries and retains container formatting. Partial merge backups are discarded.</summary>
    public DocumentFragment CopyFragment() => DocumentFragments.Extract(Document, Index, Selection);

    /// <summary>Copies a rectangular cell range, expanding its edges to include every intersected merged cell.</summary>
    public DocumentFragment CopyCells(Guid tableId, int row, int column, int rowCount, int columnCount) =>
        DocumentFragments.ExtractCells(Document, tableId, row, column, rowCount, columnCount);
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
        if (Selection.IsEmpty && paragraph.Length == 0 &&
            new DocumentStyleResolver(Document).ResolveParagraphStyle(paragraph.Style).List != ListKind.None)
            ApplyParagraphStyle(ClearList);
        else InsertText("\n");
    }

    public void ApplyStyle(Func<TextStyle, TextStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (IsReadOnly) return;
        var resolver = new DocumentStyleResolver(Document);
        var activeParagraph = Index.At(Selection.Active).Paragraph;
        var newStyle = ChangeTextStyle(activeParagraph, TypingStyle, change, resolver);
        (Document with { Blocks = [new Paragraph("", newStyle)] }).Validate();
        if (Selection.IsEmpty)
        {
            Commit(Document, Selection, editedRange: Selection, typingStyle: newStyle); return;
        }
        var selection = Selection;
        var document = ChangeParagraphs(position =>
        {
            var p = position.Paragraph;
            var start = Math.Max(0, selection.Start - position.Start);
            var end = Math.Min(p.Length, selection.End - position.Start);
            return end > start ? p.Format(start, end - start, s => ChangeTextStyle(p, s, change, resolver)) : p with { DefaultStyle = ChangeTextStyle(p, p.DefaultStyle, change, resolver) };
        });
        Commit(document, selection, editedRange: selection, typingStyle: newStyle);
    }

    public void ToggleBold()
    {
        var value = FormattingState.Bold; var enabled = value.IsMixed || !value.Value;
        ApplyStyle(s => s with { Bold = enabled, FontWeight = null });
    }
    public void ToggleItalic()
    {
        var value = FormattingState.Italic; var enabled = value.IsMixed || !value.Value;
        ApplyStyle(s => s with { Italic = enabled });
    }
    public void ToggleUnderline()
    {
        var value = FormattingState.Underline; var enabled = value.IsMixed || !value.Value;
        ApplyStyle(s => s with { Underline = enabled, UnderlineKind = UnderlineKind.None });
    }
    public void ToggleStrikethrough()
    {
        var value = FormattingState.Strikethrough; var enabled = value.IsMixed || !value.Value;
        ApplyStyle(s => s with { Strikethrough = enabled, StrikeKind = StrikeKind.None });
    }
    public void ClearFormatting() => ApplyStyle(_ => TextStyle.Default);

    public void ApplyParagraphStyle(Func<ParagraphStyle, ParagraphStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (IsReadOnly) return;
        var resolver = new DocumentStyleResolver(Document);
        var document = ChangeParagraphs(p => p.Paragraph with { Style = ChangeParagraphStyle(p.Paragraph.Style, change, resolver) });
        Commit(document, Selection, editedRange: Selection);
    }

    public void SetHeading(int level)
    {
        if (level is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(level));
        if (IsReadOnly) return;
        var size = level switch { 1 => 32, 2 => 26, 3 => 22, 4 => 20, 5 => 18, _ => 16 };
        var resolver = new DocumentStyleResolver(Document);
        TextStyle Change(TextStyle style) => style with { FontSize = size, Bold = level > 0, FontWeight = null };
        var document = ChangeParagraphs(entry =>
        {
            var p = entry.Paragraph;
            return p.Format(0, p.Length, s => ChangeTextStyle(p, s, Change, resolver)) with
            { Style = ChangeParagraphStyle(p.Style, s => s with { HeadingLevel = level, SpaceBefore = level > 0 ? 12 : 0 }, resolver),
                DefaultStyle = ChangeTextStyle(p, p.DefaultStyle, Change, resolver) };
        });
        Commit(document, Selection, editedRange: Selection, typingStyle: ChangeTextStyle(Index.At(Selection.Active).Paragraph, TypingStyle, Change, resolver));
    }

    public void ToggleList(ListKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var active = FormattingState.List;
        if (kind == ListKind.None || !active.IsMixed && active.Value == kind) { ApplyParagraphStyle(ClearList); return; }
        var identity = Guid.NewGuid();
        ApplyParagraphStyle(s => s with { List = kind, ListId = identity, ListDefinition = null, ListRestart = false, ListStart = null });
    }

    /// <summary>Assigns one identity and level definition to all selected paragraphs.</summary>
    public void SetList(ListKind kind, ListDefinition? definition = null, Guid? listId = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == ListKind.None) { ApplyParagraphStyle(ClearList); return; }
        var identity = listId ?? Guid.NewGuid();
        ApplyParagraphStyle(s => s with { List = kind, ListId = identity, ListDefinition = definition, ListRestart = false, ListStart = null });
    }

    /// <summary>Restarts the first selected item, leaving following items in the same list.</summary>
    public void RestartList(int start = 1)
    {
        if (start is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(start));
        var first = true;
        ApplyParagraphStyle(s =>
        {
            if (s.List == ListKind.None || !first) return s;
            first = false;
            return s with { ListRestart = true, ListStart = start };
        });
    }

    /// <summary>Continues the specified existing list across any intervening content.</summary>
    public void ContinueList(Guid listId)
    {
        if (IsReadOnly) return;
        var resolver = new DocumentStyleResolver(Document);
        var source = Index.Enumerate(0, Index.Length).Select(p => resolver.ResolveParagraphStyle(p.Paragraph.Style))
            .FirstOrDefault(style => style.ListId == listId && style.List != ListKind.None)
            ?? throw new ArgumentException("The list identity does not exist in this document.", nameof(listId));
        SetList(source.List, source.ListDefinition, listId);
    }

    public void IndentList(int levels = 1) => ApplyParagraphStyle(s => s.List == ListKind.None ? s :
        s with { ListLevel = (int)Math.Clamp((long)s.ListLevel + levels, 0, 8), ListRestart = false, ListStart = null });

    private static ParagraphStyle ClearList(ParagraphStyle style) => style with
    { List = ListKind.None, ListLevel = 0, ListId = null, ListDefinition = null, ListRestart = false, ListStart = null };

    private FlowDocument ChangeParagraphs(Func<ParagraphPosition, Paragraph> change)
    {
        var replacements = new Dictionary<Guid, ImmutableArray<Paragraph>>();
        foreach (var entry in Index.Enumerate(Selection.Start, Selection.End))
        {
            if (!Selection.IsEmpty && entry.Start >= Selection.End) break;
            var paragraph = change(entry);
            if (ReferenceEquals(paragraph, entry.Paragraph)) continue;
            (Document with { Blocks = [paragraph] }).Validate();
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

    /// <summary>Inserts a table after the active paragraph in its innermost block container.</summary>
    public void InsertTable(int rows = 2, int columns = 3)
    {
        if (IsReadOnly) return;
        var table = Table.Create(rows, columns);
        var paragraphId = Index.At(Selection.Active).Paragraph.Id;
        ImmutableArray<Block> Insert(ImmutableArray<Block> blocks)
        {
            var index = blocks.FindIndex(b => b.Id == paragraphId);
            if (index >= 0)
            {
                var inserted = blocks.Insert(index + 1, table);
                return index + 2 == inserted.Length ? inserted.Add(new Paragraph()) : inserted;
            }
            return blocks.Select(block => block switch
            {
                Section section => section with { Blocks = Insert(section.Blocks) },
                Table current => current with { Rows = current.Rows.Select((row, r) => row.Select((cell, c) =>
                    current.IsCovered(r, c) ? cell : cell with { Blocks = Insert(cell.Blocks) }).ToImmutableArray()).ToImmutableArray() },
                _ => block
            }).ToImmutableArray();
        }
        var document = Document with { Blocks = Insert(Document.Blocks) };
        document.Validate();
        var caret = new DocumentIndex(document).ById(((Paragraph)table.Rows[0][0].Blocks[0]).Id).Start;
        Commit(document, new(caret, caret));
    }

    /// <summary>Returns the innermost containing cell, including when a section lies inside it.</summary>
    public (Table Table, int Row, int Column)? CurrentCell()
    {
        var paragraphId = Index.At(Selection.Active).Paragraph.Id;
        var path = Index.Tree.Paths?.Find(paragraphId)?.Value;
        if (path is null) return null;
        var node = Index.Tree.Root;
        Table? table = null;
        (Table Table, int Row, int Column)? result = null;
        foreach (var key in path.Keys())
        {
            node = node.Children!.Find(key)!.Value;
            if (node.Source is Table current) table = current;
            else if (node.Source is TableCell && table is not null) result = (table, node.Row, node.Column);
        }
        return result;
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
            .Select(b => b switch
            {
                Section s => s with { Blocks = Remove(s.Blocks) },
                Table t => t with { Rows = t.Rows.Select((row, r) => row.Select((cell, c) =>
                    t.IsCovered(r, c) ? cell : cell with { Blocks = Remove(cell.Blocks) }).ToImmutableArray()).ToImmutableArray() },
                _ => b
            }).ToImmutableArray());
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
    private void Commit(FlowDocument document, TextSelection selection, bool typing = false, TextSelection? editedRange = null, TextStyle? typingStyle = null)
    {
        // Typing formatting can own a named reference even when no run uses it yet.
        // Check before changing history or document roots, including application edits.
        DocumentStyleCatalog.Reference(document.Styles.Characters, (typingStyle ?? TypingStyle).StyleId);
        var oldLength = Index.Length;
        var changed = editedRange is { } range ? Index.Enumerate(range.Start, range.End).Select(p => p.Paragraph.Id).ToArray() : [];
        var now = Timestamp();
        if (!(typing && _typingGroup && Stopwatch.GetElapsedTime(_lastTypingTick, now).TotalMilliseconds < 800))
        {
            if (UndoLimit > 0) { var state = Capture(); _undo.Add(state); _retained.Add(state); }
        }
        // Text-only local edits cannot orphan an encoded resource. Avoid materializing the
        // complete block tree while typing in a large document containing images elsewhere.
        if (!document.Resources.IsEmpty && (editedRange is null ||
            editedRange.Value is { Start: 0, Length: > 0 } full && full.End == oldLength || RangeContainsImage(editedRange.Value)))
            document = document.PruneUnusedResources();
        ClearRedo(); SetDocument(document, new(document));
        if (selection.IsEmpty)
        {
            var desired = Math.Clamp(selection.Active, 0, Index.Length);
            var caret = Snap(desired);
            // Joining text across a deleted object can form a new grapheme. Keep the caret
            // after that joined character instead of inside it or before its preceding base.
            if (caret < desired) caret = NextCaret(caret);
            Selection = new(caret, caret);
        }
        else Selection = new(Snap(selection.Anchor), Snap(selection.Active));
        if (typingStyle is not null) TypingStyle = typingStyle;
        TrimUndo();
        _typingGroup = typing && _undo.Count > 0; _lastTypingTick = now; Revision++;
        LastEdit = new(Revision - 1, Revision, editedRange?.Start ?? 0, editedRange?.Length ?? oldLength,
            editedRange is { } edit ? Index.Length - oldLength + edit.Length : Index.Length, changed, editedRange is null);
        OnChanged();
    }
    private bool RangeContainsImage(TextSelection range)
    {
        if (range.IsEmpty) return false;
        foreach (var entry in Index.Enumerate(range.Start, range.End))
        {
            var offset = entry.Start;
            foreach (var run in entry.Paragraph.Runs)
            {
                var end = offset + run.Storage.Length;
                if (offset >= range.End) break;
                if (end > range.Start && run.Inline?.Payload is ImageInlinePayload) return true;
                offset = end;
            }
        }
        return false;
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
        _retained.Add(index.Tree, current: true);
        _retained.Add(document.Resources, current: true);
        RetainFormatting(document, true);
        _retained.Remove(Index.Tree, current: true);
        _retained.Remove(Document.Resources, current: true);
        RetainFormatting(Document, false);
        Document = document; Index = index;
    }
    private void RetainFormatting(FlowDocument document, bool add)
    {
        void Change(object value) { if (add) _retained.Add(value, current: true); else _retained.Remove(value, current: true); }
        Change(document.Styles); Change(document.Defaults); Change(document.Theme);
        if (!document.Fonts.IsEmpty) Change(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(document.Fonts)!);
    }
    private void ClearRedo()
    { foreach (var state in _redo) _retained.Remove(state); _redo.Clear(); }
    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static (FlowDocument Document, int Caret) ReplaceRange(
        FlowDocument document, TextSelection selection, ImmutableArray<Paragraph> fragment)
    {
        var index = new DocumentIndex(document);
        if (selection.Start == 0 && selection.End == index.Length && !selection.IsEmpty)
        {
            var replacement = document with { Blocks = fragment.Select(p => (Block)(p with { Id = Guid.NewGuid() })).ToImmutableArray() };
            return (replacement, new DocumentIndex(replacement).Length);
        }
        var first = index.At(selection.Start);
        var last = index.At(selection.End);
        // Removing complete items keeps the next surviving item's identity and restart semantics.
        if (first.Paragraph.Id != last.Paragraph.Id && first.ContainerId == last.ContainerId &&
            selection.Start == first.Start && selection.End == last.Start && fragment.Length == 1 && fragment[0].Length == 0)
        {
            var removals = index.Enumerate(first.Start, last.Start).Where(p => p.Paragraph.Id != last.Paragraph.Id)
                .ToDictionary(p => p.Paragraph.Id, _ => ImmutableArray<Paragraph>.Empty);
            var removed = index.Tree.Rewrite(document, removals);
            return (removed, new DocumentIndex(removed).ById(last.Paragraph.Id).Start);
        }
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
