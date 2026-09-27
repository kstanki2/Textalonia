using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class ContentDragTests
{
    [Theory]
    [InlineData(1, 3, 6, "adefbc")]
    [InlineData(3, 5, 0, "deabcf")]
    public void Internal_move_is_one_history_entry_and_restores_reverse_selection(int start, int end, int target, string expected)
    {
        var session = new EditorSession(FlowDocument.FromText("abcdef"));
        session.Select(end, start); var selection = session.Selection; var revision = session.Revision;
        var snapshot = session.CaptureContentDrag()!;
        Assert.Equal(ContentDropResult.Move, session.DropContent(snapshot.Fragment, target, revision, snapshot, move: true));
        Assert.Equal(expected, session.Document.Text); Assert.Equal(revision + 1, session.Revision);
        session.Undo(); Assert.Equal("abcdef", session.Document.Text); Assert.Equal(selection, session.Selection); Assert.False(session.CanUndo);
        session.Redo(); Assert.Equal(expected, session.Document.Text);
    }

    [Fact]
    public void Internal_copy_preserves_source_and_undoes_once()
    {
        var session = new EditorSession(FlowDocument.FromText("abcdef")); session.Select(1, 3);
        var snapshot = session.CaptureContentDrag()!;
        Assert.Equal(ContentDropResult.Copy, session.DropContent(snapshot.Fragment, 6, session.Revision, snapshot));
        Assert.Equal("abcdefbc", session.Document.Text);
        session.Undo(); Assert.Equal("abcdef", session.Document.Text); Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void Drops_inside_source_including_edges_are_no_ops(int target)
    {
        var session = new EditorSession(FlowDocument.FromText("abcdef")); session.Select(1, 3);
        var snapshot = session.CaptureContentDrag()!;
        foreach (var move in new[] { false, true })
            Assert.Equal(ContentDropResult.None, session.DropContent(snapshot.Fragment, target, session.Revision, snapshot, move));
        Assert.Equal("abcdef", session.Document.Text); Assert.False(session.CanUndo); Assert.False(snapshot.Completed);
    }

    [Fact]
    public void Invalid_insertion_and_readonly_target_preserve_source_selection_and_history()
    {
        var source = new EditorSession(FlowDocument.FromText("source")); source.SelectAll();
        var target = new EditorSession(FlowDocument.FromText("target")); var snapshot = source.CaptureContentDrag()!;
        Assert.Throws<NotSupportedException>(() => target.DropContent(snapshot.Fragment with { Version = 99 }, 2, target.Revision, snapshot, true));
        Assert.Equal("source", source.Document.Text); Assert.Equal("target", target.Document.Text);
        Assert.False(source.CanUndo); Assert.False(target.CanUndo); Assert.False(snapshot.Completed);
        target.IsReadOnly = true;
        Assert.Equal(ContentDropResult.None, target.DropContent(snapshot.Fragment, 2, target.Revision, snapshot, true));
        Assert.Equal("source", source.SelectedText);
    }

    [Fact]
    public void Stale_source_or_preview_revision_rejects_copy_and_move()
    {
        var source = new EditorSession(FlowDocument.FromText("source")); source.SelectAll();
        var target = new EditorSession(FlowDocument.FromText("target")); var snapshot = source.CaptureContentDrag()!;
        var revision = target.Revision; target.InsertText("new");
        Assert.Equal(ContentDropResult.None, target.DropContent(snapshot.Fragment, 2, revision, snapshot, true));
        source.InsertText("edited");
        Assert.Equal(ContentDropResult.None, target.DropContent(snapshot.Fragment, 2, target.Revision, snapshot));
        Assert.Equal("newtarget", target.Document.Text); Assert.Equal("edited", source.Document.Text);
    }

    [Fact]
    public void Cross_editor_move_has_independent_histories_and_selection_only_changes_are_safe()
    {
        var source = new EditorSession(FlowDocument.FromText("abcdef")); source.Select(1, 3);
        var target = new EditorSession(FlowDocument.FromText("target")); var snapshot = source.CaptureContentDrag()!;
        source.Select(5, 5);
        Assert.Equal(ContentDropResult.Move, target.DropContent(snapshot.Fragment, 2, target.Revision, snapshot, true));
        Assert.Equal("adef", source.Document.Text); Assert.Equal("tabcrget", target.Document.Text);
        target.Undo(); Assert.Equal("target", target.Document.Text); Assert.Equal("adef", source.Document.Text);
        source.Undo(); Assert.Equal("abcdef", source.Document.Text); Assert.False(source.CanUndo); Assert.False(target.CanUndo);
    }

    [Fact]
    public void Target_callback_cannot_delete_new_source_content()
    {
        var source = new EditorSession(FlowDocument.FromText("source")); source.Select(1, 3);
        var target = new EditorSession(); var snapshot = source.CaptureContentDrag()!;
        target.Changed += (_, _) => source.InsertText("new");
        Assert.Equal(ContentDropResult.Copy, target.DropContent(snapshot.Fragment, 0, target.Revision, snapshot, true));
        Assert.Equal("snewrce", source.Document.Text); Assert.Equal("ou", target.Document.Text);
    }

    [Fact]
    public void Host_callback_cannot_reuse_an_active_cross_editor_drag()
    {
        var source = new EditorSession(FlowDocument.FromText("source")); source.Select(1, 3);
        var target = new EditorSession(); var snapshot = source.CaptureContentDrag()!;
        target.Changed += (_, _) => Assert.Equal(ContentDropResult.None,
            target.DropContent(snapshot.Fragment, target.Index.Length, target.Revision, snapshot, true));
        Assert.Equal(ContentDropResult.Move, target.DropContent(snapshot.Fragment, 0, target.Revision, snapshot, true));
        Assert.Equal("srce", source.Document.Text); Assert.Equal("ou", target.Document.Text);
    }

    [Fact]
    public void Nested_table_move_retains_resource_and_removes_complete_source_container()
    {
        var image = new InlineDescriptor { Payload = new ImageInlinePayload("image"), AltText = "image" };
        var nested = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph([new RichRun(image)])] });
        var table = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [nested] });
        var document = new FlowDocument([new Paragraph("before"), table, new Paragraph("after")])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", new() { Kind = DocumentResourceKind.Embedded, MediaType = "image/png", Data = [1, 2, 3] }) };
        var session = new EditorSession(document);
        var entry = session.Index.Paragraphs.Single(p => p.Paragraph.Runs.Any(r => r.Inline is not null));
        session.Select(entry.Start, entry.End); var snapshot = session.CaptureContentDrag()!;
        Assert.Equal(ContentDropResult.Move, session.DropContent(snapshot.Fragment, session.Index.Length, session.Revision, snapshot, true));
        session.Document.Validate(); var moved = Assert.Single(session.Document.Blocks.OfType<Table>());
        Assert.IsType<Table>(Assert.Single(moved.Rows[0][0].Blocks)); Assert.Single(session.Document.Resources);
        Assert.Single(session.Index.Paragraphs.SelectMany(p => p.Paragraph.Runs), r => r.Inline is not null);
        session.Undo(); Assert.Same(document, session.Document); Assert.False(session.CanUndo);
    }
}

public class ContentDragControlTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private const DragDropEffects Both = DragDropEffects.Copy | DragDropEffects.Move;
    [Fact]
    public Task Modifiers_choose_move_or_copy_and_readonly_sources_only_copy() => fixture.Session.Dispatch(() =>
    {
        var source = new TextaloniaEditor { Text = "abcdef" }; source.Session.Select(1, 3);
        var target = new TextaloniaEditor { Text = "target" }; var snapshot = source.Session.CaptureContentDrag()!;
        var token = TextaloniaEditor.RegisterContentDrag(snapshot);
        try
        {
            using var data = source.CreateContentDragData(snapshot, token);
            Assert.Equal(DragDropEffects.Move, source.GetContentDropEffect(data, 6, source.Session.Revision, KeyModifiers.None, Both));
            Assert.Equal(DragDropEffects.Copy, source.GetContentDropEffect(data, 6, source.Session.Revision, KeyModifiers.Control, Both));
            Assert.Equal(DragDropEffects.Copy, target.GetContentDropEffect(data, 2, target.Session.Revision, KeyModifiers.None, Both));
            Assert.Equal(DragDropEffects.Move, target.GetContentDropEffect(data, 2, target.Session.Revision, KeyModifiers.Shift, Both));
            source.IsReadOnly = true;
            Assert.Equal(DragDropEffects.Copy, target.GetContentDropEffect(data, 2, target.Session.Revision, KeyModifiers.Shift, Both));
            Assert.Equal(DragDropEffects.Copy, target.DropContent(data, 2, target.Session.Revision, KeyModifiers.Shift, Both));
            Assert.Equal("abcdef", source.Text); Assert.Equal("tabcrget", target.Text);
        }
        finally { TextaloniaEditor.FinishContentDrag(token); }
    }, CancellationToken.None);

    [Fact]
    public Task Native_html_text_fallbacks_and_unsupported_payloads_are_safe() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "target" }; using var data = new DataTransfer(); var item = new DataTransferItem();
        item.Set(DataFormat.CreateStringApplicationFormat("org.textalonia.fragment"), "{\"fragmentVersion\":99}");
        item.Set(DataFormat.CreateStringPlatformFormat("text/html"), "<p><b>HTML</b></p>"); item.SetText("plain"); data.Add(item);
        Assert.Equal(DragDropEffects.Copy, editor.DropContent(data, 6, editor.Session.Revision, KeyModifiers.Shift, Both));
        Assert.Equal("targetHTML", editor.Text); Assert.True(editor.Session.Index.At(6).Paragraph.Runs.Last().Style.Bold);
        Assert.Contains(editor.LastConversionReport.Diagnostics, d => d.Code == "drop.native-rejected"); editor.Undo();
        using var plain = new DataTransfer(); plain.Add(DataTransferItem.CreateText("plain"));
        Assert.Equal(DragDropEffects.Copy, editor.DropContent(plain, 2, editor.Session.Revision, KeyModifiers.None, Both));
        Assert.Equal("taplainrget", editor.Text); editor.Undo();
        using var unknown = new DataTransfer(); unknown.Add(DataTransferItem.Create(DataFormat.CreateStringApplicationFormat("unsupported"), "payload"));
        Assert.Equal(DragDropEffects.None, editor.DropContent(unknown, 0, editor.Session.Revision, KeyModifiers.None, Both));
        Assert.Equal("target", editor.Text); Assert.False(editor.Session.CanUndo);
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task Actual_pointer_threshold_keeps_selection_and_starts_native_drag_once(bool readOnly) => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "abcdefgh", IsReadOnly = readOnly };
        var window = new Window { Width = 700, Height = 400, Content = editor }; window.Show(); window.UpdateLayout();
        try
        {
            editor.FocusDocument(); editor.Session.Select(1, 6);
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var selection = editor.Session.Selection; var caret = surface.Layout.Caret(3);
            var point = surface.TranslatePoint(new Point(caret.X + 1, caret.Y + caret.Height / 2), window)!.Value;
            var calls = 0;
            surface.NativeDragStarter = (_, data, effects) =>
            {
                using var owned = data;
                calls++; Assert.Equal("bcdef", data.TryGetText());
                Assert.Equal(readOnly ? DragDropEffects.Copy : Both, effects);
                return Task.FromResult(DragDropEffects.None);
            };
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point + new Vector(3, 0), RawInputModifiers.LeftMouseButton);
            Assert.Equal(0, calls); Assert.Equal(selection, editor.Session.Selection);
            window.MouseMove(point + new Vector(12, 0), RawInputModifiers.LeftMouseButton);
            Assert.Equal(1, calls); Assert.Equal(selection, editor.Session.Selection);
            window.MouseMove(point + new Vector(24, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(point + new Vector(24, 0), MouseButton.Left);
            Assert.Equal(1, calls); Assert.Equal("abcdefgh", editor.Text); Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Pointer_drag_through_native_event_handlers_moves_once_and_undo_restores_source() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "abcdefgh" };
        var window = new Window { Width = 700, Height = 400, Content = editor }; window.Show(); window.UpdateLayout();
        try
        {
            editor.FocusDocument(); editor.Session.Select(3, 1);
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var selection = editor.Session.Selection; var caret = surface.Layout.Caret(2);
            var point = surface.TranslatePoint(new Point(caret.X + 1, caret.Y + caret.Height / 2), window)!.Value;
            surface.NativeDragStarter = (_, data, effects) =>
            {
                using var owned = data;
                var end = surface.Layout.Caret(8); var destination = new Point(end.X + 2, end.Y + end.Height / 2);
                var over = new DragEventArgs(DragDrop.DragOverEvent, data, surface, destination, KeyModifiers.None) { DragEffects = effects };
                surface.RaiseEvent(over); Assert.True(surface.HasDropPreview);
                Assert.Equal("abcdefgh", editor.Text);
                var drop = new DragEventArgs(DragDrop.DropEvent, data, surface, destination, KeyModifiers.None) { DragEffects = effects };
                surface.RaiseEvent(drop); Assert.Equal(DragDropEffects.Move, drop.DragEffects);
                return Task.FromResult(drop.DragEffects);
            };
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point + new Vector(12, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(point + new Vector(12, 0), MouseButton.Left);
            Assert.Null(editor.LastError); Assert.Equal("adefghbc", editor.Text); Assert.False(surface.HasDropPreview);
            editor.Undo(); Assert.Equal("abcdefgh", editor.Text); Assert.Equal(selection, editor.Session.Selection); Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task Edit_or_selection_change_between_press_and_threshold_cancels_pending_drag(bool selectionOnly) => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "abcdefgh" };
        var window = new Window { Width = 700, Height = 400, Content = editor }; window.Show(); window.UpdateLayout();
        try
        {
            editor.FocusDocument(); editor.Session.Select(1, 6);
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var caret = surface.Layout.Caret(3);
            var point = surface.TranslatePoint(new Point(caret.X + 1, caret.Y + caret.Height / 2), window)!.Value;
            var calls = 0;
            surface.NativeDragStarter = (_, data, _) => { using var owned = data; calls++; return Task.FromResult(DragDropEffects.None); };
            window.MouseDown(point, MouseButton.Left);
            if (selectionOnly) editor.Session.Select(0, 0); else editor.InsertText("new");
            var selection = editor.Session.Selection; var text = editor.Text;
            window.MouseMove(point + new Vector(24, 0), RawInputModifiers.LeftMouseButton); window.MouseUp(point + new Vector(24, 0), MouseButton.Left);
            Assert.Equal(0, calls); Assert.Equal(selection, editor.Session.Selection); Assert.Equal(text, editor.Text);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Preview_is_nonmutating_and_stale_or_readonly_drop_clears_it() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "target" };
        var window = new Window { Width = 700, Height = 400, Content = editor }; window.Show(); window.UpdateLayout();
        try
        {
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            using var data = new DataTransfer(); data.Add(DataTransferItem.CreateText("drop"));
            var caret = surface.Layout.Caret(2); var point = new Point(caret.X, caret.Y + caret.Height / 2);
            var over = new DragEventArgs(DragDrop.DragOverEvent, data, surface, point, KeyModifiers.None) { DragEffects = Both };
            surface.RaiseEvent(over);
            Assert.True(surface.HasDropPreview); Assert.Equal(DragDropEffects.Copy, over.DragEffects);
            Assert.Equal(surface.Layout.HitTestCaret(point), surface.ContentDropCaret);
            Assert.Equal("target", editor.Text); Assert.False(editor.Session.CanUndo);
            editor.InsertText("changed"); var changed = editor.Text;
            var drop = new DragEventArgs(DragDrop.DropEvent, data, surface, point, KeyModifiers.None) { DragEffects = Both };
            surface.RaiseEvent(drop); Assert.Equal(DragDropEffects.None, drop.DragEffects);
            Assert.Equal(changed, editor.Text); Assert.False(surface.HasDropPreview);
            surface.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, data, surface, point, KeyModifiers.None) { DragEffects = Both });
            Assert.True(surface.HasDropPreview); editor.IsReadOnly = true;
            surface.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, surface, point, KeyModifiers.None) { DragEffects = Both });
            Assert.Equal(changed, editor.Text); Assert.False(surface.HasDropPreview);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Native_starter_owns_transfer_data_after_returning_to_source() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "source" };
        var window = new Window { Width = 700, Height = 400, Content = editor }; window.Show(); window.UpdateLayout();
        IDataTransfer? ownedByPlatform = null;
        try
        {
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            surface.NativeDragStarter = (_, data, _) => { ownedByPlatform = data; return Task.FromResult(DragDropEffects.None); };
            surface.AddHandler(InputElement.PointerPressedEvent, (_, e) => { editor.SelectAll(); surface.StartContentDrag(e); }, handledEventsToo: true);
            window.MouseDown(surface.TranslatePoint(new Point(40, 40), window)!.Value, MouseButton.Left);
            window.MouseUp(surface.TranslatePoint(new Point(40, 40), window)!.Value, MouseButton.Left);
            // The source must not dispose data owned by the platform, which is responsible
            // for its final release. The shim deliberately defers that release to this finally.
            Assert.NotNull(ownedByPlatform);
            Assert.Equal("source", ownedByPlatform.TryGetText());
        }
        finally { ownedByPlatform?.Dispose(); window.Close(); }
    }, CancellationToken.None);

    [Theory]
    [InlineData(false, DragDropEffects.None)] [InlineData(false, DragDropEffects.Move)] [InlineData(true, DragDropEffects.None)]
    public Task Cancelled_failed_or_external_move_result_never_removes_source(bool fail, DragDropEffects nativeResult) => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "source" };
        var window = new Window { Width = 700, Height = 400, Content = editor }; window.Show(); window.UpdateLayout();
        try
        {
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var calls = 0;
            surface.NativeDragStarter = (_, data, _) => { using var owned = data; calls++; return fail ? Task.FromException<DragDropEffects>(new IOException("native drag unavailable")) : Task.FromResult(nativeResult); };
            surface.AddHandler(InputElement.PointerPressedEvent, (_, e) => { editor.SelectAll(); Assert.True(surface.StartContentDrag(e)); }, handledEventsToo: true);
            window.MouseDown(surface.TranslatePoint(new Point(40, 40), window)!.Value, MouseButton.Left);
            window.MouseUp(surface.TranslatePoint(new Point(40, 40), window)!.Value, MouseButton.Left);
            Assert.Equal(1, calls); Assert.Equal("source", editor.Text); Assert.False(editor.Session.CanUndo); Assert.False(surface.HasDropPreview);
            if (fail) Assert.IsType<IOException>(editor.LastError);
        }
        finally { window.Close(); }
    }, CancellationToken.None);
}
