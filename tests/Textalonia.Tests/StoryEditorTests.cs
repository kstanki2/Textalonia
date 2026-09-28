using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class StoryEditorTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);

    private static (Window Window, TextaloniaEditor Editor, DocumentSurface Surface) Create()
    {
        var editor = new TextaloniaEditor { Document = FlowDocument.FromText("Body selection survives"),
            ShowToolbar = false, ViewMode = DocumentViewMode.PrintLayout };
        var window = new Window { Width = 900, Height = 800, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument();
        return (window, editor, editor.GetVisualDescendants().OfType<DocumentSurface>().Single());
    }

    [Fact]
    public Task Commands_route_typing_composition_and_escape_to_the_active_story() => Run(() =>
    {
        var (window, editor, surface) = Create();
        try
        {
            editor.Session.Select(9, 4);
            editor.EditHeader();
            var story = editor.ActiveStoryId;
            Assert.NotEqual(Guid.Empty, story);
            window.KeyTextInput("Header"); window.UpdateLayout();
            Assert.Equal("Header", editor.Document.GetStoryDocument(story).Text);
            Assert.Equal("Body selection survives", editor.Text);
            surface.Composition.SetPreedit(" preview", 3); window.UpdateLayout();
            Assert.Equal("Header preview", surface.Composition.PreviewDocument!.GetStoryDocument(story).Text);
            Assert.Equal("Body selection survives", surface.Composition.PreviewDocument.Text);
            Assert.True(surface.CaretRectangle.Width > 0);
            surface.Composition.Cancel();
            window.KeyPress(Key.Escape, RawInputModifiers.None);
            Assert.Equal(Guid.Empty, editor.ActiveStoryId);
            Assert.Equal(9, editor.SelectionStart); Assert.Equal(4, editor.SelectionEnd);
            editor.Session.Undo();
            Assert.Equal("", editor.Document.GetStoryDocument(story).Text);
            Assert.Equal("Body selection survives", editor.Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(.5)]
    [InlineData(2d)]
    public Task Header_hit_testing_and_selection_share_scaled_page_geometry(double zoom) => Run(() =>
    {
        var (window, editor, surface) = Create();
        try
        {
            editor.EditHeader(); editor.Session.InsertText("Header text");
            editor.Zoom = zoom; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var story = editor.ActiveStoryId;
            var rect = surface.GeometryCaret(3);
            var hit = surface.GeometryHitTest(new Point(rect.X, rect.Center.Y));
            Assert.Equal(3, hit);
            var expected = surface.PagedLayout!.Caret(story, 3, editor.ActiveStoryPageIndex);
            Assert.Equal(expected.X * zoom, rect.X, 2);
            Assert.Equal(expected.Y * zoom, rect.Y, 2);
            editor.Session.Select(1, 5);
            Assert.NotEmpty(surface.GeometrySelectionRects(1, 4));
            editor.CloseStory();
            Assert.Equal(Guid.Empty, editor.ActiveStoryId);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Note_commands_edit_and_remove_notes_without_replacing_the_body() => Run(() =>
    {
        var (window, editor, surface) = Create();
        try
        {
            editor.Session.Select(4, 4); editor.InsertFootnote("*");
            var note = Assert.Single(editor.Document.Notes);
            Assert.Equal(note.StoryId, editor.ActiveStoryId);
            window.KeyTextInput("Rich note"); window.UpdateLayout();
            Assert.Equal("Rich note", editor.Document.GetStoryDocument(note.StoryId).Text);
            Assert.Contains(surface.PagedLayout!.StoryRegions, r => r.StoryId == note.StoryId);
            Assert.True(surface.CaretRectangle.Height > 0);
            editor.CloseStory();
            Assert.Equal(5, editor.SelectionEnd);
            editor.EditNote(note.Id); editor.Session.RemoveNote(note.Id);
            Assert.Equal(Guid.Empty, editor.ActiveStoryId);
            Assert.Empty(editor.Document.Notes);
            Assert.Equal("Body selection survives", editor.Document.Text);
            editor.Session.Undo();
            Assert.Single(editor.Document.Notes);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Hidden_even_header_is_editable_and_read_only_cannot_create_a_story() => Run(() =>
    {
        var (window, editor, surface) = Create();
        try
        {
            editor.IsReadOnly = true; editor.EditHeader();
            Assert.Empty(editor.Document.Stories);
            editor.IsReadOnly = false;
            editor.EditHeaderFooter(false, HeaderFooterVariant.Even);
            window.KeyTextInput("Even"); window.UpdateLayout();
            Assert.Equal("Even", editor.Document.GetStoryDocument(editor.ActiveStoryId).Text);
            Assert.True(surface.CaretRectangle.Height > 0);
            editor.CloseStory(); window.UpdateLayout();
            Assert.NotNull(surface.PagedLayout);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Double_click_page_margin_activates_a_header() => Run(() =>
    {
        var (window, editor, surface) = Create();
        try
        {
            var sheet = surface.PagedLayout!.Pages[0];
            var position = surface.TranslatePoint(new Point(sheet.ContentBounds.X + 20, sheet.Bounds.Y + 48), window)!.Value;
            window.MouseDown(position, MouseButton.Left); window.MouseUp(position, MouseButton.Left);
            window.MouseDown(position, MouseButton.Left); window.MouseUp(position, MouseButton.Left);
            Assert.NotEqual(Guid.Empty, editor.ActiveStoryId);
            Assert.Equal(DocumentStoryKind.Header, editor.Document.Stories[editor.ActiveStoryId].Kind);
            window.KeyTextInput("Margin header");
            Assert.Equal("Margin header", editor.Document.GetStoryDocument(editor.ActiveStoryId).Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Story_pages_render_headers_footers_fields_and_note_continuations() => Run(() =>
    {
        var body = new Paragraph("A document with independent stories", new TextStyle { FontSize = 23, Bold = true });
        var noteStory = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [new Paragraph(string.Join(" ", Enumerable.Repeat(
            "A note is rich content with its own editing selection and shared document history.", 8)), new TextStyle { FontSize = 12 })] };
        var note = new DocumentNote { StoryId = noteStory.Id, Kind = DocumentNoteKind.Footnote };
        var reference = new Paragraph([new RichRun("The reference stays with the note"), new RichRun(InlineDescriptor.Note(note.Id)), new RichRun(" as the text flows across sheets.")]);
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("TEXTALONIA  /  RESEARCH", new TextStyle { FontSize = 12, Foreground = "#42648B" })] };
        var footer = new DocumentStory { Kind = DocumentStoryKind.Footer, Blocks = [new Paragraph([
            new RichRun("Page "), new RichRun(InlineDescriptor.PageField(PageFieldKind.Page)), new RichRun(" of "),
            new RichRun(InlineDescriptor.PageField(PageFieldKind.NumPages))]) { Style = new() { Alignment = ParagraphAlignment.Right } }] };
        var doc = new FlowDocument([body, reference, new Paragraph(string.Join(" ", Enumerable.Repeat(
            "Headers repeat on each page. Page fields use the context of that page, while the note can continue below the body on later sheets.", 5)))])
        {
            Stories = new[] { noteStory, header, footer }.ToImmutableDictionary(story => story.Id), Notes = [note],
            Sections = [new DocumentSection { PageSettings = new() { Width = 400, Height = 560, Margins = new(32, 68, 32, 60) },
                HeaderFooter = new() { HeaderDistance = 24, FooterDistance = 24,
                    PrimaryHeader = new() { LinkToPrevious = false, StoryId = header.Id }, PrimaryFooter = new() { LinkToPrevious = false, StoryId = footer.Id } } }]
        };
        var editor = new TextaloniaEditor { Document = doc, ViewMode = DocumentViewMode.PrintLayout, ShowToolbar = false,
            PagesPerRow = 2, Zoom = 1, FontFamily = new FontFamily("Inter") };
        var window = new Window { Width = 920, Height = 700, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            Assert.True(surface.PagedLayout!.Pages.Length >= 2);
            Assert.Contains(surface.PagedLayout.StoryRegions, r => r.StoryId == noteStory.Id && r.IsContinuation);
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/stories"));
            Directory.CreateDirectory(directory);
            using (var image = window.CaptureRenderedFrame())
            {
                Assert.NotNull(image); image.Save(Path.Combine(directory, "story-pages.png"), PngBitmapEncoderOptions.Default);
            }
            editor.EditHeader(); editor.Session.Select(0, 10); window.UpdateLayout();
            Assert.All(surface.PagedLayout!.InlineVisuals().Where(v => v.StoryId != editor.ActiveStoryId), v => Assert.False(surface.IsInlineSelected(v)));
            using var active = window.CaptureRenderedFrame();
            Assert.NotNull(active); active.Save(Path.Combine(directory, "story-active-header.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Repeated_header_navigation_uses_the_target_sections_link_context() => Run(() =>
    {
        var first = new Paragraph("first"); var second = new Paragraph("second");
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("Shared")] };
        var page = new PageSettings { Width = 400, Height = 400, Margins = new(36, 72, 36, 72) };
        var document = new FlowDocument([first, second]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header),
            Sections = [new DocumentSection { PageSettings = page, HeaderFooter = new() { PrimaryHeader = new() { StoryId = header.Id, LinkToPrevious = false } } },
                new DocumentSection { PageSettings = page, StartParagraphId = second.Id }] };
        var editor = new TextaloniaEditor { Document = document, ViewMode = DocumentViewMode.PrintLayout, PagesPerRow = 2, ShowToolbar = false };
        var window = new Window { Content = editor, Width = 940, Height = 600 };
        try
        {
            window.Show(); window.UpdateLayout(); editor.EditHeader(); editor.Session.Select(2, 2);
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single(); window.UpdateLayout();
            surface.MovePagedCaret(true, true, 20, false);
            Assert.Equal(1, editor.ActiveStoryPageIndex);
            editor.LinkHeaderFooterToPrevious(false);
            var local = editor.Document.ResolveHeaderFooter(1, false, HeaderFooterVariant.Primary)!;
            Assert.NotEqual(header.Id, local.Id);
            Assert.Equal(header.Id, editor.Document.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary)!.Id);
            editor.CloseStory(); editor.Session.Select(2, 2); editor.InsertFootnote();
            var before = editor.Document;
            editor.LinkHeaderFooterToPrevious(true);
            Assert.Same(before, editor.Document);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Completely_clipped_header_opens_a_visible_editing_surface() => Run(() =>
    {
        var (window, editor, surface) = Create();
        try
        {
            editor.Session.SetHeaderFooterSettings(Guid.Empty, new() { HeaderDistance = 100 });
            editor.EditHeader(); editor.Session.InsertText("Still editable"); window.UpdateLayout();
            Assert.Null(surface.PagedLayout);
            Assert.True(surface.CaretRectangle.Height > 0);
        }
        finally { window.Close(); }
    });
}
