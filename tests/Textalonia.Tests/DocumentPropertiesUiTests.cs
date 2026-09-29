using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class DocumentPropertiesUiTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public void Property_changes_are_validated_permission_checked_and_undoable()
    {
        var session = new EditorSession(FlowDocument.FromText("Body"));
        var original = session.Document;
        Assert.True(session.SetDocumentProperties(new DocumentCoreProperties { Title = "Report" },
            [new DocumentCustomProperty { Name = "Count", Type = DocumentPropertyType.Integer, Value = "42" }]));
        Assert.Equal("Report", session.Document.CoreProperties.Title);
        Assert.Equal("42", Assert.Single(session.Document.CustomProperties).Value);
        session.Undo(); Assert.Same(original, session.Document);
        session.Redo(); Assert.Equal("Report", session.Document.CoreProperties.Title);
        Assert.Throws<FormatException>(() => session.SetDocumentProperties(new(),
            [new DocumentCustomProperty { Name = "Count", Type = DocumentPropertyType.Integer, Value = "no" }]));
        var current = session.Document;
        session.EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty
            .Add(EditOperation.Metadata, CommandCapability.Disabled) };
        Assert.False(session.SetDocumentProperties(new DocumentCoreProperties { Title = "Blocked" }, []));
        Assert.Same(current, session.Document);
    }

    [Fact]
    public Task Symbol_and_properties_dialogs_preserve_selection_and_commit_once() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { Text = "abc" };
        var owner = new Window { Content = editor, Width = 800, Height = 600 }; owner.Show(); owner.UpdateLayout();
        try
        {
            editor.Session.Select(1, 2);
            Assert.True(editor.InsertSymbol("✓")); Assert.Equal("a✓c", editor.Text);
            Assert.Throws<ArgumentException>(() => editor.InsertSymbol("ab"));
            Assert.Throws<ArgumentException>(() => editor.InsertSymbol("\ud800"));
            editor.Session.Undo(); Assert.Equal("abc", editor.Text);
            var selection = editor.Session.Selection;
            var pending = editor.ShowDocumentPropertiesDialogAsync();
            var dialog = Assert.Single(owner.OwnedWindows); dialog.UpdateLayout();
            Find<TextBox>(dialog, "Title").Text = "Report";
            Assert.Equal("Report", Find<TextBox>(dialog, "Title").Text);
            Click(dialog, "Apply"); Dispatcher.UIThread.RunJobs();
            Assert.True(pending.GetAwaiter().GetResult());
            Assert.Equal("Report", editor.Session.Document.CoreProperties.Title);
            Assert.Equal("Report", editor.Document.CoreProperties.Title);
            Assert.Equal(selection, editor.Session.Selection);
            editor.Session.Undo(); Assert.Null(editor.Document.CoreProperties.Title);
            Assert.Equal("abc", editor.Text);
        }
        finally { foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close(); owner.Close(); }
    }, CancellationToken.None);

    private static T Find<T>(Window dialog, string name) where T : Control =>
        dialog.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
    private static void Click(Window dialog, string content) => dialog.GetVisualDescendants().OfType<Button>()
        .Single(button => Equals(button.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
