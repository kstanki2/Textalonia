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

internal sealed class InlineObjectRun(InlineDescriptor descriptor, TextRunProperties properties) : DrawableTextRun
{
    public InlineDescriptor Descriptor { get; } = descriptor;
    public override int Length => 1;
    public override ReadOnlyMemory<char> Text => "\uFFFC".AsMemory();
    public override TextRunProperties Properties => properties;
    public override Size Size => new(Descriptor.Width, Descriptor.Height);
    public override double Baseline => Descriptor.Height;

    public override void Draw(DrawingContext context, Point origin)
    {
        var bounds = new Rect(origin, Size);
        using var clip = context.PushClip(bounds);
        context.DrawRectangle(Brushes.WhiteSmoke, new Pen(Brushes.Gray, 1), bounds.Deflate(.5));
        using var label = new TextLayout(string.IsNullOrEmpty(Descriptor.AltText) ? "Object" : Descriptor.AltText,
            properties.Typeface, Math.Min(12, properties.FontRenderingEmSize), properties.ForegroundBrush,
            maxWidth: Math.Max(1, Descriptor.Width - 6), maxLines: 1, textTrimming: TextTrimming.CharacterEllipsis);
        label.Draw(context, origin + new Vector(3, Math.Max(0, (Descriptor.Height - label.Height) / 2)));
    }
}

internal sealed record InlineVisual(InlineDescriptor Descriptor, int Position, Rect Bounds, Rect? Clip);
