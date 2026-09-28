using Avalonia;

namespace Textalonia.Layout;

/// <summary>Rectangle-only wrap geometry shared by positioned tables and image placement.</summary>
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
}
