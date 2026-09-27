using System.Collections.Immutable;
using System.Windows.Input;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    public ICommand FindCommand { get; private set; } = null!;
    public ICommand ReplaceCommand { get; private set; } = null!;
    public ICommand NavigationCommand { get; private set; } = null!;
    public event EventHandler? ReplaceRequested;
    public event EventHandler? NavigationRequested;

    private void InitializeNavigationCommands()
    {
        FindCommand = NavigationAction(RequestFind);
        ReplaceCommand = NavigationAction(RequestReplace);
        NavigationCommand = NavigationAction(() =>
        { _toolbar?.OpenNavigation(); NavigationRequested?.Invoke(this, EventArgs.Empty); });
    }
    private EditorCommand NavigationAction(Action action)
    {
        var command = new EditorCommand(() => { _surface?.Composition.Cancel(); action(); return Task.CompletedTask; }, () => true);
        _commands.Add(command); return command;
    }
    internal void RequestReplace()
    { _toolbar?.OpenFind(replace: true); ReplaceRequested?.Invoke(this, EventArgs.Empty); }

    public ImmutableArray<DocumentSearchResult> Search(string query, bool matchCase = false, bool allStories = true) => Session.Search(query, matchCase, allStories);
    public ImmutableArray<DocumentOutlineEntry> GetOutline(bool allStories = false) => Session.GetOutline(allStories);
    public bool NavigateToSearchResult(DocumentSearchResult result)
    {
        if (!Session.IsCurrentSearchResult(result)) return false;
        PrepareNavigation(result.StoryId);
        return Session.SelectSearchResult(result);
    }
    public bool NavigateToOutline(DocumentOutlineEntry entry)
    {
        PrepareNavigation(entry.StoryId);
        var found = Session.NavigateToOutline(entry);
        if (found) FocusDocument(); return found;
    }
    public bool NavigateToBookmark(string name)
    {
        var bookmark = Session.Document.Bookmarks.FirstOrDefault(b => b.Name == name);
        if (bookmark is null) return false;
        PrepareNavigation(bookmark.Start.StoryId);
        var found = Session.NavigateToBookmark(name);
        if (found) FocusDocument(); return found;
    }
    public void AddBookmark(string name) => Session.AddBookmark(name);
    public void RenameBookmark(string name, string newName) => Session.RenameBookmark(name, newName);
    public void DeleteBookmark(string name) => Session.DeleteBookmark(name);
    public void SetInternalLink(string bookmarkName, string? tooltip = null, InternalLinkActivation activation = InternalLinkActivation.ModifierClick)
    {
        if (!Session.Document.Bookmarks.Any(b => b.Name == bookmarkName)) throw new ArgumentException("The bookmark does not exist.", nameof(bookmarkName));
        ApplyStyle(s => s with { Hyperlink = null, InternalLink = new() { BookmarkName = bookmarkName, Tooltip = tooltip, Activation = activation } });
    }
    private void PrepareNavigation(Guid storyId)
    {
        _surface?.Composition.Cancel(); ActiveStoryPageIndex = -1;
        if (storyId != Guid.Empty) ViewMode = DocumentViewMode.PrintLayout;
    }
}
