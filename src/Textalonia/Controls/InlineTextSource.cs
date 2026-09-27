using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using System.Globalization;
using Textalonia.Model;

namespace Textalonia.Controls;

// Shaping owns only descriptor data. Images and controls belong to the surface.
internal sealed class InlineTextSource : ITextSource
{
    private readonly List<(int Start, int Length, TextRunProperties Properties, InlineDescriptor? Inline)> _runs = [];
    private readonly string _text;
    private readonly bool _customTabs;

    public InlineTextSource(Paragraph paragraph, int start, string text, FontFamily font, IBrush foreground,
        DocumentFontService? fonts = null, bool customTabs = false)
    {
        _text = text;
        _customTabs = customTabs;
        var offset = 0;
        foreach (var run in paragraph.Runs)
        {
            var from = Math.Max(start, offset);
            var to = Math.Min(start + text.Length, offset + run.Storage.Length);
            offset += run.Storage.Length;
            if (to <= from) continue;
            if ((run.Style.AllCaps || run.Style.SmallCaps) && run.Inline is null)
            {
                var upper = _text.Substring(from - start, to - from).ToUpper(Culture(run.Style));
                // A display projection must preserve storage positions used by selection and IME.
                if (upper.Length == to - from) _text = _text[..(from - start)] + upper + _text[(to - start)..];
            }
            var segment = from - start;
            while (segment < to - start)
            {
                var whitespace = char.IsWhiteSpace(_text[segment]);
                var small = run.Style.SmallCaps && !run.Style.AllCaps && IsLower(text, segment);
                var family = ScriptFamily(run.Style, text, segment);
                var end = segment + 1;
                while (end < to - start &&
                    (!run.Style.UnderlineWordsOnly || char.IsWhiteSpace(_text[end]) == whitespace) &&
                    (!run.Style.SmallCaps || IsLower(text, end) == small || IsContinuation(text, end)) &&
                    (ScriptFamily(run.Style, text, end) == family || IsContinuation(text, end))) end++;
                var segmentStyle = run.Style with { FontFamily = family };
                if (small) segmentStyle = segmentStyle with { FontSize = run.Style.FontSize * .8, SmallCaps = false };
                var runFont = fonts?.Resolve(segmentStyle) ?? font;
                if (fonts is not null) segmentStyle = segmentStyle with { FontFamily = null };
                _runs.Add((segment, end - segment,
                    Properties(segmentStyle, runFont, foreground, whitespace && run.Style.UnderlineWordsOnly), run.Inline));
                segment = end;
            }
        }
    }

    private static bool IsLower(string text, int index) =>
        System.Text.Rune.TryGetRuneAt(text, index, out var rune) && System.Text.Rune.IsLower(rune);

    private static bool IsContinuation(string text, int index)
    {
        if (char.IsLowSurrogate(text[index])) return true;
        if (!System.Text.Rune.TryGetRuneAt(text, index, out var rune)) return false;
        return rune.Value == 0x200D || System.Text.Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    private static string? ScriptFamily(TextStyle style, string text, int index)
    {
        if (style.EastAsianFontFamily is null && style.ComplexScriptFontFamily is null) return style.FontFamily;
        if (index > 0 && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1])) index--;
        var scalar = System.Text.Rune.TryGetRuneAt(text, index, out var rune) ? rune.Value : text[index];
        if (scalar is >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or
            >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x323AF)
            return style.EastAsianFontFamily ?? style.FontFamily;
        if (scalar is >= 0x0590 and <= 0x1CFF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF or
            >= 0x1E800 and <= 0x1EEFF)
            return style.ComplexScriptFontFamily ?? style.FontFamily;
        return style.FontFamily;
    }

    internal static CultureInfo Culture(TextStyle style)
    {
        try { return style.Language is null ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(style.Language); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    internal static TextRunProperties Properties(TextStyle style, FontFamily font, IBrush foreground, bool suppressUnderline = false)
    {
        TextDecorationCollection? decorations = null;
        var underline = style.UnderlineKind != UnderlineKind.None ? style.UnderlineKind :
            style.Underline || style.Hyperlink is not null ? UnderlineKind.Single : UnderlineKind.None;
        if (!suppressUnderline && underline is not (UnderlineKind.None or UnderlineKind.Wave))
        {
            Add(TextDecorationLocation.Underline, 0, underline == UnderlineKind.Thick ? 2 : 1);
            if (underline == UnderlineKind.Double) Add(TextDecorationLocation.Underline, 3);
        }
        var strike = style.StrikeKind != StrikeKind.None ? style.StrikeKind : style.Strikethrough ? StrikeKind.Single : StrikeKind.None;
        if (strike != StrikeKind.None)
        {
            Add(TextDecorationLocation.Strikethrough, 0);
            if (strike == StrikeKind.Double) Add(TextDecorationLocation.Strikethrough, 3);
        }
        void Add(TextDecorationLocation location, double offset, double thickness = 1)
        {
            var decoration = new TextDecoration { Location = location, StrokeThickness = thickness,
                StrokeThicknessUnit = TextDecorationUnit.Pixel, StrokeOffset = offset, StrokeOffsetUnit = TextDecorationUnit.Pixel };
            if (location == TextDecorationLocation.Underline)
            {
                decoration.Stroke = DocumentLayout.Brush(style.UnderlineColor);
                if (underline == UnderlineKind.Dotted) decoration.StrokeDashArray = [1, 2];
                else if (underline == UnderlineKind.Dashed) decoration.StrokeDashArray = [4, 2];
            }
            (decorations ??= []).Add(decoration);
        }
        FontFeatureCollection? features = null;
        if (style.SmallCaps) (features ??= []).Add(new FontFeature { Tag = "smcp", Value = 1 });
        if (style.KerningThreshold is { } threshold)
            (features ??= []).Add(new FontFeature { Tag = "kern", Value = style.FontSize >= threshold ? 1 : 0 });
        return new GenericTextRunProperties(DocumentLayout.Typeface(style, font),
            style.FontSize * (style.Baseline == Baseline.Normal ? 1 : .75), decorations,
            DocumentLayout.Brush(style.Foreground) ?? (style.Hyperlink is null ? foreground : Brushes.RoyalBlue),
            DocumentLayout.Brush(style.Background), style.Baseline switch
            {
                Baseline.Subscript => BaselineAlignment.Subscript,
                Baseline.Superscript => BaselineAlignment.Superscript,
                _ => BaselineAlignment.Baseline
            }, Culture(style), features);
    }

    public TextRun? GetTextRun(int textSourceIndex)
    {
        if (textSourceIndex >= _text.Length) return null;
        foreach (var run in _runs)
        {
            if (textSourceIndex < run.Start || textSourceIndex >= run.Start + run.Length) continue;
            if (run.Inline is { } inline) return new InlineObjectRun(inline, run.Properties);
            var length = run.Start + run.Length - textSourceIndex;
            if (_customTabs)
            {
                if (_text[textSourceIndex] == '\t') return new TabTextRun(run.Properties);
                if (_text[textSourceIndex] is '\u2028' or '\n' or '\r') return new TextEndOfLine(1);
                var special = _text.AsSpan(textSourceIndex, length).IndexOfAny('\t', '\u2028', '\n');
                if (special > 0) length = special;
            }
            return new TextCharacters(_text.AsMemory(textSourceIndex, length), run.Properties);
        }
        return null;
    }
}

internal sealed class InlineObjectRun : DrawableTextRun
{
    private readonly TextRunProperties _properties;
    private readonly Size _size;
    private readonly double _baseline;
    public InlineDescriptor Descriptor { get; }

    public InlineObjectRun(InlineDescriptor descriptor, TextRunProperties properties)
    {
        Descriptor = descriptor;
        _properties = properties;
        if (descriptor.Payload is MergeFieldInlinePayload)
        {
            using var label = FieldLabel();
            _size = new(Math.Max(12, label.WidthIncludingTrailingWhitespace + 8), label.Height + 4);
            _baseline = label.Baseline + 2;
        }
        else
        {
            _size = new(descriptor.Width, descriptor.Height);
            _baseline = descriptor.Height;
        }
    }

    public override int Length => 1;
    public override ReadOnlyMemory<char> Text => "\uFFFC".AsMemory();
    public override TextRunProperties Properties => _properties;
    public override Size Size => _size;
    public override double Baseline => _baseline;

    // Field labels use their run typography and measured geometry. Long/multiline previews
    // are a single bounded chip; final merged text uses normal wrapping and soft breaks.
    private TextLayout FieldLabel() => new(
        Descriptor.AltText.Length == 0 ? "\u200B" : Descriptor.AltText.Replace('\r', ' ').Replace('\n', ' ').Replace('\u2028', ' '),
        _properties.Typeface, _properties.FontRenderingEmSize, _properties.ForegroundBrush,
        textDecorations: _properties.TextDecorations, maxWidth: 600, maxLines: 1,
        textTrimming: TextTrimming.CharacterEllipsis);

    public override void Draw(DrawingContext context, Point origin)
    {
        var bounds = new Rect(origin, Size);
        using var clip = context.PushClip(bounds);
        if (Descriptor.Payload is MergeFieldInlinePayload)
        {
            using (context.PushOpacity(.12)) context.DrawRectangle(_properties.ForegroundBrush, null, bounds);
            using (context.PushOpacity(.4)) context.DrawRectangle(null, new Pen(_properties.ForegroundBrush, 1), bounds.Deflate(.5));
            using var fieldLabel = FieldLabel();
            fieldLabel.Draw(context, origin + new Vector(4, 2));
            return;
        }
        context.DrawRectangle(Brushes.WhiteSmoke, new Pen(Brushes.Gray, 1), bounds.Deflate(.5));
        using var label = new TextLayout(string.IsNullOrEmpty(Descriptor.AltText) ? "Object" : Descriptor.AltText,
            _properties.Typeface, Math.Min(12, _properties.FontRenderingEmSize), _properties.ForegroundBrush,
            maxWidth: Math.Max(1, Descriptor.Width - 6), maxLines: 1, textTrimming: TextTrimming.CharacterEllipsis);
        label.Draw(context, origin + new Vector(3, Math.Max(0, (Descriptor.Height - label.Height) / 2)));
    }
}

internal sealed record InlineVisual(InlineDescriptor Descriptor, int Position, Rect Bounds, Rect? Clip);
