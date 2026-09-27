using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    public PageSettings CurrentPageSettings => CurrentSection?.PageSettings ?? new();
    public DocumentSection? CurrentSection => SectionAt(Document, Index, Selection.Active);

    private static DocumentSection? SectionAt(FlowDocument document, DocumentIndex index, int offset)
    {
        DocumentSection? result = null;
        foreach (var section in document.Sections)
        {
            if (section.StartParagraphId != Guid.Empty && index.ById(section.StartParagraphId).Start > offset) break;
            result = section;
        }
        return result;
    }

    /// <summary>Changes the physical section at the caret (or the specified section) in one undo step.</summary>
    public void SetPageSettings(PageSettings settings, Guid? sectionId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (IsReadOnly) return;
        settings.Validate();
        ChangeSection(sectionId, section => section with { PageSettings = settings });
    }

    public void SetSectionNumbering(int? start, PageNumberFormat format = PageNumberFormat.Decimal, Guid? sectionId = null)
    {
        if (IsReadOnly) return;
        ChangeSection(sectionId, section => section with { PageNumberStart = start, PageNumberFormat = format });
    }

    private void ChangeSection(Guid? sectionId, Func<DocumentSection, DocumentSection> change)
    {
        var sections = Document.Sections;
        if (sections.IsEmpty) sections = [new DocumentSection()];
        var id = sectionId ?? CurrentSection?.Id ?? sections[0].Id;
        var position = -1;
        for (var i = 0; i < sections.Length; i++) if (sections[i].Id == id) { position = i; break; }
        if (position < 0) throw new ArgumentException("The physical section does not exist.", nameof(sectionId));
        var document = Document with { Sections = sections.SetItem(position, change(sections[position])) };
        document.Validate();
        Commit(document, Selection);
    }

    /// <summary>Starts a physical section at the selection; a mid-paragraph boundary splits that paragraph.</summary>
    public void InsertSectionBreak(SectionBreakKind kind, PageSettings? settings = null)
    {
        if (IsReadOnly) return;
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        settings?.Validate();
        var (document, caret) = PreparePageBoundary();
        var index = new DocumentIndex(document);
        var paragraph = index.At(caret).Paragraph;
        var current = SectionAt(document, index, caret);
        var sections = document.Sections;
        if (sections.IsEmpty) sections = [new DocumentSection()];
        var start = caret == 0 ? Guid.Empty : paragraph.Id;
        var existing = -1;
        for (var i = 0; i < sections.Length; i++) if (sections[i].StartParagraphId == start) existing = i;
        if (existing >= 0)
            sections = sections.SetItem(existing, sections[existing] with { BreakKind = kind, PageSettings = settings ?? sections[existing].PageSettings });
        else
        {
            var created = new DocumentSection { StartParagraphId = start, BreakKind = kind,
                PageSettings = settings ?? current?.PageSettings ?? new(), PageNumberFormat = current?.PageNumberFormat ?? PageNumberFormat.Decimal };
            sections = sections.Add(created).OrderBy(section => section.StartParagraphId == Guid.Empty ? 0 : index.ById(section.StartParagraphId).Start).ToImmutableArray();
        }
        document = document with { Sections = sections };
        document.Validate();
        Commit(document, new(caret, caret));
    }

    /// <summary>Removes a boundary; the preceding physical section's settings apply to the joined content.</summary>
    public void RemoveSection(Guid sectionId)
    {
        if (IsReadOnly) return;
        var position = -1;
        for (var i = 0; i < Document.Sections.Length; i++) if (Document.Sections[i].Id == sectionId) position = i;
        if (position < 0) throw new ArgumentException("The physical section does not exist.", nameof(sectionId));
        if (position == 0) throw new InvalidOperationException("The initial physical section cannot be removed.");
        Commit(Document with { Sections = Document.Sections.RemoveAt(position) }, Selection);
    }

    public void InsertPageBreak() => InsertPhysicalBreak(false);
    public void InsertColumnBreak() => InsertPhysicalBreak(true);

    private void InsertPhysicalBreak(bool column)
    {
        if (IsReadOnly) return;
        var (document, caret) = PreparePageBoundary();
        var paragraph = new DocumentIndex(document).At(caret).Paragraph;
        // An inserted break occupies a paragraph boundary, including at the start of a paragraph.
        // Keep the original paragraph identity on the empty prefix so section anchors remain attached.
        if (caret == Selection.Start)
        {
            var empty = paragraph with { Runs = [] };
            var tail = paragraph with { Id = Guid.NewGuid() };
            document = document.RewriteParagraphs(p => p.Id == paragraph.Id ? [empty, tail] : [p]);
            paragraph = tail;
            caret = new DocumentIndex(document).ById(tail.Id).Start;
        }
        var style = ChangeParagraphStyle(paragraph.Style, value => value with
        { PageBreakBefore = !column, ColumnBreakBefore = column }, new(document));
        document = document.ReplaceBlock(paragraph.Id, paragraph with { Style = style });
        document.Validate();
        Commit(document, new(caret, caret));
    }

    private (FlowDocument Document, int Caret) PreparePageBoundary()
    {
        var document = Document;
        var caret = Selection.Start;
        if (!Selection.IsEmpty)
            (document, caret) = ReplaceRange(document, Selection, [new Paragraph("", TypingStyle)]);
        document = DocumentSection.Reconcile(document);
        var index = new DocumentIndex(document);
        var at = index.At(caret);
        if (!DocumentSection.IsOutsideTable(document.Blocks, at.Paragraph.Id))
            throw new InvalidOperationException("Physical section and break boundaries cannot be inserted inside a table.");
        if (caret > at.Start)
        {
            var head = at.Paragraph with { Runs = at.Paragraph.Slice(0, caret - at.Start) };
            var tail = at.Paragraph with { Id = Guid.NewGuid(), Runs = at.Paragraph.Slice(caret - at.Start, at.End - caret),
                Style = FollowingParagraphStyle(at.Paragraph.Style) };
            document = document.RewriteParagraphs(p => p.Id == at.Paragraph.Id ? [head, tail] : [p]);
            caret = new DocumentIndex(document).ById(tail.Id).Start;
        }
        return (document, caret);
    }
}
