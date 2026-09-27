using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;

namespace Textalonia.Controls;

/// <summary>Owns a drag's anchor and edge clock independently of pointer motion.</summary>
internal sealed class SelectionAutoScroller(DocumentInputContext context, Func<bool> hasCapture, Action? cancelled = null)
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Point _viewportPoint;
    private int _anchor;
    private int _revision;
    private long _lastTick;
    internal bool IsActive { get; private set; }
    internal bool IsTimerRunning => _timer.IsEnabled;

    internal void Begin(int anchor, Point point, bool startTimer = true)
    {
        Stop();
        _anchor = anchor;
        _revision = context.Session.Revision;
        IsActive = true;
        context.Surface.IsSelectingWithPointer = true;
        _lastTick = Stopwatch.GetTimestamp();
        _timer.Tick += OnTick;
        Update(point);
        if (startTimer) _timer.Start();
    }

    internal void Update(Point surfacePoint)
    {
        if (!IsActive) return;
        _viewportPoint = context.Editor.Scroller is { } scroller
            ? context.Surface.TranslatePoint(surfacePoint, scroller) ?? surfacePoint : surfacePoint;
        SelectAtPointer();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        Advance(TimeSpan.FromSeconds((double)(now - _lastTick) / Stopwatch.Frequency));
        _lastTick = now;
    }

    internal void Advance(TimeSpan elapsed)
    {
        if (!IsActive) return;
        if (!hasCapture() || !context.IsFocused || context.Session.Revision != _revision)
        { Stop(); cancelled?.Invoke(); return; }
        if (context.Editor.Scroller is { } scroller)
        {
            var point = SurfacePoint();
            var viewport = context.Surface.InteractionViewport;
            var seconds = Math.Clamp(elapsed.TotalSeconds, 0, .1);
            var delta = new Vector(EdgeVelocity(point.X, viewport.Left, viewport.Right) * seconds,
                EdgeVelocity(point.Y, viewport.Top, viewport.Bottom) * seconds);
            if (delta != default)
            {
                scroller.Offset += delta;
                // Publish the new viewport before hit testing. Virtualized measurements
                // may adjust its offset; the stored pointer remains viewport-relative.
                context.Surface.UpdateLayout();
            }
        }
        SelectAtPointer();
    }

    internal static double EdgeVelocity(double coordinate, double start, double end)
    {
        if (!double.IsFinite(coordinate) || end <= start) return 0;
        var edge = Math.Min(24, (end - start) / 2);
        var distance = coordinate < start + edge ? coordinate - start - edge :
            coordinate > end - edge ? coordinate - end + edge : 0;
        return Math.Sign(distance) * Math.Min(1200, Math.Abs(distance) * 12);
    }

    private Point SurfacePoint() => context.Editor.Scroller is { } scroller
        ? scroller.TranslatePoint(_viewportPoint, context.Surface) ?? _viewportPoint : _viewportPoint;

    private void SelectAtPointer()
    {
        if (context.Session.Revision != _revision) { Stop(); cancelled?.Invoke(); return; }
        var point = SurfacePoint();
        var viewport = context.Surface.InteractionViewport;
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        point = new Point(Math.Clamp(point.X, viewport.Left, Math.Max(viewport.Left, viewport.Right - 1)),
            Math.Clamp(point.Y, viewport.Top, Math.Max(viewport.Top, viewport.Bottom - 1)));
        if (context.TryHitTest(point, out var position) &&
            (context.Session.Selection.Anchor != _anchor || context.Session.Selection.Active != position))
            context.Session.Select(_anchor, position);
    }

    internal void Stop()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        IsActive = false;
        context.Surface.IsSelectingWithPointer = false;
    }
}
