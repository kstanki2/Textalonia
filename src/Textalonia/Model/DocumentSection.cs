using System.Collections.Immutable;

namespace Textalonia.Model;

public enum SectionBreakKind { Continuous, NextPage, OddPage, EvenPage, NextColumn }

/// <summary>A physical section partition, distinct from the decorative nested Section block.</summary>
public sealed record DocumentSection
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Guid.Empty denotes the document start and is required on the first section. Later sections start at visible paragraph boundaries.</summary>
    public Guid StartParagraphId { get; init; }
    public SectionBreakKind BreakKind { get; init; } = SectionBreakKind.NextPage;
    public PageSettings PageSettings { get; init; } = new();
    /// <summary>Null continues numbering from the preceding section.</summary>
    public int? PageNumberStart { get; init; }
    public PageNumberFormat PageNumberFormat { get; init; }
    public HeaderFooterSettings HeaderFooter { get; init; } = new();

    internal static void Validate(FlowDocument document, HashSet<Guid> ids)
    {
        if (document.Sections.IsDefault || document.Sections.Length > 10000)
            throw new FormatException("Invalid physical sections.");
        if (document.Sections.IsEmpty) return;
        var index = new DocumentIndex(document);
        var positions = index.Paragraphs.ToDictionary(p => p.Paragraph.Id, p => p.Start);
        var previous = -1;
        for (var i = 0; i < document.Sections.Length; i++)
        {
            var section = document.Sections[i];
            if (section is null || section.Id == Guid.Empty || !ids.Add(section.Id) ||
                !Enum.IsDefined(section.BreakKind) || !Enum.IsDefined(section.PageNumberFormat) ||
                section.PageNumberStart is < 1 || section.PageSettings is null || section.HeaderFooter is null)
                throw new FormatException("Invalid physical section.");
            section.PageSettings.Validate();
            section.HeaderFooter.Validate(document);
            int start;
            if (i == 0)
            {
                if (section.StartParagraphId != Guid.Empty) throw new FormatException("The first physical section must start at the document start.");
                start = 0;
            }
            else if (!positions.TryGetValue(section.StartParagraphId, out start) || !IsOutsideTable(document.Blocks, section.StartParagraphId))
                throw new FormatException("Physical section boundary is detached from visible content.");
            if (start <= previous) throw new FormatException("Physical section boundaries must be strictly ordered.");
            previous = start;
        }
    }

    internal static bool IsOutsideTable(IEnumerable<Block> blocks, Guid id)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph && paragraph.Id == id) return true;
            if (block is Section section && IsOutsideTable(section.Blocks, id)) return true;
        }
        return false;
    }

    // Deleting a section's boundary joins it to the preceding section, whose properties survive.
    // Existing boundaries retain paragraph identities through ordinary insert/split edits.
    internal static FlowDocument Reconcile(FlowDocument document)
    {
        if (document.Sections.IsDefaultOrEmpty || document.Sections.Length == 1) return document;
        var index = new DocumentIndex(document);
        var result = ImmutableArray.CreateBuilder<DocumentSection>();
        var previous = -1;
        foreach (var section in document.Sections)
        {
            var start = section.StartParagraphId == Guid.Empty ? 0 :
                index.Tree.Paths?.Find(section.StartParagraphId) is null ? -1 : index.ById(section.StartParagraphId).Start;
            if (start <= previous) continue;
            result.Add(section); previous = start;
        }
        return result.Count == document.Sections.Length ? document : document with { Sections = result.ToImmutable() };
    }
}
