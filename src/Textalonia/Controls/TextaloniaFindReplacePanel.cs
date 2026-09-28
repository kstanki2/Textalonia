using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Textalonia.Editing;

namespace Textalonia.Controls;

/// <summary>Reusable literal find/replace UI with story-aware results and transactional replacement.</summary>
public sealed class TextaloniaFindReplacePanel : StackPanel
{
    public static readonly StyledProperty<TextaloniaEditor?> EditorProperty =
        AvaloniaProperty.Register<TextaloniaFindReplacePanel, TextaloniaEditor?>(nameof(Editor));
    private readonly TextBox _query = new() { PlaceholderText = "Find text" };
    private readonly TextBox _replacement = new() { PlaceholderText = "Replace with" };
    private readonly CheckBox _matchCase = new() { Content = "Match case" };
    private readonly CheckBox _allStories = new() { Content = "Search all stories", IsChecked = true };
    private readonly ListBox _results = new() { MaxHeight = 180 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _replace;
    private readonly Button _replaceAll;
    private readonly List<TextHighlight> _highlights = [];
    private ImmutableArray<DocumentSearchResult> _matches = [];
    private DocumentSearchResult? _current;
    private bool _refreshing;
    private bool _subscribed;
    private int _revision = -1;
    public TextaloniaEditor? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public string Query { get => _query.Text ?? ""; set { _query.Text = value; RefreshResults(); } }
    public string Replacement { get => _replacement.Text ?? ""; set => _replacement.Text = value; }
    public bool MatchCase { get => _matchCase.IsChecked == true; set { _matchCase.IsChecked = value; RefreshResults(); } }
    public bool AllStories { get => _allStories.IsChecked == true; set { _allStories.IsChecked = value; RefreshResults(); } }
    public ImmutableArray<DocumentSearchResult> Results => _matches;
    public event EventHandler? CloseRequested;

    public TextaloniaFindReplacePanel()
    {
        Width = 310; Spacing = 6;
        AutomationProperties.SetName(this, "Find and replace");
        AutomationProperties.SetName(_query, "Find text");
        AutomationProperties.SetName(_replacement, "Replacement text");
        AutomationProperties.SetName(_matchCase, "Match case");
        AutomationProperties.SetName(_allStories, "Search all stories");
        AutomationProperties.SetName(_results, "Search results");
        AutomationProperties.SetName(_status, "Search status");
        Children.Add(new TextBlock { Text = "Find and replace", FontWeight = FontWeight.SemiBold });
        foreach (var control in new Control[] { _query, _replacement, _matchCase, _allStories }) Children.Add(control);
        var navigation = new WrapPanel();
        navigation.Children.Add(Button("Previous", () => FindNext(backward: true)));
        navigation.Children.Add(Button("Find next", () => FindNext())); Children.Add(navigation);
        var replacements = new WrapPanel();
        _replace = Button("Replace selected", () => ReplaceSelected());
        _replaceAll = Button("Replace all", () => ReplaceAll());
        replacements.Children.Add(_replace); replacements.Children.Add(_replaceAll); Children.Add(replacements);
        Children.Add(_results); Children.Add(_status);
        Children.Add(Button("Close search", Close));
        _query.TextChanged += (_, _) => RefreshResults();
        _matchCase.IsCheckedChanged += (_, _) => RefreshResults();
        _allStories.IsCheckedChanged += (_, _) => RefreshResults();
        _results.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _results.SelectedItem is ResultItem item) Select(item.Result);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FindNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        RefreshResults();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty) return;
        if (change.OldValue is TextaloniaEditor old)
        { old.Session.Changed -= SessionChanged; ClearHighlights(old); }
        _subscribed = false;
        Subscribe(); RefreshResults();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); Subscribe(); RefreshResults(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Editor is { } editor) { editor.Session.Changed -= SessionChanged; ClearHighlights(editor); }
        _subscribed = false; base.OnDetachedFromVisualTree(e);
    }
    private void Subscribe()
    {
        if (!_subscribed && Editor is { } editor) { editor.Session.Changed += SessionChanged; _subscribed = true; }
    }
    private void SessionChanged(object? sender, EventArgs e)
    {
        if (_revision != Editor?.Session.Revision) RefreshResults();
        else RefreshButtons();
    }

    public void FocusQuery(bool replace = false)
    { if (replace) _replacement.Focus(); else { _query.Focus(); _query.SelectAll(); } }

    public void RefreshResults()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var editor = Editor;
            _matches = editor?.Session.Search(Query, MatchCase, AllStories) ?? [];
            _revision = editor?.Session.Revision ?? -1;
            _current = _current is { } current ? _matches.FirstOrDefault(m => m.StoryId == current.StoryId && m.Start == current.Start && m.Length == current.Length) : null;
            _results.ItemsSource = _matches.Take(500).Select(m => new ResultItem(m, StoryName(m.StoryId))).ToArray();
            _status.Text = _matches.IsEmpty ? "No matches" : $"{_matches.Length} match(es)" + (_matches.Length > 500 ? " (first 500 listed)" : "");
            if (editor is not null)
            {
                ClearHighlights(editor);
                foreach (var match in _matches.Where(m => m.StoryId == editor.ActiveStoryId).Take(500))
                {
                    var highlight = new TextHighlight(match.Start, match.Length, new SolidColorBrush(Color.FromArgb(85, 245, 190, 65)));
                    _highlights.Add(highlight); editor.Highlights.Add(highlight);
                }
            }
            RefreshButtons();
        }
        finally { _refreshing = false; }
    }

    public bool FindNext(bool backward = false)
    {
        RefreshResults();
        if (Editor is not { } editor || _matches.IsEmpty) return false;
        var sameStory = _matches.Where(m => m.StoryId == editor.ActiveStoryId);
        var match = backward
            ? sameStory.LastOrDefault(m => m.Start + m.Length <= editor.Session.Selection.Start)
            : sameStory.FirstOrDefault(m => m.Start >= editor.Session.Selection.End);
        if (match is null)
        {
            var storyOrder = _matches.Select(m => m.StoryId).Distinct().ToList();
            var at = storyOrder.IndexOf(editor.ActiveStoryId);
            var next = backward ? at - 1 : at + 1;
            if (next < 0) next = storyOrder.Count - 1;
            if (next >= storyOrder.Count) next = 0;
            var destination = storyOrder[next];
            match = backward ? _matches.Last(m => m.StoryId == destination) : _matches.First(m => m.StoryId == destination);
        }
        return Select(match);
    }

    private bool Select(DocumentSearchResult match)
    {
        if (Editor is not { } editor || !editor.NavigateToSearchResult(match)) return false;
        _current = editor.Session.Search(Query, MatchCase, AllStories).FirstOrDefault(m => m.StoryId == match.StoryId && m.Start == match.Start && m.Length == match.Length);
        RefreshResults();
        _status.Text = $"{StoryName(match.StoryId)}, position {match.Start + 1}";
        return true;
    }

    public int ReplaceSelected()
    {
        if (Editor is not { } editor || !editor.CanEdit(EditOperation.Text) || _current is not { } current ||
            !editor.Session.IsCurrentSearchResult(current) || editor.ActiveStoryId != current.StoryId || editor.Session.Selection != current.Selection) return 0;
        var count = 0;
        editor.Run(() => count = editor.Session.ReplaceSearchResults([current], Replacement), focusDocument: false);
        _current = null; RefreshResults(); return count;
    }

    public int ReplaceAll()
    {
        if (Editor is not { } editor || editor.IsReadOnly || editor.Session.EditPolicy.GetCapability(EditOperation.Text) != CommandCapability.Enabled) return 0;
        RefreshResults();
        var count = 0;
        editor.Run(() => count = editor.Session.ReplaceSearchResults(_matches, Replacement), focusDocument: false);
        _current = null; RefreshResults(); _status.Text = $"{count} replacement(s)"; return count;
    }

    private void RefreshButtons()
    {
        var visibility = Editor?.Session.EditPolicy.GetCapability(EditOperation.Text) ?? CommandCapability.Disabled;
        _replacement.IsVisible = _replace.IsVisible = _replaceAll.IsVisible = visibility != CommandCapability.Hidden;
        _replaceAll.IsEnabled = Editor is { IsReadOnly: false } && visibility == CommandCapability.Enabled && !_matches.IsEmpty;
        _replace.IsEnabled = Editor is { } editor && editor.CanEdit(EditOperation.Text) && _current is { } current &&
            editor.Session.IsCurrentSearchResult(current) && current.StoryId == editor.ActiveStoryId && editor.Session.Selection == current.Selection;
    }
    private string StoryName(Guid id) => id == Guid.Empty ? "Body" : Editor?.Document.Stories.GetValueOrDefault(id)?.Kind.ToString() ?? "Story";
    private void ClearHighlights(TextaloniaEditor editor)
    { foreach (var highlight in _highlights) editor.Highlights.Remove(highlight); _highlights.Clear(); }
    private void Close()
    { if (Editor is { } editor) { ClearHighlights(editor); editor.FocusDocument(); } CloseRequested?.Invoke(this, EventArgs.Empty); }
    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(2), Padding = new Thickness(6, 4) };
        AutomationProperties.SetName(button, label); button.Click += (_, _) => action(); return button;
    }
    private sealed record ResultItem(DocumentSearchResult Result, string Story)
    { public override string ToString() => $"{Story} / {Result.Start + 1}: {Result.Text.Replace('\n', ' ')}"; }
}
