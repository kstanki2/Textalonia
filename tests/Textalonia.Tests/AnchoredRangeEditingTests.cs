using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class AnchoredRangeEditingTests
{
    [Fact]
    public void Paragraph_result_imports_conflicting_images_and_named_styles_without_changing_existing_content()
    {
        static FlowDocument Content(byte value, bool bold) => new([new Paragraph([new RichRun(new InlineDescriptor
            { Payload = new ImageInlinePayload("image"), AltText = "image" }, TextStyle.ForStyle("Style"))])])
        {
            Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", new() { MediaType = "image/png", Data = [value] }),
            Styles = new() { Characters = ImmutableDictionary<string, CharacterStyleDefinition>.Empty.Add("Style", new() { Id = "Style", Formatting = new() { Bold = bold } }) }
        };
        var document = Content(1, false); var result = Content(2, true);
        var updated = DocumentRangeEditing.Replace(document, Guid.Empty, 1, 0, result);
        var runs = ((Paragraph)updated.Blocks[0]).Runs;
        var originalId = ((ImageInlinePayload)runs[0].Inline!.Payload).ResourceId;
        var addedId = ((ImageInlinePayload)runs[1].Inline!.Payload).ResourceId;
        Assert.Equal("image", originalId); Assert.NotEqual(originalId, addedId);
        Assert.Equal((byte)1, updated.Resources[originalId].Data[0]); Assert.Equal((byte)2, updated.Resources[addedId].Data[0]);
        var resolver = new DocumentStyleResolver(updated); var paragraph = (Paragraph)updated.Blocks[0];
        Assert.False(resolver.ResolveText(paragraph, runs[0].Style).Bold); Assert.True(resolver.ResolveText(paragraph, runs[1].Style).Bold);
        updated.Validate();
    }

    [Fact]
    public void Whole_body_structural_result_keeps_headers_properties_page_settings_and_existing_ranges()
    {
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("header")] };
        var section = new DocumentSection { PageSettings = new() { Width = 500 },
            HeaderFooter = new() { PrimaryHeader = new() { StoryId = header.Id, LinkToPrevious = false } } };
        var document = FlowDocument.FromText("cached") with
        {
            Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header), Sections = [section],
            Properties = ImmutableDictionary<string, string>.Empty.Add("Title", "Owner")
        };
        document = document with
        {
            Fields = [new() { Instruction = "DOCVARIABLE Rich", Start = DocumentAnchor.Create(document, Guid.Empty, 0, AnchorAffinity.Before), End = DocumentAnchor.Create(document, Guid.Empty, 6) }],
            Bookmarks = [new() { Name = "header", Start = DocumentAnchor.Create(document, header.Id, 0), End = DocumentAnchor.Create(document, header.Id, 6) }]
        };
        var result = new FlowDocument([Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("replacement")] })]);
        var updated = DocumentRangeEditing.Replace(document, Guid.Empty, 0, 6, result);
        Assert.Equal("replacement", updated.Text); Assert.Equal("Owner", updated.Properties["Title"]);
        Assert.Equal(500, updated.Sections[0].PageSettings.Width); Assert.Equal(header.Id, updated.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary)!.Id);
        Assert.Equal(11, updated.Fields[0].End.Resolve(updated)); Assert.Equal(6, updated.Bookmarks[0].End.Resolve(updated)); updated.Validate();
    }

    [Fact]
    public void Editing_outside_a_cached_field_marks_it_dirty_for_document_wide_dependencies()
    {
        var document = FlowDocument.FromText("cache other");
        document = document with { Fields = [new() { Instruction = "NUMCHARS", IsDirty = false,
            Start = DocumentAnchor.Create(document, Guid.Empty, 0), End = DocumentAnchor.Create(document, Guid.Empty, 5) }] };
        var session = new EditorSession(document); session.Select(11, 11); session.InsertText(" more");
        Assert.True(session.Document.Fields[0].IsDirty);
        var changed = document with { Blocks = [((Paragraph)document.Blocks[0]) with { Runs = [new RichRun("cache much more")] }] };
        session = new EditorSession(document); session.Execute(_ => changed); Assert.True(session.Document.Fields[0].IsDirty);
    }
}
