using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>Logical navigation units; character means one grapheme, including an atomic inline object.</summary>
public enum DocumentTextUnit { Character, Word, Line, Paragraph, Document }

/// <summary>A description attached to indexed text, without replacing its U+FFFC object coordinates.</summary>
public sealed record DocumentTextDescription(int Start, int Length, string Kind, string Text);

public sealed class DocumentTextChangedEventArgs(int revision, bool documentChanged, bool selectionChanged, bool readOnlyChanged) : EventArgs
{
    public int Revision { get; } = revision;
    public bool DocumentChanged { get; } = documentChanged;
    public bool SelectionChanged { get; } = selectionChanged;
    public bool ReadOnlyChanged { get; } = readOnlyChanged;
}

/// <summary>
/// UI-thread text contract for host bridges. Avalonia 12.1.3 has no public text-range provider;
/// this contract is not automatically exposed as a native UIA/AX/AT-SPI text pattern.
/// </summary>
public sealed class DocumentTextProvider
{
    private readonly TextaloniaEditor _editor;
    private int _revision;
    private TextSelection _selection;
    private bool _readOnly;
    internal EditorSession Session => _editor.Session;

    internal DocumentTextProvider(TextaloniaEditor editor)
    {
        _editor = editor;
        _revision = Session.Revision; _selection = Session.Selection; _readOnly = Session.IsReadOnly;
        // Both objects belong to the editor. No surface or replaced template is retained here.
        Session.Changed += OnChanged;
    }

    public bool HasNativeTextPattern => false;
    public bool IsReadOnly => Session.IsReadOnly;
    public int CaretPosition => Session.Selection.Active;
    public bool IsCaretActive => _editor.AccessibilitySurface?.IsFocused == true;
    public bool IsSelectionReversed => Session.Selection.Active < Session.Selection.Anchor;
    public DocumentTextRange DocumentRange => Range(0, Session.Index.Length);
    public DocumentTextRange SelectionRange => Range(Session.Selection.Start, Session.Selection.End);
    public DocumentTextRange CaretRange => Range(CaretPosition, CaretPosition);
    public event EventHandler<DocumentTextChangedEventArgs>? Changed;

    private void OnChanged(object? sender, EventArgs args)
    {
        var document = _revision != Session.Revision;
        var selection = _selection != Session.Selection;
        var readOnly = _readOnly != Session.IsReadOnly;
        _revision = Session.Revision; _selection = Session.Selection; _readOnly = Session.IsReadOnly;
        if (!document && !selection && !readOnly) return;
        var change = new DocumentTextChangedEventArgs(_revision, document, selection, readOnly);
        if (_editor.AccessibilitySurface is { } surface &&
            ControlAutomationPeer.FromElement(surface) is DocumentSurfaceAutomationPeer peer) peer.Notify(change);
        Changed?.Invoke(this, change);
    }

    /// <summary>Creates an ordered range, snapping both UTF-16 endpoints back to valid grapheme boundaries.</summary>
    public DocumentTextRange Range(int start, int end)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (start < 0 || end < start || end > Session.Index.Length) throw new ArgumentOutOfRangeException(nameof(start));
        return new(this, Session.Revision, Session.Index.Snap(start), Session.Index.Snap(end));
    }

    /// <summary>Point and returned bounds use surface-local DIPs. A platform bridge must convert screen coordinates.</summary>
    public DocumentTextRange RangeFromPoint(Point point)
    {
        var surface = GeometrySurface();
        try { var position = surface.GeometryHitTest(point); return Range(position, position); }
        catch (ShapingLimitExceededException error) { surface.RejectLayout(error); throw; }
    }

    /// <summary>Returns shaped fragments intersecting the current viewport. Simple view queries its bounded layout window.</summary>
    public IReadOnlyList<DocumentTextRange> GetVisibleRanges()
    {
        var surface = GeometrySurface();
        var view = _editor.Scroller is { } scroller
            ? new Rect(scroller.Offset.X, scroller.Offset.Y, scroller.Viewport.Width, scroller.Viewport.Height)
            : new Rect(surface.Bounds.Size);
        return surface.GeometryRanges().Where(p => p.Bounds.Intersects(view))
            .OrderBy(p => p.Start).Select(p => Range(p.Start, p.End)).ToArray();
    }

    internal void Validate(DocumentTextRange range)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (range.Revision != Session.Revision)
            throw new InvalidOperationException("The document changed. Request a new accessibility range.");
    }

    private DocumentSurface GeometrySurface()
    {
        Dispatcher.UIThread.VerifyAccess();
        var surface = _editor.AccessibilitySurface ?? throw new InvalidOperationException("The editor template is not attached.");
        if (TopLevel.GetTopLevel(surface) is null) throw new InvalidOperationException("The editor is not attached to a visual root.");
        if (surface.HasComposition) throw new InvalidOperationException("Committed range geometry is unavailable during transient composition.");
        surface.EnsureLayout(surface.Bounds.Width);
        if (_editor.LayoutError is { } error) throw error;
        return surface;
    }

    internal (int Start, int End) UnitAt(int position, DocumentTextUnit unit)
    {
        var index = Session.Index;
        switch (unit)
        {
            case DocumentTextUnit.Document: return (0, index.Length);
            case DocumentTextUnit.Paragraph:
                var paragraph = index.At(position);
                return (paragraph.Start, Math.Min(index.Length, paragraph.End + 1));
            case DocumentTextUnit.Character: return (position, Session.NextCaret(position));
            case DocumentTextUnit.Word:
                var start = position;
                while (start > 0 && !char.IsWhiteSpace(index.CharAt(start - 1))) start = Session.PreviousCaret(start);
                return (start, Session.NextWord(start));
            case DocumentTextUnit.Line:
                var surface = GeometrySurface();
                try
                {
                    return surface.GeometryLineRange(position);
                }
                catch (ShapingLimitExceededException error) { surface.RejectLayout(error); throw; }
            default: throw new ArgumentOutOfRangeException(nameof(unit));
        }
    }

    internal int Navigate(int position, DocumentTextUnit unit, int count)
    {
        if (!Enum.IsDefined(unit)) throw new ArgumentOutOfRangeException(nameof(unit));
        var forward = count > 0;
        for (long i = 0; i < Math.Abs((long)count); i++)
        {
            var next = unit switch
            {
                DocumentTextUnit.Character => forward ? Session.NextCaret(position) : Session.PreviousCaret(position),
                DocumentTextUnit.Word => forward ? Session.NextWord(position) : Session.PreviousWord(position),
                DocumentTextUnit.Document => forward ? Session.Index.Length : 0,
                _ => forward ? UnitAt(position, unit).End : UnitAt(Math.Max(0, position - 1), unit).Start
            };
            if (next == position) break;
            position = next;
        }
        return position;
    }

    internal IReadOnlyList<Rect> Bounds(DocumentTextRange range, int maximumRectangles)
    {
        Validate(range);
        if (maximumRectangles < 1) throw new ArgumentOutOfRangeException(nameof(maximumRectangles));
        var surface = GeometrySurface();
        var result = new List<Rect>();
        try
        {
            if (range.Start == range.End)
            {
                if (range.Start == Session.Selection.Active)
                {
                    if (surface.SelectionEndpointCaret(false) is { } activeCaret) Add(activeCaret);
                    if (_editor.LayoutError is { } error) throw error;
                }
                else
                {
                    Add(surface.GeometryCaret(range.Start));
                }
                return result;
            }
            if (surface.HasPagedLayout)
            {
                foreach (var rect in surface.GeometrySelectionRects(range.Start, range.End - range.Start)) Add(rect);
                return result;
            }
            var position = range.Start;
            while (position < range.End)
            {
                // Resolve only this page. At prunes previous offscreen targets and bounds its layout cache.
                var visual = surface.Layout.At(position)!;
                using var lease = visual.Acquire();
                var end = Math.Min(range.End, visual.TextEnd);
                if (end > position)
                    foreach (var rect in lease.Layout.HitTestTextRange(position - visual.TextStart, end - position))
                    {
                        var translated = rect.Translate(new Vector(visual.Origin.X, visual.Origin.Y));
                        Add(surface.ToSurface(visual.Clip?.Intersect(translated) ?? translated));
                    }
                if (end == visual.Position.End && end < range.End)
                {
                    var caret = surface.Layout.Caret(end).WithWidth(5);
                    Add(surface.ToSurface(visual.Clip?.Intersect(caret) ?? caret));
                    end++;
                }
                if (end <= position) break;
                position = end;
            }
        }
        catch (ShapingLimitExceededException error) { surface.RejectLayout(error); throw; }
        finally { surface.InvalidateMeasure(); }
        return result;
        void Add(Rect bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            if (result.Count == maximumRectangles)
                throw new InvalidOperationException("The range exceeds the rectangle limit. Query smaller text ranges.");
            result.Add(bounds);
        }
    }

    internal void Select(DocumentTextRange range, bool reverse)
    {
        Validate(range);
        _editor.AccessibilitySurface?.CancelComposition();
        Session.Select(reverse ? range.End : range.Start, reverse ? range.Start : range.End);
    }

    internal void Scroll(DocumentTextRange range, bool alignToTop)
    {
        Validate(range);
        var surface = GeometrySurface();
        try
        {
            var target = alignToTop ? range.Start : range.End;
            var rect = surface.GeometryCaret(target);
            surface.InvalidateMeasure(); surface.UpdateLayout();
            rect = surface.GeometryCaret(target);
            if (_editor.Scroller is { } scroller)
                scroller.Offset = new Vector(scroller.Offset.X,
                    Math.Max(0, alignToTop ? rect.Top : rect.Bottom - scroller.Viewport.Height));
            surface.BringIntoView(rect);
        }
        catch (ShapingLimitExceededException error) { surface.RejectLayout(error); throw; }
    }

    internal IReadOnlyList<DocumentTextDescription> Descriptions(DocumentTextRange range)
    {
        Validate(range);
        var result = new List<DocumentTextDescription>();
        var described = new HashSet<Guid>();
        foreach (var paragraph in Session.Index.Enumerate(range.Start, Math.Max(range.Start, range.End - 1)))
        {
            var node = Session.Index.Tree.Root;
            var nodeStart = 0;
            Table? table = null;
            foreach (var key in Session.Index.Tree.Paths!.Find(paragraph.Paragraph.Id)!.Value.Keys())
            {
                nodeStart += node.Children!.Prefix(key).Length;
                node = node.Children.Find(key)!.Value;
                if (node.Source is Table current)
                {
                    table = current;
                    if (described.Add(current.Id)) result.Add(new(nodeStart, Math.Min(node.Length, Session.Index.Length - nodeStart), "table",
                        $"Table, {current.Rows.Length} rows, {current.ColumnCount} columns"));
                }
                else if (node.Source is TableCell cell && table is not null && described.Add(cell.Id))
                    result.Add(new(nodeStart, Math.Min(node.Length, Session.Index.Length - nodeStart), "cell",
                        $"Row {node.Row + 1}, column {node.Column + 1}, row span {cell.RowSpan}, column span {cell.ColumnSpan}"));
            }
            var offset = paragraph.Start;
            foreach (var run in paragraph.Paragraph.Runs)
            {
                if (run.Inline is { } inline && offset < range.End && offset + 1 > range.Start)
                    result.Add(new(offset, 1, inline.Payload switch
                        { ImageInlinePayload => "image", MergeFieldInlinePayload => "merge-field", _ => "control" },
                        inline.Payload is MergeFieldInlinePayload field ? $"Merge field {field.Name}: {inline.AltText}" :
                            string.IsNullOrWhiteSpace(inline.AltText) ? "Embedded object" : inline.AltText));
                offset += run.Storage.Length;
            }
        }
        return result;
    }
}

/// <summary>Immutable UTF-16 range valid only for its originating revision. It never retains an old document snapshot.</summary>
public sealed class DocumentTextRange
{
    private readonly DocumentTextProvider _provider;
    internal DocumentTextRange(DocumentTextProvider provider, int revision, int start, int end)
    { _provider = provider; Revision = revision; Start = start; End = end; }
    public int Revision { get; }
    public int Start { get; }
    public int End { get; }
    public string GetText(int maximumLength = -1)
    {
        _provider.Validate(this);
        if (maximumLength < -1) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        return _provider.Session.Index.ReadText(Start, maximumLength < 0 ? End - Start : Math.Min(maximumLength, End - Start));
    }
    public DocumentTextRange Expand(DocumentTextUnit unit)
    {
        _provider.Validate(this);
        var extent = _provider.UnitAt(Start, unit);
        return _provider.Range(extent.Start, extent.End);
    }
    /// <summary>Moves one endpoint; crossing the other endpoint collapses the range at the moved endpoint.</summary>
    public DocumentTextRange MoveEndpoint(bool end, DocumentTextUnit unit, int count)
    {
        _provider.Validate(this);
        var position = _provider.Navigate(end ? End : Start, unit, count);
        return end ? _provider.Range(Math.Min(Start, position), position) : _provider.Range(position, Math.Max(End, position));
    }
    public IReadOnlyList<Rect> GetBoundingRectangles(int maximumRectangles = 1024) => _provider.Bounds(this, maximumRectangles);
    public IReadOnlyList<DocumentTextDescription> GetDescriptions() => _provider.Descriptions(this);
    public void Select(bool reverse = false) => _provider.Select(this, reverse);
    public void ScrollIntoView(bool alignToTop = true) => _provider.Scroll(this, alignToTop);
}

internal sealed class DocumentSurfaceAutomationPeer(DocumentSurface owner) : ControlAutomationPeer(owner), IValueProvider
{
    // Only native clients requesting the complete value pay for value-change string materialization.
    private string? _lastValue;
    public bool IsReadOnly => owner.Editor?.IsReadOnly ?? true;
    public string Value => _lastValue = owner.Editor?.Session.Index.Text ?? "";
    public void SetValue(string? value)
    {
        if (IsReadOnly) throw new InvalidOperationException("The document is read-only.");
        owner.CancelComposition();
        owner.Editor!.Session.SelectAll(); owner.Editor.Session.InsertText(value ?? "");
    }
    internal void Notify(DocumentTextChangedEventArgs change)
    {
        if (change.DocumentChanged && _lastValue is { } old)
        {
            var current = Value;
            if (old != current) RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, old, current);
        }
        if (change.ReadOnlyChanged) RaisePropertyChangedEvent(ValuePatternIdentifiers.IsReadOnlyProperty, !IsReadOnly, IsReadOnly);
    }
    protected override string GetClassNameCore() => nameof(TextaloniaEditor);
    protected override string GetNameCore() => owner.Editor is null ? "Textalonia editor" :
        AutomationProperties.GetName(owner.Editor) ?? "Textalonia editor";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
    protected override object? GetProviderCore(Type providerType)
    {
        if (owner.Editor is { } editor)
        {
            var provider = editor.Accessibility;
            if (providerType == typeof(DocumentTextProvider)) return provider;
        }
        return providerType == typeof(IValueProvider) ? this : base.GetProviderCore(providerType);
    }
}
