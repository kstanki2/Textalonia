using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class BookmarkEditingTests
{
    [Fact]
    public void Backward_selection_bookmark_tracks_split_join_and_undo()
    {
        var session = new EditorSession(FlowDocument.FromText("alpha beta"));
        session.Select(10, 6); var bookmark = session.AddBookmark("word")!;
        Assert.Equal(6, bookmark.Start.Resolve(session.Document));
        session.Select(2, 2); session.InsertText("x\ny");
        Assert.Equal(9, session.Document.Bookmarks[0].Start.Resolve(session.Document));
        Assert.True(session.NavigateToBookmark("word")); Assert.Equal("beta", session.SelectedText);
        session.Select(1, 7); session.InsertText("");
        session.Document.Validate();
        Assert.Equal("beta", Read(session.Document, session.Document.Bookmarks[0]));
        session.Undo(); session.Undo();
        Assert.Equal("alpha beta", session.Document.Text);
        Assert.Equal(6, session.Document.Bookmarks[0].Start.Resolve(session.Document));
        session.Redo(); session.Document.Validate();
    }

    [Fact]
    public void Collapsed_bookmark_follows_inserted_text_and_survives_deletion()
    {
        var session = new EditorSession(FlowDocument.FromText("abc"));
        session.Select(1, 1); session.AddBookmark("caret"); session.InsertText("XX");
        Assert.Equal(3, session.Document.Bookmarks[0].Start.Resolve(session.Document));
        Assert.Equal(session.Document.Bookmarks[0].Start, session.Document.Bookmarks[0].End);
        session.SelectAll(); session.InsertText(""); session.Document.Validate();
        Assert.Equal(0, session.Document.Bookmarks[0].Start.Resolve(session.Document));
    }

    [Fact]
    public void Secondary_story_edits_preserve_body_bookmarks_and_global_history()
    {
        var session = new EditorSession(FlowDocument.FromText("body"));
        session.SelectAll(); session.AddBookmark("body");
        session.ActivateHeaderFooter(Guid.Empty, false);
        var story = session.ActiveStoryId;
        session.InsertText("header"); session.SelectAll(); session.AddBookmark("header");
        session.Select(0, 0); session.InsertText("new ");
        session.Document.Validate();
        Assert.Equal("body", Read(session.Document, session.Document.Bookmarks.Single(b => b.Name == "body")));
        Assert.Equal("new header", Read(session.Document, session.Document.Bookmarks.Single(b => b.Name == "header")));
        session.ReturnToBody(); Assert.True(session.NavigateToBookmark("header")); Assert.Equal(story, session.ActiveStoryId);
        session.Undo(); Assert.Equal("header", session.ActiveDocument.Text);
    }

    [Fact]
    public void Rename_updates_internal_links_and_duplicate_names_are_rejected()
    {
        var session = new EditorSession(FlowDocument.FromText("destination link"));
        session.Select(0, 11); session.AddBookmark("target");
        session.Select(12, 16); session.ApplyStyle(s => s with { InternalLink = new() { BookmarkName = "target", Tooltip = "Go" } });
        session.RenameBookmark("target", "renamed");
        Assert.Equal("renamed", new DocumentIndex(session.Document).At(14).Paragraph.StyleAt(14).InternalLink?.BookmarkName);
        Assert.Throws<ArgumentException>(() => session.AddBookmark("renamed"));
        session.IsReadOnly = true; Assert.Null(session.AddBookmark("blocked")); Assert.True(session.NavigateToBookmark("renamed"));
        session.DeleteBookmark("renamed"); Assert.Single(session.Document.Bookmarks);
    }

    [Fact]
    public void Arbitrary_paragraph_reorder_preserves_anchors_by_identity()
    {
        var session = new EditorSession(FlowDocument.FromText("first\nsecond"));
        session.Select(0, 5); session.AddBookmark("first");
        session.Execute(d => d with { Blocks = [d.Blocks[1], d.Blocks[0]] });
        Assert.Equal("first", Read(session.Document, session.Document.Bookmarks[0]));
        Assert.Equal(7, session.Document.Bookmarks[0].Start.Resolve(session.Document));
    }

    [Fact]
    public void Invalid_detached_or_surrogate_boundary_anchors_are_rejected()
    {
        var document = FlowDocument.FromText("a\U0001F600b");
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentAnchor.Create(document, Guid.Empty, 2));
        var invalid = document with { Bookmarks = [new() { Name = "bad", Start = new(), End = new() }] };
        Assert.Throws<FormatException>(invalid.Validate);
    }

    [Fact]
    public void Arbitrary_text_insertion_preserves_anchors_after_the_inserted_span()
    {
        var session = new EditorSession(FlowDocument.FromText("abcdef"));
        session.Select(4, 6); session.AddBookmark("tail");
        session.Execute(document => document.RewriteParagraphs(p => [p with { Runs = [new RichRun("abXXcdef")] }]));
        Assert.Equal("ef", Read(session.Document, session.Document.Bookmarks[0]));
        Assert.Equal(6, session.Document.Bookmarks[0].Start.Resolve(session.Document));
        session.Undo(); Assert.Equal("ef", Read(session.Document, session.Document.Bookmarks[0]));
    }

    [Fact]
    public void After_affinity_snaps_past_a_grapheme_formed_by_deleting_an_inline_object()
    {
        var document = new FlowDocument([new Paragraph([new RichRun("e"), new RichRun(MergeFields.Create("Name")), new RichRun("\u0301")])]);
        var session = new EditorSession(document); session.Select(2, 2); session.AddBookmark("after");
        session.Select(1, 2); session.InsertText("");
        Assert.Equal("e\u0301", session.Document.Text);
        Assert.Equal(2, session.Document.Bookmarks[0].Start.Resolve(session.Document)); session.Document.Validate();
    }

    [Fact]
    public void Vertical_table_merge_and_split_follow_cloned_paragraphs_instead_of_text_order()
    {
        var table = Table.Create(2, 2);
        for (var r = 0; r < 2; r++) for (var c = 0; c < 2; c++)
            table = table.SetCell(r, c, new TableCell { Blocks = [new Paragraph($"cell{r}{c}")] });
        var session = new EditorSession(new FlowDocument([table]));
        session.Select(14, 20); var bookmark = session.AddBookmark("lower");
        session.Select(0, 0); session.UpdateCurrentTable((value, _, _) => value.MergeCells(0, 0, 2, 1));
        Assert.Equal("cell10", Read(session.Document, session.Document.Bookmarks[0]));
        Assert.Equal(7, session.Document.Bookmarks[0].Start.Resolve(session.Document));
        session.UpdateCurrentTable((value, _, _) => value.SplitCell(0, 0));
        Assert.Equal("cell10", Read(session.Document, session.Document.Bookmarks[0]));
        Assert.Equal(14, session.Document.Bookmarks[0].Start.Resolve(session.Document));
        Assert.Equal(bookmark!.Id, session.Document.Bookmarks[0].Id); session.Document.Validate();
    }

    [Fact]
    public void Removing_a_note_cleans_its_ranges_and_preserves_shifted_body_ranges()
    {
        var session = new EditorSession(FlowDocument.FromText("body")); session.Select(0, 0);
        var note = session.InsertNote(DocumentNoteKind.Endnote)!; session.InsertText("note"); session.SelectAll(); session.AddBookmark("note");
        session.ReturnToBody(); session.Select(1, 5); session.AddBookmark("body"); session.RemoveNote(note.Id);
        Assert.Equal("body", session.Document.Text); Assert.Equal("body", Assert.Single(session.Document.Bookmarks).Name);
        Assert.Equal("body", Read(session.Document, session.Document.Bookmarks[0])); session.Document.Validate();
        session.Undo(); Assert.Equal(2, session.Document.Bookmarks.Length); session.Select(0, 1); session.InsertText("");
        Assert.Single(session.Document.Bookmarks); Assert.Empty(session.Document.Notes); session.Document.Validate();
    }

    [Fact]
    public void Rename_rewrites_named_styles_sparse_overrides_backups_and_keeps_unknown_field_source()
    {
        var link = new InternalLinkDestination { BookmarkName = "target" };
        var definition = new CharacterStyleDefinition { Id = "Link", Formatting = new() { InternalLink = link } };
        var paragraph = new Paragraph([new RichRun("link", TextStyle.ForStyle("Link")),
            new RichRun("direct", TextStyle.ForStyle(null) with { Overrides = new() { InternalLink = link } })]);
        var table = Table.Create(1, 2).SetCell(0, 0, new TableCell { Blocks = [paragraph] }).MergeCells(0, 0, 1, 2);
        var document = new FlowDocument([table]) { Styles = new() { Characters = ImmutableDictionary<string, CharacterStyleDefinition>.Empty.Add("Link", definition) } };
        document = document with { Fields = [new() { Instruction = "UNKNOWN \"unterminated", Start = DocumentAnchor.Create(document, Guid.Empty, 0), End = DocumentAnchor.Create(document, Guid.Empty, 0) }] };
        var session = new EditorSession(document); session.Select(0, 4); session.AddBookmark("target"); session.RenameBookmark("target", "renamed");
        Assert.Equal("renamed", session.Document.Styles.Characters["Link"].Formatting.InternalLink.Value!.BookmarkName);
        var merged = (Table)session.Document.Blocks[0];
        var backup = (Paragraph)merged.Rows[0][0].MergeOriginalBlocks[0];
        Assert.Equal("renamed", backup.Runs[1].Style.Overrides!.InternalLink.Value!.BookmarkName);
        Assert.Equal("UNKNOWN \"unterminated", session.Document.Fields[0].Instruction);
        session.UpdateCurrentTable((value, _, _) => value.SplitCell(0, 0));
        var restored = session.Index.At(0).Paragraph;
        Assert.All(restored.Runs, run => Assert.Equal("renamed", new DocumentStyleResolver(session.Document).ResolveText(restored, run.Style).InternalLink!.BookmarkName));
    }

    [Fact]
    public void Internal_move_transfers_bookmark_and_field_identity_with_one_undo()
    {
        var document = FlowDocument.FromText("abcdef");
        document = document with { Fields = [new() { Instruction = "UNKNOWN", Start = DocumentAnchor.Create(document, Guid.Empty, 1, AnchorAffinity.Before), End = DocumentAnchor.Create(document, Guid.Empty, 3) }] };
        var session = new EditorSession(document); session.Select(1, 3); var bookmark = session.AddBookmark("moved")!;
        var original = session.Document; var snapshot = session.CaptureContentDrag()!;
        Assert.Equal(ContentDropResult.Move, session.DropContent(snapshot.Fragment, 6, session.Revision, snapshot, true));
        Assert.Equal("adefbc", session.Document.Text);
        Assert.Equal(bookmark.Id, Assert.Single(session.Document.Bookmarks).Id);
        Assert.Equal("moved", session.Document.Bookmarks[0].Name); Assert.Equal("bc", Read(session.Document, session.Document.Bookmarks[0]));
        Assert.Equal(document.Fields[0].Id, Assert.Single(session.Document.Fields).Id);
        Assert.Equal(4, session.Document.Fields[0].Start.Resolve(session.Document)); session.Document.Validate();
        session.Undo(); Assert.Same(original, session.Document);
    }

    [Theory]
    [InlineData("")]
    [InlineData("prefix")]
    public void Pasting_into_a_secondary_story_uses_document_wide_bookmark_names(string headerText)
    {
        var session = new EditorSession(FlowDocument.FromText("body")); session.SelectAll(); session.AddBookmark("target");
        var source = new EditorSession(FlowDocument.FromText("source")); source.SelectAll(); source.AddBookmark("target");
        session.ActivateHeaderFooter(Guid.Empty, false); session.InsertText(headerText); session.InsertDocument(source.Document);
        Assert.Equal(2, session.Document.Bookmarks.Length);
        Assert.Contains(session.Document.Bookmarks, bookmark => bookmark.Name == "target_2" && bookmark.Start.StoryId == session.ActiveStoryId);
        session.Document.Validate();
    }

    [Fact]
    public void Unlinking_a_header_clones_fields_bookmarks_and_internal_targets()
    {
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("header")] };
        var document = FlowDocument.FromText("first\nsecond");
        var second = new DocumentSection { StartParagraphId = document.Blocks[1].Id };
        document = document with { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header),
            Sections = [new() { HeaderFooter = new() { PrimaryHeader = new() { StoryId = header.Id, LinkToPrevious = false } } }, second] };
        document = document with
        {
            Bookmarks = [new() { Name = "head", Start = DocumentAnchor.Create(document, header.Id, 0), End = DocumentAnchor.Create(document, header.Id, 6) }],
            Fields = [new() { Instruction = "REF head", Start = DocumentAnchor.Create(document, header.Id, 0), End = DocumentAnchor.Create(document, header.Id, 6) }]
        };
        var session = new EditorSession(document); session.SetHeaderFooterLink(second.Id, false, HeaderFooterVariant.Primary, false);
        var clone = session.Document.ResolveHeaderFooter(1, false, HeaderFooterVariant.Primary)!;
        Assert.NotEqual(header.Id, clone.Id); Assert.Equal("header", session.Document.GetStoryDocument(clone.Id).Text);
        Assert.Contains(session.Document.Bookmarks, bookmark => bookmark.Name == "head_2" && bookmark.Start.StoryId == clone.Id);
        Assert.Contains(session.Document.Fields, field => field.Instruction == "REF head_2" && field.Start.StoryId == clone.Id);
        session.Document.Validate(); session.Undo(); Assert.Same(document, session.Document);
    }

    [Fact]
    public void Invalid_story_storage_and_nul_field_instructions_are_rejected_as_format_errors()
    {
        var document = FlowDocument.FromText("text");
        Assert.Throws<FormatException>(() => (document with { Stories = null! }).Validate());
        var anchor = DocumentAnchor.Create(document, Guid.Empty, 0);
        Assert.Throws<FormatException>(() => (document with { Fields = [new() { Instruction = "UNKNOWN\0data", Start = anchor, End = anchor }] }).Validate());
    }

    private static string Read(FlowDocument document, DocumentBookmark bookmark) => document.GetStoryIndex(bookmark.Start.StoryId)
        .ReadPlainText(bookmark.Start.Resolve(document), bookmark.End.Resolve(document) - bookmark.Start.Resolve(document));
}
