using System.Collections.Immutable;

namespace Textalonia.Model;

public enum DocumentStoryKind { Header, Footer, Footnote, Endnote }
public enum DocumentNoteKind { Footnote, Endnote }
public enum HeaderFooterVariant { Primary, First, Even }
public enum NoteRestartPolicy { Continuous, EachSection, EachPage }
public enum NotePlacement { PageBottom, BelowText, DocumentEnd, SectionEnd }
public enum PageFieldKind { Page, NumPages, SectionPages }

/// <summary>An independently editable immutable story sharing its owner's resources and styles.</summary>
public sealed record DocumentStory
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DocumentStoryKind Kind { get; init; }
    public ImmutableArray<Block> Blocks { get; init; } = [new Paragraph()];
}

public sealed record DocumentNote
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid StoryId { get; init; }
    public DocumentNoteKind Kind { get; init; }
    public string? CustomMark { get; init; }
}

/// <summary>A linked reference inherits the same variant from the preceding physical section.</summary>
public sealed record StoryReference
{
    public Guid? StoryId { get; init; }
    public bool LinkToPrevious { get; init; } = true;
}

public sealed record HeaderFooterSettings
{
    public StoryReference PrimaryHeader { get; init; } = new();
    public StoryReference FirstHeader { get; init; } = new();
    public StoryReference EvenHeader { get; init; } = new();
    public StoryReference PrimaryFooter { get; init; } = new();
    public StoryReference FirstFooter { get; init; } = new();
    public StoryReference EvenFooter { get; init; } = new();
    public bool DifferentFirstPage { get; init; }
    public bool DifferentOddEvenPages { get; init; }
    public double HeaderDistance { get; init; } = 36;
    public double FooterDistance { get; init; } = 36;

    public StoryReference GetReference(bool footer, HeaderFooterVariant variant) => (footer, variant) switch
    {
        (false, HeaderFooterVariant.Primary) => PrimaryHeader, (false, HeaderFooterVariant.First) => FirstHeader,
        (false, HeaderFooterVariant.Even) => EvenHeader, (true, HeaderFooterVariant.Primary) => PrimaryFooter,
        (true, HeaderFooterVariant.First) => FirstFooter, (true, HeaderFooterVariant.Even) => EvenFooter,
        _ => throw new ArgumentOutOfRangeException(nameof(variant))
    };

    public HeaderFooterSettings WithReference(bool footer, HeaderFooterVariant variant, StoryReference reference) => (footer, variant) switch
    {
        (false, HeaderFooterVariant.Primary) => this with { PrimaryHeader = reference },
        (false, HeaderFooterVariant.First) => this with { FirstHeader = reference },
        (false, HeaderFooterVariant.Even) => this with { EvenHeader = reference },
        (true, HeaderFooterVariant.Primary) => this with { PrimaryFooter = reference },
        (true, HeaderFooterVariant.First) => this with { FirstFooter = reference },
        (true, HeaderFooterVariant.Even) => this with { EvenFooter = reference },
        _ => throw new ArgumentOutOfRangeException(nameof(variant))
    };

    internal void Validate(FlowDocument document)
    {
        if (!double.IsFinite(HeaderDistance) || HeaderDistance is < 0 or > 10000 ||
            !double.IsFinite(FooterDistance) || FooterDistance is < 0 or > 10000)
            throw new FormatException("Invalid header/footer distance.");
        foreach (var footer in new[] { false, true }) foreach (var variant in Enum.GetValues<HeaderFooterVariant>())
        {
            var reference = GetReference(footer, variant);
            if (reference is null || reference.LinkToPrevious && reference.StoryId is not null ||
                reference.StoryId is { } id && (!document.Stories.TryGetValue(id, out var story) ||
                    story.Kind != (footer ? DocumentStoryKind.Footer : DocumentStoryKind.Header)))
                throw new FormatException("Invalid header/footer story reference.");
        }
    }
}

public sealed record NoteSettings
{
    public PageNumberFormat NumberFormat { get; init; }
    public int Start { get; init; } = 1;
    public NoteRestartPolicy Restart { get; init; }
    public NotePlacement Placement { get; init; }
    public string SeparatorText { get; init; } = "────────";
    public string ContinuationSeparatorText { get; init; } = "──────── (continued)";

    internal void Validate(DocumentNoteKind kind)
    {
        if (!Enum.IsDefined(NumberFormat) || !Enum.IsDefined(Restart) || !Enum.IsDefined(Placement) || Start is < 1 or > 1000000 ||
            SeparatorText is null || SeparatorText.Length > 256 || ContinuationSeparatorText is null || ContinuationSeparatorText.Length > 256 ||
            kind == DocumentNoteKind.Footnote && Placement is not (NotePlacement.PageBottom or NotePlacement.BelowText) ||
            kind == DocumentNoteKind.Endnote && (Placement is not (NotePlacement.DocumentEnd or NotePlacement.SectionEnd) || Restart == NoteRestartPolicy.EachPage))
            throw new FormatException("Invalid note settings.");
    }
}

public sealed record NoteInlinePayload(Guid NoteId) : InlinePayload;
public sealed record PageFieldInlinePayload(PageFieldKind Field) : InlinePayload;

/// <summary>Note labels in reference order. Page restarts require a map from note identity to physical page.</summary>
public static class DocumentNoteNumbering
{
    public static string GetMark(FlowDocument document, Guid noteId, IReadOnlyDictionary<Guid, int>? notePages = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var target = document.Notes.FirstOrDefault(note => note.Id == noteId) ?? throw new ArgumentException("The note does not exist.", nameof(noteId));
        if (target.CustomMark is { } custom) return custom;
        var settings = target.Kind == DocumentNoteKind.Footnote ? document.FootnoteSettings : document.EndnoteSettings;
        var index = new DocumentIndex(document);
        var catalog = document.Notes.ToDictionary(note => note.Id);
        var currentSection = 0; var counter = settings.Start - 1; int? currentPage = null;
        foreach (var paragraph in index.Paragraphs)
        {
            while (currentSection + 1 < document.Sections.Length &&
                index.ById(document.Sections[currentSection + 1].StartParagraphId).Start <= paragraph.Start)
            {
                currentSection++;
                if (settings.Restart == NoteRestartPolicy.EachSection) counter = settings.Start - 1;
            }
            foreach (var run in paragraph.Paragraph.Runs)
            {
                if (run.Inline?.Payload is not NoteInlinePayload reference || !catalog.TryGetValue(reference.NoteId, out var note) || note.Kind != target.Kind) continue;
                if (settings.Restart == NoteRestartPolicy.EachPage && notePages is not null && notePages.TryGetValue(note.Id, out var page) && page != currentPage)
                { counter = settings.Start - 1; currentPage = page; }
                if (note.CustomMark is null) counter++;
                if (note.Id == noteId) return Format(counter, settings.NumberFormat);
            }
        }
        return Format(settings.Start, settings.NumberFormat);
    }

    public static string Format(int number, PageNumberFormat format) => ListNumbering.Format(number, format switch
    {
        PageNumberFormat.UpperRoman => ListMarkerStyle.UpperRoman, PageNumberFormat.LowerRoman => ListMarkerStyle.LowerRoman,
        PageNumberFormat.UpperLetter => ListMarkerStyle.UpperLetter, PageNumberFormat.LowerLetter => ListMarkerStyle.LowerLetter,
        _ => ListMarkerStyle.Decimal
    });
}
