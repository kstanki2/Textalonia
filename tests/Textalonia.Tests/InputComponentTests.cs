using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Xunit;

namespace Textalonia.Tests;

public class InputComponentTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);
    private static RawInputModifiers Command => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface) Create(TextaloniaEditor? editor = null)
    {
        editor ??= new TextaloniaEditor { Text = "hello world" };
        var window = new Window { Width = 700, Height = 400, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument(); Dispatcher.UIThread.RunJobs();
        return (window, editor, editor.GetVisualDescendants().OfType<DocumentSurface>().Single());
    }
    private static TextInputMethodClient Client(DocumentSurface surface)
    {
        var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        surface.RaiseEvent(request);
        return Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
    }

    [Fact]
    public Task Custom_keymap_preserves_default_selection_IME_typing_and_undo() => Run(() =>
    {
        var keyboard = new TrackingKeyboard();
        var caret = new TrackingCaret();
        var (window, editor, surface) = Create(new() { Text = "hello world", KeyboardComponent = keyboard, CaretComponent = caret });
        try
        {
            window.KeyPress(Key.F6, RawInputModifiers.None);
            Assert.Equal("hello ", editor.SelectedText);
            window.KeyPress(Key.Right, RawInputModifiers.None);
            Assert.Equal(6, editor.Session.Selection.Active);
            var client = Client(surface);
            client.SetPreeditText("preview", 3);
            Assert.True(surface.HasComposition);
            Assert.Equal("hello world", editor.Text);
            window.KeyTextInput("!");
            Assert.Equal("hello !world", editor.Text);
            Assert.False(surface.HasComposition);
            window.KeyPress(Key.Z, Command);
            Assert.Equal("hello world", editor.Text);
            window.KeyPress(Key.A, Command);
            Assert.Equal(editor.Text, editor.SelectedText);
            Assert.Equal(1, keyboard.Attachments);
            Assert.Equal(1, caret.Attachments);
        }
        finally { window.Close(); }
        Assert.Equal(1, keyboard.Detachments);
        Assert.Equal(1, caret.Detachments);
    });

    [Fact]
    public async Task Custom_keymap_keeps_clipboard_commands()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var (window, editor, _) = Create(new() { Text = "copy", KeyboardComponent = new TrackingKeyboard() });
            try
            {
                window.KeyPress(Key.A, Command);
                window.KeyPress(Key.C, Command);
                await Task.Yield();
                editor.Session.Select(editor.Text.Length, editor.Text.Length);
                await editor.PasteAsync();
                Assert.Equal("copycopy", editor.Text);
                return true;
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public Task Replacing_one_component_preserves_composition_and_detaches_old_component() => Run(() =>
    {
        var first = new TrackingKeyboard();
        var second = new TrackingKeyboard();
        var (window, editor, surface) = Create(new() { Text = "base", KeyboardComponent = first });
        try
        {
            var client = Client(surface);
            client.SetPreeditText("preview");
            editor.KeyboardComponent = second;
            Assert.Equal(1, first.Detachments);
            Assert.Equal(1, second.Attachments);
            Assert.Same(client, Client(surface));
            Assert.True(surface.HasComposition);
            surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F6, Handled = true });
            Assert.Equal(0, second.Keys);
            window.KeyPress(Key.F6, RawInputModifiers.None);
            Assert.Equal(1, second.Keys);
            Assert.False(surface.HasComposition);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Detach_and_retemplate_release_components_and_invalidate_old_IME_clients() => Run(() =>
    {
        var keyboard = new TrackingKeyboard();
        var caret = new TrackingCaret();
        var (window, editor, surface) = Create(new() { Text = "base", KeyboardComponent = keyboard, CaretComponent = caret });
        try
        {
            var oldClient = Client(surface);
            var resets = 0;
            oldClient.ResetRequested += (_, _) => resets++;
            oldClient.SetPreeditText("preview");
            editor.Template = new FuncControlTemplate<TextaloniaEditor>((_, scope) =>
            {
                var replacement = new DocumentSurface();
                var scroller = new ScrollViewer { Content = replacement };
                scope.Register("PART_Surface", replacement); scope.Register("PART_ScrollViewer", scroller);
                return scroller;
            });
            window.UpdateLayout();
            var newSurface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            Assert.NotSame(surface, newSurface);
            Assert.Null(surface.Editor);
            Assert.False(surface.HasComposition);
            Assert.True(resets > 0);
            Assert.Equal(2, keyboard.Attachments);
            Assert.Equal(1, keyboard.Detachments);
            oldClient.SetPreeditText("stale");
            Assert.False(newSurface.HasComposition);
            Assert.Empty(oldClient.SurroundingText);
            for (var i = 0; i < 3; i++)
            {
                window.Content = null; window.UpdateLayout();
                Assert.Equal(keyboard.Attachments, keyboard.Detachments);
                Assert.Equal(caret.Attachments, caret.Detachments);
                window.Content = editor; window.UpdateLayout();
            }
            Assert.Equal(keyboard.Attachments - 1, keyboard.Detachments);
            editor.FocusDocument(); window.KeyTextInput("!");
            Assert.Equal("!base", editor.Text);
        }
        finally { window.Close(); }
        Assert.Equal(keyboard.Attachments, keyboard.Detachments);
        Assert.Equal(caret.Attachments, caret.Detachments);
    });

    [Fact]
    public Task Replacing_pointer_component_releases_capture() => Run(() =>
    {
        var pointer = new TrackingPointer();
        var (window, editor, surface) = Create(new() { Text = "hello world", PointerComponent = pointer });
        try
        {
            var point = surface.TranslatePoint(new Point(29, 29), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            Assert.Same(surface, pointer.Pointer!.Captured);
            editor.PointerComponent = null;
            Assert.Null(pointer.Pointer.Captured);
            window.MouseUp(point, MouseButton.Left);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Replaced_composition_client_cannot_change_current_preview_or_selection() => Run(() =>
    {
        var (window, editor, surface) = Create();
        try
        {
            var previous = Client(surface);
            previous.SetPreeditText("transient");
            editor.CompositionComponent = new DefaultCompositionComponent();
            Assert.False(surface.HasComposition);
            var current = Client(surface);
            Assert.NotSame(previous, current);
            previous.SetPreeditText("stale");
            previous.Selection = new(3, 5);
            Assert.False(surface.HasComposition);
            Assert.Equal(0, editor.Session.Selection.Active);
            current.SetPreeditText("current");
            editor.IsReadOnly = true;
            Assert.False(surface.HasComposition);
            Assert.Equal("hello world", editor.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task An_attachment_failure_does_not_detach_another_surfaces_component() => Run(() =>
    {
        var shared = new TrackingKeyboard();
        var (firstWindow, firstEditor, firstSurface) = Create(new() { Text = "first text", KeyboardComponent = shared });
        var (secondWindow, secondEditor, _) = Create();
        try
        {
            Assert.Throws<InvalidOperationException>(() => secondEditor.KeyboardComponent = shared);
            Assert.Equal(1, shared.Attachments);
            Assert.Equal(0, shared.Detachments);
            firstSurface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F6 });
            Assert.Equal("first ", firstEditor.SelectedText);
            secondEditor.KeyboardComponent = null;
            secondEditor.FocusDocument();
            secondWindow.KeyTextInput("!");
            Assert.Equal("!hello world", secondEditor.Text);
        }
        finally { secondWindow.Close(); firstWindow.Close(); }
        Assert.Equal(1, shared.Detachments);
    });

    private sealed class TrackingKeyboard : DefaultKeyboardComponent
    {
        public int Attachments { get; private set; }
        public int Detachments { get; private set; }
        public int Keys { get; private set; }
        protected override void OnAttached() { Attachments++; base.OnAttached(); }
        protected override void OnDetached() { Detachments++; base.OnDetached(); }
        public override void KeyDown(KeyEventArgs e)
        {
            Keys++;
            if (!e.Handled && e.Key == Key.F6)
            {
                Context.CancelComposition();
                var active = Context.Session.Selection.Active;
                Context.Session.Select(active, Context.Session.NextWord(active));
                e.Handled = true;
            }
            else base.KeyDown(e);
        }
    }
    private sealed class TrackingCaret : DefaultCaretComponent
    {
        public int Attachments { get; private set; }
        public int Detachments { get; private set; }
        protected override void OnAttached() { Attachments++; base.OnAttached(); }
        protected override void OnDetached() { Detachments++; base.OnDetached(); }
    }
    private sealed class TrackingPointer : DefaultPointerComponent
    {
        public IPointer? Pointer { get; private set; }
        public override void PointerPressed(PointerPressedEventArgs e) { Pointer = e.Pointer; base.PointerPressed(e); }
    }
}
