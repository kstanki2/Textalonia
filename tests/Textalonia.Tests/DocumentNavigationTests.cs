using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class DocumentNavigationTests
{
    [Fact]
    public void Search_identifies_story_revision_and_never_splits_graphemes()
    {
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("hello header")] };
        var session = new EditorSession(FlowDocument.FromText("HELLO e\u0301 \U0001F600") with { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header) });
        var matches = session.Search("hello");
        Assert.Equal(2, matches.Length);
        Assert.Equal(Guid.Empty, matches[0].StoryId);
        Assert.Equal(header.Id, matches[1].StoryId);
        Assert.All(matches, m => Assert.Equal(session.Revision, m.Revision));
        Assert.Single(session.Search("hello", matchCase: true));
        Assert.Empty(session.Search("e", matchCase: true, allStories: false));
        Assert.Empty(session.Search("\uD83D"));
        Assert.Single(session.Search("e\u0301"));
        Assert.True(session.SelectSearchResult(matches[1]));
        Assert.Equal(header.Id, session.ActiveStoryId);
        Assert.Equal("hello", session.SelectedText);
        Assert.False(session.IsCurrentSearchResult(matches[1]));
        Assert.Single(session.Search("hello", allStories: false));
    }

    [Fact]
    public void Cross_story_replacement_is_one_undo_transaction_and_preserves_bookmark_ranges()
    {
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("one one")] };
        var session = new EditorSession(FlowDocument.FromText("one body one") with { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header) });
        session.Select(9, 12); session.AddBookmark("body");
        session.SwitchStory(header.Id); session.Select(4, 7); session.AddBookmark("header");
        var before = session.Document;
        var matches = session.Search("one");
        Assert.Equal(4, session.ReplaceSearchResults(matches, "longer"));
        Assert.Equal("longer body longer", session.Document.Text);
        Assert.Equal("longer longer", session.Document.GetStoryDocument(header.Id).Text);
        Assert.True(session.NavigateToBookmark("header")); Assert.Equal("longer", session.SelectedText);
        Assert.True(session.NavigateToBookmark("body")); Assert.Equal("longer", session.SelectedText);
        session.Undo();
        Assert.Equal(before, session.Document);
        session.Redo();
        Assert.Equal("longer body longer", session.Document.Text);
    }

    [Fact]
    public void Replacement_rejects_stale_foreign_and_duplicate_results_before_any_edit()
    {
        var session = new EditorSession(FlowDocument.FromText("one one"));
        var result = session.Search("one")[0];
        var original = session.Document;
        var revision = session.Revision;
        Assert.Throws<ArgumentException>(() => session.ReplaceSearchResults([result, result], "two"));
        Assert.Same(original, session.Document); Assert.Equal(revision, session.Revision);
        var foreign = new EditorSession(original).Search("one")[0];
        Assert.Throws<InvalidOperationException>(() => session.ReplaceSearchResults([foreign], "two"));
        session.InsertText("x");
        original = session.Document;
        Assert.Throws<InvalidOperationException>(() => session.ReplaceSearchResults([result], "two"));
        Assert.Same(original, session.Document); Assert.False(session.SelectSearchResult(result));
    }

    [Fact]
    public void Read_only_allows_search_and_navigation_but_blocks_replacement()
    {
        var session = new EditorSession(FlowDocument.FromText("one")) { IsReadOnly = true };
        var matches = session.Search("one");
        Assert.True(session.SelectSearchResult(matches[0]));
        Assert.Equal(0, session.ReplaceSearchResults(matches, "two"));
        Assert.Equal("one", session.Document.Text); Assert.False(session.CanUndo);
    }

    [Fact]
    public void Outline_uses_inherited_named_styles_and_explicit_outline_levels_and_resolves_after_edits()
    {
        var styles = new DocumentStyleCatalog { Paragraphs = ImmutableDictionary<string, ParagraphStyleDefinition>.Empty
            .Add("base", new() { Id = "base", Formatting = new() { OutlineLevel = 3 } })
            .Add("child", new() { Id = "child", BasedOn = "base" }) };
        var session = new EditorSession(new FlowDocument([
            new Paragraph("Body"),
            new Paragraph("Named") { Style = new() { StyleId = "child", Overrides = new() } },
            new Paragraph("Direct") { Style = new() { OutlineLevel = 2, HeadingLevel = 1 } },
            new Paragraph("Heading") { Style = new() { HeadingLevel = 4 } }
        ]) { Styles = styles });
        var outline = session.GetOutline();
        Assert.Equal(new[] { 3, 2, 4 }, outline.Select(e => e.Level));
        Assert.Equal("child", outline[0].StyleId);
        session.Select(0, 0); session.InsertText("prefix ");
        Assert.True(session.NavigateToOutline(outline[0]));
        Assert.Equal("Named", session.Index.At(session.Selection.Start).Paragraph.Text);
        Assert.Equal(12, session.Selection.Start);
    }
}

public class DocumentNavigationUiTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Reusable_panel_navigates_all_stories_and_replaces_in_one_step() => fixture.Session.Dispatch(() =>
    {
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("one")] };
        var editor = new TextaloniaEditor { Document = FlowDocument.FromText("one") with { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header) } };
        var panel = new TextaloniaFindReplacePanel { Editor = editor, Query = "one", Replacement = "two" };
        Assert.Equal(2, panel.Results.Length);
        Assert.True(panel.FindNext()); Assert.Equal(Guid.Empty, editor.ActiveStoryId);
        Assert.True(panel.FindNext()); Assert.Equal(header.Id, editor.ActiveStoryId);
        Assert.Equal(DocumentViewMode.PrintLayout, editor.ViewMode);
        Assert.Equal(1, panel.ReplaceSelected());
        Assert.Equal("two", editor.Document.GetStoryDocument(header.Id).Text);
        Assert.Equal(1, panel.ReplaceAll()); Assert.Equal("two", editor.Document.Text);
        editor.Session.Undo(); Assert.Equal("one", editor.Document.Text);
        editor.IsReadOnly = true;
        Assert.Equal(0, panel.ReplaceAll());
        Assert.True(panel.FindNext());
        panel.Editor = null;
        Assert.Empty(editor.Highlights);
    }, CancellationToken.None);

    [Fact]
    public Task Toolbar_exposes_navigation_and_fields_and_read_only_keeps_search_available() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { IsReadOnly = true };
        var toolbar = new TextaloniaToolbar { Editor = editor };
        Button Named(string name) => toolbar.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
        Assert.True(Named("Find and replace").IsEnabled);
        Assert.True(Named("Outline, bookmarks and internal links").IsEnabled);
        Assert.True(Named("Insert and update document fields").IsEnabled);
        Assert.True(editor.FindCommand.CanExecute(null)); Assert.True(editor.ReplaceCommand.CanExecute(null));
    }, CancellationToken.None);

    [Fact]
    public Task Toolbar_field_authoring_evaluates_edits_and_locks_instructions() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Document = FlowDocument.FromText("prefix ") };
        editor.Session.Select(7, 7);
        var toolbar = new TextaloniaToolbar { Editor = editor };
        var button = toolbar.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Insert and update document fields");
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        var panel = Assert.IsType<StackPanel>(Assert.IsType<ScrollViewer>(flyout.Content).Content);
        var instruction = panel.Children.OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Field instruction");
        var fields = panel.Children.OfType<ComboBox>().Single();
        void Click(string name) => panel.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        instruction.Text = "= 2 + 3"; Click("Insert field"); Click("Update fields");
        Assert.Null(editor.LastError); Assert.Equal("prefix 5", editor.Text);
        fields.SelectedIndex = 0; instruction.Text = "= 4 * 3"; Click("Apply field instruction"); Click("Update fields");
        Assert.Null(editor.LastError); Assert.Equal("prefix 12", editor.Text);
        var preview = panel.Children.OfType<Expander>().Single(); preview.IsExpanded = true;
        var previewPanel = Assert.IsType<StackPanel>(preview.Content);
        var previewText = previewPanel.Children.OfType<TextBox>().Single();
        var previewCodes = previewPanel.Children.OfType<CheckBox>().Single();
        Assert.True(previewText.IsReadOnly); Assert.Equal("prefix { = 4 * 3 }", previewText.Text);
        var revision = editor.Session.Revision;
        previewCodes.IsChecked = false; Assert.Equal("prefix 12", previewText.Text);
        Assert.Equal(revision, editor.Session.Revision);
        Click("Lock field"); Assert.True(Assert.Single(editor.Document.Fields).IsLocked);
        Click("Unlock field"); Assert.False(Assert.Single(editor.Document.Fields).IsLocked);
        Click("Remove field, keep result"); Assert.Empty(editor.Document.Fields); Assert.Equal("prefix 12", editor.Text);
    }, CancellationToken.None);
    [Fact]
    public Task Keyboard_find_and_replace_route_to_host_events_without_a_toolbar() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { ShowToolbar = false };
        var window = new Window { Width = 500, Height = 400, Content = editor };
        var finds = 0; var replacements = 0;
        editor.FindRequested += (_, _) => finds++;
        editor.ReplaceRequested += (_, _) => replacements++;
        try
        {
            window.Show(); window.UpdateLayout(); editor.FocusDocument();
            var modifier = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.F, modifier); editor.FocusDocument();
            window.KeyPress(Key.H, modifier);
            Assert.Equal(1, finds); Assert.Equal(1, replacements);
        }
        finally { window.Close(); }
    }, CancellationToken.None);
}
