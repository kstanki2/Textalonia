using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
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
}
