using System.Collections.Immutable;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Layout;
using Textalonia.Model;
using Textalonia.Pdf.Skia;
using Textalonia.Rendering;
using Xunit;

namespace Textalonia.Tests;

public sealed class FormControlsUiTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static (FlowDocument Document, Guid Text, Guid Check) Form()
    {
        var session = new EditorSession(FlowDocument.FromText("Name: guest\nConsent: "));
        session.Select(6, 11);
        var text = session.InsertContentControl(new() { Kind = ContentControlKind.PlainText, Title = "Name", LockControl = true })!;
        session.Select(session.Index.Length, session.Index.Length);
        var check = session.InsertContentControl(new() { Kind = ContentControlKind.CheckBox, Title = "Consent", LockControl = true })!;
        return (session.Document with { Protection = new() { Mode = DocumentProtectionMode.FormsOnly } }, text.Id, check.Id);
    }
    private static Window Open(TextaloniaEditor editor)
    {
        var window = new Window { Width = 900, Height = 650, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument(); Dispatcher.UIThread.RunJobs();
        return window;
    }
    private static void Close(Window window)
    {
        foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(false);
        window.Close();
    }

    [Fact]
    public Task Protected_form_keyboard_and_ime_fill_keep_labels_and_undo_history() => Run(() =>
    {
        var form = Form(); var editor = new TextaloniaEditor { Document = form.Document, ShowToolbar = false };
        var window = Open(editor);
        try
        {
            window.KeyTextInput("forbidden"); Assert.Same(form.Document, editor.Document); Assert.False(editor.Session.CanUndo);
            window.KeyPress(Key.Tab, RawInputModifiers.None); Assert.Equal(form.Text, editor.CurrentContentControl!.Id);
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            surface.RaiseEvent(request); var client = Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
            client.SetPreeditText("ni", 2); Assert.Same(form.Document, editor.Document);
            window.KeyTextInput("\u65e5\u672c"); Assert.StartsWith("Name: \u65e5\u672c", editor.Text);
            Assert.Equal("\u65e5\u672c", editor.Document.ContentControls.Single(control => control.Id == form.Text).Value);
            window.KeyPress(Key.Tab, RawInputModifiers.None); Assert.Equal(form.Check, editor.CurrentContentControl!.Id);
            window.KeyPress(Key.Space, RawInputModifiers.None); Assert.True(editor.CurrentContentControl.IsChecked);
            window.KeyPress(Key.Tab, RawInputModifiers.Shift); Assert.Equal(form.Text, editor.CurrentContentControl!.Id);
            editor.Undo(); Assert.False(editor.Document.ContentControls.Single(control => control.Id == form.Check).IsChecked);
            editor.Undo(); Assert.Same(form.Document, editor.Document);
        }
        finally { Close(window); }
    });

    [Fact]
    public Task Ime_preview_uses_host_identity_and_tab_cancels_uncommitted_form_input() => Run(() =>
    {
        var form = Form();
        var permission = new DocumentPermissionRange { Start = form.Document.ContentControls[0].Start, End = form.Document.ContentControls[0].End, User = "writer" };
        var document = form.Document with { Protection = new() { Mode = DocumentProtectionMode.ReadOnly }, PermissionRanges = [permission] };
        var editor = new TextaloniaEditor { Document = document, ShowToolbar = false }; editor.Session.Identity = new() { User = "writer" };
        var window = Open(editor);
        try
        {
            editor.SelectContentControl(form.Text);
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            surface.Composition.SetPreedit("preview", 3);
            Assert.NotNull(surface.Composition.PreviewDocument);
            Assert.StartsWith("Name: preview", surface.Composition.PreviewDocument!.Text);
            Assert.Same(document, editor.Document);
            window.KeyPress(Key.Tab, RawInputModifiers.None);
            Assert.False(surface.Composition.IsComposing); Assert.Equal(form.Check, editor.CurrentContentControl!.Id);
            Assert.Same(document, editor.Document); Assert.False(editor.Session.CanUndo);
        }
        finally { Close(window); }
    });

    [Fact]
    public Task Checkbox_pointer_and_locked_value_use_form_permissions() => Run(() =>
    {
        var form = Form(); var editor = new TextaloniaEditor { Document = form.Document, ShowToolbar = false };
        var window = Open(editor);
        try
        {
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var visual = Assert.Single(surface.Layout.InlineVisuals());
            var point = surface.TranslatePoint(visual.Bounds.Center, window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.True(editor.Document.ContentControls.Single(control => control.Id == form.Check).IsChecked);
            editor.Session.EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty.Add(EditOperation.Forms, CommandCapability.Disabled) };
            var document = editor.Document;
            window.KeyPress(Key.Space, RawInputModifiers.None); Assert.Same(document, editor.Document);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Assert.Same(document, editor.Document);
        }
        finally { Close(window); }
    });

    [Fact]
    public Task Form_dialog_rejects_invalid_dates_then_commits_one_valid_value() => Run(() =>
    {
        var session = new EditorSession();
        var control = session.InsertContentControl(new() { Kind = ContentControlKind.Date, Title = "Start date", Value = "2026-09-28" })!;
        var original = session.Document with { Protection = new() { Mode = DocumentProtectionMode.FormsOnly } };
        var editor = new TextaloniaEditor { Document = original, ShowToolbar = false }; editor.SelectContentControl(control.Id);
        var window = Open(editor);
        try
        {
            var pending = editor.ShowContentControlValueDialogAsync(); var dialog = Assert.Single(window.OwnedWindows); dialog.UpdateLayout();
            var text = dialog.GetVisualDescendants().OfType<TextBox>().Single();
            var apply = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Apply"));
            text.Text = "tomorrow"; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Assert.False(pending.IsCompleted); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
            text.Text = "2026-10-01"; apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
            Assert.True(pending.GetAwaiter().GetResult()); Assert.Equal("2026-10-01", editor.CurrentContentControl!.Value);
            editor.Undo(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
        }
        finally { Close(window); }
    });

    [Fact]
    public Task Toolbar_visibility_and_commands_follow_the_same_host_capability() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "text" }; var window = Open(editor);
        try
        {
            editor.SelectAll();
            editor.Session.EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty.Add(EditOperation.Formatting, CommandCapability.Hidden) };
            var bold = editor.GetVisualDescendants().OfType<ToggleButton>().Single(button => AutomationProperties.GetName(button) == "Bold");
            Assert.False(bold.IsVisible); Assert.False(editor.BoldCommand.CanExecute(null));
            editor.Session.EditPolicy = new(); Assert.True(bold.IsVisible); Assert.True(editor.BoldCommand.CanExecute(null));
        }
        finally { Close(window); }
    });

    [Fact]
    public Task Clipboard_capability_denies_direct_async_cut_and_paste_without_disabling_typing() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "text", ShowToolbar = false }; var window = Open(editor);
        try
        {
            var clipboardRequests = 0;
            editor.ClipboardProvider = () => { clipboardRequests++; throw new InvalidOperationException("Clipboard must not be accessed."); };
            editor.SelectAll();
            editor.Session.EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty.Add(EditOperation.Clipboard, CommandCapability.Disabled) };
            editor.CutAsync().GetAwaiter().GetResult(); editor.PasteAsync().GetAwaiter().GetResult();
            Assert.Equal(0, clipboardRequests); Assert.Equal("text", editor.Text); Assert.False(editor.CutCommand.CanExecute(null)); Assert.False(editor.PasteCommand.CanExecute(null));
            window.KeyTextInput("typed"); Assert.Equal("typed", editor.Text);
        }
        finally { Close(window); }
    });

    [Fact]
    public Task Demo_form_preserves_labels_outside_all_six_editable_controls() => Run(() =>
    {
        var document = Textalonia.Demo.FormWindow.CreateDocument();
        document.Validate(); Assert.Equal(6, document.ContentControls.Length);
        Assert.Equal(DocumentProtectionMode.FormsOnly, document.Protection.Mode);
        Assert.Equal("Enter your name", document.ContentControls.Single(control => control.Kind == ContentControlKind.PlainText).Value);
        Assert.Equal("Add a note", document.ContentControls.Single(control => control.Kind == ContentControlKind.RichText).Value);
        Assert.Contains("Contact preference: ", document.PlainText);
    });

    [Fact]
    public Task Atomic_values_share_flow_paged_and_pdf_output_without_interactive_pdf_forms() => fixture.Session.Dispatch(async () =>
    {
        var session = new EditorSession();
        session.InsertContentControl(new() { Kind = ContentControlKind.DropDown, Value = "accepted", Items = [new("Approved value", "accepted")] });
        var document = session.Document;
        using var flow = new DocumentLayout(); flow.Build(document, 500, FontFamily.Default, Brushes.Black, Brushes.Gray, new Thickness(20));
        var inline = Assert.Single(flow.InlineVisuals()); Assert.Equal("Approved value", inline.Descriptor.AltText); Assert.True(inline.Bounds.Width > 50);
        using var engine = new PaginationEngine(); using var snapshot = engine.Paginate(document);
        var paged = Assert.Single(snapshot.InlineVisuals()); Assert.Equal(inline.Descriptor.AltText, paged.Descriptor.AltText);
        using var renderer = new DocumentRenderer(snapshot); Assert.Empty(renderer.Diagnostics);
        var recorded = new DrawingGroup();
        using (var drawing = recorded.Open()) renderer.DrawPage(drawing, 0);
        Assert.Contains("Approved value", string.Concat(GlyphText(recorded)));
        using var output = new MemoryStream(); var result = await new PdfExporter().ExportAsync(renderer, output);
        Assert.Equal(1, result.PageCount); var pdf = Encoding.Latin1.GetString(output.ToArray());
        Assert.StartsWith("%PDF-", pdf); Assert.Contains("/ToUnicode", pdf); Assert.DoesNotContain("/AcroForm", pdf);
        return true;
    }, CancellationToken.None);

    private static IEnumerable<string> GlyphText(Drawing drawing)
    {
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children!) foreach (var text in GlyphText(child)) yield return text;
        if (drawing is GlyphRunDrawing { GlyphRun: { } run }) yield return run.Characters.ToString();
    }
}

