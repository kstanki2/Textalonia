using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class FormattingDialogTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);

    [Fact]
    public Task Font_dialog_preserves_mixed_properties_and_cancel_has_no_history_effect() => Run(() =>
    {
        var editor = new TextaloniaEditor { Document = new([new Paragraph([new("first", new() { Bold = true }), new("second", new() { Italic = true })])]) };
        var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout();
        try
        {
            editor.Session.SelectAll(); var before = editor.Session.Document; var selection = editor.Session.Selection;
            var cancelled = editor.ShowFontDialogAsync(); var dialog = Assert.Single(owner.OwnedWindows);
            dialog.UpdateLayout();
            Find<NumericUpDown>(dialog, "Size (DIP)").Value = 30;
            dialog.Close(false); Dispatcher.UIThread.RunJobs();
            Assert.Same(before, editor.Session.Document); Assert.Equal(selection, editor.Session.Selection);
            Assert.False(cancelled.GetAwaiter().GetResult());
            var accepted = editor.ShowFontDialogAsync(); dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<NumericUpDown>(dialog, "Size (DIP)").Value = 24;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.True(accepted.GetAwaiter().GetResult());
            var runs = editor.Session.Index.Paragraphs[0].Paragraph.Runs;
            Assert.All(runs, r => Assert.Equal(24, r.Style.FontSize));
            Assert.True(runs[0].Style.Bold); Assert.False(runs[1].Style.Bold); Assert.True(runs[1].Style.Italic);
            editor.Session.Undo(); Assert.Same(before, editor.Session.Document);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    });

    [Fact]
    public Task Tabs_dialog_authors_stops_and_read_only_cannot_open_dialogs() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "A\tB" }; var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout();
        try
        {
            var task = editor.ShowTabsDialogAsync(); var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<NumericUpDown>(dialog, "Position (DIP)").Value = 144;
            Find<ComboBox>(dialog, "Tab alignment").SelectedItem = TabAlignment.Right;
            Find<ComboBox>(dialog, "Tab leader").SelectedItem = TabLeader.Dots;
            Click(dialog, "Add or replace tab"); Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.True(task.GetAwaiter().GetResult());
            Assert.Equal(new TabStop(144, TabAlignment.Right, TabLeader.Dots), Assert.Single(editor.Session.Index.Paragraphs[0].Paragraph.Style.TabStops));
            editor.IsReadOnly = true; Assert.False(editor.ShowFontDialogAsync().GetAwaiter().GetResult()); Assert.Empty(owner.OwnedWindows);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    });

    private static T Find<T>(Window dialog, string name) where T : Control =>
        dialog.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
    private static void Click(Window dialog, string content) => dialog.GetVisualDescendants().OfType<Button>()
        .Single(button => Equals(button.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
