using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Model.Fields;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class HostIntegrationTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private sealed class RecordingDialogs : IEditorDialogService
    {
        internal Window? Dialog;
        internal Window? Owner;
        internal CancellationToken Token;
        public Task<TResult> ShowAsync<TResult>(Window dialog, Window owner, CancellationToken cancellationToken)
        {
            Dialog = dialog; Owner = owner; Token = cancellationToken;
            return Task.FromResult(default(TResult)!);
        }
    }

    private sealed class DelayedCloseDialogs : IEditorDialogService
    {
        public async Task<TResult> ShowAsync<TResult>(Window dialog, Window owner, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
            return await dialog.ShowDialog<TResult>(owner);
        }
    }

    [Fact]
    public Task Host_can_replace_modal_presenter_without_changing_the_document() => fixture.Session.Dispatch(async () =>
    {
        var service = new RecordingDialogs();
        var editor = new TextaloniaEditor { Text = "unchanged", DialogService = service };
        var owner = new Window { Width = 600, Height = 400, Content = editor };
        owner.Show(); owner.UpdateLayout();
        try
        {
            Assert.False(await editor.ShowFontDialogAsync());
            Assert.IsType<Window>(service.Owner);
            Assert.Same(owner, service.Owner);
            Assert.Equal("Font", service.Dialog!.Title);
            Assert.True(service.Token.CanBeCanceled);
            Assert.Equal("unchanged", editor.Text);
            Assert.False(editor.Session.CanUndo);
        }
        finally { owner.Close(); }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Cancel_active_dialogs_closes_a_modal_without_committing() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "unchanged" };
        var owner = new Window { Width = 600, Height = 400, Content = editor };
        owner.Show(); owner.UpdateLayout();
        try
        {
            var pending = editor.ShowFontDialogAsync();
            Assert.Single(owner.OwnedWindows);
            editor.CancelActiveDialogs();
            Assert.Empty(owner.OwnedWindows);
            Dispatcher.UIThread.RunJobs();
            Assert.False(pending.GetAwaiter().GetResult());
            Assert.Empty(owner.OwnedWindows);
            Assert.Equal("unchanged", editor.Text);
            Assert.False(editor.Session.CanUndo);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Canceled_dialog_cannot_apply_during_a_delayed_host_close() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "unchanged", DialogService = new DelayedCloseDialogs() };
        var owner = new Window { Width = 600, Height = 400, Content = editor };
        owner.Show(); owner.UpdateLayout();
        try
        {
            var pending = editor.ShowFontDialogAsync();
            var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            editor.CancelActiveDialogs();
            dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Apply"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("unchanged", editor.Text);
            Assert.False(editor.Session.CanUndo);
            Dispatcher.UIThread.RunJobs();
            Assert.False(pending.GetAwaiter().GetResult());
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Cancel_active_print_preview_reports_cancellation() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "preview" };
        var owner = new Window { Width = 700, Height = 500, Content = editor };
        owner.Show(); owner.UpdateLayout();
        try
        {
            var pending = editor.ShowPrintPreviewDialogAsync();
            Assert.Single(owner.OwnedWindows);
            editor.CancelActiveDialogs();
            Dispatcher.UIThread.RunJobs();
            Assert.False(pending.GetAwaiter().GetResult());
            Assert.Null(editor.LastError);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Load_and_save_completion_report_the_exact_document_and_only_successes() => fixture.Session.Dispatch(async () =>
    {
        var editor = new TextaloniaEditor { Text = "saved" };
        var saved = new List<DocumentIoCompletedEventArgs>();
        var loaded = new List<DocumentIoCompletedEventArgs>();
        editor.SaveCompleted += (_, result) => saved.Add(result);
        editor.LoadCompleted += (_, result) => loaded.Add(result);
        using var stream = new MemoryStream();
        await editor.SaveAsync(stream, DocumentFormats.Json);
        var snapshot = editor.Document;
        Assert.Single(saved); Assert.Same(snapshot, saved[0].Document);
        Assert.Same(DocumentFormats.Json, saved[0].Format); Assert.Null(saved[0].Report);

        editor.Text = "changed";
        stream.Position = 0;
        await editor.LoadAsync(stream, DocumentFormats.Json);
        Assert.Single(loaded); Assert.Same(editor.Document, loaded[0].Document);
        Assert.Equal("saved", loaded[0].Document.Text);
        using var bad = new MemoryStream("not JSON"u8.ToArray());
        await Assert.ThrowsAnyAsync<Exception>(() => editor.LoadAsync(bad, DocumentFormats.Json));
        Assert.Single(loaded);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Layout_and_editing_mode_events_fire_on_meaningful_changes() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "page" };
        var layouts = new List<EditorLayoutCompletedEventArgs>();
        var modes = new List<EditingModeChangedEventArgs>();
        editor.LayoutCompleted += (_, result) => { layouts.Add(result); editor.InvalidateVisual(); };
        editor.EditingModeChanged += (_, result) => modes.Add(result);
        var owner = new Window { Width = 700, Height = 500, Content = editor };
        owner.Show(); owner.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.NotEmpty(layouts);
            var count = layouts.Count;
            owner.UpdateLayout(); Assert.Equal(count, layouts.Count);
            editor.InsertText("more"); owner.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(layouts.Count > count);
            count = layouts.Count;
            owner.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Assert.Equal(count, layouts.Count);
            editor.IsReadOnly = true;
            Assert.True(Assert.Single(modes).IsReadOnly);
            editor.Session.EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty
                .Add(EditOperation.Formatting, CommandCapability.Hidden) };
            Assert.Equal(2, modes.Count);
            Assert.Equal(CommandCapability.Hidden, modes[1].Policy.GetCapability(EditOperation.Formatting));
        }
        finally { owner.Close(); }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Host_field_options_are_the_default_and_per_call_options_take_precedence() => fixture.Session.Dispatch(() =>
    {
        var source = FieldOperations.Insert(FlowDocument.FromText("cached"), Guid.Empty, 0, 6,
            "DOCVARIABLE Host", FlowDocument.FromText("cached"));
        var editor = new TextaloniaEditor { Document = source,
            FieldOptions = new FieldEvaluationOptions { DocumentVariableResolver = _ => FlowDocument.FromText("host") } };
        Assert.Equal("host", editor.UpdateFieldsWithLayout().Document.Text);
        editor.Document = source;
        Assert.Equal("call", editor.UpdateFieldsWithLayout(new FieldEvaluationOptions
        { DocumentVariableResolver = _ => FlowDocument.FromText("call") }).Document.Text);
        return true;
    }, CancellationToken.None);
}
