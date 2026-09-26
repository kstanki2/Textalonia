using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace Textalonia.Controls;

/// <summary>A blinking insertion caret. Subclasses can customize drawing while retaining timer ownership.</summary>
public class DefaultCaretComponent : DocumentInputComponent, ICaretComponent
{
    private DispatcherTimer? _timer;
    protected bool IsVisible { get; private set; } = true;
    protected override void OnAttached()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _timer.Tick += OnTick;
        FocusChanged(Context.IsFocused);
    }
    protected override void OnDetached()
    {
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }
    }
    private void OnTick(object? sender, EventArgs e) { IsVisible = !IsVisible; AttachedContext?.InvalidateVisual(); }
    public virtual void FocusChanged(bool focused)
    {
        IsVisible = true;
        if (focused) _timer?.Start(); else _timer?.Stop();
        AttachedContext?.InvalidateVisual();
    }
    public virtual void Reset() { IsVisible = true; AttachedContext?.InvalidateVisual(); }
    public virtual void Render(DrawingContext drawing, Rect bounds, IBrush brush)
    {
        if (IsVisible) drawing.FillRectangle(brush, bounds);
    }
}
