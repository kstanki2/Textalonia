using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Model;

namespace Textalonia.Layout;

/// <summary>
/// Exact synchronous pagination using the same Avalonia shaping backend as Simple view. Call on the
/// backend's UI thread. The reusable glyph cache is bounded; immutable snapshots own their checkpoints.
/// </summary>
public sealed class PaginationEngine : IDisposable
{
    private sealed record Key(Paragraph Paragraph, double Width, int Offset, int MaxCharacters);
    private sealed record Cached(ExactParagraph Value, LinkedListNode<Key> Node);
    private readonly Dictionary<Key, Cached> _measurements = [];
    private readonly LinkedList<Key> _lru = [];
    private ShapingEnvironment? _environment;
    private FlowDocument? _document;
    private ConditionalWeakTable<Paragraph, Paragraph> _resolvedParagraphs = new();
    private Builder? _previous;
    private bool _disposed;
    public int CachedMeasurements => _measurements.Count;
    public int MeasuredParagraphs { get; private set; }
    public long CachedLayoutBytes => _environment?.Cache.Bytes ?? 0;
    public long PeakLayoutBytes => _environment?.Cache.PeakBytes ?? 0;
    /// <summary>Block checkpoints reused during the most recent exact layout.</summary>
    public int ReusedCheckpoints { get; private set; }
    public int ReflowedBlocks { get; private set; }

    public PageLayoutSnapshot Paginate(FlowDocument document, FontFamily? font = null, IBrush? foreground = null,
        IBrush? border = null, PaginationOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(document);
        options ??= new(); font ??= FontFamily.Default; foreground ??= Brushes.Black; border ??= Brushes.Gray;
        if (!double.IsFinite(options.PageGap) || options.PageGap < 0 || options.PagesPerRow is < 1 or > 32 ||
            options.MaxShapingCharacters < 0 || options.MaxShapingCharacters is > 0 and < ParagraphLayout.WindowLength)
            throw new ArgumentOutOfRangeException(nameof(options));
        document.Validate();
        if (_environment is null || !Equals(font, _environment.Font) || !Equals(foreground, _environment.Foreground) ||
            _document?.Styles != document.Styles || _document?.Defaults != document.Defaults || _document?.Theme != document.Theme ||
            _document?.Fonts != document.Fonts || !document.Fonts.IsEmpty && !ReferenceEquals(_document?.Resources, document.Resources))
        {
            Clear(); _environment = new(document, font, foreground);
        }
        _document = document;
        var previous = _previous;
        var builder = new Builder(this, document, options, border, previous);
        try
        {
            var result = builder.Build();
            _previous = builder; previous?.Release();
            return result;
        }
        catch { builder.Release(); throw; }
    }

    private ExactParagraph Measure(Paragraph paragraph, double width, int offset, int maxCharacters)
    {
        var key = new Key(paragraph, Math.Max(16, width), offset, maxCharacters);
        if (_measurements.TryGetValue(key, out var cached))
        { _lru.Remove(cached.Node); _lru.AddLast(cached.Node); return cached.Value; }
        var value = new ExactParagraph(paragraph, key.Width, offset, _environment!, maxCharacters);
        MeasuredParagraphs++;
        _measurements[key] = new(value, _lru.AddLast(key));
        while (_measurements.Count > 256 && _lru.First is { } first)
        {
            _measurements[first.Value].Value.Release(); _measurements.Remove(first.Value); _lru.RemoveFirst();
        }
        return value;
    }
    public void Clear()
    {
        _previous?.Release(); _previous = null;
        foreach (var cached in _measurements.Values) cached.Value.Release();
        _measurements.Clear(); _lru.Clear(); _environment?.Dispose(); _environment = null; _document = null; _resolvedParagraphs = new();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Clear(); }

    private sealed class Builder(PaginationEngine engine, FlowDocument document, PaginationOptions options, IBrush border, Builder? previous)
    {
        private sealed class Sheet
        {
            public required DocumentSection Section;
            public required PageSettings Settings;
            public int Number;
            public bool Blank;
            public Rect Body;
            public Rect[] Columns = [];
            public double Height, Width;
        }
        private sealed record Item(Block Block, double Left = 0, double Right = 0, Section? Decoration = null);
        private sealed record State(int PageCount, int Column, double Y, int LineNumber, DocumentSection Section,
            int PageNumber, Rect Body, ImmutableArray<Rect> Columns);
        private sealed record Checkpoint(Item Item, ImmutableArray<Block> Dependencies, State Before, State After,
            int FragmentStart, int FragmentCount, int DecorationStart, int DecorationCount, int CellStart, int CellCount, ImmutableArray<Sheet> Sheets);
        private readonly Dictionary<Guid, Checkpoint> _checkpoints = [];
        private readonly Dictionary<Key, ExactParagraph> _knownMeasurements = [];
        private Builder? _prior = previous;
        private sealed record LocalLine(ParagraphPosition Position, ExactParagraph Measurement, ExactLine Line, double X, double Y, double Width, Rect? Clip = null);
        private sealed class CellContent
        {
            public required Table Table;
            public int Row, Column;
            public TableCell Cell = null!;
            public double X, Width, Height, Top, GroupHeight, LastColumnWidth, LastRowHeight;
            public List<LocalLine> Lines = [];
            public List<BlockDecoration> Decorations = [];
            public List<TableCellVisual> Cells = [];
            public int Next;
        }
        private readonly DocumentIndex _index = new(document);
        private readonly DocumentStyleResolver _resolver = new(document);
        private readonly List<Sheet> _pages = [];
        private readonly List<LineFragment> _fragments = [];
        private readonly List<(int Page, BlockDecoration Decoration)> _decorations = [];
        private readonly List<(int Page, TableCellVisual Cell)> _cells = [];
        private readonly HashSet<ExactParagraph> _held = [];
        private readonly Dictionary<Guid, Paragraph> _resolved = [];
        private readonly List<Item> _items = [];
        private readonly Dictionary<Guid, DocumentSection> _sections = [];
        private DocumentSection _section = null!;
        private Sheet Page => _pages[^1];
        private int _column, _lineNumber = 1;
        private double _y;
        private Rect Column => Page.Columns[Math.Min(_column, Page.Columns.Length - 1)];
        private double Remaining => options.Draft ? double.PositiveInfinity : Math.Max(0, Column.Bottom - _y);
        private bool AtTop => _y <= Column.Top + .01;
        private readonly Dictionary<(Guid Section, int Page), double> _balanceLimits = [];
        private PaginationOptions _options => options;
        private FlowDocument _source => document;
        private IBrush _border => border;

        public PageLayoutSnapshot Build()
        {
            foreach (var section in document.Sections)
                if (section.StartParagraphId != Guid.Empty) _sections.Add(section.StartParagraphId, section);
            Flatten(document.Blocks, 0, 0, null);
            Run();
            // Binary search the final occupied page of each multicolumn section. Every trial reruns exact
            // line layout at the actual (possibly unequal) column widths; estimates never choose breaks.
            if (!options.Draft)
            {
                var candidates = _pages.Select((p, i) => (p, i)).GroupBy(v => v.p.Section.Id)
                    .Select(g => g.Last()).Where(v => v.p.Columns.Length > 1 && v.p.Settings.BalanceColumns && !v.p.Blank).ToArray();
                foreach (var candidate in candidates)
                {
                    var targetCount = _pages.Count;
                    var key = (candidate.p.Section.Id, candidate.i);
                    var high = candidate.p.Body.Height;
                    var low = Math.Min(high, _fragments.Where(f => f.PageIndex == candidate.i).Select(f => f.Bounds.Height).DefaultIfEmpty(1).Max());
                    for (var pass = 0; pass < 12; pass++)
                    {
                        var middle = (low + high) / 2;
                        _balanceLimits[key] = middle; Run();
                        if (_pages.Count <= targetCount) high = middle; else low = middle;
                    }
                    _balanceLimits[key] = high + .01; Run();
                }
            }
            var result = Finish();
            var retained = _fragments.Select(f => f.Measurement).ToHashSet();
            foreach (var measurement in _held.Where(m => !retained.Contains(m)).ToArray()) { measurement.Release(); _held.Remove(measurement); }
            foreach (var key in _knownMeasurements.Where(p => !retained.Contains(p.Value)).Select(p => p.Key).ToArray()) _knownMeasurements.Remove(key);
            _prior = null; return result;
        }

        private void Run()
        {
            _pages.Clear(); _fragments.Clear(); _decorations.Clear(); _cells.Clear();
            _checkpoints.Clear(); engine.ReusedCheckpoints = 0; engine.ReflowedBlocks = 0;
            _column = 0;
            _section = document.Sections.FirstOrDefault() ?? new DocumentSection { Id = Guid.Empty };
            _lineNumber = _section.PageSettings.LineNumbering?.Start ?? 1;
            NewPage();
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                var first = FirstParagraph(item.Block);
                if (first is not null && _sections.TryGetValue(first.Id, out var section) && section.Id != _section.Id) Transition(section);
                var before = Capture(); var dependencies = Dependencies(i);
                var fragmentStart = _fragments.Count; var decorationStart = _decorations.Count; var cellStart = _cells.Count;
                if (TryReuse(item, dependencies, before)) { Record(); continue; }
                engine.ReflowedBlocks++;
                if (item.Block is Paragraph paragraph) PlaceParagraph(paragraph, item, i);
                else if (item.Block is Table table) PlaceTable(table, item);
                Record();
                void Record() => _checkpoints[item.Block.Id] = new(item, dependencies, before, Capture(), fragmentStart,
                    _fragments.Count - fragmentStart, decorationStart, _decorations.Count - decorationStart,
                    cellStart, _cells.Count - cellStart, _pages.Skip(before.PageCount - 1).Select(Copy).ToImmutableArray());
            }
            if (options.Draft) Page.Height = Math.Max(Page.Settings.EffectiveHeight, _y + Page.Settings.Margins.Bottom);
        }

        private State Capture() => new(_pages.Count, _column, _y, _lineNumber, _section, Page.Number, Page.Body, Page.Columns.ToImmutableArray());
        private static Sheet Copy(Sheet page) => new() { Section = page.Section, Settings = page.Settings, Number = page.Number,
            Blank = page.Blank, Body = page.Body, Columns = page.Columns.ToArray(), Height = page.Height, Width = page.Width };
        private ImmutableArray<Block> Dependencies(int at)
        {
            var result = ImmutableArray.CreateBuilder<Block>();
            if (at > 0 && _items[at].Block is Paragraph p && Resolve(p).Style.ContextualSpacing) result.Add(_items[at - 1].Block);
            var keep = _items[at].Block is Paragraph source && Resolve(source).Style.KeepWithNext;
            if (at + 1 < _items.Count) result.Add(_items[at + 1].Block);
            for (var i = at + 1; keep && i + 1 < _items.Count && _items[i].Block is Paragraph next; i++)
            { keep = Resolve(next).Style.KeepWithNext; if (keep) result.Add(_items[i + 1].Block); }
            return result.ToImmutable();
        }
        private bool TryReuse(Item item, ImmutableArray<Block> dependencies, State before)
        {
            if (_prior is null || _prior._options != options || !Equals(_prior._border, border) ||
                !_prior._source.Sections.SequenceEqual(document.Sections) || _balanceLimits.Count != 0 || _prior._balanceLimits.Count != 0 ||
                !_prior._checkpoints.TryGetValue(item.Block.Id, out var checkpoint) || checkpoint.Item != item ||
                !checkpoint.Dependencies.SequenceEqual(dependencies) ||
                checkpoint.Before with { Columns = before.Columns } != before || !checkpoint.Before.Columns.SequenceEqual(before.Columns)) return false;
            foreach (var old in _prior._fragments.Skip(checkpoint.FragmentStart).Take(checkpoint.FragmentCount))
            {
                var position = _index.ById(old.ParagraphId); var shift = position.Start - old.Position.Start;
                _fragments.Add(old with { Position = position, TextStart = old.TextStart + shift, TextEnd = old.TextEnd + shift, SourceStart = old.SourceStart + shift,
                    Marker = old.Marker is null ? null : ListNumbering.GetMarker(document, old.ParagraphId)?.Text });
                if (_held.Add(old.Measurement)) old.Measurement.AddRef();
                _knownMeasurements[new(old.Measurement.Source, old.Measurement.Width, old.Measurement.Offset, old.Measurement.MaxCharacters)] = old.Measurement;
            }
            _decorations.AddRange(_prior._decorations.Skip(checkpoint.DecorationStart).Take(checkpoint.DecorationCount));
            _cells.AddRange(_prior._cells.Skip(checkpoint.CellStart).Take(checkpoint.CellCount));
            _pages[^1] = Copy(checkpoint.Sheets[0]);
            _pages.AddRange(checkpoint.Sheets.Skip(1).Select(Copy));
            _column = checkpoint.After.Column; _y = checkpoint.After.Y; _lineNumber = checkpoint.After.LineNumber; _section = checkpoint.After.Section;
            engine.ReusedCheckpoints++; return true;
        }

        private static Paragraph? FirstParagraph(Block block) => block switch
        { Paragraph paragraph => paragraph, Section s => s.Blocks.Select(FirstParagraph).FirstOrDefault(p => p is not null),
            Table t => t.Rows.SelectMany(r => r).SelectMany(c => c.Blocks).Select(FirstParagraph).FirstOrDefault(p => p is not null), _ => null };
        private void Flatten(IEnumerable<Block> blocks, double left, double right, Section? decoration)
        {
            foreach (var block in blocks)
                if (block is Section section)
                {
                    var padding = LayoutHeightIndex.SectionPadding(section);
                    Flatten(section.Blocks, left + padding.Left, right + padding.Right, section);
                }
                else _items.Add(new(block, left, right, decoration));
        }

        private void NewPage(bool blank = false)
        {
            var settings = _section.PageSettings;
            var previous = _pages.LastOrDefault();
            var number = previous is null ? _section.PageNumberStart ?? 1 : previous.Section.Id != _section.Id && _section.PageNumberStart is { } restart ? restart : previous.Number + 1;
            var even = (_pages.Count + 1) % 2 == 0;
            var left = settings.Margins.Left; var right = settings.Margins.Right;
            if (settings.MirrorMargins && even) (left, right) = (right, left);
            if (!settings.MirrorMargins || !even) left += settings.Gutter; else right += settings.Gutter;
            var body = new Rect(left, settings.Margins.Top, settings.EffectiveWidth - left - right,
                settings.EffectiveHeight - settings.Margins.Top - settings.Margins.Bottom);
            var page = new Sheet { Section = _section, Settings = settings, Number = number, Blank = blank, Body = body,
                Height = settings.EffectiveHeight, Width = settings.EffectiveWidth };
            _pages.Add(page);
            var limit = _balanceLimits.GetValueOrDefault((_section.Id, _pages.Count - 1), body.Height);
            page.Columns = options.Draft ? [body.WithHeight(1e15)] : Columns(settings, body.WithHeight(limit));
            _column = 0; _y = page.Columns[0].Top;
            if (settings.LineNumbering is { Restart: LineNumberRestart.EachPage } numbering) _lineNumber = numbering.Start;
        }
        private static Rect[] Columns(PageSettings settings, Rect body)
        {
            var definitions = settings.Columns.IsEmpty ? ImmutableArray.Create(new PageColumn()) : settings.Columns;
            var weight = definitions.Sum(c => c.Width); var x = body.Left;
            var available = body.Width - Math.Max(0, definitions.Length - 1) * settings.ColumnSpacing;
            return definitions.Select(column =>
            { var rect = new Rect(x, body.Top, available * column.Width / weight, body.Height); x = rect.Right + settings.ColumnSpacing; return rect; }).ToArray();
        }
        private void Advance(bool page = false)
        {
            if (options.Draft) return;
            if (!page && _column + 1 < Page.Columns.Length) { _column++; _y = Column.Top; }
            else NewPage();
        }
        private void Transition(DocumentSection section)
        {
            var previous = _section; _section = section;
            if (section.PageSettings.LineNumbering is { Restart: LineNumberRestart.EachSection } numbering) _lineNumber = numbering.Start;
            if (options.Draft)
            {
                var settings = section.PageSettings;
                Page.Width = Math.Max(Page.Width, settings.EffectiveWidth);
                Page.Columns = [new Rect(settings.Margins.Left + settings.Gutter, _y,
                    settings.EffectiveWidth - settings.Margins.Left - settings.Margins.Right - settings.Gutter, 1e15)];
                _column = 0; return;
            }
            var samePaper = previous.PageSettings.EffectiveWidth == section.PageSettings.EffectiveWidth &&
                previous.PageSettings.EffectiveHeight == section.PageSettings.EffectiveHeight;
            if (section.BreakKind == SectionBreakKind.Continuous && samePaper)
            {
                var top = _fragments.Where(f => f.PageIndex == _pages.Count - 1).Select(f => f.Bounds.Bottom).DefaultIfEmpty(_y).Max();
                var settings = section.PageSettings;
                var body = new Rect(settings.Margins.Left + settings.Gutter, top,
                    settings.EffectiveWidth - settings.Margins.Left - settings.Margins.Right - settings.Gutter,
                    Math.Max(0, settings.EffectiveHeight - settings.Margins.Bottom - top));
                if (body.Height > 1) { Page.Columns = Columns(settings, body); _column = 0; _y = top; return; }
            }
            else if (section.BreakKind == SectionBreakKind.NextColumn && samePaper && _column + 1 < Page.Columns.Length)
            { _column++; _y = Column.Top; return; }
            var nextNumber = _pages.Count + 1;
            if (section.BreakKind == SectionBreakKind.OddPage && nextNumber % 2 == 0 ||
                section.BreakKind == SectionBreakKind.EvenPage && nextNumber % 2 != 0)
            {
                var nextLineNumber = _lineNumber;
                _section = previous; NewPage(blank: true); _section = section; _lineNumber = nextLineNumber;
            }
            NewPage();
        }

        private Paragraph Resolve(Paragraph paragraph)
        {
            if (_resolved.TryGetValue(paragraph.Id, out var result)) return result;
            result = engine._resolvedParagraphs.GetValue(paragraph, _resolver.ResolveParagraph);
            return _resolved[paragraph.Id] = result;
        }
        private ExactParagraph Measure(Paragraph paragraph, double width, int offset = 0)
        {
            var key = new Key(paragraph, Math.Max(16, width), offset, options.MaxShapingCharacters);
            var result = _knownMeasurements.GetValueOrDefault(key) ?? _prior?._knownMeasurements.GetValueOrDefault(key) ??
                engine.Measure(paragraph, width, offset, options.MaxShapingCharacters);
            if (_held.Add(result)) result.AddRef();
            _knownMeasurements[key] = result;
            return result;
        }
        private static double Indent(Paragraph paragraph) => paragraph.Style.Indent + (paragraph.Style.List == ListKind.None ? 0 : 28 + paragraph.Style.ListLevel * 24);
        private Paragraph Grid(Paragraph paragraph)
        {
            if (!paragraph.Style.SnapToGrid || (paragraph.Style.EastAsianGrid ?? _section.PageSettings.Grid) is not { } grid) return paragraph;
            return paragraph with { Style = paragraph.Style with
            {
                LineSpacingMode = grid.LineSpacing > 0 ? LineSpacingMode.AtLeast : paragraph.Style.LineSpacingMode,
                LineSpacing = grid.LineSpacing > 0 ? Math.Max(grid.LineSpacing, paragraph.Style.LineSpacing) : paragraph.Style.LineSpacing,
                EastAsianGrid = grid
            } };
        }
        private void PlaceParagraph(Paragraph source, Item item, int itemIndex)
        {
            var paragraph = Grid(Resolve(source)); var style = paragraph.Style;
            if (!options.Draft)
            {
                // Positioned paragraphs consume a logical page/column even when they do not advance flow Y.
                var occupied = !AtTop || _fragments.Any(f => f.PageIndex == _pages.Count - 1 && f.ColumnIndex == _column);
                if (style.PageBreakBefore && (occupied || _column != 0)) Advance(true);
                else if (style.ColumnBreakBefore && occupied) Advance();
            }
            if (style.Frame is { } frame)
            {
                var shaped = Measure(paragraph, Math.Max(16, frame.Width - Indent(paragraph) - style.RightIndent));
                var region = new Rect(Column.Left + frame.X, Column.Top + frame.Y, frame.Width, frame.Height ?? shaped.Height);
                var clip = region.Intersect(Column); var frameY = region.Top;
                foreach (var line in shaped.Lines)
                {
                    _fragments.Add(Fragment(_index.ById(source.Id), shaped, line,
                        new Point(region.Left + Indent(paragraph) + line.Window.XOffset, frameY), frame.Width, clip));
                    frameY += line.Height;
                }
                if (style.Shading is not null || style.Borders is not null)
                    _decorations.Add((_pages.Count - 1, new(region, DocumentLayout.Brush(style.Shading), border, false, style.Borders, clip)));
                return;
            }
            var indent = Indent(paragraph);
            double Width() => Math.Max(16, Column.Width - item.Left - item.Right - indent - style.RightIndent);
            var measurement = Measure(paragraph, Width());
            var before = style.SpaceBefore; var after = style.SpaceAfter;
            if (style.ContextualSpacing)
            {
                if (itemIndex > 0 && _items[itemIndex - 1].Block is Paragraph previous && previous.Style.StyleId == source.Style.StyleId) before = 0;
                if (itemIndex + 1 < _items.Count && _items[itemIndex + 1].Block is Paragraph next && next.Style.StyleId == source.Style.StyleId) after = 0;
            }
            var keepHeight = before + measurement.Height + after;
            if (style.KeepWithNext)
            {
                for (var i = itemIndex + 1; i < _items.Count && _items[i].Block is Paragraph next; i++)
                {
                    var following = Grid(Resolve(next));
                    if (following.Style.PageBreakBefore || following.Style.ColumnBreakBefore || _sections.ContainsKey(next.Id)) break;
                    keepHeight += following.Style.SpaceBefore + Measure(following,
                        Math.Max(16, Column.Width - _items[i].Left - _items[i].Right - Indent(following) - following.Style.RightIndent)).Height + following.Style.SpaceAfter;
                    if (!following.Style.KeepWithNext || keepHeight > Column.Height) break;
                }
            }
            if (!AtTop && (style.KeepTogether || style.KeepWithNext) && keepHeight <= Column.Height && keepHeight > Remaining)
            { Advance(); measurement = Measure(paragraph, Width()); }
            if (before + measurement.Lines.FirstOrDefault()?.Height > Remaining && !AtTop) { Advance(); measurement = Measure(paragraph, Width()); }
            _y += Math.Min(before, options.Draft ? before : Math.Max(0, Remaining - 1));
            var nextLine = 0; var first = true;
            void ContinueInNextColumn()
            {
                var width = Width(); var offset = measurement.Lines[nextLine].Start;
                Advance();
                if (Math.Abs(width - Width()) > .01) { measurement = Measure(paragraph, Width(), offset); nextLine = 0; }
            }
            while (nextLine < measurement.Lines.Length)
            {
                var lines = measurement.Lines;
                var fit = 0; var height = 0d;
                while (nextLine + fit < lines.Length && height + lines[nextLine + fit].Height <= Remaining + .01)
                { height += lines[nextLine + fit].Height; fit++; }
                if (fit == 0 && !AtTop)
                { ContinueInNextColumn(); continue; }
                if (fit == 0) fit = 1; // An oversize line/object is clipped once, never retried indefinitely.
                var remainingLines = lines.Length - nextLine;
                if (style.WidowControl && remainingLines > fit)
                {
                    if (!AtTop && (fit == 1 || remainingLines <= 3 && lines.Skip(nextLine).Sum(l => l.Height) <= Column.Height))
                    { ContinueInNextColumn(); continue; }
                    if (remainingLines - fit == 1 && fit > 2) fit--;
                }
                for (var i = 0; i < fit; i++)
                {
                    var line = lines[nextLine++];
                    var x = Column.Left + item.Left + indent + line.Window.XOffset;
                    var origin = new Point(x, _y);
                    var fragment = Fragment(_index.ById(source.Id), measurement, line, origin, Width() - line.Window.XOffset,
                        Column, first && style.List != ListKind.None ? ListNumbering.GetMarker(document, source.Id)?.Text : null);
                    _fragments.Add(fragment);
                    if (style.Shading is not null || style.Borders is not null)
                        _decorations.Add((_pages.Count - 1, new(fragment.Bounds, DocumentLayout.Brush(style.Shading), border, false, style.Borders, Column)));
                    if (item.Decoration is { } decoration)
                        _decorations.Add((_pages.Count - 1, new(new Rect(Column.Left, _y, Column.Width, line.Height),
                            DocumentLayout.Brush(decoration.Background), DocumentLayout.Brush(decoration.BorderColor), true, decoration.Borders, Column)));
                    _y += line.Height; first = false;
                }
                if (nextLine < lines.Length)
                { ContinueInNextColumn(); }
            }
            _y += after;
        }
        private LineFragment Fragment(ParagraphPosition position, ExactParagraph measurement, ExactLine line, Point origin,
            double width, Rect clip, string? marker = null)
        {
            int? number = null;
            if (_section.PageSettings.LineNumbering is { } numbering)
            { if ((_lineNumber - numbering.Start) % numbering.CountBy == 0) number = _lineNumber; _lineNumber++; }
            return new() { ParagraphId = position.Paragraph.Id, SectionId = _section.Id, PageIndex = _pages.Count - 1,
                ColumnIndex = _column, TextStart = position.Start + line.Start, TextEnd = position.Start + line.End,
                Bounds = new Rect(origin, new Size(Math.Max(1, width), line.Height)), Clip = clip, Baseline = origin.Y + line.Baseline,
                Measurement = measurement, Line = line, Position = position, Origin = origin, ColumnBounds = Column,
                SourceStart = position.Start + measurement.Offset + line.Window.Start, Marker = marker, LineNumber = number,
                LineNumberDistance = _section.PageSettings.LineNumbering?.Distance ?? 0 };
        }

        private void PlaceTable(Table table, Item item)
        {
            // A row-span group shares one vertical cut. No cell can draw across a line in a
            // neighbouring cell, and continuation is reshaped at each actual column width.
            var progress = new Dictionary<Guid, int>();
            double Width() => Math.Max(16, Column.Width - item.Left - item.Right);
            for (var startRow = 0; startRow < table.Rows.Length;)
            {
                var endRow = startRow + 1;
                for (var r = startRow; r < endRow; r++)
                    for (var c = 0; c < table.ColumnCount; c++)
                        if (!table.IsCovered(r, c)) endRow = Math.Max(endRow, r + table.Rows[r][c].RowSpan);
                var measuredWidth = Width();
                var group = MeasureTable(table, measuredWidth, progress, startRow, endRow)[0];
                var groupHeight = group.Select(c => c.GroupHeight).DefaultIfEmpty().Max();
                var consumed = 0d;
                void Continue(bool reflow = false)
                {
                    Advance();
                    if (!reflow && Math.Abs(measuredWidth - Width()) < .01) return;
                    measuredWidth = Width();
                    group = MeasureTable(table, measuredWidth, progress, startRow, endRow)[0];
                    groupHeight = group.Select(c => c.GroupHeight).DefaultIfEmpty().Max();
                    consumed = 0;
                }
                if (groupHeight <= Column.Height && groupHeight > Remaining && !AtTop) Continue();
                while (consumed + .01 < groupHeight || group.Any(c => c.Next < c.Lines.Count))
                {
                    if (Remaining < 1 && !options.Draft) Continue();
                    var cut = Math.Min(groupHeight, consumed + Remaining);
                    var oversize = false;
                    bool changed;
                    do
                    {
                        changed = false;
                        foreach (var cell in group)
                            foreach (var line in cell.Lines.Skip(cell.Next))
                                if (line.Y < cut - .01 && Math.Min(line.Y + line.Line.Height, line.Clip?.Bottom ?? cell.Top + cell.Height) > cut + .01)
                                {
                                    var candidate = Math.Max(consumed, line.Y);
                                    if (candidate < cut - .01) { cut = candidate; changed = true; }
                                }
                    } while (changed && cut > consumed + .01);
                    if (cut <= consumed + .01)
                    {
                        if (!AtTop) { Continue(); continue; }
                        oversize = true;
                        // An oversize atomic line is emitted once with the page/cell clip.
                        cut = Math.Min(groupHeight, consumed + Remaining);
                        if (cut <= consumed + .01) cut = consumed + Math.Max(1, Math.Min(Remaining, 1));
                    }
                    foreach (var cell in group)
                    {
                        var origin = new Point(Column.Left + item.Left + cell.X, _y - consumed);
                        var top = Math.Max(consumed, cell.Top);
                        var bottom = Math.Min(cut, cell.Top + cell.Height);
                        var bounds = new Rect(origin.X, origin.Y + top, cell.Width, Math.Max(0, bottom - top));
                        var clip = bounds.Intersect(Column);
                        if (bottom > top)
                        {
                            _cells.Add((_pages.Count - 1, new(cell.Table, cell.Row, cell.Column, bounds,
                                cell.LastColumnWidth, cell.LastRowHeight, clip)));
                            _decorations.Add((_pages.Count - 1, new(bounds, DocumentLayout.Brush(cell.Cell.Background), border, false, cell.Cell.Borders, clip)));
                            foreach (var nested in cell.Cells)
                            {
                                var shifted = nested with { Bounds = nested.Bounds.Translate(new Vector(origin.X, origin.Y)), Clip = clip };
                                if (shifted.Bounds.Intersects(clip)) _cells.Add((_pages.Count - 1, shifted));
                            }
                            foreach (var decoration in cell.Decorations)
                            {
                                var shifted = decoration with { Bounds = decoration.Bounds.Translate(new Vector(origin.X, origin.Y)), Clip = clip };
                                if (shifted.Bounds.Intersects(clip)) _decorations.Add((_pages.Count - 1, shifted));
                            }
                        }
                        while (cell.Next < cell.Lines.Count && cell.Lines[cell.Next].Y < cut - .01)
                        {
                            var local = cell.Lines[cell.Next++];
                            _fragments.Add(Fragment(local.Position, local.Measurement, local.Line,
                                new Point(origin.X + local.X, origin.Y + local.Y), local.Width,
                                local.Clip is { } localClip ? clip.Intersect(localClip.Translate(new Vector(origin.X, origin.Y))) : clip));
                            progress[local.Position.Paragraph.Id] = local.Line.End >= local.Position.Paragraph.Length ? -1 : local.Line.End;
                        }
                    }
                    _y += cut - consumed; consumed = cut;
                    if (consumed + .01 < groupHeight || group.Any(c => c.Next < c.Lines.Count)) Continue(oversize);
                }
                startRow = endRow;
            }
            _y += 12;
        }

        private static IEnumerable<Paragraph> CellParagraphs(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
                switch (block)
                {
                    case Paragraph p: yield return p; break;
                    case Section section:
                        foreach (var paragraph in CellParagraphs(section.Blocks)) yield return paragraph;
                        break;
                    case Table table:
                        for (var r = 0; r < table.Rows.Length; r++)
                            for (var c = 0; c < table.ColumnCount; c++)
                                if (!table.IsCovered(r, c))
                                    foreach (var paragraph in CellParagraphs(table.Rows[r][c].Blocks)) yield return paragraph;
                        break;
                }
        }

        private List<List<CellContent>> MeasureTable(Table table, double width, Dictionary<Guid, int>? progress = null,
            int firstRow = 0, int endRow = -1)
        {
            if (endRow < 0) endRow = table.Rows.Length;
            var cells = new List<CellContent>();
            var heights = new double[table.Rows.Length];
            var resolved = _resolver.ResolveTableStyle(table);
            for (var r = firstRow; r < endRow; r++)
            {
                var sizing = table.RowSizing.IsEmpty ? new TableRowSizing() : table.RowSizing[r];
                for (var c = 0; c < table.ColumnCount; c++)
                {
                    if (table.IsCovered(r, c)) continue;
                    var original = table.Rows[r][c];
                    var cell = original with { Padding = original.Padding ?? resolved.Padding, Background = original.Background ?? resolved.Background, Borders = original.Borders ?? resolved.Borders };
                    var value = new CellContent { Table = table, Row = r, Column = c, Cell = cell,
                        X = LayoutHeightIndex.ColumnOffset(table, width, c), Width = LayoutHeightIndex.ColumnWidth(table, width, c, cell.ColumnSpan),
                        LastColumnWidth = LayoutHeightIndex.ColumnWidth(table, width, c + cell.ColumnSpan - 1) };
                    var paragraphs = CellParagraphs(cell.Blocks).ToArray();
                    var pending = progress is null || paragraphs.Any(p => !progress.TryGetValue(p.Id, out var offset) || offset >= 0);
                    if (pending)
                    {
                        var continued = progress is not null && paragraphs.Any(p => progress.ContainsKey(p.Id));
                        heights[r] = Math.Max(heights[r], sizing.Mode == TableRowHeightMode.Auto ? continued ? 0 : 36 : sizing.Height);
                        var padding = LayoutHeightIndex.CellPadding(cell);
                        value.Height = MeasureLocal(cell.Blocks, padding.Left, continued ? 0 : padding.Top,
                            Math.Max(16, value.Width - padding.Left - padding.Right), value, progress) + padding.Bottom;
                        if (cell.RowSpan == 1 && sizing.Mode != TableRowHeightMode.Exact) heights[r] = Math.Max(heights[r], value.Height);
                    }
                    cells.Add(value);
                }
            }
            foreach (var cell in cells.Where(c => c.Cell.RowSpan > 1))
            {
                var covered = heights.Skip(cell.Row).Take(cell.Cell.RowSpan).Sum();
                if (covered < cell.Height)
                    for (var r = cell.Row + cell.Cell.RowSpan - 1; r >= cell.Row; r--)
                        if (table.RowSizing.IsEmpty || table.RowSizing[r].Mode != TableRowHeightMode.Exact) { heights[r] += cell.Height - covered; break; }
            }
            var offsets = new double[heights.Length + 1];
            for (var r = 0; r < heights.Length; r++) offsets[r + 1] = offsets[r] + heights[r];
            var groups = new List<List<CellContent>>();
            for (var start = firstRow; start < endRow;)
            {
                var end = start + 1;
                for (var r = start; r < end; r++)
                    foreach (var cell in cells.Where(c => c.Row == r)) end = Math.Max(end, cell.Row + cell.Cell.RowSpan);
                var group = cells.Where(c => c.Row >= start && c.Row < end).ToList();
                foreach (var cell in group)
                {
                    cell.Top = offsets[cell.Row] - offsets[start];
                    cell.Height = offsets[cell.Row + cell.Cell.RowSpan] - offsets[cell.Row];
                    cell.GroupHeight = offsets[end] - offsets[start];
                    cell.LastRowHeight = heights[cell.Row + cell.Cell.RowSpan - 1];
                    var exact = !table.RowSizing.IsEmpty && Enumerable.Range(cell.Row, cell.Cell.RowSpan).All(r => table.RowSizing[r].Mode == TableRowHeightMode.Exact);
                    if (exact) cell.Lines.RemoveAll(l => l.Y >= cell.Height);
                    for (var i = 0; i < cell.Lines.Count; i++) cell.Lines[i] = cell.Lines[i] with { Y = cell.Lines[i].Y + cell.Top,
                        Clip = cell.Lines[i].Clip?.Translate(new Vector(0, cell.Top)) };
                    for (var i = 0; i < cell.Decorations.Count; i++) cell.Decorations[i] = cell.Decorations[i] with
                    { Bounds = cell.Decorations[i].Bounds.Translate(new Vector(0, cell.Top)) };
                    for (var i = 0; i < cell.Cells.Count; i++) cell.Cells[i] = cell.Cells[i] with
                    { Bounds = cell.Cells[i].Bounds.Translate(new Vector(0, cell.Top)) };
                }
                groups.Add(group); start = end;
            }
            return groups;
        }

        private double MeasureLocal(IEnumerable<Block> blocks, double x, double y, double width, CellContent target,
            Dictionary<Guid, int>? progress = null)
        {
            foreach (var block in blocks)
                switch (block)
                {
                    case Paragraph source:
                    {
                        var offset = 0;
                        if (progress is not null && progress.TryGetValue(source.Id, out offset) && offset < 0) break;
                        var paragraph = Grid(Resolve(source)); var indent = Indent(paragraph);
                        var available = Math.Max(16, width - indent - paragraph.Style.RightIndent);
                        var measurement = Measure(paragraph, available, offset);
                        if (offset == 0) y += paragraph.Style.SpaceBefore;
                        foreach (var line in measurement.Lines)
                        {
                            target.Lines.Add(new(_index.ById(source.Id), measurement, line, x + indent + line.Window.XOffset, y, available - line.Window.XOffset));
                            y += line.Height;
                        }
                        y += paragraph.Style.SpaceAfter; break;
                    }
                    case Section section:
                    {
                        var padding = LayoutHeightIndex.SectionPadding(section); var top = y;
                        y = MeasureLocal(section.Blocks, x + padding.Left, y + padding.Top, Math.Max(16, width - padding.Left - padding.Right), target, progress) + padding.Bottom;
                        target.Decorations.Add(new(new Rect(x, top, width, y - top), DocumentLayout.Brush(section.Background),
                            DocumentLayout.Brush(section.BorderColor), true, section.Borders)); break;
                    }
                    case Table table:
                        foreach (var row in MeasureTable(table, width, progress))
                        {
                            var height = row.Select(c => c.GroupHeight).DefaultIfEmpty(0).Max();
                            foreach (var cell in row)
                            {
                                var bounds = new Rect(x + cell.X, y + cell.Top, cell.Width, cell.Height);
                                target.Cells.Add(new(cell.Table, cell.Row, cell.Column, bounds, cell.LastColumnWidth, cell.LastRowHeight, null));
                                foreach (var nested in cell.Cells) target.Cells.Add(nested with { Bounds = nested.Bounds.Translate(new Vector(x + cell.X, y)) });
                                foreach (var line in cell.Lines) target.Lines.Add(line with { X = x + cell.X + line.X, Y = y + line.Y,
                                    Clip = line.Clip is { } nestedClip ? bounds.Intersect(nestedClip.Translate(new Vector(x + cell.X, y))) : bounds });
                                target.Decorations.Add(new(bounds, DocumentLayout.Brush(cell.Cell.Background), border, false, cell.Cell.Borders));
                                foreach (var decoration in cell.Decorations) target.Decorations.Add(decoration with { Bounds = decoration.Bounds.Translate(new Vector(x + cell.X, y)) });
                            }
                            y += height;
                        }
                        y += 12; break;
                }
            target.Lines.Sort((a, b) => a.Y.CompareTo(b.Y));
            return y;
        }

        private PageLayoutSnapshot Finish()
        {
            var pages = ImmutableArray.CreateBuilder<PageLayout>();
            var positions = new Vector[_pages.Count];
            var y = 0d;
            for (var row = 0; row < _pages.Count; row += options.PagesPerRow)
            {
                var x = 0d; var height = 0d;
                for (var i = row; i < Math.Min(_pages.Count, row + options.PagesPerRow); i++)
                {
                    var page = _pages[i]; var shift = positions[i] = new Vector(x, y);
                    pages.Add(new(i, page.Section.Id, page.Number, NumberText(page.Number, page.Section.PageNumberFormat),
                        new Rect(x, y, page.Width, page.Height), page.Body.Translate(shift),
                        page.Columns.Select(c => c.WithHeight(options.Draft ? Math.Max(0, page.Height - page.Settings.Margins.Top - page.Settings.Margins.Bottom) : c.Height).Translate(shift)).ToImmutableArray(), page.Settings, page.Blank));
                    x += page.Width + options.PageGap; height = Math.Max(height, page.Height);
                }
                y += height + options.PageGap;
            }
            return new(document, pages.ToImmutable(), _fragments.Select(f => f with
                { Bounds = f.Bounds.Translate(positions[f.PageIndex]), Clip = f.Clip.Translate(positions[f.PageIndex]),
                    Origin = f.Origin + positions[f.PageIndex], Baseline = f.Baseline + positions[f.PageIndex].Y,
                    ColumnBounds = f.ColumnBounds.Translate(positions[f.PageIndex]) }).ToImmutableArray(),
                _decorations.Select(d => d.Decoration with { Bounds = d.Decoration.Bounds.Translate(positions[d.Page]),
                    Clip = d.Decoration.Clip?.Translate(positions[d.Page]) }).ToImmutableArray(),
                _cells.Select(c => c.Cell with { Bounds = c.Cell.Bounds.Translate(positions[c.Page]), Clip = c.Cell.Clip?.Translate(positions[c.Page]) }).ToImmutableArray(),
                engine._environment!.Font, engine._environment.Foreground, engine._environment.Fonts.Diagnostics);
        }
        public void Release() { foreach (var measurement in _held) measurement.Release(); _held.Clear(); _prior = null; }
    }

    private static string NumberText(int number, PageNumberFormat format)
    {
        if (format == PageNumberFormat.Decimal) return number.ToString(CultureInfo.InvariantCulture);
        string result = "";
        if (format is PageNumberFormat.UpperLetter or PageNumberFormat.LowerLetter)
            for (var value = number; value > 0; value = (value - 1) / 26) result = (char)('A' + (value - 1) % 26) + result;
        else
        {
            if (number > 3999) return number.ToString(CultureInfo.InvariantCulture);
            var values = new[] { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            var symbols = new[] { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            for (var i = 0; i < values.Length; i++) while (number >= values[i]) { result += symbols[i]; number -= values[i]; }
        }
        return format is PageNumberFormat.LowerLetter or PageNumberFormat.LowerRoman ? result.ToLowerInvariant() : result;
    }
}




