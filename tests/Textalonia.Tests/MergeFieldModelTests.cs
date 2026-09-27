using Avalonia;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class MergeFieldModelTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("First\nName")]
    [InlineData("Name\0")]
    public void Invalid_field_names_are_rejected(string name)
    {
        Assert.Throws<FormatException>(() => MergeFields.Create(name));
        var document = new FlowDocument([new Paragraph([new RichRun(new InlineDescriptor
            { Payload = new MergeFieldInlinePayload(name) })])]);
        Assert.Throws<FormatException>(() => document.Validate());
    }

    [Fact]
    public void Field_metadata_has_bounded_validated_lengths()
    {
        Assert.Throws<ArgumentNullException>(() => MergeFields.Create(null!));
        Assert.Throws<FormatException>(() => MergeFields.Create(new string('a', 257)));
        Assert.Throws<FormatException>(() => MergeFields.Create("Name", new string('0', 1025)));
        Assert.Throws<FormatException>(() => MergeFields.Create("Name", "bad\nformat"));
        Assert.Throws<FormatException>(() => MergeFields.Create("Name", fallbackText: new string('x', 16385)));
        Assert.Throws<FormatException>(() => MergeFields.Create("Name", fallbackText: "bad\0text"));
        var field = MergeFields.Create("Customer.Display Name", "N2", "No value\nprovided");
        var document = new FlowDocument([new Paragraph([new RichRun(field)])]);
        document.Validate();
        Assert.Equal("\uFFFC", document.Text);
        Assert.Equal("«Customer.Display Name»", document.PlainText);
    }

    [Fact]
    public void Field_payload_strings_are_counted_towards_history_budget()
    {
        var field = MergeFields.Create("Name", fallbackText: new string('x', 16000));
        var session = new EditorSession(new FlowDocument([new Paragraph([new RichRun(field)])]));
        session.Select(0, 1);
        session.InsertText("replacement");
        Assert.True(session.RetainedHistoryBytes >= 32000);
        session.HistoryByteLimit = 1024;
        Assert.True(session.RetainedHistoryBytes <= 1024);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void Native_field_fixture_round_trips_the_current_schema()
    {
        var document = DocumentFormats.Json.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "native-merge-field.json")));
        var paragraph = Assert.IsType<Paragraph>(Assert.Single(document.Blocks));
        var inline = paragraph.Runs[1].Inline!;
        var field = Assert.IsType<MergeFieldInlinePayload>(inline.Payload);
        Assert.Equal("Total", field.Name);
        Assert.Equal("N2", field.Format);
        Assert.Equal("0.00", field.FallbackText);
        Assert.Equal("Invoice: «Total»", document.PlainText);
        var serialized = DocumentFormats.Json.Serialize(document);
        Assert.Equal(serialized, DocumentFormats.Json.Serialize(DocumentFormats.Json.Parse(serialized)));
    }
}

public class MergeFieldLayoutTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);

    [Fact]
    public Task Fields_measure_their_labels_and_share_atomic_selection_geometry() => Run(() =>
    {
        var field = MergeFields.Create("FirstName");
        var paragraph = new Paragraph([new RichRun("A"), new RichRun(field, new TextStyle { Bold = true, FontSize = 28 }), new RichRun("B")]);
        using var layout = new DocumentLayout();
        layout.Build(new FlowDocument([paragraph]), 800, FontFamily.Default, Brushes.Black, Brushes.Gray, new Thickness(10));
        var visual = Assert.Single(layout.InlineVisuals());
        Assert.Equal(1, visual.Position);
        Assert.True(visual.Bounds.Height > 24);
        Assert.InRange(layout.Caret(2).X - layout.Caret(1).X, visual.Bounds.Width - .5, visual.Bounds.Width + .5);
        Assert.InRange(Assert.Single(layout.SelectionRects(1, 1)).Width, visual.Bounds.Width - .5, visual.Bounds.Width + .5);
        Assert.Equal(1, layout.HitTest(visual.Bounds.TopLeft + new Vector(2, visual.Bounds.Height / 2)));
        Assert.Equal(2, layout.HitTest(visual.Bounds.TopRight + new Vector(-2, visual.Bounds.Height / 2)));
        var properties = InlineTextSource.Properties(TextStyle.Default, FontFamily.Default, Brushes.Black);
        var shortRun = new InlineObjectRun(field with { AltText = "A" }, properties);
        var longRun = new InlineObjectRun(field with { AltText = "Long recipient name" }, properties);
        Assert.True(longRun.Size.Width > shortRun.Size.Width);
        var emptyRun = new InlineObjectRun(field with { AltText = "" }, properties);
        Assert.True(emptyRun.Size.Width > 0);
        Assert.True(emptyRun.Size.Height > 0);
    });

    [Fact]
    public Task Long_multiline_and_unicode_labels_have_finite_bounded_geometry() => Run(() =>
    {
        foreach (var fontSize in new[] { 1d, 16d, 512d })
        foreach (var value in new[] { "", "\U0001F680", "Ada\r\nLovelace\u2028London", new string('x', 16384) })
        {
            var properties = InlineTextSource.Properties(new TextStyle { FontSize = fontSize }, FontFamily.Default, Brushes.Black);
            var run = new InlineObjectRun(MergeFields.Create("Name") with { AltText = value }, properties);
            Assert.True(double.IsFinite(run.Size.Width));
            Assert.True(double.IsFinite(run.Size.Height));
            Assert.InRange(run.Size.Width, 12, 608.01);
            Assert.True(run.Size.Height > 0);
            Assert.InRange(run.Baseline, 0, run.Size.Height);
        }
    });

    [Fact]
    public Task Accessibility_identifies_field_name_and_current_display_value() => Run(() =>
    {
        var field = MergeFields.Create("FirstName") with { AltText = "Ada" };
        var editor = new TextaloniaEditor { Document = new FlowDocument([new Paragraph([new RichRun(field)])]) };
        var description = Assert.Single(editor.Accessibility.DocumentRange.GetDescriptions());
        Assert.Equal(new DocumentTextDescription(0, 1, "merge-field", "Merge field FirstName: Ada"), description);
        Assert.Equal("\uFFFC", editor.Accessibility.DocumentRange.GetText());
    });
}
