using System.Collections.Immutable;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class ImageControlsTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);

    [Fact]
    public Task Picture_dialog_applies_move_resize_crop_and_contour_as_one_undo() => Run(() =>
    {
        var descriptor = new InlineDescriptor { Payload = new ImageInlinePayload("picture"), Width = 100, Height = 50 };
        var original = new FlowDocument([new Paragraph([new RichRun(descriptor)])]);
        var editor = new TextaloniaEditor { Document = original };
        var owner = Open(editor);
        try
        {
            editor.Session.Select(0, 1);
            var selection = editor.Session.Selection;
            var pending = editor.ShowImagePropertiesDialogAsync();
            var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<ComboBox>(dialog, "Anchor").SelectedItem = ImageAnchorKind.Paragraph;
            Find<ComboBox>(dialog, "Text wrapping").SelectedItem = ImageWrapKind.Contour;
            Find<NumericUpDown>(dialog, "Left (DIP)").Value = 30;
            Find<NumericUpDown>(dialog, "Top (DIP)").Value = 45;
            Find<NumericUpDown>(dialog, "Width (DIP)").Value = 160;
            Assert.Equal(80, Find<NumericUpDown>(dialog, "Height (DIP)").Value);
            Find<NumericUpDown>(dialog, "Rotation (degrees)").Value = 20;
            Find<NumericUpDown>(dialog, "Crop left (%)").Value = 10;
            Find<TextBox>(dialog, "Alternative text").Text = "A diagram";
            Find<TextBox>(dialog, "Contour points (x,y; x,y; normalized 0 to 1)").Text = "0,0; 1,0; 0.5,1";
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.True(pending.GetAwaiter().GetResult());
            var updated = editor.CurrentImage!;
            Assert.Equal(160, updated.Width); Assert.Equal(80, updated.Height);
            Assert.Equal("A diagram", updated.AltText);
            Assert.Equal(ImageAnchorKind.Paragraph, updated.Placement!.Anchor);
            Assert.Equal(ImageWrapKind.Contour, updated.Placement.Wrap);
            Assert.Equal(30, updated.Placement.X); Assert.Equal(45, updated.Placement.Y);
            Assert.Equal(.1, updated.Placement.Crop.Left); Assert.Equal(20, updated.Placement.Rotation);
            Assert.Equal(3, updated.Placement.Contour.Length);
            Assert.Equal(selection, editor.Session.Selection);
            editor.Undo(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
        }
        finally { Close(owner); }
    });

    [Fact]
    public Task Invalid_cancelled_stale_and_readonly_picture_dialogs_leave_document_unchanged() => Run(() =>
    {
        var descriptor = new InlineDescriptor { Payload = new ImageInlinePayload("picture") };
        var original = new FlowDocument([new Paragraph([new RichRun(descriptor)])]);
        var editor = new TextaloniaEditor { Document = original };
        var owner = Open(editor);
        try
        {
            var pending = editor.ShowImagePropertiesDialogAsync();
            var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<NumericUpDown>(dialog, "Crop left (%)").Value = 70;
            Find<NumericUpDown>(dialog, "Crop right (%)").Value = 50;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.False(pending.IsCompleted); Assert.Same(original, editor.Document);
            dialog.Close(false); Dispatcher.UIThread.RunJobs();
            Assert.False(pending.GetAwaiter().GetResult()); Assert.False(editor.Session.CanUndo);
            pending = editor.ShowImagePropertiesDialogAsync();
            dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            editor.InsertText("changed"); var changed = editor.Document;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.False(pending.IsCompleted); Assert.Same(changed, editor.Document);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), control => control.Text?.Contains("Close and reopen") == true);
            dialog.Close(false); Dispatcher.UIThread.RunJobs();
            editor.IsReadOnly = true;
            Assert.False(editor.ShowImagePropertiesDialogAsync().GetAwaiter().GetResult());
            Assert.False(editor.ShowWatermarkDialogAsync().GetAwaiter().GetResult());
            Assert.False(editor.ShowInsertImageDialogAsync().GetAwaiter().GetResult());
            Assert.False(editor.ShowInsertOleDialogAsync().GetAwaiter().GetResult());
            editor.RemoveCurrentImageOrOle(); editor.RemoveWatermark();
            Assert.Same(changed, editor.Document); Assert.Empty(owner.OwnedWindows);
        }
        finally { Close(owner); }
    });

    [Fact]
    public Task Watermark_dialog_authors_image_and_text_and_removes_only_current_section() => Run(() =>
    {
        var first = new Paragraph("First"); var second = new Paragraph("Second");
        var firstSection = new DocumentSection { Watermark = new() { Text = "First watermark" } };
        var secondSection = new DocumentSection { StartParagraphId = second.Id };
        var resource = new DocumentResource { Kind = DocumentResourceKind.Embedded, MediaType = "image/png", Data = [1, 2, 3] };
        var original = new FlowDocument([first, second]) { Sections = [firstSection, secondSection], Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("watermark", resource) };
        var editor = new TextaloniaEditor { Document = original };
        editor.Session.Select(first.Length + 1, first.Length + 1);
        var owner = Open(editor);
        try
        {
            var pending = editor.ShowWatermarkDialogAsync(); var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<CheckBox>(dialog, "Use image watermark").IsChecked = true;
            Find<ComboBox>(dialog, "Watermark image resource").SelectedItem = "watermark";
            Find<NumericUpDown>(dialog, "Watermark opacity (%)").Value = 35;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.True(pending.GetAwaiter().GetResult());
            Assert.Equal("watermark", editor.Document.Sections[1].Watermark!.ResourceId);
            Assert.Equal(.35, editor.Document.Sections[1].Watermark!.Opacity);
            Assert.Equal(firstSection, editor.Document.Sections[0]);
            editor.Undo(); Assert.Same(original, editor.Document);
            pending = editor.ShowWatermarkDialogAsync(); dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<TextBox>(dialog, "Watermark text").Text = "DRAFT";
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs(); Assert.True(pending.GetAwaiter().GetResult());
            Assert.Equal("DRAFT", editor.Document.Sections[1].Watermark!.Text);
            pending = editor.ShowWatermarkDialogAsync(); dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Click(dialog, "Remove watermark from section"); Dispatcher.UIThread.RunJobs(); Assert.True(pending.GetAwaiter().GetResult());
            Assert.Null(editor.Document.Sections[1].Watermark); Assert.Equal(firstSection, editor.Document.Sections[0]);
            editor.Undo(); Assert.Equal("DRAFT", editor.Document.Sections[1].Watermark!.Text);
        }
        finally { Close(owner); }
    });

    [Fact]
    public Task Object_commands_target_active_story_and_allow_readonly_extraction() => Run(() =>
    {
        var image = new InlineDescriptor { Payload = new ImageInlinePayload("image") };
        var editor = new TextaloniaEditor { Document = new([new Paragraph([new RichRun(image)])]) };
        editor.EditHeader(); var story = editor.ActiveStoryId;
        var original = editor.Document;
        var ole = new InlineDescriptor { Payload = new OleInlinePayload("package", "preview") { FileName = "report.bin" } };
        var package = new DocumentResource { Kind = DocumentResourceKind.Embedded, Data = [1, 2, 3] };
        var preview = new DocumentResource { Kind = DocumentResourceKind.Embedded, MediaType = "image/png", Data = [4, 5, 6] };
        editor.InsertOle(ole, package, preview);
        Assert.Equal(ole, editor.CurrentOleObject); Assert.Null(editor.CurrentImage);
        editor.IsReadOnly = true;
        Assert.Equal(package, editor.ExtractOle(ole.Id));
        var inserted = editor.Document;
        editor.RemoveCurrentImageOrOle(); editor.UpdateImage(ole.Id, new() { Rotation = 90 });
        Assert.Same(inserted, editor.Document);
        editor.IsReadOnly = false; editor.RemoveCurrentImageOrOle();
        Assert.Equal("", editor.Document.GetStoryDocument(story).Text);
        Assert.Equal(image, Assert.IsType<Paragraph>(editor.Document.Blocks[0]).Runs[0].Inline);
        editor.Undo(); Assert.Equal(ole, editor.CurrentOleObject);
        editor.Undo(); Assert.Same(original, editor.Document);
        editor.CloseStory(); editor.Session.Select(0, 1); Assert.Equal(image, editor.CurrentImage);
    });

    [Theory]
    [InlineData(.5)]
    [InlineData(2d)]
    public Task Floating_picture_drag_and_resize_share_zoom_and_commit_once(double zoom) => Run(() =>
    {
        var descriptor = new InlineDescriptor
        {
            Payload = new ImageInlinePayload("picture"), Width = 100, Height = 50,
            Placement = new() { Anchor = ImageAnchorKind.Page, Wrap = ImageWrapKind.InFrontOfText, X = 100, Y = 150 }
        };
        var original = new FlowDocument([new Paragraph([new RichRun(descriptor), new RichRun("tail")])]);
        var editor = new TextaloniaEditor { Document = original, ViewMode = DocumentViewMode.PrintLayout, ShowToolbar = false, Zoom = zoom };
        var pointer = new DefaultPointerComponent(); editor.PointerComponent = pointer;
        var owner = Open(editor);
        var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
        try
        {
            surface.EnsureLayout(surface.Bounds.Width);
            var visual = Assert.Single(surface.GeometryInlineVisuals());
            var start = surface.TranslatePoint(visual.Bounds.Center, owner)!.Value;
            var end = start + new Vector(30 * zoom, 20 * zoom);
            Assert.NotNull(surface.HitTestImage(visual.Bounds.Center));
            owner.MouseDown(start, MouseButton.Left); Assert.Equal(1, editor.Session.Selection.Length); Assert.True(pointer.ImagePointer!.IsActive, "Active after press"); owner.MouseMove(end, RawInputModifiers.LeftMouseButton); Assert.True(pointer.ImagePointer!.IsActive, "Active after move"); Assert.NotEqual(visual.Bounds, pointer.ImagePointer.PreviewBounds);
            Assert.Equal(descriptor.Id, editor.CurrentImage!.Id); Assert.Same(original, editor.Document);
            owner.MouseUp(end, MouseButton.Left);
            Assert.Equal(130, editor.CurrentImage!.Placement!.X); Assert.Equal(170, editor.CurrentImage.Placement.Y);
            editor.Undo(); Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
            owner.UpdateLayout(); surface.EnsureLayout(surface.Bounds.Width);
            visual = Assert.Single(surface.GeometryInlineVisuals());
            start = surface.TranslatePoint(visual.Bounds.BottomRight, owner)!.Value;
            end = start + new Vector(40 * zoom, 10 * zoom);
            Assert.NotNull(surface.HitTestImage(visual.Bounds.Center));
            owner.MouseDown(start, MouseButton.Left); Assert.Equal(1, editor.Session.Selection.Length); Assert.True(pointer.ImagePointer!.IsActive, "Active after press"); owner.MouseMove(end, RawInputModifiers.LeftMouseButton); Assert.True(pointer.ImagePointer!.IsActive, "Active after move"); Assert.NotEqual(visual.Bounds, pointer.ImagePointer.PreviewBounds);
            Assert.Same(original, editor.Document);
            owner.MouseUp(end, MouseButton.Left);
            Assert.Equal(140, editor.CurrentImage!.Width); Assert.Equal(70, editor.CurrentImage.Height);
            editor.Undo(); owner.UpdateLayout(); surface.EnsureLayout(surface.Bounds.Width);
            Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
            start = surface.TranslatePoint(Assert.Single(surface.GeometryInlineVisuals()).Bounds.Center, owner)!.Value;
            owner.MouseDown(start, MouseButton.Left); owner.MouseMove(start + new Vector(20 * zoom, 0), RawInputModifiers.LeftMouseButton);
            owner.KeyPress(Key.Escape, RawInputModifiers.None); owner.MouseUp(start, MouseButton.Left);
            Assert.Same(original, editor.Document); Assert.False(editor.Session.CanUndo);
            editor.IsReadOnly = true;
            owner.MouseDown(start, MouseButton.Left); owner.MouseMove(start + new Vector(20 * zoom, 0), RawInputModifiers.LeftMouseButton); owner.MouseUp(start, MouseButton.Left);
            Assert.Same(original, editor.Document); Assert.Equal(descriptor.Id, editor.CurrentImage!.Id);
        }
        finally { Close(owner); }
    });
    private static Window Open(TextaloniaEditor editor)
    {
        var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout(); return owner;
    }
    private static void Close(Window owner)
    {
        foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close();
    }
    private static T Find<T>(Window dialog, string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
    private static void Click(Window dialog, string content) => dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
