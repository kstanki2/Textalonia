using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class InlineModelTests
{
    private static InlineDescriptor Image(string id = "image", string alt = "A diagram") =>
        new() { AltText = alt, Width = 80, Height = 40, Payload = new ImageInlinePayload(id) };
    private static DocumentResource Bytes(int count = 128) => new()
    {
        Kind = DocumentResourceKind.Embedded, MediaType = "image/png",
        Data = Enumerable.Range(0, count).Select(i => (byte)i).ToImmutableArray()
    };

    [Fact]
    public void Inline_positions_are_atomic_and_plain_text_uses_alt_text()
    {
        var image = Image();
        var paragraph = new Paragraph([new RichRun("a"), new RichRun(image), new RichRun("\u0301b")]);
        var document = new FlowDocument([paragraph]);
        document.Validate(); // Unresolved resources are valid snapshot data.
        var session = new EditorSession(document);
        Assert.Equal(4, session.Index.Length);
        Assert.Equal("a\uFFFC\u0301b", document.Text);
        Assert.Equal("aA diagram\u0301b", document.PlainText);
        Assert.Equal(1, session.NextCaret(0));
        Assert.Equal(2, session.NextCaret(1));
        Assert.Equal(2, session.PreviousCaret(3));
        session.Select(1, 2);
        Assert.Equal("A diagram", session.SelectedText);
        Assert.Equal("\uFFFC", session.Index.ReadText(1, 1));
        Assert.Same(image, paragraph.Slice(1, 1).Single().Inline);
        Assert.Equal(3, paragraph.Runs.Length);
        session.DeleteForward();
        Assert.Equal("a\u0301b", session.Document.Text);
        session.Undo();
        Assert.Equal(document.Text, session.Document.Text);
        session.Select(2, 2);
        session.DeleteBackward();
        Assert.Equal("a\u0301b", session.Document.Text);
    }

    [Fact]
    public void Inline_insert_resize_and_resource_deletion_are_undoable()
    {
        var session = new EditorSession(FlowDocument.FromText("ab"));
        session.Select(1, 1);
        var inline = Image();
        var data = Bytes();
        session.InsertInline(inline, data);
        var snapshot = session.Document;
        Assert.Equal("a\uFFFCb", snapshot.Text);
        Assert.Equal(2, session.Selection.Active);
        Assert.Same(data, snapshot.Resources["image"]);
        session.UpdateInline(inline.Id, value => value with { Width = 200, AltText = "Resized" });
        Assert.Equal(200, new DocumentIndex(session.Document).At(1).Paragraph.Runs[1].Inline!.Width);
        session.Undo();
        Assert.Equal(80, new DocumentIndex(session.Document).At(1).Paragraph.Runs[1].Inline!.Width);
        session.Redo();
        Assert.Equal("aResizedb", session.Document.PlainText);
        session.Select(1, 2);
        session.DeleteForward();
        Assert.Empty(session.Document.Resources);
        Assert.Same(data, snapshot.Resources["image"]);
        session.Undo();
        Assert.Same(data, session.Document.Resources["image"]);
    }

    [Fact]
    public void Retained_history_counts_encoded_bytes_and_eviction_releases_its_ownership()
    {
        var session = new EditorSession();
        var bytes = Bytes(8192);
        session.InsertInline(Image(), bytes);
        var savedSnapshot = session.Document;
        session.SelectAll();
        session.DeleteForward();
        Assert.Empty(session.Document.Resources);
        Assert.True(session.RetainedHistoryBytes >= bytes.Data.Length);
        session.HistoryByteLimit = 0;
        Assert.Equal(0, session.RetainedHistoryBytes);
        Assert.False(session.CanUndo);
        Assert.Equal(8192, savedSnapshot.Resources["image"].Data.Length);
    }

    [Fact]
    public void Clipboard_preserves_resources_and_remaps_collisions_and_inline_identities()
    {
        var source = new EditorSession();
        var image = Image();
        source.InsertInline(image, Bytes(32));
        source.SelectAll();
        var copy = source.CopySelection();
        Assert.Single(copy.Resources);
        var destination = new EditorSession();
        destination.InsertInline(Image(), Bytes(64));
        destination.InsertDocument(copy);
        destination.Document.Validate();
        Assert.Equal(2, destination.Index.Length);
        Assert.Equal(2, destination.Document.Resources.Count);
        var pasted = destination.Index.At(1).Paragraph.Runs.Last().Inline!;
        Assert.NotEqual(image.Id, pasted.Id);
        Assert.NotEqual("image", ((ImageInlinePayload)pasted.Payload).ResourceId);
        Assert.Equal(32, destination.Document.Resources[((ImageInlinePayload)pasted.Payload).ResourceId].Data.Length);
        destination.InsertDocument(copy);
        destination.Document.Validate();
        Assert.Equal(3, destination.Index.Length);
    }

    [Fact]
    public async Task Native_roundtrip_preserves_typed_payloads_and_embedded_local_host_resources()
    {
        var image = Image();
        var control = new InlineDescriptor
        {
            AltText = "A button", Payload = new ControlInlinePayload("sample.counter")
            { Properties = ImmutableDictionary<string, string>.Empty.Add("value", "2") }
        };
        var document = new FlowDocument([new Paragraph([new RichRun(image), new RichRun(control)])])
        {
            Resources = ImmutableDictionary<string, DocumentResource>.Empty
                .Add("image", Bytes())
                .Add("local", new() { Kind = DocumentResourceKind.Local, Location = "photos/a.png" })
                .Add("host", new() { Kind = DocumentResourceKind.Host, Location = "tenant:asset:123" })
        };
        using var stream = new MemoryStream();
        await DocumentFormats.Json.SaveAsync(document, stream);
        var json = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"version\": 4", json);
        Assert.Contains(Convert.ToBase64String(document.Resources["image"].Data.AsSpan()), json);
        stream.Position = 0;
        var loaded = await DocumentFormats.Json.LoadAsync(stream);
        Assert.Equal(json, DocumentFormats.Json.Serialize(loaded));
        Assert.Equal(document.Resources["image"].Data.ToArray(), loaded.Resources["image"].Data.ToArray());
        Assert.Equal("photos/a.png", loaded.Resources["local"].Location);
        var loadedControl = Assert.IsType<ControlInlinePayload>(new DocumentIndex(loaded).At(1).Paragraph.Runs[1].Inline!.Payload);
        Assert.Equal("2", loadedControl.Properties["value"]);
        Assert.Equal("A diagramA button", loaded.PlainText);
        Assert.Throws<JsonException>(() => DocumentFormats.Json.Parse(json.Replace("\"kind\": \"control\"", "\"kind\": \"System.Windows.Controls.Button\"")));
    }

    [Fact]
    public void Version_two_migration_keeps_the_old_vocabulary_frozen()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "native-v2.json"));
        var loaded = DocumentFormats.Json.Parse(fixture);
        Assert.Empty(loaded.Resources);
        var json = JsonNode.Parse(fixture)!;
        json["document"]!["resources"] = new JsonObject();
        Assert.Throws<JsonException>(() => DocumentFormats.Json.Parse(json.ToJsonString()));
        json = JsonNode.Parse(fixture)!;
        json["document"]!["blocks"]![0]!["runs"]![0]!["inline"] = null;
        Assert.Throws<JsonException>(() => DocumentFormats.Json.Parse(json.ToJsonString()));
        Assert.Equal(loaded.Text, DocumentFormats.Json.Parse(DocumentFormats.Json.Serialize(loaded)).Text);
    }

    [Fact]
    public async Task Non_native_exports_use_missing_object_alt_text()
    {
        var document = new FlowDocument([new Paragraph([new RichRun("before "), new RichRun(Image(alt: "missing image")), new RichRun(" after")])]);
        Assert.Equal("before missing image after", DocumentFormats.PlainText.Serialize(document));
        Assert.Equal(document.PlainText, DocumentFormats.Html.Parse(DocumentFormats.Html.Serialize(document)).Text);
        Assert.Equal(document.PlainText, DocumentFormats.Rtf.Parse(DocumentFormats.Rtf.Serialize(document)).Text);
        using var stream = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(document, stream);
        stream.Position = 0;
        Assert.Equal(document.PlainText, (await DocumentFormats.Docx.LoadAsync(stream)).Text);
    }

    [Fact]
    public void Typing_in_a_large_document_with_images_keeps_index_updates_local()
    {
        var paragraphs = Enumerable.Range(0, 10000).Select(i => new Paragraph("Paragraph " + i)).ToArray();
        paragraphs[^1] = new Paragraph([new RichRun(Image())]);
        var document = new FlowDocument(paragraphs)
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", Bytes()) };
        var session = new EditorSession(document);
        var position = session.Index.ById(paragraphs[5000].Id).Start;
        session.Select(position, position);
        var original = session.Index;
        var visited = original.VisitedParagraphs;
        session.InsertText("typed");
        Assert.InRange(original.VisitedParagraphs - visited, 1, 12);
        Assert.InRange(session.Index.Tree.UpdatedNodes, 1, 3);
        Assert.Same(document.Resources, session.Document.Resources);
        Assert.Same(paragraphs[^1], session.Index.ById(paragraphs[^1].Id).Paragraph);
    }

    [Fact]
    public void Invalid_inline_and_resource_data_are_rejected_without_resolving_any_location()
    {
        Assert.Throws<FormatException>(() => new FlowDocument([new Paragraph([new RichRun(Image() with { Width = double.NaN })])]).Validate());
        Assert.Throws<FormatException>(() => new FlowDocument([new Paragraph([new RichRun(Image() with { Height = 0 })])]).Validate());
        var image = Image();
        Assert.Throws<FormatException>(() => new FlowDocument([new Paragraph([new RichRun(image), new RichRun(image)])]).Validate());
        Assert.Throws<FormatException>(() => new FlowDocument { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("too-large", Bytes(DocumentResource.MaximumEmbeddedBytes + 1)) }.Validate());
        Assert.Throws<FormatException>(() => new FlowDocument { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("local", new() { Kind = DocumentResourceKind.Local }) }.Validate());
        var session = new EditorSession { IsReadOnly = true };
        session.InsertInline(image, Bytes());
        Assert.Empty(session.Document.Text);
        Assert.Empty(session.Document.Resources);
    }

    [Fact]
    public void Merged_hidden_inline_resources_survive_until_their_source_cells_are_removed()
    {
        var inline = Image();
        var table = Table.Create(1, 2);
        table = table.SetCell(0, 1, table.Rows[0][1] with { Blocks = [new Paragraph([new RichRun(inline)])] });
        var document = new FlowDocument([table.MergeCells(0, 0, 1, 2)])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", Bytes()) };
        document.Validate();
        Assert.Single(document.PruneUnusedResources().Resources);
        var loaded = DocumentFormats.Json.Parse(DocumentFormats.Json.Serialize(document));
        var merged = Assert.IsType<Table>(loaded.Blocks.Single());
        var split = merged.SplitCell(0, 0);
        Assert.Equal(inline.Id, Assert.IsType<Paragraph>(split.Rows[0][1].Blocks.Single()).Runs.Single().Inline!.Id);
        Assert.Single((loaded with { Blocks = [split] }).PruneUnusedResources().Resources);
    }
}
