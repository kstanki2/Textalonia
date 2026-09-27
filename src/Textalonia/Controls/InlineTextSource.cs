using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Textalonia.Model;

namespace Textalonia.Controls;

// Shaping owns only descriptor data. Images and controls belong to the surface.
internal sealed class InlineTextSource : ITextSource
{
    private readonly List<(int Start, int Length, TextRunProperties Properties, InlineDescriptor? Inline)> _runs = [];
    private readonly string _text;

    public InlineTextSource(Paragraph paragraph, int start, string text, FontFamily font, IBrush foreground)
    {
        _text = text;
        var offset = 0;
        foreach (var run in paragraph.Runs)
        {
            var from = Math.Max(start, offset);
            var to = Math.Min(start + text.Length, offset + run.Storage.Length);
            offset += run.Storage.Length;
            if (to > from) _runs.Add((from - start, to - from, Properties(run.Style, font, foreground), run.Inline));
        }
    }

    internal static TextRunProperties Properties(TextStyle style, FontFamily font, IBrush foreground)
    {
        TextDecorationCollection? decorations = null;
        if (style.Underline || style.Hyperlink is not null)
            (decorations ??= []).Add(new TextDecoration { Location = TextDecorationLocation.Underline });
        if (style.Strikethrough)
            (decorations ??= []).Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
        return new GenericTextRunProperties(DocumentLayout.Typeface(style, font),
            style.FontSize * (style.Baseline == Baseline.Normal ? 1 : .75), decorations,
            DocumentLayout.Brush(style.Foreground) ?? (style.Hyperlink is null ? foreground : Brushes.RoyalBlue),
            DocumentLayout.Brush(style.Background), style.Baseline switch
            {
                Baseline.Subscript => BaselineAlignment.Subscript,
                Baseline.Superscript => BaselineAlignment.Superscript,
                _ => BaselineAlignment.Baseline
            });
    }

    public TextRun? GetTextRun(int textSourceIndex)
    {
        if (textSourceIndex >= _text.Length) return null;
        foreach (var run in _runs)
        {
            if (textSourceIndex < run.Start || textSourceIndex >= run.Start + run.Length) continue;
            return run.Inline is { } inline ? new InlineObjectRun(inline, run.Properties) :
                new TextCharacters(_text.AsMemory(textSourceIndex, run.Start + run.Length - textSourceIndex), run.Properties);
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
