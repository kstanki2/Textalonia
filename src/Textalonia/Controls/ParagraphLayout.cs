using System.Globalization;
using Avalonia.Media.TextFormatting;
using Textalonia.Model;

namespace Textalonia.Controls;

// Line-break checkpoints own no glyphs. Shaped windows can be discarded without
// losing the measured prefix or requiring a whole paragraph to be shaped again.
internal sealed class ParagraphLayout : IDisposable
{
    internal sealed class Page
    {
        public int Start, End, InputEnd, LineCount;
        public double Top, Height;
        public TextLayout? Layout;
        public string? Text;
        public TextStyle? RunStyle, DefaultStyle;
        public ParagraphStyle? ParagraphStyle;
        public long LastUse;
        public long Bytes => Layout is null ? 0 : 256L + (InputEnd - Start) * 32L;
    }

    internal const int WindowLength = 2048;
    private readonly Func<Paragraph, int, string, TextLayout> _shape;
    private readonly Action _disposed;
    private readonly List<Page> _pages = [];
    private readonly HashSet<Page> _resident = [];
    private List<Page> _tail = [];
    private Page? _spare;
    private int _tailShift, _suffixStart;
    public Paragraph Paragraph { get; private set; }
    public IEnumerable<Page> Pages => _resident;
    private int End => _pages.Count == 0 ? 0 : _pages[^1].End;
    private double MeasuredHeight => _pages.Count == 0 ? 0 : _pages[^1].Top + _pages[^1].Height;
    private bool Complete => _pages.Count > 0 && End == Paragraph.Length;
    private readonly double _width;
    public double Height => Math.Max(Paragraph.DefaultStyle.FontSize * 1.25,
        MeasuredHeight + (Complete ? 0 : Estimate(Paragraph.Length - End)));

    public ParagraphLayout(Paragraph paragraph, double width, Func<Paragraph, int, string, TextLayout> shape, Action disposed)
    { Paragraph = paragraph; _width = width; _shape = shape; _disposed = disposed; }

    private double Estimate(int length) => Math.Ceiling(length * Paragraph.DefaultStyle.FontSize * .52 / _width) * Paragraph.DefaultStyle.FontSize * 1.25;

    public void Update(Paragraph paragraph)
    {
        if (ReferenceEquals(paragraph, Paragraph)) return;
        if (_spare is not null) { Release(_spare); _spare = null; }
        foreach (var page in _tail) Release(page);
        _tail.Clear();
        var prefix = 0; var suffix = 0;
        if (paragraph.Style == Paragraph.Style && paragraph.DefaultStyle == Paragraph.DefaultStyle &&
            paragraph.Runs.Length == 1 && Paragraph.Runs.Length == 1 && paragraph.Runs[0].Style == Paragraph.Runs[0].Style &&
            !paragraph.Style.RightToLeft && !ParagraphText.For(paragraph).RequiresBidiContext && !ParagraphText.For(Paragraph).RequiresBidiContext)
        {
            var before = Paragraph.Runs[0].Storage; var after = paragraph.Runs[0].Storage;
            prefix = before.CommonPrefix(after);
            suffix = before.CommonSuffix(after, Math.Min(before.Length, after.Length) - prefix);
        }
        var keep = _pages.FindIndex(p => p.InputEnd >= prefix);
        if (keep < 0) keep = _pages.Count;
        _tailShift = paragraph.Length - Paragraph.Length;
        _suffixStart = paragraph.Length - suffix;
        _tail = _pages.GetRange(keep, _pages.Count - keep);
        _pages.RemoveRange(keep, _pages.Count - keep);
        Paragraph = paragraph;
    }

    public IEnumerable<Page> View(double top, double bottom)
    {
        // Unknown prefixes are discovered once, releasing glyphs as we pass them.
        // Exact distant targets may require this linear discovery; subsequent
        // visits use the compact checkpoints and shape only intersecting windows.
        while (!Complete && (_pages.Count == 0 || MeasuredHeight <= bottom))
        {
            var page = Append();
            if (page.Top + page.Height < top) Release(page, reuse: true);
        }
        for (var i = LowerBound(p => p.Top + p.Height < top); i < _pages.Count; i++)
        {
            var page = _pages[i];
            if (page.Top > bottom) break;
            Ensure(page);
            yield return page;
        }
    }

    public Page At(int offset)
    {
        while (!Complete && (_pages.Count == 0 || End <= offset))
        {
            var page = Append();
            if (page.End <= offset && !Complete) Release(page, reuse: true);
        }
        var target = _pages[Math.Min(LowerBound(p => p.End <= offset), _pages.Count - 1)];
        Ensure(target);
        return target;
    }

    private int LowerBound(Func<Page, bool> before)
    {
        var low = 0; var high = _pages.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (before(_pages[middle])) low = middle + 1; else high = middle;
        }
        return low;
    }

    private Page Append()
    {
        var start = End; var length = Math.Min(WindowLength, Paragraph.Length - start);
        // Unicode bidi resolution is paragraph scoped. Keep the exact Avalonia
        // path for RTL/embedding controls instead of treating a window as a new
        // bidi paragraph. The rope carries this conservative summary per subtree.
        if (Paragraph.Style.RightToLeft || ParagraphText.For(Paragraph).RequiresBidiContext)
            length = Paragraph.Length - start;
        TextLayout layout;
        string text;
        int count, end;
        while (true)
        {
            var inputEnd = start + length;
            text = ParagraphText.For(Paragraph).Read(start, length);
            if (inputEnd < Paragraph.Length)
            {
                // Start is a measured line/grapheme boundary. Drop the last
                // possibly incomplete grapheme of this bounded window. A single
                // enormous grapheme necessarily requires a larger context.
                var boundaries = StringInfo.ParseCombiningCharacters(text);
                var safeLength = boundaries[^1];
                if (safeLength == 0)
                { length = Math.Min(Paragraph.Length - start, length * 2); continue; }
                text = text[..safeLength]; inputEnd = start + safeLength;
            }
            length = inputEnd - start;
            layout = Shape(start, text);
            count = layout.TextLines.Count;
            if (inputEnd < Paragraph.Length) count--; // incomplete trailing line is lookahead only
            if (count > 0 || inputEnd == Paragraph.Length) break;
            layout.Dispose(); _disposed();
            length = Math.Min(Paragraph.Length - start, Math.Max(length + 1, length * 2));
        }
        end = start + (count == 0 ? 0 : layout.TextLines[count - 1].FirstTextSourceIndex + layout.TextLines[count - 1].Length);
        end = Math.Min(end, Paragraph.Length);
        var page = new Page { Start = start, End = end, InputEnd = start + length, Top = MeasuredHeight,
            Height = layout.TextLines.Take(count).Sum(l => l.Height), LineCount = count, Layout = layout, Text = text,
            RunStyle = Paragraph.Runs.Length == 1 ? Paragraph.Runs[0].Style : null,
            DefaultStyle = Paragraph.DefaultStyle, ParagraphStyle = Paragraph.Style };
        _pages.Add(page);
        _resident.Add(page);
        // Reflow stops when the new wrapping reaches an unchanged old boundary.
        // The suffix's line breaks, shapes, and heights are then reusable.
        if (end >= _suffixStart && _tail.Count > 0)
        {
            var match = _tail.FindIndex(p => p.End + _tailShift == end);
            if (match >= 0)
            {
                var shiftY = MeasuredHeight - (_tail[match].Top + _tail[match].Height);
                for (var i = 0; i <= match; i++) Release(_tail[i]);
                for (var i = match + 1; i < _tail.Count; i++)
                {
                    var next = _tail[i];
                    next.Start += _tailShift; next.End += _tailShift; next.InputEnd += _tailShift; next.Top += shiftY;
                    _pages.Add(next);
                }
                _tail.Clear();
            }
        }
        return page;
    }

    private TextLayout Shape(int start, string text)
    {
        if (_spare is { Layout: { } layout } && _spare.Text == text && Paragraph.Runs.Length == 1 &&
            _spare.RunStyle == Paragraph.Runs[0].Style && _spare.DefaultStyle == Paragraph.DefaultStyle && _spare.ParagraphStyle == Paragraph.Style)
        { _resident.Remove(_spare); _spare = null; return layout; }
        return _shape(Paragraph, start, text);
    }
    private void Ensure(Page page)
    {
        if (page.Layout is not null) return;
        page.Text = ParagraphText.For(Paragraph).Read(page.Start, page.InputEnd - page.Start);
        page.Layout = Shape(page.Start, page.Text);
        _resident.Add(page);
    }
    public void Release(Page page, bool reuse = false)
    {
        if (page.Layout is null) return;
        if (reuse && page.RunStyle is not null && page.InputEnd - page.Start <= WindowLength)
        {
            if (_spare is not null) Release(_spare);
            _spare = new Page { Layout = page.Layout, Text = page.Text, InputEnd = page.InputEnd - page.Start,
                RunStyle = page.RunStyle, DefaultStyle = page.DefaultStyle, ParagraphStyle = page.ParagraphStyle, LastUse = page.LastUse };
            _resident.Add(_spare);
        }
        else { page.Layout.Dispose(); _disposed(); }
        page.Layout = null; page.Text = null;
        _resident.Remove(page);
    }
    public void Dispose()
    {
        foreach (var page in Pages.ToArray()) Release(page);
        _pages.Clear(); _tail.Clear(); _spare = null;
    }
}
