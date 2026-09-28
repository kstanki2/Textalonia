using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Model.Fields;

namespace Textalonia.MailMerge;

/// <summary>How the first section of each later record obtains its headers and footers.</summary>
public enum CombinedHeaderFooterPolicy
{
    /// <summary>Use the record's own header and footer stories, including explicit blank variants.</summary>
    EachRecord,
    /// <summary>Inherit every header and footer variant from the preceding record.</summary>
    LinkToPrevious
}

/// <summary>How page numbers begin in the first section of each later record.</summary>
public enum CombinedPageNumberPolicy
{
    /// <summary>Keep the first section's page-number setting from the template.</summary>
    Template,
    /// <summary>Continue numbering from the preceding record.</summary>
    Continue,
    /// <summary>Start at page one for each record.</summary>
    RestartEachRecord
}

/// <summary>Controls the boundaries between records in one physical document.</summary>
public sealed record CombinedMailMergeOptions
{
    public CombinedHeaderFooterPolicy HeaderFooterPolicy { get; init; } = CombinedHeaderFooterPolicy.EachRecord;
    public CombinedPageNumberPolicy PageNumberPolicy { get; init; } = CombinedPageNumberPolicy.RestartEachRecord;

    /// <summary>
    /// Optional host completion step, invoked once after all records have been expanded and joined.
    /// Exact pagination uses the host's Avalonia UI thread and font context. Supply this callback
    /// to run PaginationEngine.UpdateFields with OnlyDirty=true and return its Document when cached
    /// page-dependent field results are needed before serialization. Print/PDF output can instead
    /// paginate the assembled document through its normal output path.
    /// </summary>
    public Func<FlowDocument, CancellationToken, FlowDocument>? CompleteDocument { get; init; }
}

/// <summary>Builds one document from a mail-merge batch, with a new physical section per record.</summary>
public static class MailMergeCombinedProcessor
{
    /// <summary>Combines selected recipients from a host-supplied data source.</summary>
    public static FlowDocument Merge(FlowDocument template, IMailMergeDataSource source,
        MailMergeRecipientSelection? selection = null, MailMergeOptions? mergeOptions = null,
        CombinedMailMergeOptions? combinedOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        IEnumerable<IReadOnlyDictionary<string, object?>> Values()
        {
            foreach (var recipient in MailMergeDataSources.SelectRecipients(source, selection, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return recipient.ToMergeValues();
            }
        }
        return Merge(template, Values(), mergeOptions, combinedOptions, cancellationToken);
    }

    /// <summary>
    /// Expands records in enumeration order and joins them into one document. The source is disposed
    /// on completion, failure, or cancellation. An empty source produces an empty document.
    /// General fields requested by <paramref name="mergeOptions"/> are evaluated per record in
    /// ordinary mode; page-dependent fields should be completed after assembly through
    /// <see cref="CombinedMailMergeOptions.CompleteDocument"/>.
    /// </summary>
    public static FlowDocument Merge(FlowDocument template,
        IEnumerable<IReadOnlyDictionary<string, object?>> records,
        MailMergeOptions? mergeOptions = null,
        CombinedMailMergeOptions? combinedOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(records);
        combinedOptions ??= new();
        if (!Enum.IsDefined(combinedOptions.HeaderFooterPolicy) || !Enum.IsDefined(combinedOptions.PageNumberPolicy))
            throw new ArgumentOutOfRangeException(nameof(combinedOptions));

        // Page fields need the pages of the completed batch, while ordinary fields need each
        // recipient's values. MailMergeProcessor preserves its existing separate-output default.
        var recordOptions = mergeOptions?.FieldOptions is { } fields
            ? mergeOptions with { FieldOptions = fields with { Mode = FieldUpdateMode.Ordinary } }
            : mergeOptions;
        using var source = MailMergeProcessor.MergeMany(template, records, recordOptions, cancellationToken).GetEnumerator();
        FlowDocument? combined = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.MoveNext()) break;
            cancellationToken.ThrowIfCancellationRequested();
            var record = source.Current;
            record.Validate();
            if (record.Protection.Mode != DocumentProtectionMode.None || record.Protection.Password is not null ||
                !record.Protection.ProtectedSectionIds.IsEmpty)
                throw new NotSupportedException("Combined mail merge does not support protected templates.");
            record = EnsureSections(record);
            if (combined is null)
            {
                combined = record;
                continue;
            }

            if (record.FootnoteSettings != combined.FootnoteSettings || record.EndnoteSettings != combined.EndnoteSettings)
                throw new NotSupportedException("Combined mail merge requires common note settings.");
            if (record.Properties.Count != combined.Properties.Count ||
                record.Properties.Any(pair => !combined.Properties.TryGetValue(pair.Key, out var value) || value != pair.Value))
                throw new NotSupportedException("Combined mail merge requires common document properties.");

            // Clipboard preparation already handles the graph-wide identity work: fresh block,
            // inline, story, note, list, bookmark, field and control IDs; resource/style collisions;
            // and internal links and anchors. The returned snapshot also owns the union catalogs.
            var copy = DocumentFragments.Prepare(record, combined);
            var blocks = copy.Blocks;
            var boundary = FirstParagraphAtStart(blocks);
            if (boundary is null)
            {
                // A section boundary must name a visible paragraph outside a table. A record
                // beginning with a table needs a paragraph before it to carry that boundary.
                var marker = new Paragraph();
                blocks = blocks.Insert(0, marker);
                boundary = marker.Id;
            }

            var first = copy.Sections[0] with
            {
                StartParagraphId = boundary.Value,
                BreakKind = SectionBreakKind.NextPage,
                PageNumberStart = combinedOptions.PageNumberPolicy switch
                {
                    CombinedPageNumberPolicy.RestartEachRecord => 1,
                    CombinedPageNumberPolicy.Continue => null,
                    _ => copy.Sections[0].PageNumberStart
                },
                HeaderFooter = FirstHeaderFooter(copy.Sections[0].HeaderFooter, combinedOptions.HeaderFooterPolicy)
            };
            combined = combined with
            {
                Blocks = combined.Blocks.AddRange(blocks),
                Sections = combined.Sections.Add(first).AddRange(copy.Sections.Skip(1)),
                Styles = copy.Styles,
                Resources = copy.Resources,
                Fonts = copy.Fonts,
                Stories = combined.Stories.SetItems(copy.Stories),
                Notes = combined.Notes.AddRange(copy.Notes),
                Bookmarks = combined.Bookmarks.AddRange(copy.Bookmarks),
                Fields = combined.Fields.AddRange(copy.Fields),
                ContentControls = combined.ContentControls.AddRange(copy.ContentControls),
                PermissionRanges = combined.PermissionRanges.AddRange(copy.PermissionRanges)
            };
        }

        combined ??= new FlowDocument();
        cancellationToken.ThrowIfCancellationRequested();
        combined.Validate();
        if (combinedOptions.CompleteDocument is { } complete)
        {
            combined = complete(combined, cancellationToken) ??
                throw new InvalidOperationException("The combined-document completion callback returned null.");
            cancellationToken.ThrowIfCancellationRequested();
            combined.Validate();
        }
        return combined;
    }

    private static FlowDocument EnsureSections(FlowDocument record) => record.Sections.IsEmpty
        ? record with { Sections = [new DocumentSection()] } : record;

    private static Guid? FirstParagraphAtStart(ImmutableArray<Block> blocks) => blocks[0] switch
    {
        Paragraph paragraph => paragraph.Id,
        Section section => FirstParagraphAtStart(section.Blocks),
        _ => null
    };

    private static HeaderFooterSettings FirstHeaderFooter(HeaderFooterSettings source, CombinedHeaderFooterPolicy policy)
    {
        var settings = source;
        foreach (var footer in new[] { false, true })
            foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
            {
                var reference = source.GetReference(footer, variant);
                if (policy == CombinedHeaderFooterPolicy.LinkToPrevious)
                    reference = new StoryReference();
                else if (reference.LinkToPrevious)
                    reference = new StoryReference { LinkToPrevious = false };
                settings = settings.WithReference(footer, variant, reference);
            }
        return settings;
    }
}
