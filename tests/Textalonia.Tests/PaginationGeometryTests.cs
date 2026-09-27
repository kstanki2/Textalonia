using Avalonia;
using Textalonia.Controls;
using Textalonia.Layout;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class PaginationGeometryTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);
    private static readonly PageSettings Paper = new()
    {
        Width = 240, Height = 120, Margins = new EdgeInsets(10, 10, 10, 10), BalanceColumns = false
    };
    private static readonly ParagraphStyle LineStyle = new()
    {
        SpaceBefore = 0, SpaceAfter = 0, LineSpacingMode = LineSpacingMode.Exact,
        LineSpacing = 20, WidowControl = false
    };

    private static Paragraph Lines(int count) => new(string.Join("\u2028", Enumerable.Range(1, count).Select(i => $"Line {i}")))
    { Style = LineStyle };
    private static FlowDocument Document(params Block[] blocks) => new(blocks)
    { Sections = [new DocumentSection { PageSettings = Paper }] };

    private static void AssertTextCoverage(PageLayoutSnapshot snapshot)
    {
        var index = new DocumentIndex(snapshot.Document);
        foreach (var paragraph in index.Paragraphs)
        {
            var position = paragraph.Start;
            var fragments = snapshot.Fragments.Where(fragment => fragment.ParagraphId == paragraph.Paragraph.Id)
                .OrderBy(fragment => fragment.TextStart).ToArray();
            Assert.NotEmpty(fragments);
            foreach (var fragment in fragments)
            {
                Assert.Equal(position, fragment.TextStart);
                Assert.Equal(fragment.TextStart, index.Snap(fragment.TextStart));
                Assert.Equal(fragment.TextEnd, index.Snap(fragment.TextEnd));
                Assert.True(double.IsFinite(fragment.Baseline));
                position = fragment.TextEnd;
            }
            Assert.Equal(paragraph.End, position);
        }
    }

    [Theory]
    [InlineData(false, 5, 1)]
    [InlineData(true, 4, 2)]
    public Task Widow_control_moves_a_final_single_line_to_keep_two_lines_on_both_pages(
        bool widowControl, int firstPageLines, int secondPageLines) => Run(() =>
    {
        using var engine = new PaginationEngine();
        using var pages = engine.Paginate(Document(Lines(6) with { Style = LineStyle with { WidowControl = widowControl } }));
        Assert.Equal(2, pages.Pages.Length);
        Assert.Equal(firstPageLines, pages.Fragments.Count(fragment => fragment.PageIndex == 0));
        Assert.Equal(secondPageLines, pages.Fragments.Count(fragment => fragment.PageIndex == 1));
        Assert.All(pages.Fragments, fragment => Assert.Equal(20, fragment.Bounds.Height));
        AssertTextCoverage(pages);
    });

    [Fact]
    public Task Orphan_control_moves_a_paragraph_when_only_one_line_fits_below_previous_content() => Run(() =>
    {
        var target = Lines(4) with { Style = LineStyle with { WidowControl = true } };
        using var engine = new PaginationEngine();
        using var pages = engine.Paginate(Document(Lines(4), target));
        Assert.Equal(2, pages.Pages.Length);
        Assert.All(pages.Fragments.Where(fragment => fragment.ParagraphId == target.Id), fragment => Assert.Equal(1, fragment.PageIndex));
        AssertTextCoverage(pages);
    });

    [Fact]
    public Task Keep_together_and_keep_with_next_move_content_and_unsatisfiable_chains_make_progress() => Run(() =>
    {
        using var engine = new PaginationEngine();
        var together = Lines(4) with { Style = LineStyle with { KeepTogether = true } };
        using var first = engine.Paginate(Document(Lines(3), together));
        Assert.All(first.Fragments.Where(fragment => fragment.ParagraphId == together.Id), fragment => Assert.Equal(1, fragment.PageIndex));

        var heading = Lines(1) with { Style = LineStyle with { KeepWithNext = true } };
        var body = Lines(2);
        using var second = engine.Paginate(Document(Lines(4), heading, body));
        Assert.All(second.Fragments.Where(fragment => fragment.ParagraphId == heading.Id || fragment.ParagraphId == body.Id),
            fragment => Assert.Equal(1, fragment.PageIndex));

        var chain = Enumerable.Range(0, 12).Select(_ => (Block)(Lines(1) with
            { Style = LineStyle with { KeepWithNext = true, KeepTogether = true } })).ToArray();
        using var oversized = engine.Paginate(Document(chain));
        Assert.InRange(oversized.Pages.Length, 3, 12);
        AssertTextCoverage(oversized);
    });

    [Fact]
    public Task Explicit_page_and_column_breaks_target_distinct_physical_locations() => Run(() =>
    {
        var first = Lines(1);
        var nextPage = Lines(1) with { Style = LineStyle with { PageBreakBefore = true } };
        var nextColumn = Lines(1) with { Style = LineStyle with { ColumnBreakBefore = true } };
        var document = Document(first, nextPage, nextColumn);
        document = document with { Sections = [document.Sections[0] with { PageSettings = Paper with
            { Columns = [new PageColumn(), new PageColumn()], ColumnSpacing = 20 } }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(2, pages.Pages.Length);
        var onNextPage = Assert.Single(pages.Fragments.Where(fragment => fragment.ParagraphId == nextPage.Id));
        var onNextColumn = Assert.Single(pages.Fragments.Where(fragment => fragment.ParagraphId == nextColumn.Id));
        Assert.Equal(1, onNextPage.PageIndex); Assert.Equal(0, onNextPage.ColumnIndex);
        Assert.Equal(1, onNextColumn.PageIndex); Assert.Equal(1, onNextColumn.ColumnIndex);
        Assert.True(onNextColumn.Bounds.Left > onNextPage.Bounds.Left);
        AssertTextCoverage(pages);
    });

    [Theory]
    [InlineData(SectionBreakKind.OddPage, 1, 2)]
    [InlineData(SectionBreakKind.EvenPage, 6, 3)]
    public Task Parity_section_inserts_blank_sheet_and_restarts_numbering_on_its_first_content_page(
        SectionBreakKind breakKind, int initialLines, int contentPage) => Run(() =>
    {
        var first = Lines(initialLines); var next = Lines(1);
        var section = new DocumentSection
        {
            StartParagraphId = next.Id, BreakKind = breakKind,
            PageSettings = Paper, PageNumberStart = 7, PageNumberFormat = PageNumberFormat.UpperRoman
        };
        var document = Document(first, next);
        document = document with { Sections = [document.Sections[0], section] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(contentPage + 1, pages.Pages.Length);
        Assert.True(pages.Pages[contentPage - 1].IsBlank);
        Assert.Equal(document.Sections[0].Id, pages.Pages[contentPage - 1].SectionId);
        Assert.DoesNotContain(pages.Fragments, fragment => fragment.PageIndex == contentPage - 1);
        var fragment = Assert.Single(pages.Fragments.Where(fragment => fragment.ParagraphId == next.Id));
        Assert.Equal(contentPage, fragment.PageIndex); Assert.Equal(section.Id, fragment.SectionId);
        Assert.Equal(7, pages.Pages[contentPage].Number); Assert.Equal("VII", pages.Pages[contentPage].NumberText);
        AssertTextCoverage(pages);
    });

    [Fact]
    public Task Continuous_section_stays_on_same_sheet_and_changed_paper_starts_a_new_sheet() => Run(() =>
    {
        var first = Lines(1); var continuous = Lines(1); var landscape = Lines(1);
        var document = Document(first, continuous, landscape);
        document = document with { Sections = [document.Sections[0],
            new DocumentSection { StartParagraphId = continuous.Id, BreakKind = SectionBreakKind.Continuous, PageSettings = Paper },
            new DocumentSection { StartParagraphId = landscape.Id, BreakKind = SectionBreakKind.Continuous,
                PageSettings = Paper with { Orientation = PageOrientation.Landscape } }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(2, pages.Pages.Length);
        Assert.Equal(0, Assert.Single(pages.Fragments.Where(fragment => fragment.ParagraphId == continuous.Id)).PageIndex);
        Assert.Equal(1, Assert.Single(pages.Fragments.Where(fragment => fragment.ParagraphId == landscape.Id)).PageIndex);
        Assert.Equal(Paper.Height, pages.Pages[1].Bounds.Width);
        Assert.Equal(Paper.Width, pages.Pages[1].Bounds.Height);
        AssertTextCoverage(pages);
    });

    [Fact]
    public Task Unequal_columns_reflow_without_gaps_or_splitting_graphemes_and_atomic_objects() => Run(() =>
    {
        var text = string.Concat(Enumerable.Repeat("A\U0001F469\u200D\U0001F4BB e\u0301 word ", 50));
        var paragraph = new Paragraph([new RichRun(text), new RichRun(new InlineDescriptor { Payload = new ImageInlinePayload("missing") }), new RichRun(text)]) { Style = LineStyle };
        var document = Document(paragraph);
        document = document with { Sections = [document.Sections[0] with { PageSettings = Paper with
            { Width = 310, Height = 180, Columns = [new PageColumn(1), new PageColumn(2)], ColumnSpacing = 20 } }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.True(pages.Pages.Length > 1);
        Assert.Contains(pages.Fragments, fragment => fragment.ColumnIndex == 0);
        Assert.Contains(pages.Fragments, fragment => fragment.ColumnIndex == 1);
        Assert.Equal(90, pages.Pages[0].Columns[0].Width);
        Assert.Equal(180, pages.Pages[0].Columns[1].Width);
        AssertTextCoverage(pages);
        Assert.Single(pages.Fragments.Where(fragment => fragment.TextStart <= text.Length && fragment.TextEnd > text.Length));
    });

    [Fact]
    public Task Oversize_atomic_line_is_clipped_once_and_subsequent_text_still_gets_a_page() => Run(() =>
    {
        var image = new Paragraph([new RichRun(new InlineDescriptor { Width = 30, Height = 400, Payload = new ImageInlinePayload("missing") })])
        { Style = new() { SpaceBefore = 0, SpaceAfter = 0 } };
        var after = Lines(1);
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(Document(image, after));
        Assert.Equal(2, pages.Pages.Length);
        var large = Assert.Single(pages.Fragments.Where(fragment => fragment.ParagraphId == image.Id));
        Assert.True(large.Bounds.Height >= 400);
        Assert.Equal(100, large.Clip.Height);
        Assert.All(pages.SelectionRects(0, 1), bounds => Assert.True(bounds.Height <= 100));
        Assert.Equal(1, Assert.Single(pages.Fragments.Where(fragment => fragment.ParagraphId == after.Id)).PageIndex);
        AssertTextCoverage(pages);
    });

    [Fact]
    public Task Table_cell_text_spans_pages_with_contiguous_offsets_and_shared_caret_hit_geometry() => Run(() =>
    {
        var paragraph = Lines(12);
        var table = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [paragraph] });
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(Document(table));
        Assert.True(pages.Pages.Length >= 3);
        Assert.True(pages.TableCells().Count >= 3);
        AssertTextCoverage(pages);
        foreach (var fragment in pages.Fragments)
        {
            var caret = pages.Caret(fragment.TextStart);
            Assert.True(caret.Intersects(fragment.Clip));
            Assert.Equal(fragment.TextStart, pages.HitTest(new Point(caret.Left, caret.Center.Y)));
        }
    });

    [Fact]
    public Task Fixed_frame_retains_text_ranges_and_clips_its_geometry_to_the_declared_region() => Run(() =>
    {
        var paragraph = Lines(4) with { Style = LineStyle with { Frame = new ParagraphFrame(25, 15, 100, 35) } };
        var document = Document(paragraph);
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Single(pages.Pages);
        Assert.Equal(new Rect(35, 25, 100, 35), pages.Fragments[0].Clip);
        Assert.Equal(35, pages.Fragments[0].Bounds.Left); Assert.Equal(25, pages.Fragments[0].Bounds.Top);
        AssertTextCoverage(pages);
        var rectangles = pages.SelectionRects(0, paragraph.Length).ToArray();
        Assert.NotEmpty(rectangles);
        Assert.All(rectangles, bounds => Assert.True(bounds.Left >= 35 && bounds.Top >= 25 && bounds.Right <= 135 && bounds.Bottom <= 60));
    });

    [Fact]
    public Task Section_line_grid_changes_exact_pagination_only_for_paragraphs_that_snap_to_it() => Run(() =>
    {
        var paragraph = Lines(5) with { Style = LineStyle with { SnapToGrid = true } };
        var document = Document(paragraph);
        document = document with { Sections = [document.Sections[0] with { PageSettings = Paper with { Grid = new EastAsianGrid(LineSpacing: 36) } }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(3, pages.Pages.Length);
        Assert.All(pages.Fragments, fragment => Assert.Equal(36, fragment.Bounds.Height));
        AssertTextCoverage(pages);
        document = document with { Blocks = [paragraph with { Style = LineStyle }] };
        using var unsnapped = engine.Paginate(document);
        Assert.Single(unsnapped.Pages);
        Assert.All(unsnapped.Fragments, fragment => Assert.Equal(20, fragment.Bounds.Height));
    });

    [Theory]
    [InlineData(LineNumberRestart.EachPage, 3)]
    [InlineData(LineNumberRestart.Continuous, 8)]
    [InlineData(LineNumberRestart.EachSection, 8)]
    public Task Line_numbering_honors_initial_start_and_its_page_restart_policy(LineNumberRestart restart, int lastNumber) => Run(() =>
    {
        var document = Document(Lines(6));
        document = document with { Sections = [document.Sections[0] with { PageSettings = Paper with
            { LineNumbering = new LineNumberingSettings(Start: 3, Restart: restart) } }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Equal(3, pages.Fragments[0].LineNumber);
        Assert.Equal(lastNumber, pages.Fragments[^1].LineNumber);
    });

    [Fact]
    public Task Character_grid_uses_fixed_pitch_for_caret_hit_testing_and_selection() => Run(() =>
    {
        var paragraph = new Paragraph("ABC") { Style = LineStyle with { SnapToGrid = true } };
        var document = Document(paragraph);
        document = document with { Sections = [document.Sections[0] with { PageSettings = Paper with
            { Grid = new EastAsianGrid(CharacterSpacing: 18, LineSpacing: 20) } }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        var first = pages.Caret(0);
        for (var position = 1; position <= 3; position++)
        {
            var caret = pages.Caret(position);
            Assert.Equal(18 * position, caret.X - first.X, 2);
            Assert.Equal(position, pages.HitTest(new Point(caret.X, caret.Center.Y)));
        }
        Assert.Equal(18, Assert.Single(pages.SelectionRects(1, 1)).Width, 2);
    });

    [Fact]
    public Task Mirrored_margins_and_gutter_follow_physical_page_parity_in_multiple_page_rows() => Run(() =>
    {
        var document = Document(Lines(6));
        document = document with { Sections = [document.Sections[0] with { PageSettings = Paper with
            { Margins = new EdgeInsets(10, 10, 30, 10), MirrorMargins = true, Gutter = 8 } }] };
        using var engine = new PaginationEngine();
        using var pages = engine.Paginate(document, options: new PaginationOptions { PageGap = 25, PagesPerRow = 2 });
        Assert.Equal(2, pages.Pages.Length);
        Assert.Equal(18, pages.Pages[0].ContentBounds.Left - pages.Pages[0].Bounds.Left);
        Assert.Equal(30, pages.Pages[1].ContentBounds.Left - pages.Pages[1].Bounds.Left);
        Assert.Equal(18, pages.Pages[1].Bounds.Right - pages.Pages[1].ContentBounds.Right);
        Assert.Equal(Paper.Width + 25, pages.Pages[1].Bounds.Left);
        Assert.Equal(pages.Pages[0].Bounds.Top, pages.Pages[1].Bounds.Top);
        AssertTextCoverage(pages);
    });

    [Fact]
    public Task Final_equal_width_columns_balance_to_within_one_exact_line() => Run(() =>
    {
        var document = Document(Lines(9));
        document = document with { Sections = [document.Sections[0] with { PageSettings = Paper with
            { Height = 220, Columns = [new PageColumn(), new PageColumn()], ColumnSpacing = 20, BalanceColumns = true } }] };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document);
        Assert.Single(pages.Pages);
        var left = pages.Fragments.Count(fragment => fragment.ColumnIndex == 0);
        var right = pages.Fragments.Count(fragment => fragment.ColumnIndex == 1);
        Assert.Equal(9, left + right);
        Assert.InRange(Math.Abs(left - right), 0, 1);
        AssertTextCoverage(pages);
    });

    [Theory]
    [InlineData(0)]
    [InlineData(349)]
    public Task Same_height_edits_near_start_and_end_reuse_stable_checkpoints_and_match_fresh_layout(int editIndex) => Run(() =>
    {
        using var engine = new PaginationEngine();
        var document = Document(Enumerable.Range(0, 350).Select(_ => (Block)Lines(1)).ToArray());
        using var before = engine.Paginate(document);
        var original = (Paragraph)document.Blocks[editIndex];
        var edited = document with { Blocks = document.Blocks.SetItem(editIndex, original with { Runs = [new RichRun("Edit 1")] }) };
        using var actual = engine.Paginate(edited);
        Assert.InRange(engine.ReflowedBlocks, 1, 2);
        Assert.InRange(engine.ReusedCheckpoints, 348, 349);
        Assert.InRange(engine.CachedMeasurements, 1, 256);
        Assert.InRange(engine.CachedLayoutBytes, 0, ShapedLayoutCache.ByteLimit);
        using var freshEngine = new PaginationEngine(); using var expected = freshEngine.Paginate(edited);
        Assert.Equal(expected.Fragments.Select(fragment => (fragment.TextStart, fragment.TextEnd, fragment.PageIndex, fragment.Bounds, fragment.Baseline)),
            actual.Fragments.Select(fragment => (fragment.TextStart, fragment.TextEnd, fragment.PageIndex, fragment.Bounds, fragment.Baseline)));
        AssertTextCoverage(actual);
    });

    [Fact]
    public Task Repeated_exact_layout_is_deterministic_and_cache_eviction_does_not_invalidate_live_snapshots() => Run(() =>
    {
        using var engine = new PaginationEngine();
        var document = Document(Enumerable.Range(0, 20).Select(_ => (Block)Lines(3)).ToArray());
        using var first = engine.Paginate(document);
        var measured = engine.MeasuredParagraphs;
        using var second = engine.Paginate(document);
        Assert.Equal(measured, engine.MeasuredParagraphs);
        Assert.Equal(first.Pages.Select(page => (page.Index, page.SectionId, page.Number, page.NumberText, page.Bounds, page.ContentBounds, page.IsBlank)), second.Pages.Select(page => (page.Index, page.SectionId, page.Number, page.NumberText, page.Bounds, page.ContentBounds, page.IsBlank)));
        Assert.Equal(first.Fragments.Select(fragment => (fragment.TextStart, fragment.TextEnd, fragment.PageIndex, fragment.Bounds, fragment.Baseline)),
            second.Fragments.Select(fragment => (fragment.TextStart, fragment.TextEnd, fragment.PageIndex, fragment.Bounds, fragment.Baseline)));

        var large = Document(Enumerable.Range(0, 350).Select(_ => (Block)Lines(1)).ToArray());
        using var retained = engine.Paginate(large);
        Assert.InRange(engine.CachedMeasurements, 1, 256);
        Assert.InRange(engine.CachedLayoutBytes, 0, ShapedLayoutCache.ByteLimit);
        engine.Clear();
        Assert.Equal(0, engine.CachedMeasurements);
        Assert.Equal(0, engine.CachedLayoutBytes);
        Assert.True(retained.Caret(0).Height > 0);
        var end = new DocumentIndex(large).Length;
        var caret = retained.Caret(end);
        Assert.True(caret.Y > 0 && caret.Height > 0);
        Assert.Equal(end, retained.HitTest(new Point(caret.Left, caret.Center.Y)));
        AssertTextCoverage(retained);
        retained.Dispose();
        Assert.Throws<ObjectDisposedException>(() => retained.Caret(0));
    });
}
