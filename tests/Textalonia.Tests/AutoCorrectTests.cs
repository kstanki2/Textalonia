using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Proofing;
using Xunit;

namespace Textalonia.Tests;

public class AutoCorrectTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Transient_IME_text_is_untouched_and_committed_correction_has_its_own_undo_step() =>
        fixture.Session.Dispatch(() =>
        {
            var editor = new TextaloniaEditor { Text = "" };
            var window = new Window { Width = 600, Height = 240, Content = editor };
            window.Show();
            try
            {
                window.UpdateLayout();
                editor.FocusDocument();
                var surface = editor.GetVisualDescendants().OfType<DocumentSurface>().Single();
                var request = new TextInputMethodClientRequestedEventArgs
                { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
                surface.RaiseEvent(request);
                var client = Assert.IsAssignableFrom<TextInputMethodClient>(request.Client);
                client.SetPreeditText("THe ");
                Assert.Equal("", editor.Text);
                window.KeyTextInput("THe ");
                Assert.Equal("The ", editor.Text);
                editor.Session.Undo();
                Assert.Equal("THe ", editor.Text);
                editor.Session.Undo();
                Assert.Equal("", editor.Text);
            }
            finally { window.Close(); }
        }, CancellationToken.None);

    [Fact]
    public Task NoProof_skips_correction_and_committed_url_gets_a_link() =>
        fixture.Session.Dispatch(() =>
        {
            var editor = new TextaloniaEditor
            {
                Document = FlowDocument.FromText("THe", TextStyle.Default with { NoProof = true })
            };
            var window = new Window { Width = 600, Height = 240, Content = editor };
            window.Show();
            try
            {
                window.UpdateLayout();
                editor.FocusDocument();
                editor.Session.Select(3, 3);
                window.KeyTextInput(" ");
                Assert.Equal("THe ", editor.Text);

                editor.Document = FlowDocument.FromText("www.example.com");
                editor.Session.Select(editor.Session.Index.Length, editor.Session.Index.Length);
                window.KeyTextInput(" ");
                Assert.Equal("www.example.com ", editor.Text);
                Assert.Equal("https://www.example.com",
                    Assert.Single(Assert.Single(editor.Document.Blocks.OfType<Paragraph>()).Runs
                        .Where(run => run.Style.Hyperlink is not null)).Style.Hyperlink);
                editor.Session.Undo();
                Assert.All(Assert.Single(editor.Document.Blocks.OfType<Paragraph>()).Runs,
                    run => Assert.Null(run.Style.Hyperlink));
            }
            finally { window.Close(); }
        }, CancellationToken.None);

    [Fact]
    public Task Multiple_boundaries_and_selected_input_are_corrected_in_committed_order() =>
        fixture.Session.Dispatch(() =>
        {
            var corrections = new AutoCorrectService();
            corrections.Replacements["teh"] = "the";
            corrections.Replacements["wrng"] = "wrong";
            var editor = new TextaloniaEditor { AutoCorrectService = corrections };
            var window = new Window { Width = 600, Height = 240, Content = editor };
            window.Show();
            try
            {
                window.UpdateLayout();
                editor.FocusDocument();
                window.KeyTextInput("teh,wrng ");
                Assert.Equal("the,wrong ", editor.Text);
                editor.Session.SelectAll();
                window.KeyTextInput("THe ");
                Assert.Equal("The ", editor.Text);
            }
            finally { window.Close(); }
        }, CancellationToken.None);

    [Fact]
    public Task Enter_commits_the_boundary_before_correction_and_mixed_proofing_runs_are_preserved() =>
        fixture.Session.Dispatch(() =>
        {
            var editor = new TextaloniaEditor { Text = "THe" };
            var window = new Window { Width = 600, Height = 240, Content = editor };
            window.Show();
            try
            {
                window.UpdateLayout();
                editor.FocusDocument();
                editor.Session.Select(3, 3);
                window.KeyPress(Key.Enter, RawInputModifiers.None);
                Assert.Equal("The\n", editor.Text);
                editor.Session.Undo();
                Assert.Equal("THe\n", editor.Text);
                editor.Session.Undo();
                Assert.Equal("THe", editor.Text);
                editor.Session.EditPolicy = new()
                {
                    Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty
                        .Add(EditOperation.Structure, CommandCapability.Disabled)
                };
                editor.Session.Select(3, 3);
                window.KeyPress(Key.Enter, RawInputModifiers.None);
                Assert.Equal("THe", editor.Text);
                editor.Session.EditPolicy = new();

                var normal = TextStyle.Default;
                var noProof = normal with { NoProof = true };
                editor.Document = new FlowDocument([new Paragraph([
                    new RichRun("TH", normal), new RichRun("e", noProof)])]);
                editor.Session.Select(3, 3);
                window.KeyTextInput(" ");
                Assert.Equal("THe ", editor.Text);
                Assert.True(Assert.Single(editor.Document.Blocks.OfType<Paragraph>()).Runs
                    .Any(run => run.Style.NoProof));
            }
            finally { window.Close(); }
        }, CancellationToken.None);

    [Fact]
    public void Replacement_table_capitals_and_url_detection_are_configurable()
    {
        var service = new AutoCorrectService();
        service.Replacements["teh"] = "the";
        AutoCorrectContext Context(string text, char boundary = ' ') =>
            new(text, CultureInfo.GetCultureInfo("en-US"), Guid.Empty, 0, text.Length, boundary, TextStyle.Default);
        Assert.Equal(new AutoCorrectText("the"), service.GetReplacement(Context("teh")));
        Assert.Equal(new AutoCorrectText("The"), service.GetReplacement(Context("THe")));
        Assert.Equal(new AutoCorrectLink("https://www.example.com"), service.GetReplacement(Context("www.example.com")));
        Assert.Null(service.GetReplacement(Context("www.example.com", '.')));
        service.ReplacementRequested = _ => new AutoCorrectText("host");
        Assert.Equal(new AutoCorrectText("host"), service.GetReplacement(Context("teh")));
    }

    [Fact]
    public void Rich_fragment_and_image_callbacks_replace_a_token_without_losing_the_boundary()
    {
        var session = new EditorSession(FlowDocument.FromText("item "));
        session.Select(5, 5);
        var rich = new DocumentFragment
        {
            Document = FlowDocument.FromText("replacement"),
            StartsInsideParagraph = true,
            EndsInsideParagraph = true
        };
        Assert.True(session.TryApplyAutoCorrect(0, 4, new AutoCorrectFragment(rich)));
        Assert.Equal("replacement ", session.Document.Text);
        session.Undo();
        Assert.Equal("item ", session.Document.Text);

        var descriptor = new InlineDescriptor { Payload = new ImageInlinePayload("corrected-image"), AltText = "Image" };
        var resource = new DocumentResource { MediaType = "image/png", Data = ImmutableArray.Create<byte>(1, 2, 3) };
        Assert.True(session.TryApplyAutoCorrect(0, 4, new AutoCorrectInline(descriptor, resource)));
        Assert.Equal("\uFFFC ", session.Document.Text);
        Assert.Single(session.Document.Resources);
        session.Undo();
        Assert.Equal("item ", session.Document.Text);
    }

    [Fact]
    public void Correction_checks_the_target_operation_permission()
    {
        var session = new EditorSession(FlowDocument.FromText("www.example.com "));
        session.Select(session.Index.Length, session.Index.Length);
        session.EditPolicy = new()
        {
            Commands = ImmutableDictionary<EditOperation, CommandCapability>.Empty
                .Add(EditOperation.Formatting, CommandCapability.Disabled)
        };
        Assert.False(session.TryApplyAutoCorrect(0, 15, new AutoCorrectLink("https://example.com")));
        Assert.Equal("www.example.com ", session.Document.Text);
    }
}
