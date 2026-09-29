using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public sealed class TabbedCommandSurfaceTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Hidden_compact_toolbar_routes_find_replace_and_navigation_to_tabbed_panels() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor
        {
            ShowToolbar = false,
            Document = new FlowDocument([
                new Paragraph("Heading") { Style = ParagraphStyle.Default with { HeadingLevel = 1 } },
                new Paragraph("Body")])
        };
        editor.Session.Select(0, 7);
        editor.AddBookmark("start");
        var surface = new TextaloniaTabbedCommandSurface { Editor = editor };
        var window = new Window { Width = 900, Height = 650,
            Content = new DockPanel { Children = { surface, editor } } };
        DockPanel.SetDock(surface, Dock.Top);
        try
        {
            window.Show(); window.UpdateLayout();
            editor.Commands.Execute(EditorCommandId.Find);
            Dispatcher.UIThread.RunJobs();
            Assert.True(surface.FindFlyout?.IsOpen);
            var find = Assert.IsType<TextaloniaFindReplacePanel>(surface.FindPanel);
            find.Query = "Heading";
            Assert.Single(find.Results);
            var query = Assert.Single(find.GetVisualDescendants().OfType<TextBox>(),
                box => AutomationProperties.GetName(box) == "Find text");
            Assert.True(query.IsFocused);

            editor.Commands.Execute(EditorCommandId.Replace);
            Dispatcher.UIThread.RunJobs();
            Assert.True(surface.FindFlyout?.IsOpen);
            var replacement = Assert.Single(find.GetVisualDescendants().OfType<TextBox>(),
                box => AutomationProperties.GetName(box) == "Replacement text");
            Assert.True(replacement.IsFocused);

            editor.Commands.Execute(EditorCommandId.Navigation);
            Dispatcher.UIThread.RunJobs();
            Assert.True(surface.NavigationFlyout?.IsOpen);
            Assert.False(surface.FindFlyout?.IsOpen);
            var navigation = Assert.IsType<TextaloniaNavigationPanel>(surface.NavigationPanel);
            var lists = navigation.GetVisualDescendants().OfType<ListBox>().ToArray();
            Assert.Equal(2, lists.Length);
            Assert.All(lists, list => Assert.Single(list.Items));
            editor.Session.Select(editor.Session.Index.Length, editor.Session.Index.Length);
            lists[1].SelectedItem = lists[1].Items[0];
            var goToBookmark = Assert.Single(navigation.GetVisualDescendants().OfType<Button>(),
                button => AutomationProperties.GetName(button) == "Go to bookmark");
            goToBookmark.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, editor.Session.Selection.Start);
            editor.IsReadOnly = true;
            var addBookmark = Assert.Single(navigation.GetVisualDescendants().OfType<Button>(),
                button => AutomationProperties.GetName(button) == "Add bookmark");
            Assert.False(addBookmark.IsEnabled);
            Assert.True(goToBookmark.IsEnabled);
            editor.Commands.Localize = key => key == "Textalonia.Navigation.DocumentOutline" ? "Contents" : null;
            Assert.Equal("Contents", AutomationProperties.GetName(lists[0]));
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Tabs_localize_and_follow_table_context() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { ShowToolbar = false };
        var surface = new TextaloniaTabbedCommandSurface { Editor = editor };
        var window = new Window
        {
            Width = 900, Height = 650,
            Content = new DockPanel
            {
                Children = { surface, editor }
            }
        };
        DockPanel.SetDock(surface, Dock.Top);
        try
        {
            window.Show();
            window.UpdateLayout();
            var tabs = Assert.Single(surface.GetVisualDescendants().OfType<TabControl>());
            Assert.Equal(11, tabs.Items.Count);
            var tableTab = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Tag, "Table"));
            Assert.False(tableTab.IsVisible);

            surface.Localize = key => key == "Textalonia.CommandSurface.Tabs.Home" ? "Start" : null;
            tabs = Assert.Single(surface.GetVisualDescendants().OfType<TabControl>());
            Assert.Contains(tabs.Items.OfType<TabItem>(), tab => Equals(tab.Header, "Start"));

            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Tag, "View"));
            window.UpdateLayout();
            var zoom = Assert.Single(surface.GetVisualDescendants().OfType<NumericUpDown>(),
                control => AutomationProperties.GetName(control) == "Zoom percent");
            zoom.Value = 150;
            Assert.Equal(1.5, editor.Zoom);

            editor.InsertTable(2, 2);
            editor.SelectCurrentTableCell();
            Assert.True(tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Tag, "Table")).IsVisible);

            editor.Document = new FlowDocument([new Paragraph([new RichRun(new InlineDescriptor
            { Payload = new ImageInlinePayload("picture"), AltText = "picture" })])]);
            editor.Session.Select(0, 1);
            Assert.True(tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Tag, "Picture")).IsVisible);

            editor.EditHeader();
            Assert.True(tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Tag, "Header/Footer")).IsVisible);
            editor.CloseStory();
            Assert.False(tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Tag, "Header/Footer")).IsVisible);
        }
        finally { window.Close(); }
    }, CancellationToken.None);
}
