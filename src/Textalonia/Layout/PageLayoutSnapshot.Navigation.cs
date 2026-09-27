using Avalonia.Media;
using System.Globalization;
using Avalonia.Media.TextFormatting;
using Textalonia.Controls;

namespace Textalonia.Layout;

public sealed partial class PageLayoutSnapshot
{
    /// <summary>The caret's unscaled horizontal distance from its current column's leading edge.</summary>
    internal double CaretColumnX(VisualCaret caret) => FindLine(caret) is { } fragment ? Caret(caret).X - fragment.ColumnBounds.Left : 0;

    internal VisualCaret MoveVerticalCaret(VisualCaret caret, bool down, double preferredX)
    {
        var current = FindLine(caret);
        if (current is null) return caret;
        var groups = Fragments.Where(f => f.Bounds.Intersect(f.Clip).Height > 0)
            .GroupBy(f => (f.PageIndex, f.SectionId, f.ColumnIndex)).ToArray();
        var groupIndex = Array.FindIndex(groups, g => g.Key == (current.PageIndex, current.SectionId, current.ColumnIndex));
        if (groupIndex < 0) return caret;
        var candidates = groups[groupIndex].Where(f => down ? f.Bounds.Top > current.Bounds.Top + .01 : f.Bounds.Top < current.Bounds.Top - .01).ToArray();
        if (candidates.Length == 0)
        {
            groupIndex += down ? 1 : -1;
            if (groupIndex < 0 || groupIndex >= groups.Length) return caret;
            candidates = groups[groupIndex].ToArray();
        }
        var y = down ? candidates.Min(f => f.Bounds.Top) : candidates.Max(f => f.Bounds.Top);
        var target = candidates.Where(f => Math.Abs(f.Bounds.Top - y) < .01).MinBy(f => HorizontalDistance(f, preferredX))!;
        return HitTestFragment(target, target.ColumnBounds.Left + preferredX);
    }

    internal VisualCaret MovePageCaret(VisualCaret caret, bool down, double preferredX)
    {
        var current = FindLine(caret);
        if (current is null) return caret;
        var page = current.PageIndex + (down ? 1 : -1);
        while (page >= 0 && page < Pages.Length && !Fragments.Any(f => f.PageIndex == page && f.Bounds.Intersect(f.Clip).Height > 0)) page += down ? 1 : -1;
        if (page < 0 || page >= Pages.Length) return caret;
        var y = current.Bounds.Top - Pages[current.PageIndex].Bounds.Top + Pages[page].Bounds.Top;
        var candidates = Fragments.Where(f => f.PageIndex == page && f.Bounds.Intersect(f.Clip).Height > 0).ToArray();
        var column = candidates.Select(f => f.ColumnIndex).Distinct().MinBy(c => Math.Abs(c - current.ColumnIndex));
        var target = candidates.Where(f => f.ColumnIndex == column).MinBy(f => Math.Abs(f.Bounds.Top - y) * 100000 + HorizontalDistance(f, preferredX));
        return target is null ? caret : HitTestFragment(target, target.ColumnBounds.Left + preferredX);
    }

    private static double HorizontalDistance(LineFragment fragment, double preferredX)
    {
        var x = fragment.ColumnBounds.Left + preferredX;
        return Math.Max(Math.Max(fragment.Bounds.Left - x, 0), x - fragment.Bounds.Right);
    }

    private sealed record Stop(VisualCaret Caret, double X, bool ArriveFromLeft, bool ZeroWidth);
    private sealed record NavigationLine(LineFragment Fragment, bool RightToLeft, bool EndsWithBreak, List<Stop> Stops)
    {
        public Stop Resolve(VisualCaret caret) => Stops.FirstOrDefault(s => s.Caret.FirstCharacterIndex == caret.FirstCharacterIndex &&
            s.Caret.TrailingLength == caret.TrailingLength) ?? Stops.FirstOrDefault(s => s.Caret.Position == caret.Position && s.Caret.TrailingLength == 0) ??
            Stops.FirstOrDefault(s => s.Caret.Position == caret.Position) ?? Stops[0];
        public VisualCaret Edge(bool right)
        {
            var candidates = Stops.Where(s => Math.Abs(s.X - (right ? Stops[^1].X : Stops[0].X)) < .01 && s.ArriveFromLeft == right).ToArray();
            return (candidates.Length == 0 ? right ? Stops[^1] : Stops[0] :
                right != RightToLeft ? candidates.MaxBy(s => s.Caret.Position)! : candidates.MinBy(s => s.Caret.Position)!).Caret;
        }
    }
    private NavigationLine? ReadLine(VisualCaret caret)
    {
        var fragment = FindLine(caret);
        if (fragment is null) return null;
        using var lease = fragment.Acquire();
        var line = lease.Layout.TextLines[fragment.Line.Index];
        var start = Math.Max(line.FirstTextSourceIndex, fragment.TextStart - fragment.SourceStart);
        var end = Math.Min(line.FirstTextSourceIndex + line.Length - line.NewLineLength, fragment.TextEnd - fragment.SourceStart);
        var stops = new List<Stop>();
        var text = fragment.Line.Window.Text!;
        var elements = StringInfo.ParseCombiningCharacters(text.Substring(start, Math.Max(0, end - start)));
        for (var i = 0; i < elements.Length; i++)
        {
            var first = start + elements[i];
            var length = (i + 1 < elements.Length ? elements[i + 1] : end - start) - elements[i];
            var leading = line.GetDistanceFromCharacterHit(new CharacterHit(first, 0));
            var trailing = line.GetDistanceFromCharacterHit(new CharacterHit(first, length));
            stops.Add(new(new(fragment.SourceStart + first, 0, fragment.TextStart), fragment.Origin.X + leading, leading > trailing, Math.Abs(leading - trailing) < .01));
            stops.Add(new(new(fragment.SourceStart + first, length, fragment.TextStart), fragment.Origin.X + trailing, trailing >= leading, Math.Abs(leading - trailing) < .01));
        }
        if (stops.Count == 0)
        {
            var position = new VisualCaret(fragment.TextStart, 0, fragment.TextStart);
            var x = fragment.Origin.X + line.GetDistanceFromCharacterHit(new CharacterHit(start, 0));
            stops.Add(new(position, x, false, false)); stops.Add(new(position, x, true, false));
        }
        stops.Sort((a, b) => a.X.CompareTo(b.X));
        return new(fragment, fragment.Measurement.Paragraph.Style.RightToLeft, line.NewLineLength > 0, stops);
    }

    internal VisualCaret MoveCaret(VisualCaret caret, bool right)
    {
        var line = ReadLine(caret);
        if (line is null) return caret;
        var current = line.Resolve(caret);
        var zero = line.Stops.FirstOrDefault(s => s.ZeroWidth && s.Caret.TrailingLength > 0 && Math.Abs(s.X - current.X) < .01 &&
            (right != line.RightToLeft ? s.Caret.FirstCharacterIndex == current.Caret.Position : s.Caret.Position == current.Caret.Position));
        if (zero is not null) return right != line.RightToLeft ? zero.Caret : new(zero.Caret.FirstCharacterIndex, 0, line.Fragment.TextStart);
        var candidates = line.Stops.Where(s => right ? s.X > current.X + .01 : s.X < current.X - .01);
        var next = right ? candidates.FirstOrDefault() : candidates.LastOrDefault();
        if (next is not null) return line.Stops.FirstOrDefault(s => Math.Abs(s.X - next.X) < .01 && s.ArriveFromLeft == right)?.Caret ?? next.Caret;
        var advance = right != line.RightToLeft;
        var position = advance ? line.Fragment.TextEnd : line.Fragment.TextStart - 1;
        if (advance && position >= line.Fragment.Position.End && !line.EndsWithBreak) position = line.Fragment.Position.End + 1;
        if (position < 0 || position > _index.Length) return current.Caret;
        return ReadLine(VisualCaret.Logical(position))?.Edge(!right) ?? current.Caret;
    }
    internal VisualCaret MoveWordCaret(VisualCaret caret, bool right)
    {
        for (var pass = 0; pass <= Fragments.Length; pass++)
        {
            var line = ReadLine(caret);
            if (line is null) return caret;
            var current = line.Resolve(caret);
            bool Boundary(Stop stop) => stop.Caret.Position == line.Fragment.Position.Start || stop.Caret.Position == line.Fragment.Position.End ||
                stop.Caret.Position > 0 && stop.Caret.Position < _index.Length && char.IsWhiteSpace(_index.CharAt(stop.Caret.Position - 1)) && !char.IsWhiteSpace(_index.CharAt(stop.Caret.Position));
            var candidates = line.Stops.Where(s => (right ? s.X > current.X + .01 : s.X < current.X - .01) && Boundary(s));
            var target = right ? candidates.FirstOrDefault() : candidates.LastOrDefault();
            if (target is not null) return line.Stops.FirstOrDefault(s => Math.Abs(s.X - target.X) < .01 && s.ArriveFromLeft == right && Boundary(s))?.Caret ?? target.Caret;
            var edge = line.Edge(right); var adjacent = MoveCaret(edge, right);
            if (adjacent == edge) return edge;
            var next = ReadLine(adjacent)!;
            if (adjacent.Position == next.Fragment.Position.Start || adjacent.Position == next.Fragment.Position.End || adjacent.Position > 0 &&
                char.IsWhiteSpace(_index.CharAt(adjacent.Position - 1)) && adjacent.Position < _index.Length && !char.IsWhiteSpace(_index.CharAt(adjacent.Position))) return adjacent;
            caret = adjacent;
        }
        return caret;
    }
    internal VisualCaret LineBoundary(VisualCaret caret, bool end) => ReadLine(caret)?.Edge(end) ?? caret;
    internal VisualCaret CollapseSelection(VisualCaret anchor, VisualCaret active, bool right)
    {
        var a = Caret(anchor); var b = Caret(active);
        var page = GetPageIndex(anchor.Position).CompareTo(GetPageIndex(active.Position));
        var order = page != 0 ? page : Math.Abs(a.Y - b.Y) > .1 ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
        return right == (order > 0) ? anchor : active;
    }
}


