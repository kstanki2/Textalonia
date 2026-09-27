using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class AnchoredFragmentTests
{
    private static DocumentBookmark Bookmark(FlowDocument document, string name, int start, int end, Guid story = default) => new()
    {
        Name = name, Start = DocumentAnchor.Create(document, story, start, AnchorAffinity.Before),
        End = DocumentAnchor.Create(document, story, end, AnchorAffinity.After)
    };
    private static DocumentField Field(FlowDocument document, string instruction, int start, int end, Guid story = default) => new()
    {
        Instruction = instruction, Start = DocumentAnchor.Create(document, story, start, AnchorAffinity.Before),
        End = DocumentAnchor.Create(document, story, end, AnchorAffinity.After)
    };

    [Fact]
    public void Copy_keeps_contained_ranges_and_flattens_partial_bookmarks_and_fields()
    {
        var document = FlowDocument.FromText("abcdef");
        document = document with
        {
            Bookmarks = [Bookmark(document, "inside", 2, 4), Bookmark(document, "partial", 0, 3),
                Bookmark(document, "start", 1, 1), Bookmark(document, "end", 5, 5)],
            Fields = [Field(document, "DOCVARIABLE Value", 2, 4), Field(document, "UNKNOWN", 0, 6)]
        };
        var session = new EditorSession(document); session.Select(1, 5);
        var fragment = session.CopyFragment();
        Assert.Equal("bcde", fragment.Document.Text);
        Assert.Equal(new[] { "inside", "start", "end" }, fragment.Document.Bookmarks.Select(b => b.Name));
        Assert.Equal(1, fragment.Document.Bookmarks[0].Start.Resolve(fragment.Document));
        Assert.Equal(3, fragment.Document.Bookmarks[0].End.Resolve(fragment.Document));
        Assert.Equal(0, fragment.Document.Bookmarks[1].Start.Resolve(fragment.Document));
        Assert.Equal(4, fragment.Document.Bookmarks[2].End.Resolve(fragment.Document));
        Assert.Equal("DOCVARIABLE Value", Assert.Single(fragment.Document.Fields).Instruction);
        Assert.DoesNotContain(fragment.Document.Bookmarks[0].Id, document.Bookmarks.Select(b => b.Id));
        fragment.Validate();
    }

    [Fact]
    public void Copy_of_a_paragraph_break_preserves_ranges_ending_at_the_next_paragraph_start()
    {
        var document = FlowDocument.FromText("one\ntwo");
        document = document with { Bookmarks = [Bookmark(document, "line", 0, 4)], Fields = [Field(document, "UNKNOWN", 0, 4)] };
        var session = new EditorSession(document); session.Select(0, 4);
        var copy = session.CopyFragment().Document;
        Assert.Equal("one\n", copy.Text);
        Assert.Equal(4, Assert.Single(copy.Bookmarks).End.Resolve(copy));
        Assert.Equal(4, Assert.Single(copy.Fields).End.Resolve(copy));
        copy.Validate();
    }

    [Fact]
    public void Paste_remaps_ranges_links_and_field_references_and_moves_existing_bookmarks()
    {
        var source = FlowDocument.FromText("abc");
        source = source with { Bookmarks = [Bookmark(source, "target", 0, 3)], Fields = [Field(source, "REF target", 0, 3)] };
        var paragraph = Assert.IsType<Paragraph>(source.Blocks[0]);
        source = source with { Blocks = [paragraph with { Runs = [new RichRun("abc", new TextStyle
            { InternalLink = new() { BookmarkName = "target", Tooltip = "Go", Activation = InternalLinkActivation.Click } })] }] };
        var original = FlowDocument.FromText("leftRIGHT");
        original = original with { Bookmarks = [Bookmark(original, "target", 4, 9), Bookmark(original, "target_2", 0, 0)] };
        var session = new EditorSession(original); session.Select(4, 4); session.InsertDocument(source);
        var pasted = session.Document.Bookmarks.Single(b => b.Name == "target_3");
        Assert.Equal("leftabcRIGHT", session.Document.Text);
        Assert.Equal(4, pasted.Start.Resolve(session.Document)); Assert.Equal(7, pasted.End.Resolve(session.Document));
        Assert.Equal(12, session.Document.Bookmarks.Single(b => b.Name == "target").End.Resolve(session.Document));
        Assert.Contains("target_3", Assert.Single(session.Document.Fields).Instruction);
        var link = session.Index.Paragraphs.SelectMany(p => p.Paragraph.Runs).Select(r => r.Style.InternalLink).Single(l => l is not null)!;
        Assert.Equal("target_3", link.BookmarkName); Assert.Equal("Go", link.Tooltip); Assert.Equal(InternalLinkActivation.Click, link.Activation);
        session.InsertDocument(source);
        Assert.Contains(session.Document.Bookmarks, b => b.Name == "target_4");
        Assert.Equal(session.Document.Bookmarks.Length, session.Document.Bookmarks.Select(b => b.Id).Distinct().Count());
        session.Document.Validate(); session.Undo(); session.Undo(); Assert.Same(original, session.Document);
    }

    [Fact]
    public void Complete_paragraph_paste_keeps_field_boundaries_outside_destination_prefix_and_suffix()
    {
        var source = FlowDocument.FromText("result");
        source = source with { Fields = [Field(source, "UNKNOWN", 0, 6)] };
        var destination = FlowDocument.FromText("abcd");
        destination = destination with { Bookmarks = [Bookmark(destination, "suffix", 3, 4)] };
        var session = new EditorSession(destination); session.Select(2, 2);
        session.InsertFragment(new() { Document = source });
        Assert.Equal("ab\nresult\ncd", session.Document.Text);
        Assert.Equal(3, Assert.Single(session.Document.Fields).Start.Resolve(session.Document));
        Assert.Equal(9, Assert.Single(session.Document.Fields).End.Resolve(session.Document));
        Assert.Equal(11, Assert.Single(session.Document.Bookmarks).Start.Resolve(session.Document));
        Assert.Equal(12, Assert.Single(session.Document.Bookmarks).End.Resolve(session.Document));
        session.Document.Validate();
    }

    [Fact]
    public void Copied_note_ranges_get_new_story_and_paragraph_ownership_on_each_paste()
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Endnote, Blocks = [new Paragraph("note result")] };
        var note = new DocumentNote { Kind = DocumentNoteKind.Endnote, StoryId = story.Id };
        var source = new FlowDocument([new Paragraph([new RichRun(InlineDescriptor.Note(note.Id, "1"))])])
        { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story), Notes = [note] };
        source = source with { Bookmarks = [Bookmark(source, "note", 0, 4, story.Id)], Fields = [Field(source, "DOCVARIABLE Value", 5, 11, story.Id)] };
        var session = new EditorSession(source); session.SelectAll(); var copy = session.CopyFragment(); session.Select(0, 0); session.InsertFragment(copy);
        var pastedNote = session.Document.Notes.Single(n => n.Id != note.Id);
        var pasted = session.Document.Bookmarks.Single(b => b.Name == "note_2");
        Assert.Equal(pastedNote.StoryId, pasted.Start.StoryId);
        Assert.NotEqual(story.Blocks[0].Id, pasted.Start.ParagraphId);
        Assert.Equal(4, pasted.End.Resolve(session.Document));
        Assert.Contains(session.Document.Fields, f => f.Start.StoryId == pastedNote.StoryId && f.Start.Resolve(session.Document) == 5);
        session.Document.Validate();
    }

    [Fact]
    public void Rectangular_copy_drops_ranges_spanning_omitted_cells()
    {
        var table = Table.Create(2, 2);
        for (var row = 0; row < 2; row++) for (var column = 0; column < 2; column++)
            table = table.SetCell(row, column, new TableCell { Blocks = [new Paragraph("text")] });
        var source = new FlowDocument([table]);
        source = source with { Bookmarks = [Bookmark(source, "first", 0, 4), Bookmark(source, "across", 0, 14)],
            Fields = [Field(source, "UNKNOWN", 0, 14)] };
        var copy = new EditorSession(source).CopyCells(table.Id, 0, 0, 2, 1).Document;
        Assert.Equal("first", Assert.Single(copy.Bookmarks).Name); Assert.Empty(copy.Fields); copy.Validate();
    }

    [Fact]
    public void Paste_remaps_inherited_internal_links_without_changing_the_destination_style_definition()
    {
        var definition = new CharacterStyleDefinition { Id = "Link", Formatting = new()
            { InternalLink = new InternalLinkDestination { BookmarkName = "target" } } };
        var styles = new DocumentStyleCatalog { Characters = ImmutableDictionary<string, CharacterStyleDefinition>.Empty.Add(definition.Id, definition) };
        var source = new FlowDocument([new Paragraph([new RichRun("source", TextStyle.ForStyle("Link"))])]) { Styles = styles };
        source = source with { Bookmarks = [Bookmark(source, "target", 0, 6)] };
        var destination = FlowDocument.FromText("destination") with { Styles = styles };
        destination = destination with { Bookmarks = [Bookmark(destination, "target", 0, 11)] };
        var session = new EditorSession(destination); session.Select(11, 11); session.InsertDocument(source);
        var paragraph = session.Index.Paragraphs[0].Paragraph;
        var pasted = paragraph.Runs.Single(run => run.Text == "source");
        Assert.Equal("target_2", new DocumentStyleResolver(session.Document).ResolveText(paragraph, pasted.Style).InternalLink!.BookmarkName);
        Assert.Equal("target", session.Document.Styles.Characters["Link"].Formatting.InternalLink.Value!.BookmarkName);
        session.Document.Validate();
    }

    [Fact]
    public void Paste_suffixes_maximum_length_bookmark_names_without_exceeding_the_limit()
    {
        var name = new string('a', 256);
        var source = FlowDocument.FromText("x"); source = source with { Bookmarks = [Bookmark(source, name, 0, 1)] };
        var session = new EditorSession(source); session.Select(1, 1); session.InsertDocument(source);
        var copy = session.Document.Bookmarks.Single(b => b.Name != name);
        Assert.Equal(256, copy.Name.Length); Assert.EndsWith("_2", copy.Name); session.Document.Validate();
    }

    [Fact]
    public void Field_instructions_and_properties_retained_only_by_history_count_toward_its_budget()
    {
        var source = FlowDocument.FromText("result");
        source = source with { Fields = [Field(source, "UNKNOWN " + new string('x', 16000), 0, 6)],
            Properties = ImmutableDictionary<string, string>.Empty.Add("data", new string('y', 16000)) };
        var session = new EditorSession(source);
        session.Execute(document => document with { Fields = [], Properties = ImmutableDictionary<string, string>.Empty });
        Assert.True(session.RetainedHistoryBytes >= 64000);
        session.HistoryByteLimit = 1024;
        Assert.True(session.RetainedHistoryBytes <= 1024); Assert.False(session.CanUndo);
    }
}
