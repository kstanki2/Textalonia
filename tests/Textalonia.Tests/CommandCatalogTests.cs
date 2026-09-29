using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Pdf.Skia;
using Textalonia.Printing;
using Textalonia.Export;
using Xunit;

namespace Textalonia.Tests;

public class CommandCatalogTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Existing_commands_and_catalog_share_execution_and_mixed_state() => fixture.Session.Dispatch(async () =>
    {
        var editor = new TextaloniaEditor { Text = "ab" };
        Assert.Same(editor.BoldCommand, editor.Commands[EditorCommandId.Bold]);
        Assert.Same(editor.FindCommand, editor.Commands[EditorCommandId.Find]);
        Assert.Equal("Textalonia.Commands.Bold", editor.Commands[EditorCommandId.Bold].LocalizationKey);

        editor.Session.Select(0, 1);
        editor.Session.ApplyStyle(style => style with { Bold = true });
        editor.Session.Select(0, 2);
        var bold = editor.Commands[EditorCommandId.Bold];
        Assert.True(bold.IsMixed);
        Assert.Null(bold.IsChecked);
        Assert.True(await bold.ExecuteAsync());
        Assert.False(bold.IsMixed);
        Assert.True(bold.IsChecked);
        Assert.True(editor.Session.CanUndo);

        Assert.True(editor.Commands.TryExecuteShortcut(Key.Z, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control));
        Assert.True(editor.FormattingState.Bold.IsMixed);
    }, CancellationToken.None);

    [Fact]
    public Task Capability_and_parameter_validation_are_shared_across_surfaces() => fixture.Session.Dispatch(async () =>
    {
        var editor = new TextaloniaEditor { Text = "abc" };
        editor.Session.SelectAll();
        editor.Session.EditPolicy = new() { Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty
            .Add(EditOperation.Formatting, CommandCapability.Hidden) };
        var bold = editor.Commands[EditorCommandId.Bold];
        Assert.Equal(CommandCapability.Hidden, bold.Capability);
        Assert.False(bold.IsVisible);
        Assert.False(editor.BoldCommand.CanExecute(null));
        Assert.False(await bold.ExecuteAsync());
        Assert.True(editor.Commands[EditorCommandId.Copy].CanExecute(null));

        Assert.False(editor.Commands.CanExecute(EditorCommandId.SetZoom, 8d));
        Assert.False(await editor.Commands.ExecuteAsync(EditorCommandId.SetZoom, 8d));
        Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.SetZoom, 1.5d));
        Assert.Equal(1.5, editor.Zoom);
        Assert.False(editor.Commands.CanExecute(EditorCommandId.GoToPage, 0));
        Assert.False(editor.Commands.CanExecute(EditorCommandId.PasteSpecial, null));
        Assert.False(editor.Commands.CanExecute(EditorCommandId.InsertTable, new EditorTableSize(0, 2)));
    }, CancellationToken.None);

    [Fact]
    public Task Localization_and_view_state_refresh_without_document_history() => fixture.Session.Dispatch(async () =>
    {
        var editor = new TextaloniaEditor();
        var view = editor.Commands[EditorCommandId.ViewDraft];
        Assert.False(view.IsChecked);
        editor.Commands.Localize = key => key == view.LocalizationKey ? "Brouillon" : null;
        Assert.Equal("Brouillon", view.DisplayText);
        Assert.True(await view.ExecuteAsync());
        Assert.Equal(DocumentViewMode.Draft, editor.ViewMode);
        Assert.True(view.IsChecked);
        Assert.False(editor.Session.CanUndo);
        Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.ToggleRulers));
        Assert.True(editor.ShowRulers);
        Assert.True(editor.Commands[EditorCommandId.ToggleRulers].IsChecked);
    }, CancellationToken.None);

    [Fact]
    public Task Numeric_view_commands_preserve_input_focus() => fixture.Session.Dispatch(async () =>
    {
        var editor = new TextaloniaEditor();
        var input = new TextBox();
        var window = new Window { Content = new StackPanel { Children = { input, editor } } };
        window.Show(); window.UpdateLayout();
        try
        {
            input.Focus();
            Assert.True(input.IsFocused);
            Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.SetZoom, 1.25d));
            Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.SetPageGap, 30d));
            Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.SetPagesPerRow, 2));
            Assert.True(input.IsFocused);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Output_and_header_commands_follow_available_services_and_permissions() => fixture.Session.Dispatch(async () =>
    {
        var editor = new TextaloniaEditor { Text = "body", IsReadOnly = true };
        Assert.False(editor.Commands.CanExecute(EditorCommandId.Print));
        Assert.False(editor.Commands.CanExecute(EditorCommandId.ExportPdf));
        Assert.False(editor.Commands.CanExecute(EditorCommandId.EditHeader));

        editor.PdfExporter = new PdfExporter();
        Assert.True(editor.Commands.CanExecute(EditorCommandId.ExportPdf));
        editor.PdfExporter = null;
        Assert.False(editor.Commands.CanExecute(EditorCommandId.ExportPdf));
        editor.PrintService = new EmptyPrintService();
        Assert.True(editor.Commands.CanExecute(EditorCommandId.Print));
        editor.PrintService = null;
        Assert.False(editor.Commands.CanExecute(EditorCommandId.Print));

        editor.IsReadOnly = false;
        Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.EditHeader));
        Assert.NotEqual(Guid.Empty, editor.ActiveStoryId);
        editor.CloseStory(); editor.IsReadOnly = true;
        Assert.True(editor.Commands.CanExecute(EditorCommandId.EditHeader));
        Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.EditHeader));
        Assert.NotEqual(Guid.Empty, editor.ActiveStoryId);
    }, CancellationToken.None);

    [Fact]
    public Task Bookmark_and_outline_commands_share_permission_and_navigation_routing() => fixture.Session.Dispatch(async () =>
    {
        var editor = new TextaloniaEditor { Text = "Heading\nBody" };
        editor.Session.Select(0, 7);
        Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.InsertBookmark, "start"));
        Assert.True(editor.Commands.CanExecute(EditorCommandId.RenameBookmark, new EditorBookmarkRename("start", "renamed")));
        Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.RenameBookmark, new EditorBookmarkRename("start", "renamed")));
        Assert.False(editor.Commands.CanExecute(EditorCommandId.NavigateToBookmark, "start"));
        Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.NavigateToBookmark, "renamed"));
        Assert.Equal("Heading", editor.SelectedText);

        editor.Session.Select(0, 0);
        editor.Session.SetHeading(1);
        var heading = Assert.Single(editor.GetOutline());
        editor.Session.Select(editor.Session.Index.Length, editor.Session.Index.Length);
        Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.NavigateToOutline, heading));
        Assert.Equal(0, editor.Session.Selection.Active);

        editor.IsReadOnly = true;
        Assert.False(editor.Commands.CanExecute(EditorCommandId.DeleteBookmark, "renamed"));
        Assert.True(editor.Commands.CanExecute(EditorCommandId.NavigateToBookmark, "renamed"));
        editor.IsReadOnly = false;
        Assert.True(await editor.Commands.ExecuteAsync(EditorCommandId.DeleteBookmark, "renamed"));
        Assert.Empty(editor.Document.Bookmarks);
    }, CancellationToken.None);

    [Fact]
    public Task Changing_catalog_localization_updates_mounted_toolbar_and_ruler() => fixture.Session.Dispatch(() =>
    {
        var editor = new TextaloniaEditor { ShowRulers = true };
        var window = new Window { Width = 900, Height = 600, Content = editor };
        window.Show(); window.UpdateLayout();
        try
        {
            var toolbar = Assert.Single(editor.GetVisualDescendants().OfType<TextaloniaToolbar>());
            var ruler = editor.GetVisualDescendants().OfType<DocumentRuler>()
                .Single(value => value.Orientation == RulerOrientation.Horizontal);
            Button PropertiesButton() => toolbar.Children.OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, editor.Commands[EditorCommandId.DocumentProperties]));
            Assert.Equal("Properties…", PropertiesButton().Content);

            editor.Commands.Localize = key => key switch
            {
                "Textalonia.Commands.DocumentProperties" => "Eigenschaften",
                "Textalonia.UI.Ruler.Horizontal.Name" => "Horizontales Lineal",
                _ => null
            };

            Assert.Equal("Eigenschaften", PropertiesButton().Content);
            Assert.Equal("Eigenschaften", AutomationProperties.GetName(PropertiesButton()));
            Assert.Contains("Horizontales Lineal", AutomationProperties.GetName(ruler));
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    private sealed class EmptyPrintService : IPrintService
    {
        public Task<PrintCapabilities> GetCapabilitiesAsync(string? printerName, CancellationToken cancellationToken) =>
            Task.FromResult(new PrintCapabilities());
        public Task<PrintOptions?> ShowDialogAsync(PrintDialogRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<PrintOptions?>(null);
        public Task PrintAsync(PrintJob job, IProgress<PagedOutputProgress>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
