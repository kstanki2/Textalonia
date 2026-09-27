using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Layout;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class GeneralFieldPageLayoutTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static readonly ParagraphStyle LineStyle = new()
    { SpaceBefore = 0, SpaceAfter = 0, LineSpacingMode = LineSpacingMode.Exact, LineSpacing = 20, WidowControl = false };
    private static readonly PageSettings Paper = new()
    { Width = 300, Height = 240, Margins = new EdgeInsets(30, 60, 30, 60), BalanceColumns = false };

    private static (FlowDocument Document, Guid StoryId) Report(bool footer)
    {
        var story = new DocumentStory { Kind = footer ? DocumentStoryKind.Footer : DocumentStoryKind.Header,
            Blocks = [new Paragraph("Page ? / ? / ?") { Style = LineStyle }] };
        var reference = new StoryReference { StoryId = story.Id, LinkToPrevious = false };
        var settings = new HeaderFooterSettings { HeaderDistance = 10, FooterDistance = 10 };
        settings = footer ? settings with { PrimaryFooter = reference } : settings with { PrimaryHeader = reference };
        var document = new FlowDocument(Enumerable.Range(0, 12).Select(i => new Paragraph("Body " + (i + 1))
            { Style = LineStyle with { PageBreakBefore = i > 0 } }))
        {
            Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story),
            Sections = [new DocumentSection { PageSettings = Paper, HeaderFooter = settings }]
        };
        foreach (var (offset, code) in new[] { (5, "PAGE"), (9, "NUMPAGES"), (13, "SECTIONPAGES") })
            document = FieldOperations.Insert(document, story.Id, offset, 1, code, FlowDocument.FromText("?"));
        return (document, story.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task General_page_fields_show_each_header_or_footer_instance_across_two_digit_pages(bool footer) =>
        fixture.Session.Dispatch(() =>
        {
            var (original, storyId) = Report(footer);
            using var engine = new PaginationEngine();
            var updated = engine.UpdateFields(original);
            Assert.Empty(updated.Diagnostics);
            var snapshot = updated.Document;
            var stored = Assert.IsType<Paragraph>(snapshot.Stories[storyId].Blocks[0]);
            Assert.Equal("Page 1 / 12 / 12", stored.PlainText);
            Assert.Equal(3, stored.Runs.Count(r => r.Inline?.Payload is PageFieldInlinePayload));
            using var pages = engine.Paginate(snapshot);
            Assert.Equal(12, pages.Pages.Length);
            var headers = pages.StoryFragments.Where(f => f.StoryKey == storyId).OrderBy(f => f.PageIndex).ToArray();
            Assert.Equal(12, headers.Length);
            for (var i = 0; i < headers.Length; i++)
            {
                Assert.Equal($"Page {i + 1} / 12 / 12", headers[i].Measurement.Paragraph.PlainText);
                Assert.Equal(stored.Length, headers[i].Length);
                Assert.Equal(stored.Id, headers[i].ParagraphId);
            }
            var ten = headers[9].Measurement.Paragraph.Runs.Single(r => r.Inline?.Payload is PageFieldInlinePayload { Field: PageFieldKind.Page });
            Assert.Equal("10", ten.Inline!.AltText); Assert.Equal(1, ten.Text.Length);
            Assert.NotEmpty(pages.SelectionRects(storyId, 5, 1, 9));
            Assert.Equal("Page 1 / 12 / 12", snapshot.GetStoryDocument(storyId).PlainText);
            Assert.Equal("Page ? / ? / ?", original.GetStoryDocument(storyId).PlainText);
            var again = engine.UpdateFields(snapshot);
            Assert.Empty(again.Diagnostics); Assert.Equal(0, again.UpdatedCount);
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Locked_page_field_or_enclosing_field_keeps_its_cached_header_result(bool lockEnclosingField) =>
        fixture.Session.Dispatch(() =>
        {
            var (document, storyId) = Report(false);
            using var engine = new PaginationEngine();
            document = engine.UpdateFields(document).Document;
            if (lockEnclosingField)
            {
                var outer = new DocumentField { Instruction = "IF 1 = 1 \"cached\" \"other\"", IsLocked = true,
                    Start = DocumentAnchor.Create(document, storyId, 0, AnchorAffinity.Before),
                    End = DocumentAnchor.Create(document, storyId, document.GetStoryIndex(storyId).Length, AnchorAffinity.After) };
                document = document with { Fields = document.Fields.Add(outer) };
            }
            else document = FieldOperations.SetLocked(document, document.Fields.Single(f => f.Instruction == "PAGE").Id, true);
            document.Validate();
            using var pages = engine.Paginate(document);
            Assert.Equal(12, pages.Pages.Length);
            Assert.All(pages.StoryFragments, fragment => Assert.Equal("Page 1 / 12 / 12", fragment.Measurement.Paragraph.PlainText));
            Assert.Equal("Page 1 / 12 / 12", document.GetStoryDocument(storyId).PlainText);
        }, CancellationToken.None);
    [Fact]
    public Task Locked_outer_range_preserves_page_fields_in_later_header_paragraphs() =>
        fixture.Session.Dispatch(() =>
        {
            var (document, storyId) = Report(false);
            using var engine = new PaginationEngine();
            document = engine.UpdateFields(document).Document;
            var story = document.Stories[storyId];
            document = document with { Stories = document.Stories.SetItem(storyId, story with
                { Blocks = story.Blocks.Insert(0, new Paragraph("Locked prefix") { Style = LineStyle }) }) };
            document = document with { Fields = document.Fields.Add(new DocumentField
            {
                Instruction = "IF 1 = 1 \"cached\" \"other\"", IsLocked = true,
                Start = DocumentAnchor.Create(document, storyId, 0, AnchorAffinity.Before),
                End = DocumentAnchor.Create(document, storyId, document.GetStoryIndex(storyId).Length, AnchorAffinity.After)
            }) };
            document.Validate();
            using var pages = engine.Paginate(document);
            var values = pages.StoryFragments.Where(f => f.Measurement.Paragraph.Runs.Any(r => r.Inline?.Payload is PageFieldInlinePayload)).ToArray();
            Assert.Equal(12, values.Length);
            Assert.All(values, fragment => Assert.Equal("Page 1 / 12 / 12", fragment.Measurement.Paragraph.PlainText));
        }, CancellationToken.None);
}
