using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class InlineReviewTests
{
    private static InlineDescriptor Image() => new() { AltText = "image", Payload = new ImageInlinePayload("image") };
    private static DocumentResource Resource() => new()
    {
        MediaType = "image/png", Kind = DocumentResourceKind.Embedded, Data = [1, 2, 3]
    };

    [Fact]
    public void Replacing_all_text_releases_resources_owned_only_by_discarded_hidden_cells()
    {
        var table = Table.Create(1, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Paragraph("anchor")] });
        table = table.SetCell(0, 1, table.Rows[0][1] with { Blocks = [new Paragraph([new RichRun(Image())])] });
        var session = new EditorSession(new FlowDocument([table.MergeCells(0, 0, 1, 2)])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", Resource()) });
        var imagePosition = session.Index.Paragraphs.Last().Start;
        session.Select(imagePosition, imagePosition + 1);
        session.DeleteForward();
        Assert.Single(session.Document.Resources); // Covered source cell still owns this resource.
        session.SelectAll();
        session.InsertText("replacement");
        Assert.Equal("replacement", session.Document.Text);
        Assert.Empty(session.Document.Resources);
        session.Undo();
        Assert.Single(session.Document.Resources);
    }

    [Fact]
    public void Atomic_deletion_leaves_caret_on_a_boundary_when_neighboring_text_forms_a_grapheme()
    {
        var session = new EditorSession(new FlowDocument([new Paragraph([
            new RichRun("a"), new RichRun(Image()), new RichRun("\u0301b")]) ]));
        session.Select(1, 2);
        session.DeleteForward();
        Assert.Equal("a\u0301b", session.Document.Text);
        Assert.Equal(session.Index.Snap(session.Selection.Active), session.Selection.Active);
    }

    [Fact]
    public void Unchanged_merge_with_control_payload_restores_original_cells_after_native_roundtrip()
    {
        var descriptor = new InlineDescriptor
        {
            AltText = "Counter", Payload = new ControlInlinePayload("sample.counter")
            { Properties = ImmutableDictionary<string, string>.Empty.Add("value", "7") }
        };
        var table = Table.Create(1, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Paragraph([new RichRun(descriptor)])] });
        table = table.SetCell(0, 1, table.Rows[0][1] with { Blocks = [new Paragraph("right")] });
        var document = new FlowDocument([table.MergeCells(0, 0, 1, 2)]);
        var loaded = DocumentFormats.Json.Parse(DocumentFormats.Json.Serialize(document));
        var split = Assert.IsType<Table>(loaded.Blocks[0]).SplitCell(0, 0);
        Assert.Single(split.Rows[0][0].Blocks);
        Assert.Equal(descriptor.Id, Assert.IsType<Paragraph>(split.Rows[0][0].Blocks[0]).Runs[0].Inline!.Id);
        Assert.Equal("right", Assert.IsType<Paragraph>(split.Rows[0][1].Blocks[0]).Text);
    }
    [Fact]
    public void Native_clipboard_roundtrip_reuses_an_identical_resource_key_and_data()
    {
        var session = new EditorSession();
        session.InsertInline(Image(), Resource());
        session.SelectAll();
        var fragment = DocumentFormats.Json.Parse(DocumentFormats.Json.Serialize(session.CopySelection()));
        session.Select(session.Index.Length, session.Index.Length);
        session.InsertDocument(fragment);
        Assert.Equal(2, session.Index.Length);
        Assert.Single(session.Document.Resources);
    }

    [Fact]
    public void Replacing_an_image_validates_the_final_pruned_resource_budget()
    {
        var data = new DocumentResource
        {
            Kind = DocumentResourceKind.Embedded, MediaType = "image/png",
            Data = new byte[DocumentResource.MaximumEmbeddedBytes].ToImmutableArray()
        };
        var first = Image();
        var second = Image() with { Payload = new ImageInlinePayload("second") };
        var session = new EditorSession(new FlowDocument([new Paragraph([new RichRun(first), new RichRun(second)])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", data).Add("second", data) });
        session.Select(0, 1);
        session.InsertInline(Image() with { Payload = new ImageInlinePayload("replacement") }, data);
        Assert.Equal(2, session.Document.Resources.Count);
        Assert.False(session.Document.Resources.ContainsKey("image"));
        Assert.True(session.Document.Resources.ContainsKey("replacement"));
    }
}
