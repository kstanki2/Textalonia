using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class PageSetupDialogTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);

    [Fact]
    public Task Page_setup_cancels_without_edits_and_applies_current_section_as_one_undo() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "First section\nSecond section" };
        editor.Session.Select(14, 14); editor.Session.InsertSectionBreak(SectionBreakKind.NextPage);
        var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout();
        try
        {
            var before = editor.Document; var selection = editor.Session.Selection;
            var cancel = editor.ShowPageSetupDialogAsync(); var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<NumericUpDown>(dialog, "Paper width (DIP)").Value = 900;
            dialog.Close(false); Dispatcher.UIThread.RunJobs();
            Assert.False(cancel.GetAwaiter().GetResult()); Assert.Same(before, editor.Document); Assert.Equal(selection, editor.Session.Selection);
            var apply = editor.ShowPageSetupDialogAsync(); dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<NumericUpDown>(dialog, "Paper width (DIP)").Value = 900;
            Find<ComboBox>(dialog, "Orientation").SelectedItem = PageOrientation.Landscape;
            Find<TextBox>(dialog, "Column weights (comma separated)").Text = "1, 2";
            Find<NumericUpDown>(dialog, "All page borders width (DIP; 0 clears)").Value = 2;
            Find<TextBox>(dialog, "All page borders color").Text = "#223344";
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.True(apply.GetAwaiter().GetResult()); Assert.Equal(selection, editor.Session.Selection);
            Assert.Equal(816, editor.Document.Sections[0].PageSettings.Width);
            var settings = editor.Document.Sections[1].PageSettings;
            Assert.Equal(900, settings.Width); Assert.Equal(PageOrientation.Landscape, settings.Orientation);
            Assert.Equal([1d, 2d], settings.Columns.Select(column => column.Width));
            Assert.Equal("#223344", settings.Borders!.Left!.Color);
            editor.Session.Undo(); Assert.Same(before, editor.Document);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    });

    [Fact]
    public Task Invalid_and_stale_page_settings_leave_document_unchanged() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "Text" };
        var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout();
        try
        {
            var before = editor.Document;
            var pending = editor.ShowPageSetupDialogAsync(); var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<NumericUpDown>(dialog, "Left margin (DIP)").Value = 10000;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.False(pending.IsCompleted); Assert.Same(before, editor.Document);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("positive content area") == true);
            Find<NumericUpDown>(dialog, "Left margin (DIP)").Value = 96;
            editor.Session.InsertText("new"); var edited = editor.Document;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.False(pending.IsCompleted); Assert.Same(edited, editor.Document);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("Close and reopen") == true);
            dialog.Close(false); Dispatcher.UIThread.RunJobs();
            editor.IsReadOnly = true;
            Assert.False(editor.ShowPageSetupDialogAsync().GetAwaiter().GetResult()); Assert.Empty(owner.OwnedWindows);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    });

    [Fact]
    public Task Page_numbering_dialog_edits_metadata_with_one_undo() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "Numbered" };
        var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout();
        try
        {
            var before = editor.Document;
            var pending = editor.ShowPageNumberingDialogAsync(); var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<CheckBox>(dialog, "Restart page numbering").IsChecked = true;
            Find<NumericUpDown>(dialog, "First page number").Value = 8;
            Find<ComboBox>(dialog, "Page number format").SelectedItem = PageNumberFormat.LowerRoman;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.True(pending.GetAwaiter().GetResult()); Assert.Equal(8, editor.Document.Sections[0].PageNumberStart);
            Assert.Equal(PageNumberFormat.LowerRoman, editor.Document.Sections[0].PageNumberFormat);
            editor.Session.Undo(); Assert.Same(before, editor.Document);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    });

    [Fact]
    public Task Paragraph_dialog_authors_and_clears_frame_placement_as_one_edit() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "Placed paragraph" };
        var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout();
        try
        {
            var before = editor.Document;
            var pending = editor.ShowParagraphDialogAsync(); var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<CheckBox>(dialog, "Position paragraph in a frame").IsChecked = true;
            Find<NumericUpDown>(dialog, "Frame left (DIP)").Value = 24;
            Find<NumericUpDown>(dialog, "Frame width (DIP)").Value = 180;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.True(pending.GetAwaiter().GetResult());
            Assert.Equal(new ParagraphFrame(24, 0, 180), editor.Session.Index.Paragraphs[0].Paragraph.Style.Frame);
            var framed = editor.Document;
            pending = editor.ShowParagraphDialogAsync(); dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<CheckBox>(dialog, "Position paragraph in a frame").IsChecked = false;
            Find<NumericUpDown>(dialog, "Frame width (DIP)").Value = 240;
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.True(pending.GetAwaiter().GetResult()); Assert.Null(editor.Session.Index.Paragraphs[0].Paragraph.Style.Frame);
            editor.Session.Undo(); Assert.Same(framed, editor.Document);
            editor.Session.Undo(); Assert.Same(before, editor.Document);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    });

    [Fact]
    public Task Read_only_toolbar_allows_view_changes_without_document_edits() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "Read only", IsReadOnly = true };
        var toolbar = new TextaloniaToolbar { Editor = editor }; var before = editor.Document;
        var viewButton = toolbar.Children.OfType<Button>().Single(button => Equals(button.Content, "View"));
        Assert.True(viewButton.IsEnabled);
        Assert.False(toolbar.Children.OfType<Button>().Single(button => Equals(button.Content, "Page setup…")).IsEnabled);
        var panel = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(Assert.IsType<Flyout>(viewButton.Flyout).Content).Content);
        panel.Children.OfType<ComboBox>().Single().SelectedItem = DocumentViewMode.PrintLayout;
        panel.Children.OfType<NumericUpDown>().Single(control => AutomationProperties.GetName(control) == "Zoom (%)").Value = 175;
        Assert.Equal(DocumentViewMode.PrintLayout, editor.ViewMode); Assert.Equal(1.75, editor.Zoom);
        Assert.Same(before, editor.Document); Assert.False(editor.Session.CanUndo);
        editor.Zoom = .5;
        Assert.Equal(50, panel.Children.OfType<NumericUpDown>().Single(control => AutomationProperties.GetName(control) == "Zoom (%)").Value);
    });

    private static T Find<T>(Window dialog, string name) where T : Control =>
        dialog.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
    private static void Click(Window dialog, string content) => dialog.GetVisualDescendants().OfType<Button>()
        .Single(button => Equals(button.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
