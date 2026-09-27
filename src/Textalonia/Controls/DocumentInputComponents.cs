using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>A UI-thread component owned by one attached surface at a time. Detach must release subscriptions, capture and timers.</summary>
public interface IDocumentInputComponent
{
    void Attach(DocumentInputContext context);
    void Detach();
}

/// <summary>Receives unhandled keys originating on the surface, after Avalonia routing. Set Handled to consume a key.</summary>
public interface IKeyboardComponent : IDocumentInputComponent { void KeyDown(KeyEventArgs e); }

/// <summary>Receives unhandled pointer input originating on the surface. Inline child controls retain their own input.</summary>
public interface IPointerComponent : IDocumentInputComponent
{
    void PointerPressed(PointerPressedEventArgs e);
    void PointerMoved(PointerEventArgs e);
    void PointerReleased(PointerReleasedEventArgs e);
    void PointerCaptureLost(PointerCaptureLostEventArgs e);
}

/// <summary>Owns caret appearance and blinking; receives already clipped geometry from the shared layout.</summary>
public interface ICaretComponent : IDocumentInputComponent
{
    void FocusChanged(bool focused);
    void Reset();
    void Render(DrawingContext drawing, Rect bounds, IBrush brush);
}

/// <summary>Owns transient composition and committed text input independently of keyboard bindings.</summary>
public interface ICompositionComponent : IDocumentInputComponent
{
    TextInputMethodClient? Client { get; }
    FlowDocument? PreviewDocument { get; }
    int? CaretPosition { get; }
    bool IsComposing { get; }
    void TextInput(TextInputEventArgs e);
    void SetPreedit(string? text, int? cursor);
    void Cancel();
    void Refresh();
    void Activate();
}

/// <summary>Optional component base enforcing exclusive attachment and clearing references even when detach fails.</summary>
public abstract class DocumentInputComponent : IDocumentInputComponent
{
    protected DocumentInputContext? AttachedContext { get; private set; }
    protected DocumentInputContext Context => AttachedContext ?? throw new InvalidOperationException("The input component is not attached.");
    public void Attach(DocumentInputContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (AttachedContext is not null) throw new InvalidOperationException("Input components cannot be shared by attached surfaces.");
        AttachedContext = context;
        try { OnAttached(); }
        catch { Detach(); throw; }
    }
    public void Detach()
    {
        if (AttachedContext is null) return;
        try { OnDetached(); }
        finally { AttachedContext = null; }
    }
    protected virtual void OnAttached() { }
    protected virtual void OnDetached() { }
}

/// <summary>Shared surface/session services. Geometry is in surface coordinates and uses the same layout as rendering and IME.</summary>
public sealed class DocumentInputContext
{
    internal DocumentInputContext(DocumentSurface surface, TextaloniaEditor editor) { Surface = surface; Editor = editor; }
    public DocumentSurface Surface { get; }
    public TextaloniaEditor Editor { get; }
    public EditorSession Session => Editor.Session;
    public Rect CaretRectangle => Surface.CaretRectangle;
    public double ViewportHeight => Editor.Scroller?.Viewport.Height ?? 300;
    public bool IsFocused => Surface.IsFocused;
    public bool IsComposing => Surface.Composition.IsComposing;
    public double? PreferredCaretX { get; set; }
    public bool TryHitTest(Point point, out int position) => Surface.HitTestDocument(point, out position);
    public int? GetLineBoundary(int position, bool end) => Surface.GetLineBoundary(position, end);
    public void CancelComposition() => Surface.Composition.Cancel();
    public void ActivateInputPane() => Surface.Composition.Activate();
    public void InvalidateVisual() => Surface.InvalidateVisual();
    public void Refresh(bool bringCaret = false) => Surface.Refresh(bringCaret);
}
