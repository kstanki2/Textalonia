using Avalonia;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    public static readonly StyledProperty<IKeyboardComponent?> KeyboardComponentProperty =
        AvaloniaProperty.Register<TextaloniaEditor, IKeyboardComponent?>(nameof(KeyboardComponent));
    public static readonly StyledProperty<IPointerComponent?> PointerComponentProperty =
        AvaloniaProperty.Register<TextaloniaEditor, IPointerComponent?>(nameof(PointerComponent));
    public static readonly StyledProperty<ICaretComponent?> CaretComponentProperty =
        AvaloniaProperty.Register<TextaloniaEditor, ICaretComponent?>(nameof(CaretComponent));
    public static readonly StyledProperty<ICompositionComponent?> CompositionComponentProperty =
        AvaloniaProperty.Register<TextaloniaEditor, ICompositionComponent?>(nameof(CompositionComponent));

    static TextaloniaEditor()
    {
        KeyboardComponentProperty.Changed.AddClassHandler<TextaloniaEditor>((editor, _) => editor._surface?.UpdateInputComponents());
        PointerComponentProperty.Changed.AddClassHandler<TextaloniaEditor>((editor, _) => editor._surface?.UpdateInputComponents());
        CaretComponentProperty.Changed.AddClassHandler<TextaloniaEditor>((editor, _) => editor._surface?.UpdateInputComponents());
        CompositionComponentProperty.Changed.AddClassHandler<TextaloniaEditor>((editor, _) => editor._surface?.UpdateInputComponents());
    }

    /// <summary>Custom keyboard routing, or null for standard shortcuts. One component instance may attach to one surface.</summary>
    public IKeyboardComponent? KeyboardComponent { get => GetValue(KeyboardComponentProperty); set => SetValue(KeyboardComponentProperty, value); }
    /// <summary>Custom pointer selection, or null for the default behavior.</summary>
    public IPointerComponent? PointerComponent { get => GetValue(PointerComponentProperty); set => SetValue(PointerComponentProperty, value); }
    /// <summary>Custom caret rendering and blinking, or null for the default caret.</summary>
    public ICaretComponent? CaretComponent { get => GetValue(CaretComponentProperty); set => SetValue(CaretComponentProperty, value); }
    /// <summary>Custom composition and committed-text routing, or null for native IME integration.</summary>
    public ICompositionComponent? CompositionComponent { get => GetValue(CompositionComponentProperty); set => SetValue(CompositionComponentProperty, value); }
}
