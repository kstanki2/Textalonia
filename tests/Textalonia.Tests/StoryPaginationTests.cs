using System.Collections.Immutable;
using Avalonia;
using Textalonia.Layout;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class StoryPaginationTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);
    private static readonly ParagraphStyle LineStyle = new()
    { SpaceBefore = 0, SpaceAfter = 0, LineSpacingMode = LineSpacingMode.Exact, LineSpacing = 20, WidowControl = false };
    private static readonly PageSettings Paper = new()
    { Width = 300, Height = 240, Margins = new EdgeInsets(30, 60, 30, 60), BalanceColumns = false };
    private static Paragraph Lines(int count) => new(string.Join('\u2028', Enumerable.Range(1, count).Select(i => $"Line {i}"))) { Style = LineStyle };
    private static StoryReference Ref(DocumentStory story) => new() { StoryId = story.Id, LinkToPrevious = false };
    private static DocumentStory Header(string text) => new() { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph(text) { Style = LineStyle }] };

    [Fact]
    public Task First_even_primary_and_linked_instances_keep_separate_page_contexts() => Run(() =>
    {
        var primary = Header("primary"); var first = Header("first"); var even = Header("even");
        var second = Lines(6);
        var section = new DocumentSection { PageSettings = Paper, HeaderFooter = new()
        { PrimaryHeader = Ref(primary), FirstHeader = Ref(first), EvenHeader = Ref(even), DifferentFirstPage = true, DifferentOddEvenPages = true, HeaderDistance = 10 } };
        var document = new FlowDocument([Lines(18), second])
        {
            Stories = new[] { primary, first, even }.ToImmutableDictionary(s => s.Id),
            Sections = [section, new DocumentSection { StartParagraphId = second.Id, PageSettings = Paper,
                HeaderFooter = new() { DifferentOddEvenPages = true, HeaderDistance = 10 } }]
        };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(4, pages.Pages.Length);
        Assert.Equal(new[] { first.Id, even.Id, primary.Id, even.Id }, pages.StoryRegions.Select(r => r.StoryId));
        Assert.All(pages.StoryRegions, region => Assert.Equal(4, region.Context.PageCount));
        var hitRegion = pages.StoryRegions[3];
        var caret = pages.Caret(even.Id, 1, 3);
        Assert.Equal(even.Id, pages.HitTestStory(caret.Center).StoryId);
        Assert.Equal(1, pages.HitTestStory(caret.Center).Position);
        Assert.Equal(3, pages.HitTestStory(caret.Center).PageIndex);
        Assert.NotEmpty(pages.SelectionRects(even.Id, 0, 2, 3));
        Assert.True(hitRegion.Bounds.Contains(caret.Center));
        Assert.All(pages.Fragments, fragment => Assert.Equal(Guid.Empty, fragment.StoryKey));
    });

    [Fact]
    public Task Repeated_page_fields_use_physical_instance_values_and_keep_atomic_offsets() => Run(() =>
    {
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph([
            new RichRun(InlineDescriptor.PageField(PageFieldKind.Page)), new RichRun(" / "),
            new RichRun(InlineDescriptor.PageField(PageFieldKind.NumPages))]) { Style = LineStyle }] };
        var document = new FlowDocument([Lines(13)]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header),
            Sections = [new DocumentSection { PageSettings = Paper, PageNumberStart = 5,
                HeaderFooter = new() { PrimaryHeader = Ref(header), HeaderDistance = 10 } }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(3, pages.Pages.Length);
        Assert.Equal(new[] { "5", "6", "7" }, pages.StoryFragments.Select(f => f.Measurement.Paragraph.Runs[0].Inline!.AltText));
        Assert.All(pages.StoryFragments, f =>
        { Assert.Equal("3", f.Measurement.Paragraph.Runs[2].Inline!.AltText); Assert.Equal(5, f.Length); });
        Assert.Equal("1", ((Paragraph)header.Blocks[0]).Runs[0].Inline!.AltText);
    });

    [Fact]
    public Task Footnote_reservation_moves_its_reference_and_splits_long_content_without_loss() => Run(() =>
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [Lines(16)] };
        var note = new DocumentNote { StoryId = story.Id, Kind = DocumentNoteKind.Footnote };
        var owner = new Paragraph([new RichRun("Reference "), new RichRun(InlineDescriptor.Note(note.Id))]) { Style = LineStyle };
        var document = new FlowDocument([Lines(5), owner, Lines(8)]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story),
            Notes = [note], Sections = [new DocumentSection { PageSettings = Paper }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        var reference = pages.Fragments.Single(f => f.ParagraphId == owner.Id);
        var notes = pages.StoryRegions.Where(r => r.StoryId == story.Id).ToArray();
        Assert.Equal(1, reference.PageIndex);
        Assert.Equal(reference.PageIndex, notes[0].PageIndex);
        Assert.False(notes[0].IsContinuation); Assert.All(notes.Skip(1), r => Assert.True(r.IsContinuation));
        Assert.InRange(pages.Pages.Length, 3, 20);
        var lines = pages.StoryFragments.Where(f => f.StoryKey == story.Id).OrderBy(f => f.TextStart).ToArray();
        Assert.Equal(16, lines.Length);
        Assert.Equal(0, lines[0].TextStart); Assert.Equal(((Paragraph)story.Blocks[0]).Length, lines[^1].TextEnd);
        for (var i = 1; i < lines.Length; i++) Assert.Equal(lines[i - 1].TextEnd, lines[i].TextStart);
        foreach (var region in notes)
            Assert.All(pages.Fragments.Where(f => f.PageIndex == region.PageIndex), f => Assert.True(f.Bounds.Bottom <= region.Bounds.Top));
    });

    [Fact]
    public Task Rich_header_keeps_table_cells_images_and_story_selection_geometry() => Run(() =>
    {
        var image = new InlineDescriptor { Payload = new ImageInlinePayload("picture"), Width = 12, Height = 12 };
        var paragraph = new Paragraph([new RichRun("image "), new RichRun(image)]) { Style = LineStyle };
        var table = new Table { Rows = [ImmutableArray.Create(new TableCell { Blocks = [paragraph], Padding = new EdgeInsets(0, 0, 0, 0) })] };
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [table] };
        var document = new FlowDocument([Lines(8)]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header),
            Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("picture", new DocumentResource { Kind = DocumentResourceKind.Host, Location = "picture", MediaType = "image/png" }),
            Sections = [new DocumentSection { PageSettings = Paper, HeaderFooter = new() { PrimaryHeader = Ref(header), HeaderDistance = 0 } }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(2, pages.TableCells(header.Id).Count);
        Assert.Equal(2, pages.InlineVisuals().Count(v => v.Descriptor.Id == image.Id));
        Assert.Equal(2, pages.StoryFragments.Length);
        Assert.NotEmpty(pages.SelectionRects(header.Id, 0, paragraph.Length, 1));
    });

    [Fact]
    public Task Section_endnotes_precede_the_next_section_and_keep_custom_mark() => Run(() =>
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Endnote, Blocks = [Lines(2)] };
        var note = new DocumentNote { StoryId = story.Id, Kind = DocumentNoteKind.Endnote, CustomMark = "*" };
        var owner = new Paragraph([new RichRun(InlineDescriptor.Note(note.Id))]) { Style = LineStyle };
        var next = Lines(1);
        var document = new FlowDocument([owner, next]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story),
            Notes = [note], EndnoteSettings = new() { Placement = NotePlacement.SectionEnd }, Sections = [new DocumentSection { PageSettings = Paper },
                new DocumentSection { StartParagraphId = next.Id, PageSettings = Paper }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        var region = Assert.Single(pages.StoryRegions);
        Assert.Equal("*", region.Marker);
        Assert.True(region.PageIndex < pages.Fragments.Single(f => f.ParagraphId == next.Id).PageIndex);
        Assert.Equal("*", pages.Fragments.Single(f => f.ParagraphId == owner.Id).Measurement.Paragraph.Runs[0].Inline!.AltText);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task References_in_table_continuations_and_positioned_paragraphs_keep_their_note(bool frame) => Run(() =>
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [Lines(2)] };
        var note = new DocumentNote { StoryId = story.Id, Kind = DocumentNoteKind.Footnote };
        var prefix = frame ? "frame " : string.Join('\u2028', Enumerable.Repeat("table", 8)) + "\u2028reference ";
        var owner = new Paragraph([new RichRun(prefix), new RichRun(InlineDescriptor.Note(note.Id)), new RichRun("\u2028last")])
        { Style = LineStyle with { Frame = frame ? new ParagraphFrame(0, 20, 180) : null } };
        Block block = frame ? owner : new Table { Rows = [ImmutableArray.Create(new TableCell { Blocks = [owner] })] };
        var document = new FlowDocument([Lines(4), block]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story),
            Notes = [note], Sections = [new DocumentSection { PageSettings = Paper }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        var referenceOffset = new DocumentIndex(document).ById(owner.Id).Start + prefix.Length;
        var reference = pages.Fragments.Single(f => f.TextStart <= referenceOffset && f.TextEnd > referenceOffset);
        var firstNote = pages.StoryRegions.First(r => r.StoryId == story.Id);
        Assert.Equal(reference.PageIndex, firstNote.PageIndex);
        Assert.True(reference.Bounds.Bottom <= firstNote.Bounds.Top);
        Assert.Equal(2, pages.StoryFragments.Count(f => f.StoryKey == story.Id));
    });

    [Fact]
    public Task Body_and_continued_note_fields_use_final_page_counts_and_instance_numbers() => Run(() =>
    {
        var noteField = new Paragraph([new RichRun(InlineDescriptor.PageField(PageFieldKind.Page)), new RichRun(" / "),
            new RichRun(InlineDescriptor.PageField(PageFieldKind.NumPages))]) { Style = LineStyle };
        var story = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [Lines(2), noteField] };
        var note = new DocumentNote { StoryId = story.Id, Kind = DocumentNoteKind.Footnote };
        var owner = new Paragraph([new RichRun(InlineDescriptor.Note(note.Id)), new RichRun(InlineDescriptor.PageField(PageFieldKind.NumPages))]) { Style = LineStyle };
        var document = new FlowDocument([owner, Lines(8)]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story),
            Notes = [note], Sections = [new DocumentSection { PageSettings = Paper }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        var field = pages.StoryFragments.Single(f => f.ParagraphId == noteField.Id);
        Assert.Equal(pages.Pages[field.PageIndex].NumberText, field.Measurement.Paragraph.Runs[0].Inline!.AltText);
        Assert.Equal(pages.Pages.Length.ToString(), field.Measurement.Paragraph.Runs[2].Inline!.AltText);
        Assert.Equal(pages.Pages.Length.ToString(), pages.Fragments.Single(f => f.ParagraphId == owner.Id).Measurement.Paragraph.Runs[1].Inline!.AltText);
    });

    [Theory]
    [InlineData(NoteRestartPolicy.Continuous, "iv", "v")]
    [InlineData(NoteRestartPolicy.EachPage, "iv", "iv")]
    [InlineData(NoteRestartPolicy.EachSection, "iv", "iv")]
    public Task Note_sequence_uses_reference_order_formats_and_restart_settings(NoteRestartPolicy restart, string first, string second) => Run(() =>
    {
        var stories = Enumerable.Range(0, 2).Select(_ => new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [Lines(1)] }).ToArray();
        var notes = stories.Select(s => new DocumentNote { StoryId = s.Id, Kind = DocumentNoteKind.Footnote }).ToArray();
        var owners = notes.Select(n => new Paragraph([new RichRun(InlineDescriptor.Note(n.Id))]) { Style = LineStyle }).ToArray();
        var document = new FlowDocument(owners) { Stories = stories.ToImmutableDictionary(s => s.Id), Notes = notes.Reverse().ToImmutableArray(),
            FootnoteSettings = new() { Start = 4, NumberFormat = PageNumberFormat.LowerRoman, Restart = restart },
            Sections = [new DocumentSection { PageSettings = Paper }, new DocumentSection { PageSettings = Paper, StartParagraphId = owners[1].Id }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(new[] { first, second }, pages.Fragments.Select(f => f.Measurement.Paragraph.Runs[0].Inline!.AltText));
        Assert.Equal(new[] { first, second }, pages.StoryRegions.Select(r => r.Marker));
    });

    [Fact]
    public Task Several_notes_on_one_line_reserve_complete_first_lines() => Run(() =>
    {
        var stories = Enumerable.Range(0, 3).Select(_ => new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [Lines(1)] }).ToArray();
        var notes = stories.Select(s => new DocumentNote { Kind = DocumentNoteKind.Footnote, StoryId = s.Id }).ToArray();
        var owner = new Paragraph(notes.Select(n => new RichRun(InlineDescriptor.Note(n.Id)))) { Style = LineStyle };
        var document = new FlowDocument([owner]) { Stories = stories.ToImmutableDictionary(s => s.Id), Notes = notes.ToImmutableArray(),
            Sections = [new DocumentSection { PageSettings = Paper }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Single(pages.Pages); Assert.Equal(3, pages.StoryRegions.Length);
        Assert.All(pages.StoryFragments, f => Assert.Equal(f.Bounds.Height, f.Bounds.Intersect(f.Clip).Height));
        Assert.Empty(pages.LayoutDiagnostics);
    });

    [Fact]
    public Task Oversized_note_line_is_consumed_once_and_reports_its_clip() => Run(() =>
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [Lines(1) with
            { Style = LineStyle with { LineSpacing = 400 } }] };
        var note = new DocumentNote { Kind = DocumentNoteKind.Footnote, StoryId = story.Id };
        var owner = new Paragraph([new RichRun(InlineDescriptor.Note(note.Id))]) { Style = LineStyle };
        var document = new FlowDocument([owner]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story), Notes = [note],
            Sections = [new DocumentSection { PageSettings = Paper }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Single(pages.Pages); Assert.Single(pages.StoryFragments);
        Assert.Contains(pages.LayoutDiagnostics, message => message.Contains("oversized atomic line"));
    });

    [Fact]
    public Task Draft_remains_one_continuous_sheet_without_repeated_story_regions() => Run(() =>
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [Lines(100)] };
        var note = new DocumentNote { Kind = DocumentNoteKind.Footnote, StoryId = story.Id };
        var owner = new Paragraph([new RichRun(InlineDescriptor.Note(note.Id))]) { Style = LineStyle };
        var document = new FlowDocument([owner, Lines(100)]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story), Notes = [note],
            Sections = [new DocumentSection { PageSettings = Paper }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document, options: new() { Draft = true });
        Assert.Single(pages.Pages); Assert.Empty(pages.StoryRegions);
        Assert.Equal(101, pages.Fragments.Length);
    });

    [Fact]
    public Task Tiny_paper_consumes_every_note_line_with_bounded_progress() => Run(() =>
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [Lines(8)] };
        var note = new DocumentNote { Kind = DocumentNoteKind.Footnote, StoryId = story.Id };
        var owner = new Paragraph([new RichRun(InlineDescriptor.Note(note.Id))]) { Style = LineStyle };
        var paper = Paper with { Height = 140 };
        var document = new FlowDocument([owner]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story), Notes = [note],
            Sections = [new DocumentSection { PageSettings = paper }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(8, pages.StoryFragments.Length);
        Assert.InRange(pages.Pages.Length, 1, 8);
        Assert.All(pages.StoryRegions, region => Assert.True(pages.Pages[region.PageIndex].ContentBounds.Contains(region.Bounds)));
    });

    [Fact]
    public Task Endnotes_follow_reference_order_after_insertion_before_existing_notes() => Run(() =>
    {
        var stories = new[] { "first reference", "second reference" }.Select(text => new DocumentStory
        { Kind = DocumentStoryKind.Endnote, Blocks = [new Paragraph(text) { Style = LineStyle }] }).ToArray();
        var notes = stories.Select(story => new DocumentNote { Kind = DocumentNoteKind.Endnote, StoryId = story.Id }).ToArray();
        var owners = notes.Select(note => new Paragraph([new RichRun(InlineDescriptor.Note(note.Id))]) { Style = LineStyle }).ToArray();
        var document = new FlowDocument(owners) { Stories = stories.ToImmutableDictionary(story => story.Id), Notes = notes.Reverse().ToImmutableArray(),
            Sections = [new DocumentSection { PageSettings = Paper }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(stories.Select(story => story.Id), pages.StoryRegions.Select(region => region.StoryId));
        Assert.Equal(new[] { "1", "2" }, pages.StoryRegions.Select(region => region.Marker));
    });

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public Task A_tall_adjacent_table_cell_never_discards_a_later_note_reference(int beforeReference) => Run(() =>
    {
        var story = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [Lines(1)] };
        var note = new DocumentNote { Kind = DocumentNoteKind.Footnote, StoryId = story.Id };
        var prefix = string.Join('\u2028', Enumerable.Repeat("table", beforeReference)) + "\u2028";
        var owner = new Paragraph([new RichRun(prefix), new RichRun(InlineDescriptor.Note(note.Id))]) { Style = LineStyle };
        var table = new Table { Rows = [ImmutableArray.Create(new TableCell { Blocks = [owner] },
            new TableCell { Blocks = [Lines(1) with { Style = LineStyle with { LineSpacing = 80 } }] })] };
        var document = new FlowDocument([table]) { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story), Notes = [note],
            Sections = [new DocumentSection { PageSettings = Paper }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        var referenceOffset = new DocumentIndex(document).ById(owner.Id).Start + prefix.Length;
        var reference = pages.Fragments.Single(f => f.TextStart <= referenceOffset && f.TextEnd > referenceOffset);
        Assert.Equal(reference.PageIndex, Assert.Single(pages.StoryRegions).PageIndex);
    });
}
