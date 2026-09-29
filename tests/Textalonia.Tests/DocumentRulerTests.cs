using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class DocumentRulerTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Markers_follow_page_geometry_zoom_and_scroll() => fixture.Session.Dispatch(() =>
    {
        var editor = CreateEditor();
        var window = new Window { Width = 320, Height = 400, Content = editor };
        try
        {
            window.Show(); Settle(window);
            editor.Zoom = 2; Settle(window);
            editor.Scroller!.Offset = new Vector(64, 40); Settle(window);
            var metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            var horizontal = metrics.Markers(RulerOrientation.Horizontal);
            var vertical = metrics.Markers(RulerOrientation.Vertical);
            Assert.Equal(metrics.Page.ContentBounds.Left * 2 - editor.Scroller.Offset.X,
                horizontal.Single(marker => marker.Kind == RulerMarkerKind.LeftMargin).Position, 3);
            Assert.Equal(metrics.Page.ContentBounds.Top * 2 - editor.Scroller.Offset.Y,
                vertical.Single(marker => marker.Kind == RulerMarkerKind.TopMargin).Position, 3);
            Assert.Equal(2, horizontal.Count(marker => marker.Kind == RulerMarkerKind.TabStop));
            Assert.Single(horizontal, marker => marker.Kind == RulerMarkerKind.ColumnBoundary);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Ruler_edits_commit_once_and_undo_to_original_values() => fixture.Session.Dispatch(() =>
    {
        var editor = CreateEditor();
        var window = new Window { Width = 600, Height = 500, Content = editor };
        try
        {
            window.Show(); Settle(window);
            var metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            var original = editor.CurrentPageSettings;
            RulerEdit.Apply(editor, metrics, metrics.Markers(RulerOrientation.Horizontal)
                .Single(marker => marker.Kind == RulerMarkerKind.LeftMargin), 12);
            Assert.Equal(original.Margins.Left + 12, editor.CurrentPageSettings.Margins.Left);
            Assert.True(editor.Session.CanUndo);
            editor.Undo();
            Assert.Equal(original.Margins.Left, editor.CurrentPageSettings.Margins.Left);

            metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            var originalIndent = metrics.Paragraph.Indent;
            RulerEdit.Apply(editor, metrics, metrics.Markers(RulerOrientation.Horizontal)
                .Single(marker => marker.Kind == RulerMarkerKind.LeftIndent), 16);
            Assert.Equal(originalIndent + 16, new DocumentStyleResolver(editor.Document)
                .ResolveParagraphStyle(editor.Session.Index.At(0).Paragraph.Style).Indent);
            editor.Undo();
            Assert.Equal(originalIndent, new DocumentStyleResolver(editor.Document)
                .ResolveParagraphStyle(editor.Session.Index.At(0).Paragraph.Style).Indent);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Zero_distance_drag_does_not_add_history() => fixture.Session.Dispatch(() =>
    {
        var editor = CreateEditor();
        var window = new Window { Width = 600, Height = 500, Content = editor };
        try
        {
            window.Show(); Settle(window);
            var metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            RulerEdit.Apply(editor, metrics, metrics.Markers(RulerOrientation.Horizontal)
                .Single(marker => marker.Kind == RulerMarkerKind.LeftIndent), 0);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Pointer_drag_previews_then_commits_one_undo_step_and_escape_cancels() => fixture.Session.Dispatch(() =>
    {
        var editor = CreateEditor();
        var window = new Window { Width = 600, Height = 500, Content = editor };
        try
        {
            window.Show(); Settle(window);
            var ruler = editor.GetVisualDescendants().OfType<DocumentRuler>()
                .Single(control => control.Orientation == RulerOrientation.Horizontal);
            Assert.True(ruler.IsVisible);
            var metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            var left = metrics.Markers(RulerOrientation.Horizontal)
                .Single(marker => marker.Kind == RulerMarkerKind.LeftMargin);
            var start = ruler.TranslatePoint(new Point(left.Position, 5), window)!.Value;
            var original = editor.Document;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(16, 0), RawInputModifiers.LeftMouseButton);
            Assert.Same(original, editor.Document);
            Assert.False(editor.Session.CanUndo);
            window.MouseUp(start + new Vector(16, 0), MouseButton.Left);
            Assert.Equal(56, editor.CurrentPageSettings.Margins.Left, 3);
            editor.Undo();
            Assert.Equal(40, editor.CurrentPageSettings.Margins.Left, 3);

            metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            left = metrics.Markers(RulerOrientation.Horizontal)
                .Single(marker => marker.Kind == RulerMarkerKind.LeftMargin);
            start = ruler.TranslatePoint(new Point(left.Position, 5), window)!.Value;
            var unchanged = editor.Document;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(12, 0), RawInputModifiers.LeftMouseButton);
            window.KeyPress(Key.Escape, RawInputModifiers.None);
            window.MouseUp(start + new Vector(12, 0), MouseButton.Left);
            Assert.Same(unchanged, editor.Document);
            Assert.Equal(40, editor.CurrentPageSettings.Margins.Left, 3);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Side_by_side_pages_use_the_caret_section_instead_of_the_viewport_page() => fixture.Session.Dispatch(() =>
    {
        var first = new Paragraph("First section");
        var second = new Paragraph("Second section");
        var document = new FlowDocument([first, second])
        {
            Sections = [
                new DocumentSection { PageSettings = new PageSettings { Width = 300, Height = 360,
                    Margins = new(24, 24, 24, 24) } },
                new DocumentSection { StartParagraphId = second.Id, PageSettings = new PageSettings
                    { Width = 300, Height = 360, Margins = new(56, 24, 24, 24) } }
            ]
        };
        var editor = new TextaloniaEditor { Document = document, ViewMode = DocumentViewMode.PrintLayout,
            PagesPerRow = 2, ShowToolbar = false, ShowRulers = true };
        var window = new Window { Width = 760, Height = 440, Content = editor };
        try
        {
            window.Show(); Settle(window);
            var caret = editor.Session.Index.ById(second.Id).Start;
            editor.Session.Select(caret, caret);
            Settle(window);
            Assert.Equal(1, editor.CurrentPageNumber);
            var metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            Assert.Equal(1, metrics.Page.Index);
            Assert.Equal(document.Sections[1].Id, metrics.Page.SectionId);
            var left = metrics.Markers(RulerOrientation.Horizontal)
                .Single(marker => marker.Kind == RulerMarkerKind.LeftMargin);
            RulerEdit.Apply(editor, metrics, left, 12);
            Assert.Equal(24, editor.Document.Sections[0].PageSettings.Margins.Left);
            Assert.Equal(68, editor.Document.Sections[1].PageSettings.Margins.Left);
            editor.Undo();
            Assert.Same(document, editor.Document);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Tab_markers_follow_each_shaped_line_origin_including_first_line_indent() => fixture.Session.Dispatch(() =>
    {
        var paragraph = new Paragraph("\tX\u2028\tY")
        { Style = new ParagraphStyle { Indent = 12, FirstLineIndent = 20, TabStops = [new TabStop(80)] } };
        var editor = new TextaloniaEditor { Document = new FlowDocument([paragraph]),
            ViewMode = DocumentViewMode.PrintLayout, ShowToolbar = false, ShowRulers = true };
        var window = new Window { Width = 600, Height = 450, Content = editor };
        try
        {
            window.Show(); Settle(window);
            var pages = editor.GetVisualDescendants().OfType<DocumentSurface>().Single().PagedLayout!;
            editor.Session.Select(0, 0);
            var first = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            var firstTab = first.Markers(RulerOrientation.Horizontal)
                .Single(marker => marker.Kind == RulerMarkerKind.TabStop);
            Assert.Equal(first.Horizontal(pages.Caret(1).X), firstTab.Position, 3);

            editor.Session.Select(3, 3);
            var second = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            var secondTab = second.Markers(RulerOrientation.Horizontal)
                .Single(marker => marker.Kind == RulerMarkerKind.TabStop);
            Assert.Equal(second.Horizontal(pages.Caret(4).X), secondTab.Position, 3);
            Assert.Equal(20 * editor.Zoom, firstTab.Position - secondTab.Position, 3);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Nested_table_ruler_right_edge_tracks_the_content_container() => fixture.Session.Dispatch(() =>
    {
        var paragraph = new Paragraph("Nested content") { Style = new ParagraphStyle { RightIndent = 9 } };
        var inner = Table.Create(1, 1).SetCell(0, 0, new()
            { Blocks = [paragraph], Padding = new(3, 3, 11, 3) });
        var section = new Section { Blocks = [inner], PaddingEdges = new(5, 5, 13, 5) };
        var outer = Table.Create(1, 2) with { ColumnWidths = [1, 3] };
        outer = outer.SetCell(0, 1, outer.Rows[0][1] with
            { Blocks = [section], Padding = new(7, 7, 17, 7) });
        var editor = new TextaloniaEditor
        {
            Document = new FlowDocument([outer]) { Sections = [new DocumentSection
            { PageSettings = new PageSettings { Width = 400, Height = 500, Margins = new(30, 30, 30, 30) } }] },
            ViewMode = DocumentViewMode.PrintLayout, ShowToolbar = false, ShowRulers = true
        };
        var window = new Window { Width = 600, Height = 520, Content = editor };
        try
        {
            window.Show(); Settle(window);
            var position = editor.Session.Index.ById(paragraph.Id).Start;
            editor.Session.Select(position, position);
            var metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            var pages = editor.GetVisualDescendants().OfType<DocumentSurface>().Single().PagedLayout!;
            var fragment = Assert.Single(pages.Fragments, f => f.ParagraphId == paragraph.Id);
            var right = metrics.Markers(RulerOrientation.Horizontal)
                .Single(marker => marker.Kind == RulerMarkerKind.RightIndent);
            Assert.Equal(metrics.Horizontal(Math.Min(fragment.Bounds.Right, fragment.Clip.Right)), right.Position, 3);
            Assert.True(metrics.ParagraphRight < metrics.Page.ContentBounds.Right);
            Assert.NotNull(metrics.Cell);
            Assert.True(metrics.ParagraphRight < metrics.Cell.Bounds.Right);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Disabled_policy_capabilities_leave_ruler_edits_and_history_untouched() => fixture.Session.Dispatch(() =>
    {
        var editor = CreateEditor();
        var window = new Window { Width = 620, Height = 520, Content = editor };
        try
        {
            window.Show(); Settle(window);
            var ruler = editor.GetVisualDescendants().OfType<DocumentRuler>()
                .Single(control => control.Orientation == RulerOrientation.Horizontal);
            editor.Session.EditPolicy = Deny(EditOperation.Structure);
            var metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            var markers = metrics.Markers(RulerOrientation.Horizontal);
            AssertUnchanged(editor, () =>
            {
                var margin = markers.Single(marker => marker.Kind == RulerMarkerKind.LeftMargin);
                var column = markers.Single(marker => marker.Kind == RulerMarkerKind.ColumnBoundary);
                RulerEdit.Apply(editor, metrics, margin, 12);
                RulerEdit.Apply(editor, metrics, column, 12);
                Drag(window, ruler, margin, 5);
                Drag(window, ruler, column, 12);
            });

            editor.Session.EditPolicy = Deny(EditOperation.Formatting);
            metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(editor));
            markers = metrics.Markers(RulerOrientation.Horizontal);
            AssertUnchanged(editor, () =>
            {
                var indent = markers.Single(marker => marker.Kind == RulerMarkerKind.LeftIndent);
                var tab = markers.First(marker => marker.Kind == RulerMarkerKind.TabStop);
                RulerEdit.Apply(editor, metrics, indent, 12);
                RulerEdit.Apply(editor, metrics, tab, 12);
                Drag(window, ruler, indent, 19);
                Drag(window, ruler, tab, 12);
            });
        }
        finally { window.Close(); }

        var table = Table.Create(1, 2);
        var tableEditor = new TextaloniaEditor
        {
            Document = new FlowDocument([table]), ViewMode = DocumentViewMode.PrintLayout,
            ShowToolbar = false, ShowRulers = true
        };
        var tableWindow = new Window { Width = 720, Height = 520, Content = tableEditor };
        try
        {
            tableWindow.Show(); Settle(tableWindow);
            tableEditor.Session.EditPolicy = Deny(EditOperation.Tables);
            var metrics = Assert.IsType<RulerMetrics>(RulerMetrics.Read(tableEditor));
            var cell = Assert.IsType<TableCellVisual>(metrics.Cell);
            var boundary = metrics.Markers(RulerOrientation.Horizontal)
                .Single(marker => marker.Kind == RulerMarkerKind.TableColumnBoundary);
            AssertUnchanged(tableEditor, () =>
            {
                Assert.False(tableEditor.BeginTableResize(table.Id, TableResizeAxis.Column, cell.Column, cell.ColumnWidth));
                var ruler = tableEditor.GetVisualDescendants().OfType<DocumentRuler>()
                    .Single(control => control.Orientation == RulerOrientation.Horizontal);
                var start = ruler.TranslatePoint(new Point(boundary.Position, 19), tableWindow)!.Value;
                tableWindow.MouseDown(start, MouseButton.Left);
                tableWindow.MouseMove(start + new Vector(20, 0), RawInputModifiers.LeftMouseButton);
                tableWindow.MouseUp(start + new Vector(20, 0), MouseButton.Left);
            });
        }
        finally { tableWindow.Close(); }

        static EditPolicy Deny(EditOperation operation) => new()
        {
            Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty
                .Add(operation, CommandCapability.Disabled)
        };
        static void Drag(Window host, DocumentRuler ruler, RulerMarker marker, double cross)
        {
            var start = ruler.TranslatePoint(new Point(marker.Position, cross), host)!.Value;
            host.MouseDown(start, MouseButton.Left);
            host.MouseMove(start + new Vector(12, 0), RawInputModifiers.LeftMouseButton);
            host.MouseUp(start + new Vector(12, 0), MouseButton.Left);
        }
        static void AssertUnchanged(TextaloniaEditor target, Action action)
        {
            var document = target.Document;
            var revision = target.Session.Revision;
            var history = target.Session.RetainedHistoryBytes;
            var canUndo = target.Session.CanUndo;
            action();
            Assert.Same(document, target.Document);
            Assert.Equal(revision, target.Session.Revision);
            Assert.Equal(history, target.Session.RetainedHistoryBytes);
            Assert.Equal(canUndo, target.Session.CanUndo);
        }
    }, CancellationToken.None);

    [Fact]
    public Task Ruler_automation_name_uses_the_editor_localizer() => fixture.Session.Dispatch(() =>
    {
        var editor = CreateEditor();
        editor.Commands.Localize = key => key switch
        {
            "Textalonia.UI.Ruler.Horizontal.Name" => "Regla horizontal",
            "Textalonia.UI.Ruler.Horizontal.Instructions" => "Seleccione y ajuste la marca.",
            "Textalonia.UI.Ruler.Marker.LeftMargin" => "Margen izquierdo",
            _ => null
        };
        var window = new Window { Width = 600, Height = 500, Content = editor };
        try
        {
            window.Show(); Settle(window);
            var ruler = editor.GetVisualDescendants().OfType<DocumentRuler>()
                .Single(control => control.Orientation == RulerOrientation.Horizontal);
            ruler.Focus();
            var name = AutomationProperties.GetName(ruler);
            Assert.Contains("Regla horizontal", name);
            Assert.Contains("Margen izquierdo", name);
            Assert.Contains("Seleccione y ajuste la marca.", name);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    private static TextaloniaEditor CreateEditor() => new()
    {
        ShowToolbar = false,
        ShowRulers = true,
        ViewMode = DocumentViewMode.PrintLayout,
        Document = new FlowDocument([new Paragraph("Ruler geometry and editable markers")
        {
            Style = new ParagraphStyle { Indent = 12, FirstLineIndent = 8, RightIndent = 10,
                TabStops = [new TabStop(40), new TabStop(80)] }
        }])
        {
            Sections = [new DocumentSection { PageSettings = new PageSettings
            {
                Width = 400, Height = 500, Margins = new(40, 60, 40, 60),
                Columns = [new PageColumn(1), new PageColumn(2)]
            } }]
        }
    };

    private static void Settle(Window window)
    {
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    }
}
