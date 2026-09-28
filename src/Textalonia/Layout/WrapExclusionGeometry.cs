using Avalonia;
using Textalonia.Model;
using Textalonia.Rendering;

namespace Textalonia.Layout;

/// <summary>Wrap geometry shared by positioned tables and image placement.</summary>
internal static class WrapExclusionGeometry
{
    /// <summary>Returns the widest free interval at Y, advancing below blockers when no usable interval remains.</summary>
    internal static Rect Resolve(Rect column, double y, double lineHeight, IReadOnlyList<Rect> exclusions, double minimumWidth = 16)
    {
        minimumWidth = Math.Max(16, minimumWidth);
        lineHeight = Math.Max(1, lineHeight);
        // Each retry passes at least one rectangle's bottom, so finite inputs always terminate.
        for (var attempt = 0; attempt <= exclusions.Count; attempt++)
        {
            var blocked = exclusions.Where(rect => rect.Bottom > y + .001 && rect.Top < y + lineHeight - .001 &&
                rect.Right > column.Left && rect.Left < column.Right).OrderBy(rect => rect.Left).ToArray();
            if (blocked.Length == 0) return new(column.Left, y, column.Width, lineHeight);
            var cursor = column.Left; var bestX = cursor; var bestWidth = 0d;
            foreach (var rect in blocked)
            {
                var end = Math.Clamp(rect.Left, column.Left, column.Right);
                if (end - cursor > bestWidth) { bestX = cursor; bestWidth = end - cursor; }
                cursor = Math.Max(cursor, Math.Min(column.Right, rect.Right));
            }
            if (column.Right - cursor > bestWidth) { bestX = cursor; bestWidth = column.Right - cursor; }
            if (bestWidth >= Math.Min(minimumWidth, column.Width)) return new(bestX, y, bestWidth, lineHeight);
            y = blocked.Min(rect => rect.Bottom);
        }
        return new(column.Left, y, column.Width, lineHeight);
    }
    // Conservative horizontal bands preserve a supplied polygon without a shape engine.
    // At most 64 bands per image keeps wrapping cost bounded, including rotated contours.
    internal static IReadOnlyList<Rect> Contour(Rect bounds, ImagePlacement placement)
    {
        var rotation = ImageDrawing.Rotation(bounds, placement.Rotation);
        var polygon = placement.Contour.Select(point => new Point(bounds.Left + point.X * bounds.Width,
            bounds.Top + point.Y * bounds.Height).Transform(rotation)).ToArray();
        var top = polygon.Min(point => point.Y); var bottom = polygon.Max(point => point.Y);
        if (bottom - top < .001) return [ImageDrawing.RotatedBounds(bounds, placement.Rotation).Inflate(placement.Distance)];
        var bands = Math.Clamp((int)Math.Ceiling((bottom - top) / 4), 1, 64);
        var result = new List<Rect>(bands);
        for (var band = 0; band < bands; band++)
        {
            var from = top + (bottom - top) * band / bands;
            var to = top + (bottom - top) * (band + 1) / bands;
            var intersections = new List<double>();
            for (var i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
                if (a.Y >= from && a.Y <= to) intersections.Add(a.X);
                if (Math.Abs(b.Y - a.Y) < .0001) continue;
                foreach (var y in new[] { from, to })
                    if (y >= Math.Min(a.Y, b.Y) && y <= Math.Max(a.Y, b.Y))
                        intersections.Add(a.X + (b.X - a.X) * (y - a.Y) / (b.Y - a.Y));
            }
            if (intersections.Count != 0)
                result.Add(new Rect(intersections.Min(), from, intersections.Max() - intersections.Min(), to - from).Inflate(placement.Distance));
        }
        return result;
    }
}
