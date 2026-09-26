using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Baselines;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class ScalableLayoutTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static readonly Thickness Padding = new(28);
    private static FontFamily Font => new("avares://Avalonia.Fonts.Inter/Assets#Inter");
    private static void SameRect(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 6); Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6); Assert.Equal(expected.Height, actual.Height, 6);
    }
    private static void Build(DocumentLayout layout, FlowDocument document, double y = 0, double height = 500, double width = 800) =>
        layout.Build(document, width, Font, Brushes.Black, Brushes.Gray, Padding, new Rect(0, y, width, height));

    [Fact]
    public Task First_viewport_offscreen_edits_resize_and_scroll_have_bounded_shaping_and_cache() => fixture.Session.Dispatch(() =>
    {
        var document = BaselineDocuments.Workload("paragraphs-10000");
        using var layout = new DocumentLayout();
        Build(layout, document);
        Assert.InRange(layout.ShapedParagraphs, 1, 80);
        var count = layout.ShapedParagraphs; var geometry = layout.GeometryNodes;
        var session = new EditorSession(document);
        session.Select(session.Index.Length, session.Index.Length); session.InsertText("offscreen");
        Build(layout, session.Document);
        Assert.Equal(count, layout.ShapedParagraphs);
        Assert.InRange(layout.GeometryNodes - geometry, 1, 2);
        for (var page = 1; page < 40; page++)
        {
            Build(layout, session.Document, page * 2000);
            Assert.InRange(layout.CachedParagraphs, 1, 256);
        }
        Assert.True(layout.DisposedLayouts > 0);
        var shaped = layout.ShapedParagraphs;
        Build(layout, session.Document, width: 640);
        Assert.InRange(layout.ShapedParagraphs - shaped, 1, 80);
        var caret = layout.Caret(session.Index.Length);
        Assert.True(caret.Height > 0); Assert.True(caret.Y > 10000);
        Assert.InRange(layout.CachedParagraphs, 1, 256);
        layout.Clear(); Assert.Equal(0, layout.CachedParagraphs);
    }, CancellationToken.None);

    [Theory]
    [InlineData("structured")]
    [InlineData("mixed-scripts")]
    [InlineData("emoji")]
    [InlineData("long-paragraph")]
    public Task Geometry_matches_frozen_full_measure_reference(string name) => fixture.Session.Dispatch(() =>
    {
        var session = new EditorSession(BaselineDocuments.Create(name));
        using var layout = new DocumentLayout(); using var reference = new ReferenceDocumentLayout();
        void Verify()
        {
            reference.Build(session.Document, 800, Font, Brushes.Black, Brushes.Gray, Padding);
            Build(layout, session.Document, height: reference.Height + 100);
            Assert.Equal(reference.Height, layout.Height, 5);
            foreach (var entry in session.Index.Paragraphs)
            {
                foreach (var offset in new[] { entry.Start, entry.End })
                {
                    var expected = reference.Caret(offset); var actual = layout.Caret(offset);
                    SameRect(expected, actual);
                    var point = new Point(expected.X + .1, expected.Y + expected.Height / 2);
                    Assert.Equal(reference.HitTest(point), layout.HitTest(point));
                }
            }
            Assert.Equal(reference.SelectionRects(0, session.Index.Length).ToArray(), layout.SelectionRects(0, session.Index.Length).ToArray());
        }
        Verify();
        session.Select(0, 0); session.InsertText("new\n"); Verify(); session.Undo(); Verify();
    }, CancellationToken.None);

    [Fact]
    public Task Table_row_spans_measure_dependencies_without_shaping_unrelated_rows() => fixture.Session.Dispatch(() =>
    {
        var table = BaselineDocuments.Grid(100, 10, "layout-spans").MergeCells(0, 0, 3, 2);
        var session = new EditorSession(new FlowDocument([table]));
        using var layout = new DocumentLayout();
        Build(layout, session.Document);
        Assert.InRange(layout.ShapedParagraphs, 1, 400);
        var shaped = layout.ShapedParagraphs;
        session.Select(session.Index.Length, session.Index.Length); session.InsertText("hidden edit");
        Build(layout, session.Document);
        Assert.Equal(shaped, layout.ShapedParagraphs);
        var target = session.Index.ById(table.Rows[0][0].Paragraphs[0].Id);
        session.Select(target.Start, target.Start); session.InsertText(new string('W', 200));
        Build(layout, session.Document);
        Assert.InRange(layout.ShapedParagraphs - shaped, 1, 100);
        using var reference = new ReferenceDocumentLayout();
        reference.Build(session.Document, 800, Font, Brushes.Black, Brushes.Gray, Padding);
        foreach (var paragraph in layout.Paragraphs.Where(p => p.Bounds.Y < 500))
            SameRect(reference.Caret(paragraph.Position.Start), layout.Caret(paragraph.Position.Start));
    }, CancellationToken.None);

    [Fact]
    public Task Height_corrections_preserve_a_viewport_anchor_and_theme_changes_dispose_shapes() => fixture.Session.Dispatch(() =>
    {
        var session = new EditorSession(BaselineDocuments.Workload("paragraphs-1000"));
        using var layout = new DocumentLayout(); Build(layout, session.Document, 12000);
        var anchor = layout.Paragraphs.First(p => p.Bounds.Bottom >= 12000);
        session.Select(0, 0); session.InsertText(string.Join('\n', Enumerable.Repeat("new", 10)) + "\n");
        Build(layout, session.Document, 12000);
        var after = layout.At(session.Index.ById(anchor.Position.Paragraph.Id).Start)!;
        Assert.Equal(after.Origin.Y - anchor.Origin.Y, layout.AnchorAdjustment, 5);
        var disposed = layout.DisposedLayouts;
        layout.Build(session.Document, 800, Font, Brushes.White, Brushes.Gray, Padding, new Rect(0, 12000, 800, 500));
        Assert.True(layout.DisposedLayouts > disposed);
    }, CancellationToken.None);

    [Fact]
    public Task Offscreen_execute_and_theme_changes_keep_the_scrolled_view_away_from_the_caret() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { SynchronizeText = false, Document = BaselineDocuments.Workload("paragraphs-1000") };
        var window = new Window { Width = 800, Height = 500, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout(); editor.FocusDocument();
            var scroller = editor.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PART_ScrollViewer");
            scroller.Offset = new Vector(0, 12000); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var anchor = surface.Layout.Paragraphs.First(p => p.Bounds.Bottom >= scroller.Offset.Y);
            var relative = anchor.Origin.Y - scroller.Offset.Y;
            editor.Session.Execute(d => d with { Blocks = d.Blocks.SetItem(0, new Paragraph("changed offscreen")) });
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var after = surface.Layout.At(editor.Session.Index.ById(anchor.Position.Paragraph.Id).Start)!;
            Assert.Equal(relative, after.Origin.Y - scroller.Offset.Y, 4);
            editor.Foreground = Brushes.DarkBlue;
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(scroller.Offset.Y > 10000);
            after = surface.Layout.At(editor.Session.Index.ById(anchor.Position.Paragraph.Id).Start)!;
            Assert.Equal(relative, after.Origin.Y - scroller.Offset.Y, 4);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Document_mode_avoids_Text_notifications_and_reenabling_synchronizes_immediately() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "before", SynchronizeText = false };
        var window = new Window { Width = 800, Height = 500, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout();
            var notifications = 0;
            editor.PropertyChanged += (_, e) => { if (e.Property == TextaloniaEditor.TextProperty) notifications++; };
            editor.Session.SelectAll(); editor.InsertText("after");
            Assert.Equal(0, editor.Session.Index.FullTextReads);
            Assert.Equal("after", editor.Document.Text); Assert.Equal("before", editor.Text); Assert.Equal(0, notifications);
            editor.SynchronizeText = true;
            Assert.Equal("after", editor.Text); Assert.Equal(1, notifications);
            editor.SynchronizeText = false; editor.Text = "host";
            Assert.Equal("host", editor.Document.Text); Assert.False(editor.Session.CanUndo);
            editor.Document = BaselineDocuments.Workload("paragraphs-1000");
            editor.Session.Select(editor.Session.Index.Length, editor.Session.Index.Length);
            editor.FocusDocument(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            Assert.True(surface.CaretRectangle.Height > 0);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Long_paragraph_windows_bound_first_open_and_reuse_unchanged_line_breaks() => fixture.Session.Dispatch(() =>
    {
        var session = new EditorSession(BaselineDocuments.Workload("long-paragraph"));
        using var layout = new DocumentLayout();
        Build(layout, session.Document);
        Assert.InRange(layout.ShapedCharacters, 1, 8192);
        Assert.InRange(layout.LargestShapingWindow, 1, ParagraphLayout.WindowLength);
        Assert.InRange(layout.CachedLayouts, 1, 8);
        // Discover the exact extent once. Checkpoints survive disposal of glyphs.
        var end = layout.Caret(session.Index.Length);
        Assert.InRange(layout.CachedLayouts, 1, 8);
        var shapes = layout.ShapedCharacters;
        session.Select(session.Index.Length / 2, session.Index.Length / 2); session.InsertText("x");
        Build(layout, session.Document);
        layout.Caret(session.Index.Length);
        Assert.InRange(layout.ShapedCharacters - shapes, 1, 8192);
        using var reference = new ReferenceDocumentLayout();
        reference.Build(session.Document, 800, Font, Brushes.Black, Brushes.Gray, Padding);
        SameRect(reference.Caret(session.Index.Length), layout.Caret(session.Index.Length));
        Assert.True(end.Y > 10000);
        for (var offset = 0; offset <= session.Index.Length; offset += 3000)
        {
            SameRect(reference.Caret(offset), layout.Caret(offset));
            Assert.InRange(layout.CachedLayouts, 1, 256);
            Assert.InRange(layout.CachedLayoutBytes, 1, 16 * 1024 * 1024);
        }
        var disposed = layout.DisposedLayouts;
        layout.Clear(); Assert.Equal(0, layout.CachedLayouts); Assert.True(layout.DisposedLayouts > disposed);
    }, CancellationToken.None);

    [Theory]
    [InlineData(ParagraphAlignment.Left)]
    [InlineData(ParagraphAlignment.Center)]
    [InlineData(ParagraphAlignment.Right)]
    [InlineData(ParagraphAlignment.Justify)]
    public Task Window_seams_preserve_styles_graphemes_soft_breaks_and_geometry(ParagraphAlignment alignment) => fixture.Session.Dispatch(() =>
    {
        var text = string.Concat(Enumerable.Repeat("office e\u0301 👩‍💻 中文 A\tB soft\u2028break ", 120));
        var paragraph = new Paragraph([
            new RichRun(text[..2047], TextStyle.Default),
            new RichRun(text[2047..], TextStyle.Default with { Bold = true, FontSize = 18 })])
        { Style = new() { Alignment = alignment, List = ListKind.Numbered } };
        var session = new EditorSession(new FlowDocument([paragraph, new Paragraph("tail")]));
        using var layout = new DocumentLayout(); using var reference = new ReferenceDocumentLayout();
        void Verify()
        {
            reference.Build(session.Document, 530, Font, Brushes.Black, Brushes.Gray, Padding);
            Build(layout, session.Document, height: reference.Height + 100, width: 530);
            Assert.Equal(reference.Height, layout.Height, 6);
            var seams = layout.Paragraphs.SelectMany(p => new[] { p.TextStart, Math.Max(p.TextStart, p.TextEnd - 1), p.TextEnd }).Distinct().ToArray();
            foreach (var position in seams)
            {
                var expected = reference.Caret(position); SameRect(expected, layout.Caret(position));
                var point = new Point(expected.X + .1, expected.Y + expected.Height / 2);
                Assert.Equal(reference.HitTest(point), layout.HitTest(point));
            }
            Assert.Equal(reference.SelectionRects(0, session.Index.Length), layout.SelectionRects(0, session.Index.Length));
            Assert.Single(layout.Paragraphs, p => p.Marker is not null);
        }
        Verify();
        session.Select(2047, 2047); session.InsertText("inserted "); Verify();
        session.Undo(); Verify(); session.Redo(); Verify();
    }, CancellationToken.None);

    [Fact]
    public Task Large_cells_pass_viewport_limits_to_paragraphs_and_text_windows() => fixture.Session.Dispatch(() =>
    {
        var longParagraph = BaselineDocuments.Create("long-paragraph").Blocks[0] as Paragraph;
        var table = Table.Create(2, 2).SetCell(0, 0, new TableCell { Paragraphs = [longParagraph!] });
        table = table.SetCell(0, 1, new TableCell
        { Paragraphs = System.Collections.Immutable.ImmutableArray.CreateRange(Enumerable.Range(0, 1000).Select(i => new Paragraph($"cell paragraph {i}"))) });
        var document = new FlowDocument([table]);
        using var layout = new DocumentLayout(); Build(layout, document);
        Assert.InRange(layout.ShapedParagraphs, 1, 80);
        Assert.InRange(layout.ShapedCharacters, 1, 8192);
        var index = new DocumentIndex(document);
        var caret = layout.Caret(index.ById(longParagraph!.Id).End);
        Assert.True(caret.Y > 10000);
        Assert.InRange(layout.CachedLayouts, 1, 256);
        using var reference = new ReferenceDocumentLayout(); reference.Build(document, 800, Font, Brushes.Black, Brushes.Gray, Padding);
        SameRect(reference.Caret(index.ById(longParagraph.Id).End), caret);
    }, CancellationToken.None);

    [Fact]
    public Task A_line_inside_a_long_paragraph_anchors_edits_resize_and_theme_changes() => fixture.Session.Dispatch(() =>
    {
        var session = new EditorSession(BaselineDocuments.Workload("long-paragraph"));
        using var layout = new DocumentLayout(); Build(layout, session.Document);
        var offset = session.Index.Length / 2;
        var y = layout.Caret(offset).Y;
        Build(layout, session.Document, y);
        var anchor = layout.HitTest(new Point(28, y + 1));
        var relative = layout.Caret(anchor).Y - y;
        session.Select(0, 0); session.InsertText("new prefix "); anchor += "new prefix ".Length;
        Build(layout, session.Document, y);
        y += layout.AnchorAdjustment;
        Assert.Equal(relative, layout.Caret(anchor).Y - y, 5);
        Build(layout, session.Document, y, width: 640);
        y += layout.AnchorAdjustment;
        Assert.Equal(relative, layout.Caret(anchor).Y - y, 5);
        layout.Build(session.Document, 640, Font, Brushes.DarkBlue, Brushes.Gray, Padding, new Rect(0, y, 640, 500));
        y += layout.AnchorAdjustment;
        Assert.Equal(relative, layout.Caret(anchor).Y - y, 5);
    }, CancellationToken.None);

    [Fact]
    public Task Bidi_embedding_context_across_windows_keeps_reference_geometry() => fixture.Session.Dispatch(() =>
    {
        var document = new FlowDocument([new Paragraph("abc \u202b" + new string('x', 2100) + " العربية 123 \u202c tail")]);
        using var layout = new DocumentLayout(); using var reference = new ReferenceDocumentLayout();
        reference.Build(document, 800, Font, Brushes.Black, Brushes.Gray, Padding);
        Build(layout, document, height: reference.Height + 100);
        foreach (var position in new[] { 0, 2000, 2105, document.Text.Length }) SameRect(reference.Caret(position), layout.Caret(position));
        Assert.Equal(reference.SelectionRects(0, document.Text.Length), layout.SelectionRects(0, document.Text.Length));
        var session = new EditorSession(new FlowDocument([new Paragraph(new string('a', 4500) + " tail")]));
        Build(layout, session.Document, height: 4000);
        session.Select(3000, 3000); session.InsertText("\u202bالعربية\u202c");
        reference.Build(session.Document, 800, Font, Brushes.Black, Brushes.Gray, Padding);
        Build(layout, session.Document, height: reference.Height + 100);
        foreach (var position in new[] { 0, 2900, 3100, session.Index.Length }) SameRect(reference.Caret(position), layout.Caret(position));
        Assert.Equal(reference.SelectionRects(0, session.Index.Length), layout.SelectionRects(0, session.Index.Length));
    }, CancellationToken.None);

    [Fact]
    public Task A_single_paragraph_evicts_windows_while_retaining_exact_checkpoints() => fixture.Session.Dispatch(() =>
    {
        var document = new FlowDocument([new Paragraph(string.Concat(Enumerable.Repeat("bounded paragraph ", 40000)))]);
        using var layout = new DocumentLayout(); Build(layout, document);
        var length = new DocumentIndex(document).Length;
        var end = layout.Caret(length);
        for (var offset = 0; offset < length; offset += 2000)
        {
            var caret = layout.Caret(offset);
            Assert.True(caret.Y <= end.Y);
            Assert.InRange(layout.CachedLayouts, 1, 256);
            Assert.InRange(layout.CachedLayoutBytes, 1, 16 * 1024 * 1024);
        }
        Assert.True(layout.DisposedLayouts > 0);
        SameRect(end, layout.Caret(length));
    }, CancellationToken.None);

    [Fact]
    public Task Uninterrupted_unicode_uses_bounded_windows_without_splitting_graphemes() => fixture.Session.Dispatch(() =>
    {
        var document = new FlowDocument([new Paragraph(string.Concat(Enumerable.Repeat("中文👩‍💻e\u0301", 3000)))]);
        using var layout = new DocumentLayout(); Build(layout, document);
        Assert.InRange(layout.ShapedCharacters, 1, 8192);
        Assert.InRange(layout.LargestShapingWindow, 1, ParagraphLayout.WindowLength);
        var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(document.Text).ToHashSet();
        foreach (var page in layout.Paragraphs)
        { Assert.Contains(page.Page.Start, boundaries); Assert.Contains(page.Page.End, boundaries); }
        using var reference = new ReferenceDocumentLayout(); reference.Build(document, 800, Font, Brushes.Black, Brushes.Gray, Padding);
        SameRect(reference.Caret(25000), layout.Caret(25000));
    }, CancellationToken.None);

    [Fact]
    public Task Minimum_paragraph_height_is_applied_once_across_tiny_text_windows() => fixture.Session.Dispatch(() =>
    {
        var document = new FlowDocument([new Paragraph([new RichRun(new string('i', 10000), TextStyle.Default with { FontSize = 2 })])]);
        using var layout = new DocumentLayout(); using var reference = new ReferenceDocumentLayout();
        reference.Build(document, 800, Font, Brushes.Black, Brushes.Gray, Padding);
        Build(layout, document);
        Assert.Equal(reference.Height, layout.Height, 6);
        foreach (var offset in new[] { 0, 2048, 5000, 9999 })
        {
            var caret = reference.Caret(offset); SameRect(caret, layout.Caret(offset));
            var point = new Point(caret.X, caret.Y + .5);
            Assert.Equal(reference.HitTest(point), layout.HitTest(point));
        }
    }, CancellationToken.None);

    [Fact]
    public Task Home_end_and_ime_use_window_relative_geometry_in_the_control() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { SynchronizeText = false, Document = BaselineDocuments.Workload("long-paragraph"), FontFamily = Font };
        var window = new Window { Width = 800, Height = 500, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout(); editor.FocusDocument();
            editor.Session.Select(45000, 45000); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            using var reference = new ReferenceDocumentLayout();
            reference.Build(editor.Document, surface.Bounds.Width, Font, editor.Foreground!, editor.BorderBrush!, editor.DocumentPadding);
            var full = reference.At(45000)!;
            var line = full.Layout.TextLines[full.Layout.GetLineIndexFromCharacterIndex(45000, false)];
            window.KeyPress(Key.Home, RawInputModifiers.None);
            Assert.Equal(line.FirstTextSourceIndex, editor.Session.Selection.Active);
            editor.Session.Select(45000, 45000);
            window.KeyPress(Key.End, RawInputModifiers.None);
            Assert.Equal(line.FirstTextSourceIndex + line.Length - line.NewLineLength, editor.Session.Selection.Active);
            var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
            surface.RaiseEvent(request);
            var client = Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
            SameRect(surface.CaretRectangle, client.CursorRectangle);
            var before = editor.Session.Index.ReadText(0, editor.Session.Index.Length);
            client.SetPreeditText("日本", 1); window.UpdateLayout();
            Assert.True(client.CursorRectangle.Height > 0);
            Assert.Equal(before, editor.Document.Text);
            window.KeyTextInput("日本"); editor.Undo();
            Assert.Equal(before, editor.Document.Text);
        }
        finally { window.Close(); }
    }, CancellationToken.None);
}
