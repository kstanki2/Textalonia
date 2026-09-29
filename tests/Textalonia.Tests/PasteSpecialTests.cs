using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class PasteSpecialTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private sealed class Clipboard : IEditorClipboard
    {
        internal IAsyncDataTransfer? Data;
        internal Action? BeforeGet;
        public Task SetDataAsync(DataTransfer data) { Data = data; return Task.CompletedTask; }
        public Task<IAsyncDataTransfer?> TryGetDataAsync() { BeforeGet?.Invoke(); return Task.FromResult(Data); }
    }

    [Fact]
    public Task Requested_representation_is_used_without_falling_back_to_a_higher_priority_one() => fixture.Session.Dispatch(async () =>
    {
        var source = new EditorSession(FlowDocument.FromText("native"));
        source.SelectAll();
        var clipboard = new Clipboard();
        DataTransfer Formats()
        {
            var data = new DataTransfer();
            var item = new DataTransferItem();
            item.Set(DataFormat.CreateStringApplicationFormat("org.textalonia.fragment"), ClipboardInterchange.Serialize(source.CopyFragment()));
            if (OperatingSystem.IsWindows())
                item.Set(DataFormat.CreateBytesPlatformFormat("HTML Format"), ClipboardInterchange.EncodeWindowsHtml("<p><b>html</b></p>"));
            else
                item.Set(DataFormat.CreateStringPlatformFormat(OperatingSystem.IsMacOS() ? "public.html" : "text/html"), "<p><b>html</b></p>");
            item.SetText("plain"); data.Add(item);
            return data;
        }
        await clipboard.SetDataAsync(Formats());
        var editor = new TextaloniaEditor { ClipboardProvider = () => clipboard };

        Assert.True(await editor.PasteSpecialAsync(PasteSpecialFormat.NativeFragment));
        Assert.Equal("native", editor.Text);
        editor.Undo(); Assert.Equal("", editor.Text);

        await clipboard.SetDataAsync(Formats());
        Assert.True(await editor.PasteSpecialAsync(PasteSpecialFormat.Html));
        Assert.Equal("html", editor.Text);
        Assert.True(editor.Session.Index.Paragraphs[0].Paragraph.Runs[0].Style.Bold);
        editor.Undo(); Assert.Equal("", editor.Text);

        await clipboard.SetDataAsync(Formats());
        Assert.True(await editor.PasteSpecialAsync(PasteSpecialFormat.PlainText));
        Assert.Equal("plain", editor.Text);
        Assert.False(editor.Session.Index.Paragraphs[0].Paragraph.Runs[0].Style.Bold);
        editor.Undo(); Assert.Equal("", editor.Text);
        Assert.False(editor.Session.CanUndo);
        var empty = new DataTransfer(); empty.Add(DataTransferItem.CreateText(""));
        await clipboard.SetDataAsync(empty);
        Assert.False(await editor.PasteSpecialAsync(PasteSpecialFormat.PlainText));
        Assert.False(editor.Session.CanUndo);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Missing_or_invalid_choice_does_not_fall_back_and_does_not_edit() => fixture.Session.Dispatch(async () =>
    {
        var clipboard = new Clipboard();
        var data = new DataTransfer(); var item = new DataTransferItem();
        item.Set(DataFormat.CreateStringApplicationFormat("org.textalonia.fragment"), "{\"fragmentVersion\":99}");
        item.SetText("plain"); data.Add(item); await clipboard.SetDataAsync(data);
        var editor = new TextaloniaEditor { Text = "original", ClipboardProvider = () => clipboard };
        editor.SelectAll();

        Assert.False(await editor.PasteSpecialAsync(PasteSpecialFormat.NativeFragment));
        Assert.Equal("original", editor.Text);
        Assert.Contains(editor.LastConversionReport.Diagnostics, d => d.Code == "clipboard.native-rejected");
        var textOnly = new DataTransfer(); textOnly.Add(DataTransferItem.CreateText("plain"));
        await clipboard.SetDataAsync(textOnly);
        Assert.False(await editor.PasteSpecialAsync(PasteSpecialFormat.Html));
        Assert.Equal("original", editor.Text);
        Assert.False(editor.Session.CanUndo);
        var plainOnly = new DataTransfer(); plainOnly.Add(DataTransferItem.CreateText("plain"));
        await clipboard.SetDataAsync(plainOnly);
        Assert.True(await editor.PasteSpecialAsync(PasteSpecialFormat.PlainText));
        Assert.Equal("plain", editor.Text);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Stale_selection_and_disabled_clipboard_cannot_paste() => fixture.Session.Dispatch(async () =>
    {
        var clipboard = new Clipboard();
        var data = new DataTransfer(); data.Add(DataTransferItem.CreateText("plain"));
        await clipboard.SetDataAsync(data);
        var editor = new TextaloniaEditor { Text = "original", ClipboardProvider = () => clipboard };
        editor.SelectAll();
        clipboard.BeforeGet = () => editor.InsertText("intervening");
        Assert.False(await editor.PasteSpecialAsync(PasteSpecialFormat.PlainText));
        Assert.Equal("intervening", editor.Text);

        clipboard.BeforeGet = () => throw new InvalidOperationException("Clipboard must not be read.");
        editor.Session.EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty
            .Add(EditOperation.Clipboard, CommandCapability.Disabled) };
        Assert.False(await editor.PasteSpecialAsync(PasteSpecialFormat.PlainText));
        Assert.Equal("intervening", editor.Text);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Context_menu_exposes_choices_and_respects_clipboard_capability() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "text", ShowToolbar = false };
        var window = new Window { Width = 600, Height = 300, Content = editor };
        try
        {
            window.Show(); window.UpdateLayout();
            var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
            surface.ContextMenu!.Open(surface);
            var items = ((IEnumerable<object>)surface.ContextMenu.ItemsSource!).OfType<MenuItem>();
            var menu = Assert.Single(items, item => Equals(item.Header, "Paste Special"));
            Assert.Same(editor.Commands[EditorCommandId.PasteSpecial],
                ((IEnumerable<object>)menu.ItemsSource!).OfType<MenuItem>().First().Command);
            Assert.Contains(items, item => ReferenceEquals(item.Command, editor.Commands[EditorCommandId.InsertSymbol]));
            Assert.Contains(items, item => ReferenceEquals(item.Command, editor.Commands[EditorCommandId.DocumentProperties]));
            Assert.Equal(new[] { "Textalonia fragment", "HTML", "Plain text" },
                ((IEnumerable<object>)menu.ItemsSource!).OfType<MenuItem>().Select(item => item.Header).ToArray());
            surface.ContextMenu.Close(); Dispatcher.UIThread.RunJobs();

            editor.Session.EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty
                .Add(EditOperation.Clipboard, CommandCapability.Hidden) };
            surface.RefreshProofingContextMenu();
            surface.ContextMenu.Open(surface); Dispatcher.UIThread.RunJobs();
            items = ((IEnumerable<object>)surface.ContextMenu.ItemsSource!).OfType<MenuItem>();
            Assert.DoesNotContain(items, item => Equals(item.Header, "Paste Special"));
            Assert.DoesNotContain(items, item => ReferenceEquals(item.Command, editor.Commands[EditorCommandId.Cut]));
            Assert.DoesNotContain(items, item => ReferenceEquals(item.Command, editor.Commands[EditorCommandId.Copy]));
            Assert.DoesNotContain(items, item => ReferenceEquals(item.Command, editor.Commands[EditorCommandId.Paste]));
            surface.ContextMenu.Close();
        }
        finally { window.Close(); }
        return true;
    }, CancellationToken.None);
}
