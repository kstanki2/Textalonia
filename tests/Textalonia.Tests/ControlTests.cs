using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Demo;
using Textalonia.Model;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Textalonia.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class UiFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
    public void Dispose() => Session.Dispose();
}

public class ControlTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static RawInputModifiers CommandModifiers => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);
    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface) Create(string text = "")
    {
        var editor = new TextaloniaEditor { Text = text };
        var window = new Window { Width = 800, Height = 500, Content = editor };
        window.Show(); window.UpdateLayout();
        var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
        editor.FocusDocument();
        Dispatcher.UIThread.RunJobs();
        return (window, editor, surface);
    }

    [Fact]
    public Task Typing_format_shortcuts_selection_and_undo_work_in_real_control() => Run(() =>
    {
        var (window, editor, _) = Create();
        try
        {
            window.KeyTextInput("Hello");
            Assert.Equal("Hello", editor.Text);
            window.KeyPress(Key.A, CommandModifiers);
            window.KeyPress(Key.B, CommandModifiers);
            Assert.True(new DocumentIndex(editor.Document).Paragraphs[0].Paragraph.Runs[0].Style.Bold);
            window.KeyTextInput("World");
            Assert.Equal("World", editor.Text);
            window.KeyPress(Key.Z, CommandModifiers);
            Assert.Equal("Hello", editor.Text);
            window.KeyPress(Key.Y, CommandModifiers);
            Assert.Equal("World", editor.Text);
            window.KeyPress(Key.End, CommandModifiers);
            window.KeyPress(Key.Enter, RawInputModifiers.None);
            window.KeyTextInput("Next");
            Assert.Equal("World\nNext", editor.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Read_only_blocks_keyboard_paste_and_preserves_selection() => Run(() =>
    {
        var (window, editor, _) = Create("Keep me");
        try
        {
            editor.IsReadOnly = true;
            window.KeyPress(Key.A, CommandModifiers);
            window.KeyTextInput("Replace");
            window.KeyPress(Key.Delete, RawInputModifiers.None);
            window.KeyPress(Key.B, CommandModifiers);
            Assert.Equal("Keep me", editor.Text);
            Assert.Equal("Keep me", editor.SelectedText);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Pointer_hit_testing_and_shift_navigation_select_expected_text() => Run(() =>
    {
        var (window, editor, surface) = Create("Hello world");
        try
        {
            var point = surface.TranslatePoint(new Point(29, 29), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            window.KeyPress(Key.Home, RawInputModifiers.None);
            window.KeyPress(Key.Right, RawInputModifiers.Shift);
            window.KeyPress(Key.Right, RawInputModifiers.Shift);
            Assert.Equal("He", editor.SelectedText);
            window.KeyPress(Key.Back, RawInputModifiers.None);
            Assert.Equal("llo world", editor.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Ime_preedit_is_transient_and_commits_as_one_edit() => Run(() =>
    {
        var (window, editor, surface) = Create("before ");
        try
        {
            editor.Session.Select(editor.Text.Length, editor.Text.Length);
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            surface.RaiseEvent(request);
            var client = Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
            client.SetPreeditText("\u306b\u307b\u3093", 2);
            window.UpdateLayout();
            Assert.Equal("before ", editor.Text);
            Assert.True(client.CursorRectangle.Height > 0);
            window.KeyTextInput("\u65e5\u672c");
            Assert.Equal("before \u65e5\u672c", editor.Text);
            editor.Undo();
            Assert.Equal("before ", editor.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Tab_navigates_cells_inside_a_section() => Run(() =>
    {
        var (window, editor, _) = Create();
        try
        {
            var table = Table.Create(1, 2);
            editor.Document = new FlowDocument([new Section { Blocks = [table] }]);
            window.KeyPress(Key.Tab, RawInputModifiers.None);
            Assert.Equal(table.Rows[0][1].Id, editor.Session.Index.At(editor.Session.Selection.Active).ContainerId);
            window.KeyPress(Key.Tab, RawInputModifiers.Shift);
            Assert.Equal(table.Rows[0][0].Id, editor.Session.Index.At(editor.Session.Selection.Active).ContainerId);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Replacing_document_or_entering_readonly_cancels_preedit() => Run(() =>
    {
        var (window, editor, surface) = Create("old");
        try
        {
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            surface.RaiseEvent(request);
            var client = Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
            var resets = 0;
            client.ResetRequested += (_, _) => resets++;
            client.SetPreeditText("composition", 4);
            editor.Document = FlowDocument.FromText("new");
            Assert.True(resets > 0);
            Assert.Equal("new", editor.Text);
            resets = 0;
            client.SetPreeditText("second", 2);
            editor.IsReadOnly = true;
            Assert.True(resets > 0);
            Assert.Equal("new", editor.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Document_binding_updates_in_both_directions_without_losing_binding() => Run(() =>
    {
        var model = new TestViewModel { Document = FlowDocument.FromText("first") };
        var editor = new TextaloniaEditor { DataContext = model };
        editor.Bind(TextaloniaEditor.DocumentProperty, new Binding(nameof(TestViewModel.Document)) { Mode = BindingMode.TwoWay });
        var window = new Window { Width = 600, Height = 300, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal("first", editor.Text);
            editor.Session.SelectAll(); editor.InsertText("changed");
            Assert.Equal("changed", model.Document.Text);
            model.Document = FlowDocument.FromText("replacement");
            Assert.Equal("replacement", editor.Text);
            editor.InsertText("A");
            Assert.Equal("Areplacement", model.Document.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task Clipboard_round_trip_preserves_character_formatting()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var (window, editor, _) = Create("copied");
            try
            {
                editor.SelectAll(); editor.Session.ToggleBold();
                await editor.CopyAsync();
                editor.Session.Select(editor.Text.Length, editor.Text.Length);
                await editor.PasteAsync();
                Assert.Equal("copiedcopied", editor.Text);
                Assert.All(new DocumentIndex(editor.Document).Paragraphs[0].Paragraph.Runs, r => Assert.True(r.Style.Bold));
                return true;
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public Task Demo_renders_light_and_dark_and_handles_narrow_layout() => Run(() =>
    {
        var window = new MainWindow();
        try
        {
            window.Show(); window.UpdateLayout();
            var surface = window.GetVisualDescendants().OfType<DocumentSurface>().Single();
            Assert.True(surface.Bounds.Width > 500);
            Assert.True(surface.DesiredSize.Height > 300);
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
            Directory.CreateDirectory(output);
            var original = Textalonia.Serialization.DocumentFormats.Json.Serialize(window.FindControl<TextaloniaEditor>("Editor")!.Document);
            foreach (var (theme, name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                window.FindControl<ToggleSwitch>("ThemeToggle")!.IsChecked = theme == ThemeVariant.Dark;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                using var bitmap = window.CaptureRenderedFrame();
                Assert.NotNull(bitmap);
                bitmap.Save(Path.Combine(output, $"editor-{name}.png"), PngBitmapEncoderOptions.Default);
            }
            window.Width = 700; window.UpdateLayout();
            using var narrow = window.CaptureRenderedFrame();
            Assert.NotNull(narrow);
            narrow.Save(Path.Combine(output, "editor-narrow.png"), PngBitmapEncoderOptions.Default);
            Assert.Equal(original, Textalonia.Serialization.DocumentFormats.Json.Serialize(window.FindControl<TextaloniaEditor>("Editor")!.Document));
        }
        finally { window.Close(); }
    });

    private sealed class TestViewModel : INotifyPropertyChanged
    {
        private FlowDocument _document = new();
        public FlowDocument Document
        {
            get => _document;
            set { _document = value; PropertyChanged?.Invoke(this, new(nameof(Document))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

internal static class KeyboardTestExtensions
{
    public static void KeyPress(this TopLevel window, Key key, RawInputModifiers modifiers) =>
        HeadlessWindowExtensions.KeyPress(window, key, modifiers, PhysicalKey.None, null);
}
