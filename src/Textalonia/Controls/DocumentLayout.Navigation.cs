using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Textalonia.Controls;

// A logical UTF-16 insertion position can have two visual locations at a bidi
// boundary or wrap. Retain the shaped character edge and line, not a pixel X:
// resizing and reflow can then resolve the affinity against the current layout.
internal readonly record struct VisualCaret(int FirstCharacterIndex, int TrailingLength, int LineStart = -1)
{
    public int Position => FirstCharacterIndex + TrailingLength;
    public static VisualCaret Logical(int position) => new(position, 0);
}

internal sealed partial class DocumentLayout
{
    private sealed record CaretStop(VisualCaret Caret, double X, bool ArriveFromLeft, bool ZeroWidth = false);
    private sealed record VisualLine(int Start, int Next, int ParagraphStart, int ParagraphEnd,
        bool RightToLeft, bool EndsWithBreak, List<CaretStop> Stops)
    {
        public CaretStop Resolve(VisualCaret caret) =>
            Stops.FirstOrDefault(s => s.Caret.FirstCharacterIndex == caret.FirstCharacterIndex &&
                s.Caret.TrailingLength == caret.TrailingLength) ??
            Stops.FirstOrDefault(s => s.Caret.Position == caret.Position && s.Caret.TrailingLength == 0) ??
            Stops.FirstOrDefault(s => s.Caret.Position == caret.Position) ?? Stops[0];

        public VisualCaret Edge(bool right)
        {
            var candidates = Stops.Where(s => Math.Abs(s.X - (right ? Stops[^1].X : Stops[0].X)) < .01 && s.ArriveFromLeft == right);
            return (right != RightToLeft ? candidates.MaxBy(s => s.Caret.Position) : candidates.MinBy(s => s.Caret.Position))!.Caret;
        }
    }

    private VisualLine ReadLine(ParagraphVisual visual, TextLine line)
    {
        var start = line.FirstTextSourceIndex;
        var end = Math.Min(start + line.Length - line.NewLineLength, visual.Page.End - visual.Page.Start);
        var stops = new List<CaretStop>();
        var text = visual.Page.Text!;
        var elements = StringInfo.ParseCombiningCharacters(text.Substring(start, Math.Max(0, end - start)));
        for (var i = 0; i < elements.Length; i++)
        {
            var first = start + elements[i];
            var length = (i + 1 < elements.Length ? elements[i + 1] : end - start) - elements[i];
            // GetDistanceFromCharacterHit preserves the shaping engine's leading
            // and trailing edges, including RTL runs, ligatures and inline objects.
            var leading = line.GetDistanceFromCharacterHit(new CharacterHit(first, 0));
            var trailing = line.GetDistanceFromCharacterHit(new CharacterHit(first, length));
            stops.Add(new(new(visual.TextStart + first, 0, visual.TextStart + start), visual.Origin.X + leading, leading > trailing, Math.Abs(leading - trailing) < .01));
            stops.Add(new(new(visual.TextStart + first, length, visual.TextStart + start), visual.Origin.X + trailing, trailing >= leading, Math.Abs(leading - trailing) < .01));
        }
        if (stops.Count == 0)
        {
            var caret = new VisualCaret(visual.TextStart + start, 0, visual.TextStart + start);
            var x = visual.Origin.X + line.GetDistanceFromCharacterHit(new CharacterHit(start, 0));
            stops.Add(new(caret, x, false)); stops.Add(new(caret, x, true));
        }
        stops.Sort((left, right) => left.X.CompareTo(right.X));
        return new(visual.TextStart + start, visual.TextStart + line.FirstTextSourceIndex + line.Length,
            visual.Position.Start, visual.Position.End, visual.Position.Paragraph.Style.RightToLeft, line.NewLineLength > 0, stops);
    }

    private (ParagraphVisual Visual, int Index)? FindLine(VisualCaret caret)
    {
        var visual = At(caret.LineStart >= 0 && caret.LineStart <= caret.Position ? caret.LineStart : caret.Position);
        if (visual is null) return null;
        using var lease = visual.Acquire();
        var lines = lease.Layout.TextLines;
        var index = -1;
        for (var i = 0; i < visual.Page.LineCount; i++)
            if (visual.TextStart + lines[i].FirstTextSourceIndex == caret.LineStart &&
                caret.Position <= visual.TextStart + lines[i].FirstTextSourceIndex + lines[i].Length)
            { index = i; break; }
        if (index < 0)
        {
            // The recorded line might have disappeared after a reflow.
            if (caret.Position < visual.TextStart || caret.Position > visual.TextEnd)
                return FindLine(VisualCaret.Logical(caret.Position));
            index = Math.Clamp(lease.Layout.GetLineIndexFromCharacterIndex(caret.Position - visual.TextStart, false), 0, visual.Page.LineCount - 1);
        }
        return (visual, index);
    }

    private VisualLine? LineFor(VisualCaret caret)
    {
        if (FindLine(caret) is not { } found) return null;
        using var lease = found.Visual.Acquire();
        return ReadLine(found.Visual, lease.Layout.TextLines[found.Index]);
    }
    private VisualCaret HitTestLine(ParagraphVisual visual, Point point)
    {
        using var lease = visual.Acquire();
        var hit = lease.Layout.HitTestPoint(new Point(point.X - visual.Origin.X, point.Y - visual.Origin.Y));
        var position = visual.TextStart + Math.Clamp(hit.TextPosition, 0, visual.Page.End - visual.Page.Start);
        var top = visual.Origin.Y;
        var lineStart = visual.TextStart;
        for (var i = 0; i < visual.Page.LineCount; i++)
        {
            var line = lease.Layout.TextLines[i];
            lineStart = visual.TextStart + line.FirstTextSourceIndex;
            if (point.Y < top + line.Height) break;
            top += line.Height;
        }
        var caret = new VisualCaret(visual.TextStart + hit.CharacterHit.FirstCharacterIndex, hit.CharacterHit.TrailingLength, lineStart);
        return caret.Position == position ? caret : new(position, 0, lineStart);
    }
    internal Rect Caret(VisualCaret caret)
    {
        if (FindLine(caret) is not { } found) return new Rect(0, 0, 1.5, 20);
        var visual = found.Visual;
        using var lease = visual.Acquire();
        var line = lease.Layout.TextLines[found.Index];
        var hit = new CharacterHit(caret.FirstCharacterIndex - visual.TextStart, caret.TrailingLength);
        return new Rect(visual.Origin.X + line.GetDistanceFromCharacterHit(hit),
            visual.Origin.Y + lease.Layout.TextLines.Take(found.Index).Sum(previous => previous.Height), 1.5,
            Math.Max(line.Height, visual.Position.Paragraph.Style.LineHeight ?? visual.Position.Paragraph.DefaultStyle.FontSize * 1.1));
    }

    internal VisualCaret MoveCaret(VisualCaret caret, bool right)
    {
        var line = LineFor(caret);
        if (line is null) return caret;
        var current = line.Resolve(caret);
        // A soft break or directional control may occupy no horizontal space,
        // but its UTF-16 edge must still remain reachable by the keyboard.
        var zero = line.Stops.FirstOrDefault(s => s.ZeroWidth && s.Caret.TrailingLength > 0 &&
            Math.Abs(s.X - current.X) < .01 && (right != line.RightToLeft ?
                s.Caret.FirstCharacterIndex == current.Caret.Position : s.Caret.Position == current.Caret.Position));
        if (zero is not null) return right != line.RightToLeft ? zero.Caret : new(zero.Caret.FirstCharacterIndex, 0, line.Start);
        var candidates = line.Stops.Where(s => right ? s.X > current.X + .01 : s.X < current.X - .01);
        var next = right ? candidates.FirstOrDefault() : candidates.LastOrDefault();
        if (next is not null)
            return line.Stops.FirstOrDefault(s => Math.Abs(s.X - next.X) < .01 && s.ArriveFromLeft == right)?.Caret ?? next.Caret;
        var advance = right != line.RightToLeft;
        var position = advance ? line.Next : line.Start - 1;
        if (advance && position >= line.ParagraphEnd && !line.EndsWithBreak) position = line.ParagraphEnd + 1;
        if (position < 0 || position > (_index?.Length ?? 0)) return current.Caret;
        var adjacent = LineFor(VisualCaret.Logical(position));
        return adjacent?.Edge(!right) ?? current.Caret;
    }

    internal VisualCaret MoveWordCaret(VisualCaret caret, bool right)
    {
        while (LineFor(caret) is { } line)
        {
            var current = line.Resolve(caret);
            bool Boundary(CaretStop stop)
            {
                var position = stop.Caret.Position;
                return position == line.ParagraphStart || position == line.ParagraphEnd ||
                    position > 0 && position < _index!.Length &&
                    char.IsWhiteSpace(_index.CharAt(position - 1)) && !char.IsWhiteSpace(_index.CharAt(position));
            }
            var candidates = line.Stops.Where(s => (right ? s.X > current.X + .01 : s.X < current.X - .01) && Boundary(s));
            var target = right ? candidates.FirstOrDefault() : candidates.LastOrDefault();
            if (target is not null)
                return line.Stops.FirstOrDefault(s => Math.Abs(s.X - target.X) < .01 && s.ArriveFromLeft == right && Boundary(s))?.Caret ?? target.Caret;
            var edge = line.Edge(right);
            var adjacent = MoveCaret(edge, right);
            if (adjacent == edge) return edge;
            var adjacentLine = LineFor(adjacent)!;
            if (adjacent.Position == adjacentLine.ParagraphStart || adjacent.Position == adjacentLine.ParagraphEnd ||
                adjacent.Position > 0 && char.IsWhiteSpace(_index!.CharAt(adjacent.Position - 1)) &&
                !char.IsWhiteSpace(_index.CharAt(adjacent.Position))) return adjacent;
            caret = adjacent;
        }
        return caret;
    }
    internal VisualCaret LineBoundary(VisualCaret caret, bool end) => LineFor(caret)?.Edge(end) ?? caret;

    internal VisualCaret CollapseSelection(VisualCaret anchor, VisualCaret active, bool right)
    {
        var a = Caret(anchor); var b = Caret(active);
        var order = Math.Abs(a.Y - b.Y) > .1 ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
        return right == (order > 0) ? anchor : active;
    }
}
