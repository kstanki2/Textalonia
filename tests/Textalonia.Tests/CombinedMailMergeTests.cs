using System.Collections.Immutable;
using System.Text;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Layout;
using Textalonia.MailMerge;
using Textalonia.Model;
using Textalonia.Model.Fields;
using Textalonia.Pdf.Skia;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class CombinedMailMergeTests
{
    private static IReadOnlyDictionary<string, object?> Record(string name) =>
        new Dictionary<string, object?> { ["Name"] = name };

    private static FlowDocument Template(bool beginsWithTable = false)
    {
        var field = MergeFields.Create("Name");
        var header = new DocumentStory
        {
            Kind = DocumentStoryKind.Header,
            Blocks = [new Paragraph([new RichRun("To "), new RichRun(MergeFields.Create("Name"))])]
        };
        var body = new Paragraph([new RichRun("Dear "), new RichRun(field)]);
        Block[] blocks;
        if (beginsWithTable)
        {
            var table = Table.Create(1, 1);
            table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [body] });
            blocks = [table];
        }
        else blocks = [body];
        return new FlowDocument(blocks)
        {
            Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header),
            Sections = [new DocumentSection { HeaderFooter = new HeaderFooterSettings
            {
                PrimaryHeader = new StoryReference { StoryId = header.Id, LinkToPrevious = false }
            } }]
        };
    }

    [Fact]
    public void Combined_records_have_fresh_identities_and_independent_headers()
    {
        var template = Template();
        var document = MailMergeCombinedProcessor.Merge(template, [Record("Ada"), Record("Grace")]);

        document.Validate();
        Assert.Equal("Dear Ada\nDear Grace", document.PlainText);
        Assert.Equal(2, document.Sections.Length);
        Assert.Equal(SectionBreakKind.NextPage, document.Sections[1].BreakKind);
        Assert.Equal(1, document.Sections[1].PageNumberStart);
        Assert.Equal(Assert.IsType<Paragraph>(document.Blocks[1]).Id, document.Sections[1].StartParagraphId);
        Assert.NotEqual(document.Blocks[0].Id, document.Blocks[1].Id);
        Assert.Equal("To Ada", document.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary)!.Blocks[0].PlainText());
        Assert.Equal("To Grace", document.ResolveHeaderFooter(1, false, HeaderFooterVariant.Primary)!.Blocks[0].PlainText());
        Assert.Equal("Dear «Name»", template.PlainText);
    }

    [Fact]
    public void A_table_first_record_gets_a_valid_section_boundary_and_can_inherit_headers()
    {
        var document = MailMergeCombinedProcessor.Merge(Template(beginsWithTable: true),
            [Record("Ada"), Record("Grace")], combinedOptions: new()
            {
                HeaderFooterPolicy = CombinedHeaderFooterPolicy.LinkToPrevious,
                PageNumberPolicy = CombinedPageNumberPolicy.Continue
            });

        document.Validate();
        Assert.IsType<Table>(document.Blocks[0]);
        var marker = Assert.IsType<Paragraph>(document.Blocks[1]);
        Assert.IsType<Table>(document.Blocks[2]);
        Assert.Equal(marker.Id, document.Sections[1].StartParagraphId);
        Assert.Null(document.Sections[1].PageNumberStart);
        Assert.True(document.Sections[1].HeaderFooter.PrimaryHeader.LinkToPrevious);
        Assert.Same(document.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary),
            document.ResolveHeaderFooter(1, false, HeaderFooterVariant.Primary));
    }

    [Fact]
    public void Retained_merged_cell_backups_can_reuse_live_ids_and_keep_resolved_fields()
    {
        var table = Table.Create(1, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with
        { Blocks = [new Paragraph([new RichRun(MergeFields.Create("Name"))])] });
        table = table.SetCell(0, 1, table.Rows[0][1] with
        { Blocks = [new Paragraph([new RichRun(MergeFields.Create("Hidden"))])] });
        table = table.MergeCells(0, 0, 1, 2);
        var anchor = table.Rows[0][0];
        // Older snapshots may retain a backup of the current merged content with its IDs.
        table = table.SetCell(0, 0, anchor with { MergeOriginalBlocks = anchor.Blocks });
        var template = new FlowDocument([table]);
        template.Validate();

        var document = MailMergeCombinedProcessor.Merge(template,
            [new Dictionary<string, object?> { ["Name"] = "Ada", ["Hidden"] = "H1" },
             new Dictionary<string, object?> { ["Name"] = "Grace", ["Hidden"] = "H2" }]);
        document.Validate();
        Assert.Empty(MailMergeProcessor.GetFieldNames(document));
        var second = Assert.IsType<Table>(document.Blocks[2]);
        Assert.Equal("Grace\nH2", new FlowDocument(second.Rows[0][0].MergeOriginalBlocks).PlainText);
        Assert.Equal("H2", Assert.IsType<Paragraph>(second.Rows[0][1].Blocks[0]).PlainText);
    }

    [Fact]
    public void Completion_runs_once_after_the_whole_batch_is_expanded()
    {
        var called = 0;
        var document = MailMergeCombinedProcessor.Merge(Template(), [Record("Ada"), Record("Grace")],
            combinedOptions: new()
            {
                CompleteDocument = (combined, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    called++;
                    Assert.Equal(2, combined.Sections.Length);
                    Assert.Equal("Dear Ada\nDear Grace", combined.PlainText);
                    return combined;
                }
            });
        Assert.Equal(1, called);
        Assert.Equal(2, document.Sections.Length);
    }

    [Fact]
    public void Host_data_source_selection_feeds_combined_output()
    {
        IReadOnlyDictionary<string, object?>[] recipients = [Record("Ada"), Record("Grace"), Record("Linus")];
        var source = new DictionaryMailMergeDataSource(recipients);
        var document = MailMergeCombinedProcessor.Merge(Template(), source,
            new MailMergeRecipientSelection { SourceIndexes = [2, 0] });

        document.Validate();
        Assert.Equal("Dear Ada\nDear Linus", document.PlainText);
        Assert.Equal(2, document.Sections.Length);
    }

    [Fact]
    public async Task Combined_sections_and_headers_survive_docx_round_trip()
    {
        var document = MailMergeCombinedProcessor.Merge(Template(), [Record("Ada"), Record("Grace")]);
        using var stream = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(document, stream);
        stream.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(stream);

        loaded.Validate();
        Assert.Equal(document.PlainText, loaded.PlainText);
        Assert.Equal(2, loaded.Sections.Length);
        Assert.Equal(1, loaded.Sections[1].PageNumberStart);
        Assert.Equal("To Ada", loaded.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary)!.Blocks[0].PlainText());
        Assert.Equal("To Grace", loaded.ResolveHeaderFooter(1, false, HeaderFooterVariant.Primary)!.Blocks[0].PlainText());
    }
}

file static class MailMergeTestBlockExtensions
{
    public static string PlainText(this Block block) => block switch
    {
        Paragraph paragraph => paragraph.PlainText,
        _ => throw new InvalidOperationException("Expected a paragraph.")
    };
}

public class CombinedMailMergePageTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public Task Final_page_update_keeps_each_records_ordinary_field_result() =>
        fixture.Session.Dispatch(() =>
        {
            var template = FlowDocument.FromText("?\n?");
            template = FieldOperations.Insert(template, Guid.Empty, 0, 1,
                "MERGEFIELD Name", FlowDocument.FromText("?") );
            var second = new DocumentIndex(template).Paragraphs[1].Start;
            template = FieldOperations.Insert(template, Guid.Empty, second, 1,
                "PAGE", FlowDocument.FromText("?"));
            var document = MailMergeCombinedProcessor.Merge(template,
                new[] { new Dictionary<string, object?> { ["Name"] = "Ada" },
                    new Dictionary<string, object?> { ["Name"] = "Grace" } },
                new MailMergeOptions { FieldOptions = new FieldEvaluationOptions() },
                new CombinedMailMergeOptions
                {
                    CompleteDocument = (combined, token) =>
                    {
                        Assert.Equal("Ada\n?\nGrace\n?", combined.Text);
                        using var engine = new PaginationEngine();
                        return engine.UpdateFields(combined,
                            new FieldEvaluationOptions { OnlyDirty = true, CancellationToken = token }).Document;
                    }
                });
            document.Validate();
            Assert.Equal("Ada\n1\nGrace\n1", document.Text);
        }, CancellationToken.None);

    [Fact]
    public Task Host_completion_evaluates_page_fields_after_combining_sections() =>
        fixture.Session.Dispatch(() =>
        {
            var template = FieldOperations.Insert(FlowDocument.FromText("?"), Guid.Empty, 0, 1,
                "PAGE", FlowDocument.FromText("?"));
            var callbackCount = 0;
            var document = MailMergeCombinedProcessor.Merge(template,
                new[] { new Dictionary<string, object?>(), new Dictionary<string, object?>() },
                combinedOptions: new()
                {
                    PageNumberPolicy = CombinedPageNumberPolicy.Continue,
                    CompleteDocument = (combined, token) =>
                    {
                        callbackCount++;
                        Assert.Equal("?\n?", combined.Text);
                        using var engine = new PaginationEngine();
                        var result = engine.UpdateFields(combined,
                            new FieldEvaluationOptions { OnlyDirty = true, CancellationToken = token });
                        Assert.Empty(result.Diagnostics);
                        return result.Document;
                    }
                });
            Assert.Equal(1, callbackCount);
            Assert.Equal("1\n2", document.Text);
            document.Validate();
        }, CancellationToken.None);

    [Fact]
    public Task Combined_record_boundaries_survive_pdf_output() =>
        fixture.Session.Dispatch(async () =>
        {
            var document = MailMergeCombinedProcessor.Merge(FlowDocument.FromText("Recipient"),
                new[] { new Dictionary<string, object?>(), new Dictionary<string, object?>() });
            var editor = new TextaloniaEditor { Document = document, PdfExporter = new PdfExporter() };
            using var output = new MemoryStream();
            var result = await editor.ExportPdfAsync(output);
            Assert.Equal(2, result.PageCount);
            Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(output.ToArray()));
            return true;
        }, CancellationToken.None);
}
