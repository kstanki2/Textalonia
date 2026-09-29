using System.Collections.Immutable;
using System.Globalization;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Model.Fields;
using Xunit;

namespace Textalonia.Tests;

public sealed class GeneralFieldTests
{
    private static FlowDocument Field(string instruction, string cached = "cached") => FieldOperations.Insert(FlowDocument.FromText(cached), Guid.Empty, 0, cached.Length, instruction, FlowDocument.FromText(cached));

    [Fact]
    public void Nested_conditional_merge_formula_and_switches_use_explicit_culture()
    {
        var document = Field("IF { MERGEFIELD Total } >= 100 \"Dear { MERGEFIELD Name \\* UPPER }, { = { MERGEFIELD Total } * 1.2 \\# \"0.00\" }\" \"Small order\"");
        var result = FieldEvaluator.Update(document, new() { Culture = CultureInfo.GetCultureInfo("fr-FR"), MergeValues = new Dictionary<string, object?> { ["Total"] = 125, ["Name"] = "Ada" } });
        Assert.Empty(result.Diagnostics);
        Assert.Equal("Dear ADA, 150,00", result.Document.Text);
        Assert.False(result.Document.Fields[0].IsDirty);
        Assert.DoesNotContain('\uFFFC', result.Document.Text);
        Assert.Equal(result.Document.Text.Length, result.Document.Fields[0].End.Resolve(result.Document));
    }

    [Fact]
    public void Dates_properties_and_formula_results_are_deterministic()
    {
        var date = FieldEvaluator.Update(Field("DATE \\@ \"yyyy-MM-dd HH:mm\""), new() { Clock = new(2026, 9, 27, 14, 30, 0, TimeSpan.Zero) });
        Assert.Empty(date.Diagnostics); Assert.Equal("2026-09-27 14:30", date.Document.Text);
        var property = Field("DOCPROPERTY Title") with { Properties = ImmutableDictionary<string, string>.Empty.Add("Title", "Annual report") };
        Assert.Equal("Annual report", FieldEvaluator.Update(property).Document.Text);
        Assert.Equal("23", FieldEvaluator.Update(Field("= SUM(2,3) * 4 + 2 ^ 2 - 1")).Document.Text);
        Assert.Equal("-4", FieldEvaluator.Update(Field("= -2 ^ 2")).Document.Text);
        Assert.Equal("0.25", FieldEvaluator.Update(Field("= 2 ^ -2")).Document.Text);
    }

    [Fact]
    public void Office_metadata_feeds_property_fields_without_legacy_projection()
    {
        var core = Field("TITLE") with { CoreProperties = new DocumentCoreProperties { Title = "Quarterly report" } };
        Assert.Equal("Quarterly report", FieldEvaluator.Update(core).Document.Text);
        var typed = Field("DOCPROPERTY Count") with
        {
            CustomProperties = [new DocumentCustomProperty { Name = "Count", Type = DocumentPropertyType.Integer, Value = "42" }]
        };
        Assert.Equal("42", FieldEvaluator.Update(typed).Document.Text);
    }

    [Fact]
    public void Rich_host_result_can_span_blocks_and_cross_story_dependencies_resolve()
    {
        var document = Field("DOCVARIABLE Address");
        var result = FieldEvaluator.Update(document, new() { DocumentVariableResolver = _ => new FlowDocument([
            new Paragraph("Ada", TextStyle.Default with { Bold = true }), new Paragraph("London")]) });
        Assert.Empty(result.Diagnostics); Assert.Equal("Ada\nLondon", result.Document.Text);
        Assert.True(Assert.IsType<Paragraph>(result.Document.Blocks[0]).Runs[0].Style.Bold);
        var story = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("ref")] };
        document = result.Document with { Stories = result.Document.Stories.Add(story.Id, story), Bookmarks = [new() { Name = "Address",
            Start = DocumentAnchor.Create(result.Document, Guid.Empty, 0, AnchorAffinity.Before), End = DocumentAnchor.Create(result.Document, Guid.Empty, result.Document.Text.Length) }] };
        document = FieldOperations.Insert(document, story.Id, 0, 3, "REF Address", FlowDocument.FromText("ref"));
        result = FieldEvaluator.Update(document, new() { DocumentVariableResolver = _ => FlowDocument.FromText("New\nAddress") });
        Assert.Empty(result.Diagnostics); Assert.Equal("New\nAddress", result.Document.GetStoryDocument(story.Id).Text);
    }

    [Fact]
    public void Locked_unknown_missing_resolver_and_cycles_keep_cached_results()
    {
        var locked = Field("DATE");
        locked = FieldOperations.SetLocked(locked, locked.Fields[0].Id, true);
        Assert.Equal("cached", FieldEvaluator.Update(locked).Document.Text);
        var unknown = FieldEvaluator.Update(Field("UNKNOWNCODE foo"));
        Assert.Equal("cached", unknown.Document.Text); Assert.Equal("field.unsupported-code", Assert.Single(unknown.Diagnostics).Code);
        var missing = FieldEvaluator.Update(Field("INCLUDEPICTURE \"https://example.test/image.png\""));
        Assert.Equal("cached", missing.Document.Text); Assert.Equal("field.host-resolver-required", Assert.Single(missing.Diagnostics).Code);
    }

    [Fact]
    public void Dependency_cycles_retain_both_cached_results()
    {
        var document = FlowDocument.FromText("A B");
        document = FieldOperations.Insert(document, Guid.Empty, 0, 1, "REF B", FlowDocument.FromText("A"));
        document = FieldOperations.Insert(document, Guid.Empty, 2, 1, "REF A", FlowDocument.FromText("B"));
        document = document with { Bookmarks = [new() { Name = "A", Start = DocumentAnchor.Create(document, Guid.Empty, 0), End = DocumentAnchor.Create(document, Guid.Empty, 1) },
            new() { Name = "B", Start = DocumentAnchor.Create(document, Guid.Empty, 2), End = DocumentAnchor.Create(document, Guid.Empty, 3) }] };
        var result = FieldEvaluator.Update(document);
        Assert.Equal("A B", result.Document.Text); Assert.All(result.Diagnostics, d => Assert.Equal("field.cycle", d.Code)); Assert.Equal(2, result.Diagnostics.Length);
    }

    [Fact]
    public void Contents_has_links_bookmarks_tab_leaders_and_stable_updates()
    {
        var document = new FlowDocument([new Paragraph("cached"), new Paragraph("First") { Style = ParagraphStyle.Default with { HeadingLevel = 1 } }, new Paragraph("Second") { Style = ParagraphStyle.Default with { HeadingLevel = 2 } }]);
        document = FieldOperations.Insert(document, Guid.Empty, 0, 6, "TOC \\o \"1-3\" \\h", FlowDocument.FromText("cached"));
        var options = new FieldEvaluationOptions { AnchorPageResolver = _ => 2, Mode = FieldUpdateMode.All };
        var result = FieldEvaluator.Update(document, options);
        Assert.Empty(result.Diagnostics); Assert.StartsWith("First\t2\nSecond\t2", result.Document.Text);
        Assert.Equal(2, result.Document.Bookmarks.Length);
        var paragraph = Assert.IsType<Paragraph>(result.Document.Blocks[0]);
        Assert.NotNull(paragraph.Runs[0].Style.InternalLink); Assert.Equal(TabLeader.Dots, paragraph.Style.TabStops[0].Leader);
        var stable = FieldEvaluator.Update(result.Document, options);
        Assert.Empty(stable.Diagnostics); Assert.Equal(0, stable.UpdatedCount); Assert.Equal(result.Document.Text, stable.Document.Text);
    }

    [Fact]
    public void Page_fields_converge_and_failure_is_bounded()
    {
        var result = FieldEvaluator.UpdateUntilStable(Field("NUMPAGES", "9"), doc => new(doc.Text, _ => new(1, 2, 2)));
        Assert.Equal("2", result.Document.Text); Assert.Empty(result.Diagnostics);
        var toggle = 0;
        result = FieldEvaluator.UpdateUntilStable(Field("NUMPAGES", "9"), _ => new((toggle++).ToString(), _ => new(1, toggle % 2 + 1, 1)), maximumIterations: 3);
        Assert.Contains(result.Diagnostics, d => d.Code == "field.pagination-not-converged");
    }

    [Fact]
    public void Atomic_adapter_preserves_native_format_fallback_and_cache()
    {
        var inline = MergeFields.Create("Value", "0.00", "missing");
        var document = new FlowDocument([new Paragraph([new RichRun(inline)])]);
        document = FieldOperations.AdaptMergeField(document, Guid.Empty, inline.Id);
        Assert.Equal(inline.AltText, document.Text); Assert.Equal("0.00", document.Fields[0].LegacyMergeField!.Format);
        Assert.Equal("missing", FieldEvaluator.Update(document).Document.Text);
        Assert.Equal("2.50", FieldEvaluator.Update(document, new() { MergeValues = new Dictionary<string, object?> { ["Value"] = 2.5 } }).Document.Text);
    }

    [Fact]
    public void References_copy_rich_runs_images_and_internal_links()
    {
        var image = new InlineDescriptor { AltText = "diagram", Payload = new ImageInlinePayload("picture") };
        var document = new FlowDocument([new Paragraph("cache"), new Paragraph([new RichRun("Bold", TextStyle.Default with { Bold = true }), new RichRun(image)])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("picture", new() { Kind = DocumentResourceKind.Host, Location = "picture" }) };
        document = document with { Bookmarks = [new() { Name = "Source", Start = DocumentAnchor.Create(document, Guid.Empty, 6, AnchorAffinity.Before), End = DocumentAnchor.Create(document, Guid.Empty, 11) }] };
        document = FieldOperations.Insert(document, Guid.Empty, 0, 5, "REF Source \\h", FlowDocument.FromText("cache"));
        var updated = FieldEvaluator.Update(document);
        Assert.Empty(updated.Diagnostics);
        var paragraph = Assert.IsType<Paragraph>(updated.Document.Blocks[0]);
        Assert.True(paragraph.Runs[0].Style.Bold); Assert.IsType<ImageInlinePayload>(paragraph.Runs[1].Inline!.Payload);
        Assert.Equal("Source", paragraph.Runs[0].Style.InternalLink!.BookmarkName);
        Assert.Equal("Bold diagram".Replace(" ", ""), paragraph.PlainText);
    }

    [Fact]
    public void Caption_lists_and_explicit_entries_resolve_sequence_dependencies()
    {
        var document = new FlowDocument([new Paragraph("list"), new Paragraph(""), new Paragraph("entry")]);
        document = FieldOperations.Insert(document, Guid.Empty, 0, 4, "TOC \\c Figure \\n \\h", FlowDocument.FromText("list"));
        document = FieldOperations.InsertCaption(document, Guid.Empty, 5, "Figure", "Example");
        var updated = FieldEvaluator.Update(document);
        Assert.Empty(updated.Diagnostics); Assert.StartsWith("Figure 1: Example\n", updated.Document.Text);
        document = new FlowDocument([new Paragraph("list"), new Paragraph("marker")]);
        document = FieldOperations.Insert(document, Guid.Empty, 0, 4, "TOC \\f A \\n", FlowDocument.FromText("list"));
        document = FieldOperations.Insert(document, Guid.Empty, 5, 6, "TC \"Entry name\" \\f A \\l 2", FlowDocument.FromText("marker"));
        updated = FieldEvaluator.Update(document);
        Assert.Empty(updated.Diagnostics); Assert.StartsWith("Entry name\n", updated.Document.Text);
    }

    [Fact]
    public void Explicit_contents_entry_can_omit_only_its_own_page_number()
    {
        var document = FlowDocument.FromText("list\na\nb");
        document = FieldOperations.Insert(document, Guid.Empty, 0, 4, "TOC \\f A \\h", FlowDocument.FromText("list"));
        document = FieldOperations.Insert(document, Guid.Empty, 5, 1, "TC \"No page\" \\f A \\n", FlowDocument.FromText("a"));
        document = FieldOperations.Insert(document, Guid.Empty, 7, 1, "TC Numbered \\f A", FlowDocument.FromText("b"));
        var options = new FieldEvaluationOptions { Mode = FieldUpdateMode.All, AnchorPageResolver = _ => 7 };
        var updated = FieldEvaluator.Update(document, options);
        Assert.Empty(updated.Diagnostics);
        Assert.Equal("No page", Assert.IsType<Paragraph>(updated.Document.Blocks[0]).PlainText);
        Assert.Equal("Numbered\t7", Assert.IsType<Paragraph>(updated.Document.Blocks[1]).PlainText);
        Assert.NotNull(Assert.IsType<Paragraph>(updated.Document.Blocks[0]).Runs[0].Style.InternalLink);
        var repeated = FieldEvaluator.Update(updated.Document, options);
        Assert.Empty(repeated.Diagnostics); Assert.Equal(0, repeated.UpdatedCount);
    }

    [Fact]
    public void Page_reference_hyperlink_is_applied_even_when_cached_number_is_unchanged()
    {
        var document = FlowDocument.FromText("7\ndestination");
        document = document with { Bookmarks = [new() { Name = "Destination",
            Start = DocumentAnchor.Create(document, Guid.Empty, 2, AnchorAffinity.Before),
            End = DocumentAnchor.Create(document, Guid.Empty, document.Text.Length) }] };
        document = FieldOperations.Insert(document, Guid.Empty, 0, 1, "PAGEREF Destination \\h", FlowDocument.FromText("7"));
        var options = new FieldEvaluationOptions { Mode = FieldUpdateMode.All, AnchorPageResolver = _ => 7 };
        var updated = FieldEvaluator.Update(document, options);
        Assert.Empty(updated.Diagnostics); Assert.Equal(1, updated.UpdatedCount);
        var run = Assert.IsType<Paragraph>(updated.Document.Blocks[0]).Runs[0];
        Assert.Equal("7", run.Text); Assert.Equal("Destination", run.Style.InternalLink!.BookmarkName);
        var repeated = FieldEvaluator.Update(updated.Document, options);
        Assert.Empty(repeated.Diagnostics); Assert.Equal(0, repeated.UpdatedCount);
    }

    [Fact]
    public void Table_host_result_keeps_field_boundaries_and_structural_updates()
    {
        static FlowDocument Result(double width) => new([new Table { ColumnWidths = [width],
            Rows = [[new TableCell { Blocks = [new Paragraph("Rich cell")] }]] }]);
        var options = new FieldEvaluationOptions { DocumentVariableResolver = _ => Result(1) };
        var first = FieldEvaluator.Update(Field("DOCVARIABLE Rows"), options);
        Assert.Empty(first.Diagnostics); first.Document.Validate(); Assert.Single(first.Document.Fields);
        Assert.IsType<Table>(first.Document.Blocks[0]); Assert.Equal("Rich cell", first.Document.Text);
        var second = FieldEvaluator.Update(first.Document, options);
        Assert.Empty(second.Diagnostics); Assert.Equal(0, second.UpdatedCount);
        var changed = FieldEvaluator.Update(second.Document, options with { DocumentVariableResolver = _ => Result(2) });
        Assert.Empty(changed.Diagnostics); Assert.Equal(1, changed.UpdatedCount);
        Assert.Equal(2, Assert.IsType<Table>(changed.Document.Blocks[0]).ColumnWidths[0]);
    }

    [Fact]
    public void Code_projection_preserves_result_coordinates_and_caption_sequences_generate_a_list()
    {
        var document = Field("DATE", "2026");
        document = FieldOperations.SetShowCode(document, document.Fields[0].Id, true);
        var projection = FieldCodeProjection.Create(document);
        Assert.Equal("{ DATE }", projection.Text);
        Assert.Equal(0, projection.ToDocumentOffset(3, AnchorAffinity.Before));
        Assert.Equal(4, projection.ToDocumentOffset(3));
        Assert.Equal(0, projection.ToDisplayOffset(0, AnchorAffinity.Before));
        Assert.Equal("2026", document.Text);
        document = FieldOperations.InsertCaption(FlowDocument.FromText(""), Guid.Empty, 0, "Figure", "Example");
        var result = FieldEvaluator.Update(document);
        Assert.Empty(result.Diagnostics); Assert.Equal("Figure 1: Example", result.Document.Text);
    }

    [Fact]
    public void Hyperlinks_format_cached_results_and_unsupported_switches_preserve_them()
    {
        var linked = FieldEvaluator.Update(Field("HYPERLINK \"https://example.test\"", "Example"));
        Assert.Empty(linked.Diagnostics);
        Assert.Equal("https://example.test", Assert.IsType<Paragraph>(linked.Document.Blocks[0]).Runs[0].Style.Hyperlink);
        var unsupported = FieldEvaluator.Update(Field("DATE \\unsupported x"));
        Assert.Equal("cached", unsupported.Document.Text); Assert.Equal("field.unsupported-switch", Assert.Single(unsupported.Diagnostics).Code);
    }

    [Fact]
    public void Parser_is_bounded_and_remaps_only_reference_operands()
    {
        Assert.Throws<FormatException>(() => FieldInstructionParser.Parse("IF { IF { PAGE } = 1 a b } = 1 a b", 2));
        var source = "IF { REF Old } = \"REF Old\" \"yes\" \"no\"";
        var changed = FieldInstructionParser.RewriteBookmarkReferences(source, new Dictionary<string, string> { ["Old"] = "New" });
        Assert.Contains("REF New", changed); Assert.Contains("\"REF Old\"", changed);
        Assert.Equal("= New * 2", FieldInstructionParser.RewriteBookmarkReferences("= Old * 2", new Dictionary<string, string> { ["Old"] = "New" }));
        Assert.Equal(source, FieldInstructionParser.RewriteBookmarkReferences(source, new Dictionary<string, string>()));
    }
}
