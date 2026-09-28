using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Textalonia.Model;

namespace Textalonia.Controls;

internal sealed class TypographyLine : TextLine
{
    private readonly TextLine _line;
    private readonly List<(DrawableTextRun Run, Rect Bounds, double Scale, bool Owned)> _drawing = [];
    private readonly double _rise;
    private readonly bool _ownsLine;
    private readonly int _indexOffset;
    public TypographyLine(TextLine line, ParagraphStyle style, bool ownsLine = true, int indexOffset = 0)
    {
        _line = line; _ownsLine = ownsLine; _indexOffset = indexOffset;
        var fall = 0d;
        foreach (var run in line.TextRuns)
            if (run.Properties is TypographyProperties props)
            { _rise = Math.Max(_rise, props.Style.BaselineOffset); fall = Math.Max(fall, -props.Style.BaselineOffset); }
        var natural = line.Height + _rise + fall;
        Height = style.LineSpacingMode switch
        {
            LineSpacingMode.Multiple => natural * style.LineSpacing,
            LineSpacingMode.AtLeast => Math.Max(natural, style.LineSpacing),
            _ => line.Height
        };
        if (style.LineSpacingMode == LineSpacingMode.Natural && style.LineHeight is null) Height = natural;
        var seen = new HashSet<TextRun>(ReferenceEqualityComparer.Instance);
        if (line.Length > 0)
        foreach (var bounds in line.GetTextBounds(line.FirstTextSourceIndex, line.Length))
            foreach (var run in bounds.TextRunBounds)
            {
                if (run.TextRun is not DrawableTextRun drawable || !seen.Add(drawable)) continue;
                var scale = (drawable.Properties as TypographyProperties)?.Style.HorizontalScale ?? 1;
                if (drawable is ShapedTextRun shaped && drawable.Properties is TypographyProperties properties &&
                    (scale != 1 || properties.Style.BaselineOffset != 0))
                    _drawing.Add((new ShapedTextRun(ShapingTextLayout.Clone(shaped.ShapedBuffer, 0, properties.Style, true), shaped.Properties), run.Rectangle, scale, true));
                else _drawing.Add((drawable, run.Rectangle, 1, false));
            }
    }
    public override IReadOnlyList<TextRun> TextRuns => _line.TextRuns;
    public override int FirstTextSourceIndex => _line.FirstTextSourceIndex + _indexOffset;
    public override int Length => _line.Length;
    public override TextLineBreak? TextLineBreak => _line.TextLineBreak;
    public override double Baseline => _line.Baseline + _rise;
    public override double Extent => Math.Max(_line.Extent, Height);
    public override bool HasCollapsed => _line.HasCollapsed;
    public override bool HasOverflowed => _line.HasOverflowed;
    public override double Height { get; }
    public override int NewLineLength => _line.NewLineLength;
    public override double OverhangAfter => _line.OverhangAfter;
    public override double OverhangLeading => _line.OverhangLeading;
    public override double OverhangTrailing => _line.OverhangTrailing;
    public override double Start => _line.Start;
    public override int TrailingWhitespaceLength => _line.TrailingWhitespaceLength;
    public override double Width => _line.Width;
    public override double WidthIncludingTrailingWhitespace => _line.WidthIncludingTrailingWhitespace;
    public override void Draw(DrawingContext context, Point origin)
    {
        foreach (var item in _drawing)
        {
            var top = origin.Y + GetDrawTop(item.Run);
            using (context.PushTransform(new Matrix(item.Scale, 0, 0, 1, origin.X + item.Bounds.X, top)))
                item.Run.Draw(context, default);
            if (item.Run.Properties is TypographyProperties { Style.UnderlineKind: UnderlineKind.Wave } properties)
            {
                if (properties.Style.UnderlineWordsOnly && item.Run.Text.Span.Trim().IsEmpty) continue;
                var brush = DocumentLayout.Brush(properties.Style.UnderlineColor) ?? properties.ForegroundBrush;
                var pen = new Pen(brush, 1);
                var y = origin.Y + Baseline + 2 - properties.Style.BaselineOffset;
                for (var x = item.Bounds.X; x < item.Bounds.Right; x += 4)
                {
                    var middle = Math.Min(item.Bounds.Right, x + 2);
                    var end = Math.Min(item.Bounds.Right, x + 4);
                    context.DrawLine(pen, new(origin.X + x, y), new(origin.X + middle, y + 1.5));
                    context.DrawLine(pen, new(origin.X + middle, y + 1.5), new(origin.X + end, y));
                }
            }
        }
    }
    private double GetDrawTop(DrawableTextRun run) => (run.Properties?.BaselineAlignment switch
    {
        BaselineAlignment.Subscript => _line.Height - run.Size.Height + _rise,
        BaselineAlignment.Superscript => _rise,
        _ => Baseline - run.Baseline
    }) - ((run.Properties as TypographyProperties)?.Style.BaselineOffset ?? 0);

    // Inline overlays must follow the exact drawing origin, including rise and baseline alignment.
    internal Rect GetInlineBounds(InlineObjectRun run, Point origin)
    {
        var item = _drawing.First(item => ReferenceEquals(item.Run, run));
        return new(new Point(origin.X + item.Bounds.X, origin.Y + GetDrawTop(run)), run.Size);
    }

    public override TextLine Collapse(params TextCollapsingProperties?[] collapsingProperties) => _line.Collapse(collapsingProperties);
    public override void Justify(JustificationProperties justificationProperties) => _line.Justify(justificationProperties);
    public override CharacterHit GetCharacterHitFromDistance(double distance) => Shift(_line.GetCharacterHitFromDistance(distance), _indexOffset);
    public override double GetDistanceFromCharacterHit(CharacterHit characterHit) => _line.GetDistanceFromCharacterHit(Shift(characterHit, -_indexOffset));
    public override CharacterHit GetNextCaretCharacterHit(CharacterHit characterHit) => Shift(_line.GetNextCaretCharacterHit(Shift(characterHit, -_indexOffset)), _indexOffset);
    public override CharacterHit GetPreviousCaretCharacterHit(CharacterHit characterHit) => Shift(_line.GetPreviousCaretCharacterHit(Shift(characterHit, -_indexOffset)), _indexOffset);
    public override CharacterHit GetBackspaceCaretCharacterHit(CharacterHit characterHit) => Shift(_line.GetBackspaceCaretCharacterHit(Shift(characterHit, -_indexOffset)), _indexOffset);
    public override IReadOnlyList<TextBounds> GetTextBounds(int firstTextSourceCharacterIndex, int textLength) => _line.GetTextBounds(firstTextSourceCharacterIndex, textLength);
    private static CharacterHit Shift(CharacterHit hit, int offset) => new(hit.FirstCharacterIndex + offset, hit.TrailingLength);
    public override void Dispose()
    {
        foreach (var item in _drawing) if (item.Owned && item.Run is ShapedTextRun shaped) shaped.Dispose();
        if (_ownsLine) _line.Dispose();
    }
}

internal sealed class TabTextRun(TextRunProperties properties, double width = 0, TabLeader leader = TabLeader.None) : DrawableTextRun
{
    public override int Length => 1;
    // Treat the resolved tab as an atomic spacer, so native justification does not count it as an expandable word space.
    public override ReadOnlyMemory<char> Text => "\u2060".AsMemory();
    public override TextRunProperties Properties => properties;
    public override Size Size => new(width, properties.FontRenderingEmSize);
    public override double Baseline => properties.FontRenderingEmSize * .8;
    public override void Draw(DrawingContext context, Point origin)
    {
        if (leader == TabLeader.None || width <= 0) return;
        var y = origin.Y + Baseline;
        var pen = new Pen(properties.ForegroundBrush, 1);
        if (leader == TabLeader.Line) context.DrawLine(pen, new(origin.X, y), new(origin.X + width, y));
        else
        {
            var step = leader == TabLeader.Dots ? 4d : 8d;
            for (var x = 2d; x < width - 1; x += step)
                context.DrawLine(pen, new(origin.X + x, y), new(origin.X + Math.Min(width - 1, x + (leader == TabLeader.Dots ? 1 : 4)), y));
        }
    }
}
