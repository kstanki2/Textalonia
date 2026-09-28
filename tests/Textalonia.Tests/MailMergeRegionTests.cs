using System.Collections.Immutable;
using Textalonia.MailMerge;
using Textalonia.Model;
using Textalonia.Model.Fields;
using Xunit;

namespace Textalonia.Tests;

public class MailMergeRegionTests
{
    private static RichRun Field(string name) => new(MergeFields.Create(name));
    private static Paragraph Boundary(string name) => new([Field(name)]);
    private static IReadOnlyDictionary<string, object?> Values(params (string Name, object? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

    [Fact]
    public void Paragraph_regions_expand_nested_children_with_parent_scope_and_empty_details()
    {
        var template = new FlowDocument([
            new Paragraph("Invoice"),
            Boundary("TableStart:Groups"),
            new Paragraph([new("Group "), Field("GroupName"), new(" for "), Field("Customer")]),
            Boundary("TableStart:Items"),
            new Paragraph([new("Item "), Field("Description"), new(" for "), Field("Customer")]),
            Boundary("TableEnd:Items"),
            Boundary("TableEnd:Groups"),
            new Paragraph("Done")
        ]);
        var data = Values(("Customer", "Ada"), ("Groups", new[]
        {
            Values(("GroupName", "A"), ("Items", new[] { Values(("Description", "Pen")), Values(("Description", "Book")) })),
            Values(("GroupName", "B"), ("Items", Array.Empty<IReadOnlyDictionary<string, object?>>() ))
        }));

        var merged = MailMergeProcessor.Merge(template, data);

        merged.Validate();
        Assert.Equal("Invoice\nGroup A for Ada\nItem Pen for Ada\nItem Book for Ada\nGroup B for Ada\nDone", merged.PlainText);
        Assert.Equal(merged.Blocks.Length, merged.Blocks.Select(block => block.Id).Distinct().Count());
        Assert.Contains("TableStart:Groups", template.PlainText);
        Assert.DoesNotContain("TableStart", merged.PlainText);
    }

    [Fact]
    public void Table_row_regions_repeat_rows_and_remove_empty_regions()
    {
        var table = Table.Create(4, 2)
            .SetCell(0, 0, new TableCell { Blocks = [Boundary("TableStart:Items")] })
            .SetCell(1, 0, new TableCell { Blocks = [new Paragraph([Field("Name")])] })
            .SetCell(1, 1, new TableCell { Blocks = [new Paragraph([Field("Price")])] })
            .SetCell(2, 0, new TableCell { Blocks = [Boundary("TableEnd:Items")] })
            .SetCell(3, 0, new TableCell { Blocks = [new Paragraph("Total")] });
        var template = new FlowDocument([table]);
        var records = new[] { Values(("Name", "Pen"), ("Price", 2)), Values(("Name", "Book"), ("Price", 5)) };

        var merged = MailMergeProcessor.Merge(template, Values(("Items", records)));
        var output = Assert.IsType<Table>(merged.Blocks[0]);
        merged.Validate();
        Assert.Equal(3, output.Rows.Length);
        Assert.Equal("Pen", Assert.IsType<Paragraph>(output.Rows[0][0].Blocks[0]).PlainText);
        Assert.Equal("Book", Assert.IsType<Paragraph>(output.Rows[1][0].Blocks[0]).PlainText);
        Assert.Equal("Total", Assert.IsType<Paragraph>(output.Rows[2][0].Blocks[0]).PlainText);
        Assert.NotEqual(output.Rows[0][0].Id, output.Rows[1][0].Id);

        var emptyItems = Array.Empty<IReadOnlyDictionary<string, object?>>();
        var empty = MailMergeProcessor.Merge(template, Values(("Items", emptyItems)));
        Assert.Single(Assert.IsType<Table>(empty.Blocks[0]).Rows);
        empty.Validate();
    }

    [Fact]
    public void Repeated_bookmarks_and_internal_links_are_scoped_to_each_copy()
    {
        var style = TextStyle.Default with { InternalLink = new InternalLinkDestination { BookmarkName = "detail" } };
        var line = new Paragraph([new RichRun("Go", style), Field("Name")]);
        var template = new FlowDocument([Boundary("TableStart:Items"), line, Boundary("TableEnd:Items")]);
        template = template with { Bookmarks = [new DocumentBookmark
        {
            Name = "detail",
            Start = new DocumentAnchor { ParagraphId = line.Id, Offset = 0, Affinity = AnchorAffinity.Before },
            End = new DocumentAnchor { ParagraphId = line.Id, Offset = 0 }
        }] };
        template.Validate();

        var merged = MailMergeProcessor.Merge(template, Values(("Items", new[]
        { Values(("Name", "A")), Values(("Name", "B")) })));

        merged.Validate();
        Assert.Equal(2, merged.Bookmarks.Length);
        Assert.NotEqual(merged.Bookmarks[0].Name, merged.Bookmarks[1].Name);
        Assert.Equal(merged.Bookmarks[0].Name, Assert.IsType<Paragraph>(merged.Blocks[0]).Runs[0].Style.InternalLink?.BookmarkName);
        Assert.Equal(merged.Bookmarks[1].Name, Assert.IsType<Paragraph>(merged.Blocks[1]).Runs[0].Style.InternalLink?.BookmarkName);
    }

    [Fact]
    public void General_fields_in_repeated_blocks_use_child_values()
    {
        var line = new Paragraph("?");
        var template = new FlowDocument([Boundary("TableStart:Items"), line, Boundary("TableEnd:Items")]);
        template = template with { Fields = [new DocumentField
        {
            Instruction = "IF { MERGEFIELD Amount } > 1 High Low",
            Start = new DocumentAnchor { ParagraphId = line.Id, Offset = 0, Affinity = AnchorAffinity.Before },
            End = new DocumentAnchor { ParagraphId = line.Id, Offset = 1 }
        }] };
        var merged = MailMergeProcessor.Merge(template, Values(("Items", new[]
        { Values(("Amount", 1)), Values(("Amount", 2)) })),
            new MailMergeOptions { FieldOptions = new FieldEvaluationOptions() });

        merged.Validate();
        Assert.Equal("Low\nHigh", merged.PlainText);
    }

    [Fact]
    public void Repeated_controls_permissions_and_section_boundaries_keep_valid_anchors()
    {
        var detail = new Paragraph("Static");
        var template = new FlowDocument([new Paragraph("Intro"), Boundary("TableStart:Items"),
            detail, Boundary("TableEnd:Items")]);
        var start = new DocumentAnchor { ParagraphId = detail.Id, Offset = 0, Affinity = AnchorAffinity.Before };
        var end = new DocumentAnchor { ParagraphId = detail.Id, Offset = detail.Length };
        template = template with
        {
            ContentControls = [new DocumentContentControl { Kind = ContentControlKind.RichText,
                Start = start, End = end, Value = "Static" }],
            PermissionRanges = [new DocumentPermissionRange { Start = start, End = end, IsReadOnly = true }],
            Sections = [new DocumentSection(), new DocumentSection { StartParagraphId = detail.Id }]
        };
        template.Validate();

        var merged = MailMergeProcessor.Merge(template, Values(("Items", new[] { Values(), Values() })));

        merged.Validate();
        Assert.Equal(2, merged.ContentControls.Length);
        Assert.Equal(2, merged.PermissionRanges.Length);
        Assert.Equal(3, merged.Sections.Length);
        Assert.NotEqual(merged.ContentControls[0].Start.ParagraphId, merged.ContentControls[1].Start.ParagraphId);
    }

    [Fact]
    public void Empty_region_collapses_a_section_boundary_and_remaps_protection()
    {
        var first = new DocumentSection();
        var after = new Paragraph("After");
        var second = new DocumentSection { StartParagraphId = after.Id };
        var template = new FlowDocument([Boundary("TableStart:Items"), new Paragraph("Detail"),
            Boundary("TableEnd:Items"), after]) with
        {
            Sections = [first, second],
            Protection = new DocumentProtection { Mode = DocumentProtectionMode.ReadOnly,
                ProtectedSectionIds = [first.Id] }
        };
        template.Validate();

        var merged = MailMergeProcessor.Merge(template,
            Values(("Items", Array.Empty<IReadOnlyDictionary<string, object?>>())));

        merged.Validate();
        Assert.Single(merged.Sections);
        Assert.Equal(second.Id, merged.Sections[0].Id);
        Assert.Empty(merged.Protection.ProtectedSectionIds);
    }

    [Fact]
    public void Hidden_table_backups_may_share_live_ids_when_other_regions_expand()
    {
        var table = Table.Create(1, 1);
        var cell = table.Rows[0][0] with { Blocks = [new Paragraph("Live")] };
        table = table.SetCell(0, 0, cell with { MergeOriginalBlocks = cell.Blocks });
        var template = new FlowDocument([Boundary("TableStart:Items"), new Paragraph([Field("Name")]),
            Boundary("TableEnd:Items"), table]);
        template.Validate();

        var merged = MailMergeProcessor.Merge(template, Values(("Items", new[] { Values(("Name", "A")) })));

        merged.Validate();
        Assert.Equal("Live", Assert.IsType<Paragraph>(Assert.IsType<Table>(merged.Blocks[1]).Rows[0][0].MergeOriginalBlocks[0]).Text);
    }

    [Fact]
    public void Child_without_collection_does_not_reuse_an_ancestor_collection()
    {
        var template = new FlowDocument([Boundary("TableStart:Groups"),
            Boundary("TableStart:Items"), new Paragraph([Field("Name")]),
            Boundary("TableEnd:Items"), Boundary("TableEnd:Groups")]);
        var data = Values(("Items", new[] { Values(("Name", "wrong")) }),
            ("Groups", new[] { Values(("GroupName", "A")) }));

        var merged = MailMergeProcessor.Merge(template, data);

        merged.Validate();
        Assert.Equal("", merged.PlainText);
    }

    [Fact]
    public void Invalid_boundaries_fail_before_lazy_record_enumeration()
    {
        var read = false;
        IEnumerable<IReadOnlyDictionary<string, object?>> Records()
        {
            read = true;
            yield return Values(("Items", Array.Empty<IReadOnlyDictionary<string, object?>>()));
        }
        var template = new FlowDocument([Boundary("TableStart:Items"), new Paragraph("value")]);
        Assert.Throws<FormatException>(() => MailMergeProcessor.MergeMany(template, Records()));
        Assert.False(read);
        var inline = new FlowDocument([new Paragraph([new RichRun("prefix"), Field("TableStart:Items")])]);
        Assert.Throws<FormatException>(() => MailMergeProcessor.Merge(inline, Values()));
    }

    [Fact]
    public void A_range_crossing_a_region_is_rejected_before_records_are_read()
    {
        var before = new Paragraph("Before");
        var detail = new Paragraph("Detail");
        var template = new FlowDocument([before, Boundary("TableStart:Items"), detail,
            Boundary("TableEnd:Items")]) with
        {
            Bookmarks = [new DocumentBookmark
            {
                Name = "crossing",
                Start = new DocumentAnchor { ParagraphId = before.Id, Offset = 0, Affinity = AnchorAffinity.Before },
                End = new DocumentAnchor { ParagraphId = detail.Id, Offset = 0 }
            }]
        };
        template.Validate();
        var enumerated = false;
        IEnumerable<IReadOnlyDictionary<string, object?>> Records()
        {
            enumerated = true;
            yield return Values(("Items", new[] { Values() }));
        }

        Assert.Throws<FormatException>(() => MailMergeProcessor.MergeMany(template, Records()));
        Assert.False(enumerated);
    }

    [Fact]
    public void Host_data_source_can_stream_selected_recipients_into_separate_documents()
    {
        IReadOnlyDictionary<string, object?>[] values = [Values(("Name", "Ada")),
            Values(("Name", "Grace")), Values(("Name", "Linus"))];
        var source = new DictionaryMailMergeDataSource(values);
        var template = new FlowDocument([new Paragraph([Field("Name")])]);

        var documents = MailMergeProcessor.MergeFromSource(template, source,
            new MailMergeRecipientSelection { SourceIndexes = [2, 0] }).ToArray();

        Assert.Equal(new[] { "Ada", "Linus" }, documents.Select(document => document.PlainText));
    }

    [Fact]
    public void Lifecycle_callbacks_and_cancellation_report_the_active_record()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();
        var template = new FlowDocument([Boundary("TableStart:Items"), new Paragraph([Field("Name")]), Boundary("TableEnd:Items")]);
        var options = new MailMergeOptions
        {
            RecordStarting = e => events.Add($"start:{e.RecordIndex}"),
            RecordCompleted = e => events.Add($"done:{e.RecordIndex}"),
            RegionProgress = e =>
            {
                events.Add($"region:{e.ItemIndex}:{e.IsStarting}");
                if (e.ItemIndex == 1 && e.IsStarting) cancellation.Cancel();
            }
        };
        var records = new[] { Values(("Items", new[] { Values(("Name", "A")), Values(("Name", "B")) })) };
        Assert.Throws<OperationCanceledException>(() => MailMergeProcessor.MergeMany(template, records, options, cancellation.Token).ToArray());
        Assert.Contains("start:0", events);
        Assert.DoesNotContain("done:0", events);
        Assert.Contains("TableStart:Items", template.PlainText);
    }
}
