using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Textalonia.Model;

namespace Textalonia.Controls;

// The first pass owns Unicode shaping, font fallback and bidi levels. The second
// pass wraps adjusted glyph advances, so drawing, caret geometry and selection all
// consume the same clusters instead of scaling a finished paragraph bitmap.
internal sealed class ShapingTextLayout : IDisposable
{
    private readonly TextLayout? _native;
    private readonly TextLayout? _shaped;
    private readonly TextLayout? _formatted;
    private readonly TextLayout? _trailing;
    private readonly List<TextLine> _lines = [];
    public IReadOnlyList<TextLine> TextLines => _native?.TextLines ?? _lines;
    public double Height => _native?.Height ?? _lines.Sum(l => l.Height);
    public double Width => _native?.Width ?? _lines.Select(l => l.Width).DefaultIfEmpty().Max();
    public double WidthIncludingTrailingWhitespace => _native?.WidthIncludingTrailingWhitespace ?? _lines.Select(l => l.WidthIncludingTrailingWhitespace).DefaultIfEmpty().Max();
    // Conservative retained-buffer estimate: original shape, adjusted shape, and
    // optional drawing copy. This is cache accounting, not a process-memory guarantee.
    internal int CacheByteMultiplier => _shaped is null ? 1 : 3;
    internal int ContextCharacters { get; }

    public double LetterSpacing { get; }
    public double LineHeight { get; }

    private ShapingTextLayout(TextLayout native, double letterSpacing, double lineHeight)
    { _native = native; LetterSpacing = letterSpacing; LineHeight = lineHeight; }

    private ShapingTextLayout(Paragraph paragraph, double width, FontFamily font, IBrush foreground,
        int start, string text, DocumentFontService? fonts, bool paragraphContext = false)
    {
        LetterSpacing = paragraph.Style.LetterSpacing;
        LineHeight = EffectiveLineHeight(paragraph.Style);
        var defaults = InlineTextSource.Properties(fonts is null ? paragraph.DefaultStyle : paragraph.DefaultStyle with { FontFamily = null }, fonts?.Resolve(paragraph.DefaultStyle) ?? font, foreground);
        var natural = new ParagraphProperties(paragraph.Style, defaults, TextWrapping.NoWrap, double.NaN);
        ContextCharacters = paragraphContext ? Math.Max(0, paragraph.Length - text.Length) : 0;
        _shaped = new TextLayout(new InlineTextSource(paragraph, paragraphContext ? 0 : start,
            paragraphContext ? ParagraphText.For(paragraph).Read(0, paragraph.Length) : text,
            font, foreground, fonts, true), natural);
        var tabWidth = paragraph.Style.DefaultTabWidth;
        if (tabWidth == 0)
        {
            using var automaticTab = new TextLayout("\t", defaults.Typeface, defaults.FontRenderingEmSize, defaults.ForegroundBrush);
            tabWidth = Math.Max(1, automaticTab.WidthIncludingTrailingWhitespace);
        }
        var source = new PreparedSource(_shaped, paragraph, paragraphContext ? 0 : start, tabWidth);
        var sourceOffset = paragraphContext ? start : 0;
        ITextSource formattedSource = sourceOffset == 0 ? source : new ShiftedSource(source, sourceOffset);
        var properties = new ParagraphProperties(paragraph.Style, defaults, TextWrapping.Wrap, LineHeight);
        var position = 0;
        var maxLines = start == 0 && paragraph.Style.FirstLineIndent != 0 ? 2 : int.MaxValue;
        if (!text.Contains('\t'))
        {
            source.BeginLine(sourceOffset);
            _formatted = new TextLayout(formattedSource, properties, maxWidth: width, maxLines: maxLines == int.MaxValue ? 0 : maxLines);
            foreach (var line in _formatted.TextLines) _lines.Add(new TypographyLine(line, paragraph.Style, false));
            return;
        }
        do
        {
            source.BeginLine(position + sourceOffset);
            // Native wrapping supplies justification and a lookahead line. Tab advances
            // are recalculated from the next physical line's origin on the next pass.
            // Retain only a copied first line, not each probe's entire shaped remainder.
            using var probe = new TextLayout(new ShiftedSource(source, position + sourceOffset), properties, maxWidth: width, maxLines: 2);
            if (probe.TextLines.Count == 0 || probe.TextLines[0].Length == 0) break;
            var lineSource = new LineSource(probe.TextLines[0], position);
            var line = TextFormatter.Current.FormatLine(lineSource, position, width,
                new ParagraphProperties(paragraph.Style, defaults, TextWrapping.NoWrap, LineHeight));
            if (line is null || line.Length == 0) break;
            _lines.Add(new TypographyLine(line, paragraph.Style));
            position += line.Length;
        } while ((position < text.Length || position == text.Length && text.EndsWith('\u2028')) && _lines.Count < maxLines);
        if (text.EndsWith('\u2028') && position >= text.Length && _lines.Count > 0 &&
            _lines.Count < maxLines && _lines[^1].Length > 0)
        {
            _trailing = new TextLayout("", defaults.Typeface, defaults.FontRenderingEmSize, defaults.ForegroundBrush,
                textAlignment: properties.TextAlignment, maxWidth: width, lineHeight: LineHeight, flowDirection: properties.FlowDirection);
            _lines.Add(new TypographyLine(_trailing.TextLines[0], paragraph.Style, false, text.Length));
        }
        if (_lines.Count == 0)
        {
            var empty = new TextLayout("", defaults.Typeface, defaults.FontRenderingEmSize, defaults.ForegroundBrush,
                lineHeight: LineHeight);
            _native = empty;
        }
    }

    public static ShapingTextLayout Create(Paragraph paragraph, double width, FontFamily font, IBrush foreground,
        int start = 0, string? text = null, DocumentFontService? fonts = null)
    {
        text ??= ParagraphText.For(paragraph).ToString();
        var style = paragraph.Style;
        var characterGrid = style.SnapToGrid && style.EastAsianGrid is { CharacterSpacing: > 0 };
        if ((!text.Contains('\t') || style.TabStops.IsEmpty && style.DefaultTabWidth == 0) && style.LineSpacingMode == LineSpacingMode.Natural &&
            !characterGrid && fonts?.HasEmbeddedFonts != true && !Extended(paragraph.DefaultStyle) && !paragraph.Runs.Any(r => Extended(r.Style)))
            return new(DocumentLayout.CreateNativeTextLayout(paragraph, width, font, foreground, start, text),
                style.LetterSpacing, style.LineHeight ?? double.NaN);
        width = Math.Max(16, width - (start == 0 ? paragraph.Style.FirstLineIndent : 0));
        var advanced = characterGrid || text.Contains('\t') && (!style.TabStops.IsEmpty || style.DefaultTabWidth > 0) || style.LineSpacingMode is LineSpacingMode.Multiple or LineSpacingMode.AtLeast ||
            paragraph.Runs.Any(r => r.Style.Tracking != 0 || r.Style.HorizontalScale != 1 || r.Style.BaselineOffset != 0 || r.Style.UnderlineKind == UnderlineKind.Wave);
        if (advanced) return new(paragraph, width, font, foreground, start, text, fonts);
        var defaults = InlineTextSource.Properties(fonts is null ? paragraph.DefaultStyle : paragraph.DefaultStyle with { FontFamily = null }, fonts?.Resolve(paragraph.DefaultStyle) ?? font, foreground);
        var lineHeight = EffectiveLineHeight(style);
        var native = new TextLayout(new InlineTextSource(paragraph, start, text, font, foreground, fonts),
            new ParagraphProperties(style, defaults, TextWrapping.Wrap, lineHeight), maxWidth: width,
            maxLines: start == 0 && style.FirstLineIndent != 0 ? 2 : 0);
        return new(native, style.LetterSpacing, lineHeight);
    }

    // Rewrap the prepared runs of the whole paragraph so a width change retains
    // the direction and contextual shaping of text preceding this continuation.
    internal static ShapingTextLayout CreateContinuation(Paragraph paragraph, double width, FontFamily font,
        IBrush foreground, int start, string text, DocumentFontService? fonts) =>
        new(paragraph, Math.Max(16, width), font, foreground, start, text, fonts, paragraphContext: true);

    private static bool Extended(TextStyle style) => style.EastAsianFontFamily is not null || style.ComplexScriptFontFamily is not null ||
        style.UnderlineKind != UnderlineKind.None || style.StrikeKind != StrikeKind.None ||
        style.UnderlineColor is not null || style.UnderlineWordsOnly || style.AllCaps || style.SmallCaps || style.Language is not null ||
        style.KerningThreshold is not null || style.Tracking != 0 || style.HorizontalScale != 1 || style.BaselineOffset != 0;

    internal static double EffectiveLineHeight(ParagraphStyle style) => style.LineSpacingMode switch
    { LineSpacingMode.Exact => style.LineSpacing, LineSpacingMode.Multiple or LineSpacingMode.AtLeast => double.NaN, _ => style.LineHeight ?? double.NaN };

    public int GetLineIndexFromCharacterIndex(int position, bool trailingEdge)
    {
        if (_native is not null) return _native.GetLineIndexFromCharacterIndex(position, trailingEdge);
        for (var i = 0; i < _lines.Count; i++)
            if (position < _lines[i].FirstTextSourceIndex + _lines[i].Length ||
                trailingEdge && position == _lines[i].FirstTextSourceIndex + _lines[i].Length) return i;
        return Math.Max(0, _lines.Count - 1);
    }
    internal readonly record struct HitResult(CharacterHit CharacterHit)
    { public int TextPosition => CharacterHit.FirstCharacterIndex + CharacterHit.TrailingLength; }
    public HitResult HitTestPoint(Point point)
    {
        if (_native is not null) return new(_native.HitTestPoint(point).CharacterHit);
        var y = 0d;
        foreach (var line in _lines)
        {
            y += line.Height;
            if (point.Y < y || ReferenceEquals(line, _lines[^1])) return new(line.GetCharacterHitFromDistance(point.X));
        }
        return new(default);
    }
    public Rect HitTestTextPosition(int position)
    {
        if (_native is not null) return _native.HitTestTextPosition(position);
        var index = GetLineIndexFromCharacterIndex(position, false);
        var line = _lines[index];
        return new(line.GetDistanceFromCharacterHit(new(position, 0)), _lines.Take(index).Sum(l => l.Height), 0, line.Height);
    }
    public IEnumerable<Rect> HitTestTextRange(int start, int length)
    {
        if (_native is not null) return _native.HitTestTextRange(start, length);
        var result = new List<Rect>(); var top = 0d;
        foreach (var line in _lines)
        {
            foreach (var bounds in line.GetTextBounds(start, length))
                result.Add(new(bounds.Rectangle.X, top, bounds.Rectangle.Width, line.Height));
            top += line.Height;
        }
        return result;
    }
    public void Draw(DrawingContext context, Point origin)
    { foreach (var line in TextLines) { line.Draw(context, origin); origin += new Vector(0, line.Height); } }
    public void Dispose()
    { _native?.Dispose(); foreach (var line in _lines) line.Dispose(); _formatted?.Dispose(); _trailing?.Dispose(); _shaped?.Dispose(); }

    private sealed class ParagraphProperties(ParagraphStyle style, TextRunProperties defaults, TextWrapping wrapping, double height) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => style.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => style.Alignment switch
        { ParagraphAlignment.Center => TextAlignment.Center, ParagraphAlignment.Right => TextAlignment.Right, ParagraphAlignment.Justify => TextAlignment.Justify, _ => TextAlignment.Left };
        public override double LineHeight => height;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => defaults;
        public override TextWrapping TextWrapping => wrapping;
        public override double Indent => 0;
        public override double LetterSpacing => style.LetterSpacing;
        public override double DefaultIncrementalTab => style.DefaultTabWidth > 0 ? style.DefaultTabWidth : base.DefaultIncrementalTab;
    }

    private static ShapedTextRun Rebase(ShapedTextRun source, int shift)
    {
        var sourceBuffer = source.ShapedBuffer;
        var buffer = new ShapedBuffer(sourceBuffer.Text, sourceBuffer.Length, sourceBuffer.GlyphTypeface,
            sourceBuffer.FontRenderingEmSize, sourceBuffer.BidiLevel);
        for (var i = 0; i < buffer.Length; i++)
        {
            var glyph = source.GlyphRun.GlyphInfos[i];
            buffer[i] = new(glyph.GlyphIndex, glyph.GlyphCluster + shift, glyph.GlyphAdvance, glyph.GlyphOffset);
        }
        return new(buffer, source.Properties);
    }

    private sealed class ShiftedSource(ITextSource source, int offset) : ITextSource
    {
        public TextRun? GetTextRun(int index)
        {
            var run = source.GetTextRun(index + offset);
            if (run is not ShapedTextRun shaped) return run;
            var result = Rebase(shaped, -offset);
            shaped.Dispose();
            return result;
        }
    }

    private sealed class LineSource : ITextSource
    {
        private readonly Dictionary<int, TextRun> _runs = [];
        public LineSource(TextLine line, int offset)
        {
            var seen = new HashSet<TextRun>(ReferenceEqualityComparer.Instance);
            if (line.Length > 0)
            foreach (var bounds in line.GetTextBounds(line.FirstTextSourceIndex, line.Length))
                foreach (var run in bounds.TextRunBounds)
                    if (seen.Add(run.TextRun))
                        _runs.Add(run.TextSourceCharacterIndex + offset,
                            run.TextRun is ShapedTextRun shaped ? Rebase(shaped, offset) : run.TextRun);
            if (line.NewLineLength > 0)
                foreach (var end in line.TextRuns.OfType<TextEndOfLine>())
                    _runs.TryAdd(offset + line.Length - line.NewLineLength, end);
        }
        public TextRun? GetTextRun(int index) => _runs.GetValueOrDefault(index);
    }

    private sealed class PreparedSource : ITextSource
    {
        private readonly List<(int Start, TextRun Run)> _runs = [];
        private readonly Paragraph _paragraph;
        private readonly int _start;
        private readonly int _end;
        private readonly double _tabWidth;
        private readonly Dictionary<int, double> _advances = [];
        public PreparedSource(TextLayout shaped, Paragraph paragraph, int start, double tabWidth)
        {
            _paragraph = paragraph; _start = start; _tabWidth = tabWidth;
            _end = shaped.TextLines.Sum(l => l.Length);
            foreach (var line in shaped.TextLines)
            {
                var seen = new HashSet<TextRun>(ReferenceEqualityComparer.Instance);
                if (line.Length > 0)
                foreach (var bounds in line.GetTextBounds(line.FirstTextSourceIndex, line.Length))
                    foreach (var run in bounds.TextRunBounds)
                        if (seen.Add(run.TextRun)) _runs.Add((run.TextSourceCharacterIndex, run.TextRun));
                if (line.NewLineLength > 0 && line.TextRuns.LastOrDefault() is TextEndOfLine end && seen.Add(end))
                    _runs.Add((line.FirstTextSourceIndex + line.Length - line.NewLineLength, end));
            }
            _runs.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        public void BeginLine(int position) { _advances.Clear(); _advances[position] = 0; }
        public TextRun? GetTextRun(int index)
        {
            if (index == _end) return new TextEndOfParagraph(0);
            foreach (var entry in _runs)
            {
                if (index < entry.Start || index >= entry.Start + entry.Run.Length) continue;
                var offset = index - entry.Start;
                var advance = _advances.GetValueOrDefault(index);
                var style = _paragraph.StyleAt(_start + index);
                TextRun run;
                if (entry.Run is TabTextRun tab)
                {
                    var stop = _paragraph.Style.TabStops.FirstOrDefault(s => s.Position > advance + .01);
                    var position = stop?.Position ?? (Math.Floor(advance / _tabWidth) + 1) * _tabWidth;
                    var following = FollowingWidth(index + 1, stop?.DecimalCharacter ?? '.');
                    var adjustment = stop?.Alignment switch
                    { TabAlignment.Center => following.Width / 2, TabAlignment.Right => following.Width, TabAlignment.Decimal => following.Decimal, _ => 0 };
                    run = new TabTextRun(tab.Properties, Math.Max(0, position - advance - adjustment), stop?.Leader ?? TabLeader.None);
                }
                else if (entry.Run is ShapedTextRun shaped)
                    run = new ShapedTextRun(Clone(shaped.ShapedBuffer, offset, style, gridPitch: GridPitch), new TypographyProperties(shaped.Properties, style));
                else run = entry.Run;
                if (run is DrawableTextRun drawable) _advances[index + run.Length] = advance + drawable.Size.Width;
                return run;
            }
            return null;
        }
        private (double Width, double Decimal) FollowingWidth(int position, char decimalCharacter)
        {
            var width = 0d; double? decimalWidth = null;
            foreach (var entry in _runs)
            {
                if (entry.Start + entry.Run.Length <= position) continue;
                if (entry.Run is TabTextRun or TextEndOfLine || entry.Run.Text.Span.IndexOf('\u2028') >= 0) break;
                if (entry.Run is ShapedTextRun shaped)
                {
                    var style = _paragraph.StyleAt(_start + entry.Start);
                    using var adjusted = new ShapedTextRun(Clone(shaped.ShapedBuffer, 0, style, gridPitch: GridPitch), shaped.Properties);
                    var decimalIndex = shaped.Text.Span.IndexOf(decimalCharacter);
                    if (decimalWidth is null && decimalIndex >= 0)
                        decimalWidth = width + adjusted.GlyphRun.GetDistanceFromCharacterHit(new(decimalIndex, 0));
                    width += adjusted.Size.Width;
                }
                else if (entry.Run is DrawableTextRun drawable) width += drawable.Size.Width;
            }
            return (width, decimalWidth ?? width);
        }
        private double GridPitch => _paragraph.Style.SnapToGrid ? _paragraph.Style.EastAsianGrid?.CharacterSpacing ?? 0 : 0;
    }

    internal static ShapedBuffer Clone(ShapedBuffer source, int offset, TextStyle style, bool forDrawing = false, double gridPitch = 0)
    {
        var copy = new ShapedBuffer(source.Text, source.Length, source.GlyphTypeface, source.FontRenderingEmSize, source.BidiLevel);
        for (var i = 0; i < source.Length; i++)
        {
            var glyph = source[i];
            var advance = forDrawing ? glyph.GlyphAdvance / style.HorizontalScale :
                Math.Max(0, glyph.GlyphAdvance * style.HorizontalScale + (i + 1 == source.Length || source[i + 1].GlyphCluster != glyph.GlyphCluster ? style.Tracking : 0));
            copy[i] = new(glyph.GlyphIndex, glyph.GlyphCluster, advance,
                forDrawing ? new(glyph.GlyphOffset.X / style.HorizontalScale, glyph.GlyphOffset.Y + style.BaselineOffset) :
                new(glyph.GlyphOffset.X * style.HorizontalScale, glyph.GlyphOffset.Y - style.BaselineOffset));
        }
        if (!forDrawing && gridPitch > 0)
        {
            // Snap whole shaped clusters, preserving ligatures, combining marks and bidi caret edges.
            // The last glyph carries the added advance; drawing and wrapping use this same buffer.
            var advance = 0d;
            for (var i = 0; i < copy.Length; i++)
            {
                var glyph = copy[i]; advance += glyph.GlyphAdvance;
                if (i + 1 < copy.Length && copy[i + 1].GlyphCluster == glyph.GlyphCluster) continue;
                var extra = Math.Ceiling(advance / gridPitch) * gridPitch - advance;
                copy[i] = new(glyph.GlyphIndex, glyph.GlyphCluster, glyph.GlyphAdvance + extra, glyph.GlyphOffset);
                advance = 0;
            }
        }
        if (offset == 0) return copy;
        var split = copy.Split(offset);
        split.First?.Dispose();
        copy.Dispose();
        return split.Second!;
    }
}

internal sealed class TypographyProperties(TextRunProperties source, TextStyle style) : TextRunProperties
{
    public TextStyle Style { get; } = style;
    public override Typeface Typeface => source.Typeface;
    public override double FontRenderingEmSize => source.FontRenderingEmSize;
    public override TextDecorationCollection? TextDecorations => source.TextDecorations;
    public override IBrush? ForegroundBrush => source.ForegroundBrush;
    public override IBrush? BackgroundBrush => source.BackgroundBrush;
    public override System.Globalization.CultureInfo? CultureInfo => source.CultureInfo;
    public override FontFeatureCollection? FontFeatures => source.FontFeatures;
    public override BaselineAlignment BaselineAlignment => source.BaselineAlignment;
}
