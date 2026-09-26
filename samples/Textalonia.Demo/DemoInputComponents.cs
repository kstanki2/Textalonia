using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Textalonia.Controls;

namespace Textalonia.Demo;

// Only F6 is changed: all standard shortcuts, selection and IME remain available.
internal sealed class DemoKeyboardComponent : DefaultKeyboardComponent
{
    public override void KeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.F6 && e.KeyModifiers == KeyModifiers.None)
        {
            Context.CancelComposition();
            var end = Context.Session.NextWord(Context.Session.Selection.Active);
            Context.Session.Select(Context.Session.Selection.Active, end);
            e.Handled = true;
            return;
        }
        base.KeyDown(e);
    }
}

// Reuses the default component's owned blink timer and shared clipped geometry.
internal sealed class DemoCaretComponent : DefaultCaretComponent
{
    public override void Render(DrawingContext drawing, Rect bounds, IBrush brush)
    {
        if (IsVisible) drawing.FillRectangle(Brushes.DodgerBlue, bounds);
    }
}
