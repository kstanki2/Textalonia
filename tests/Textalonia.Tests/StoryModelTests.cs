using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.MailMerge;
using Xunit;

namespace Textalonia.Tests;

public class StoryModelTests
{
    [Fact]
    public void Header_editing_keeps_main_body_coordinates_and_one_document_history()
    {
        var session = new EditorSession(FlowDocument.FromText("body text"));
        session.Select(7, 2);
        session.ActivateHeaderFooter(Guid.Empty, false);
        var headerId = session.ActiveStoryId;
        session.InsertText("header");
        Assert.Equal("body text", session.Document.Text);
        Assert.Equal("header", session.ActiveDocument.Text);
        session.ReturnToBody(); Assert.Equal(new TextSelection(7, 2), session.Selection);
        session.Undo();
        Assert.Equal(headerId, session.ActiveStoryId); Assert.Equal("", session.ActiveDocument.Text);
        session.Redo(); Assert.Equal("header", session.Document.GetStoryDocument(headerId).Text);
        session.Document.Validate();
    }

    [Fact]
    public void Unlink_clones_inherited_rich_story_and_all_descendant_identities()
    {
        var first = new Paragraph("first"); var second = new Paragraph("second");
        var session = new EditorSession(new FlowDocument([first, second])
        { Sections = [new(), new() { StartParagraphId = second.Id }] });
        var sectionId = session.Document.Sections[1].Id;
        session.ActivateHeaderFooter(session.Document.Sections[0].Id, false);
        session.InsertText("shared"); var original = session.ActiveStoryId;
        session.ReturnToBody();
        Assert.Equal(original, session.Document.ResolveHeaderFooter(1, false, HeaderFooterVariant.Primary)!.Id);
        session.SetHeaderFooterLink(sectionId, false, HeaderFooterVariant.Primary, false);
        var clone = session.Document.ResolveHeaderFooter(1, false, HeaderFooterVariant.Primary)!;
        Assert.NotEqual(original, clone.Id);
        Assert.NotEqual(session.Document.Stories[original].Blocks[0].Id, clone.Blocks[0].Id);
        session.SwitchStory(clone.Id); session.SelectAll(); session.InsertText("different");
        Assert.Equal("shared", session.Document.GetStoryDocument(original).Text);
        session.Document.Validate();
    }

    [Fact]
    public void Note_reference_is_atomic_and_deletion_prunes_its_story_with_undo()
    {
        var session = new EditorSession(FlowDocument.FromText("body")); session.Select(2, 2);
        var note = session.InsertNote(DocumentNoteKind.Footnote, "*")!;
        session.InsertText("rich note"); session.ApplyStyle(style => style with { FontWeight = 700 });
        session.ReturnToBody();
        Assert.Equal("bo\uFFFCdy", session.Document.Text);
        session.Select(2, 3); session.InsertText("");
        Assert.Empty(session.Document.Notes); Assert.DoesNotContain(note.StoryId, session.Document.Stories.Keys);
        session.Undo(); Assert.Single(session.Document.Notes);
        Assert.Equal("rich note", session.Document.GetStoryDocument(note.StoryId).Text);
        session.Document.Validate();
    }

    [Fact]
    public void Note_copy_paste_remaps_owner_story_inline_and_resources()
    {
        var session = new EditorSession(FlowDocument.FromText("body")); session.Select(4, 4);
        var note = session.InsertNote(DocumentNoteKind.Endnote)!;
        session.InsertText("note body"); session.ReturnToBody();
        session.Select(4, 5); var fragment = session.CopyFragment();
        session.Select(0, 0); session.InsertFragment(fragment);
        Assert.Equal(2, session.Document.Notes.Length);
        var pasted = session.Document.Notes.Single(n => n.Id != note.Id);
        Assert.NotEqual(note.StoryId, pasted.StoryId);
        Assert.Equal("note body", session.Document.GetStoryDocument(pasted.StoryId).Text);
        session.Document.Validate();
        session.Select(1, 3);
        Assert.Empty(session.CopyFragment().Document.Notes);
    }

    [Fact]
    public void Secondary_resources_and_history_retention_cover_header_images()
    {
        var session = new EditorSession(FlowDocument.FromText("body")); session.ActivateHeaderFooter(Guid.Empty, false);
        var resource = new DocumentResource { Data = ImmutableArray.Create(new byte[16000]) };
        session.InsertInline(new() { Payload = new ImageInlinePayload("header-image") }, resource);
        Assert.Contains("header-image", session.Document.PruneUnusedResources().Resources.Keys);
        session.SelectAll(); session.InsertText("");
        Assert.DoesNotContain("header-image", session.Document.Resources.Keys);
        Assert.True(session.RetainedHistoryBytes >= 16000);
        session.Undo(); Assert.Contains("header-image", session.Document.Resources.Keys);
        session.Document.Validate();
    }

    [Fact]
    public void Invalid_story_references_kinds_and_nested_notes_are_rejected()
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Header };
        var document = FlowDocument.FromText("body") with { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story),
            Sections = [new() { HeaderFooter = new() { PrimaryHeader = new() { LinkToPrevious = false, StoryId = story.Id } } }] };
        document.Validate();
        Assert.Throws<FormatException>(() => (document with { Stories = document.Stories.SetItem(story.Id, story with { Kind = DocumentStoryKind.Footer }) }).Validate());
        Assert.Throws<FormatException>(() => (document with { Stories = document.Stories.SetItem(story.Id, story with { Blocks = document.Blocks }) }).Validate());
        Assert.Throws<FormatException>(() => (document with { Blocks = [new Paragraph([new RichRun(InlineDescriptor.Note(Guid.NewGuid()))])] }).Validate());
        Assert.Throws<FormatException>(() => (document with { FootnoteSettings = new() { Start = 0 } }).Validate());
    }
    [Fact]
    public void Pasted_note_resources_are_remapped_without_overwriting_destination()
    {
        var source = new EditorSession(FlowDocument.FromText("source")); source.Select(6, 6);
        source.InsertNote(DocumentNoteKind.Footnote);
        source.InsertInline(new() { Payload = new ImageInlinePayload("shared") },
            new DocumentResource { Data = ImmutableArray.Create<byte>(1, 2, 3) });
        source.ReturnToBody(); source.Select(6, 7);
        var target = new EditorSession(FlowDocument.FromText("target") with
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("shared", new() { Data = ImmutableArray.Create<byte>(8, 9) }) });
        target.Select(0, 0); target.InsertInline(new() { Payload = new ImageInlinePayload("shared") });
        target.InsertFragment(source.CopyFragment());
        Assert.Equal(new byte[] { 8, 9 }, target.Document.Resources["shared"].Data);
        var pasted = Assert.Single(target.Document.Notes);
        var image = Assert.IsType<ImageInlinePayload>(((Paragraph)target.Document.Stories[pasted.StoryId].Blocks[0]).Runs[0].Inline!.Payload);
        Assert.NotEqual("shared", image.ResourceId);
        Assert.Equal(new byte[] { 1, 2, 3 }, target.Document.Resources[image.ResourceId].Data);
        target.Document.Validate();
    }

    [Fact]
    public void Numbering_uses_reference_order_custom_marks_and_explicit_restart_context()
    {
        var first = new Paragraph("one"); var second = new Paragraph("two");
        var session = new EditorSession(new FlowDocument([first, second])
        { Sections = [new(), new() { StartParagraphId = second.Id }] });
        session.Select(0, 0); var a = session.InsertNote(DocumentNoteKind.Footnote)!; session.ReturnToBody();
        session.Select(session.Index.Length, session.Index.Length); var b = session.InsertNote(DocumentNoteKind.Footnote)!; session.ReturnToBody();
        session.SetNoteSettings(DocumentNoteKind.Footnote, new() { Start = 3, NumberFormat = PageNumberFormat.LowerRoman });
        Assert.Equal("iii", DocumentNoteNumbering.GetMark(session.Document, a.Id));
        Assert.Equal("iv", DocumentNoteNumbering.GetMark(session.Document, b.Id));
        session.SetNoteSettings(DocumentNoteKind.Footnote, session.Document.FootnoteSettings with { Restart = NoteRestartPolicy.EachSection });
        Assert.Equal("iii", DocumentNoteNumbering.GetMark(session.Document, b.Id));
        session.SetNoteSettings(DocumentNoteKind.Footnote, session.Document.FootnoteSettings with { Restart = NoteRestartPolicy.EachPage });
        Assert.Equal("iii", DocumentNoteNumbering.GetMark(session.Document, b.Id, new Dictionary<Guid, int> { [a.Id] = 1, [b.Id] = 2 }));
    }

    [Fact]
    public void Copying_later_section_materializes_its_inherited_header()
    {
        var first = new Paragraph("one"); var second = new Paragraph("two");
        var session = new EditorSession(new FlowDocument([first, second])
        { Sections = [new(), new() { StartParagraphId = second.Id }] });
        session.ActivateHeaderFooter(session.Document.Sections[0].Id, false); session.InsertText("inherited"); session.ReturnToBody();
        session.Select(4, 7);
        var fragment = session.CopyFragment();
        Assert.False(fragment.Document.Sections[0].HeaderFooter.PrimaryHeader.LinkToPrevious);
        Assert.Equal("inherited", fragment.Document.GetStoryDocument(fragment.Document.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary)!.Id).Text);
        var target = new EditorSession(); target.InsertFragment(fragment);
        Assert.Equal("inherited", target.Document.GetStoryDocument(target.Document.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary)!.Id).Text);
        target.Document.Validate();
    }

    [Fact]
    public void Mail_merge_previews_and_fills_rich_fields_in_headers_and_notes()
    {
        var session = new EditorSession(FlowDocument.FromText("body"));
        session.ActivateHeaderFooter(Guid.Empty, false);
        session.InsertInline(new() { Payload = new MergeFieldInlinePayload("Name"), AltText = "name" });
        var headerId = session.ActiveStoryId; session.ReturnToBody();
        var note = session.InsertNote(DocumentNoteKind.Endnote)!;
        session.InsertInline(new() { Payload = new MergeFieldInlinePayload("Total") { Format = "N2" }, AltText = "total" });
        var template = session.Document;
        Assert.Equal(new[] { "Name", "Total" }, MailMergeProcessor.GetFieldNames(template).OrderBy(x => x));
        var data = new Dictionary<string, object?> { ["Name"] = "Ada", ["Total"] = 12.5m };
        var preview = MailMergeProcessor.Preview(template, data);
        Assert.Equal("Ada", preview.GetStoryDocument(headerId).PlainText);
        Assert.Equal("12.50", preview.GetStoryDocument(note.StoryId).PlainText);
        Assert.Equal("name", template.GetStoryDocument(headerId).PlainText);
        var merged = MailMergeProcessor.Merge(template, data);
        Assert.Equal("Ada", merged.GetStoryDocument(headerId).Text);
        Assert.Equal("12.50", merged.GetStoryDocument(note.StoryId).Text);
        Assert.Empty(MailMergeProcessor.GetFieldNames(merged));
        merged.Validate(); preview.Validate();
    }

    [Fact]
    public void Deleting_only_a_note_reference_releases_its_image_but_undo_retains_bytes()
    {
        var session = new EditorSession(FlowDocument.FromText("body")); session.Select(2, 2);
        var note = session.InsertNote(DocumentNoteKind.Footnote)!;
        session.InsertInline(new() { Payload = new ImageInlinePayload("note-image") }, new DocumentResource { Data = ImmutableArray.Create(new byte[20000]) });
        session.ReturnToBody(); session.Select(2, 3); session.InsertText("");
        Assert.Empty(session.Document.Notes); Assert.Empty(session.Document.Resources);
        Assert.True(session.RetainedHistoryBytes >= 20000);
        session.Undo(); Assert.Contains("note-image", session.Document.Resources.Keys);
        Assert.Equal(note.Id, Assert.Single(session.Document.Notes).Id);
    }

}
