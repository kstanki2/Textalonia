using System.Collections.Immutable;
using Textalonia.Model;

namespace Textalonia.Editing;

public sealed partial class EditorSession
{
    /// <summary>Switches editing coordinates without changing content or the document-wide history.</summary>
    public void SwitchStory(Guid storyId)
    {
        if (storyId == ActiveStoryId) return;
        var projection = Document.GetStoryDocument(storyId);
        SetStorySelections(_storySelections.SetItem(ActiveStoryId, Selection));
        ActiveStoryId = storyId;
        SetDocument(Document, new DocumentIndex(projection));
        var saved = _storySelections.GetValueOrDefault(storyId);
        Selection = new(Snap(saved.Anchor), Snap(saved.Active));
        TypingStyle = Index.At(Selection.Active).Paragraph.StyleAt(Selection.Active - Index.At(Selection.Active).Start);
        BreakUndoGroup(); Revision++; LastEdit = new(Revision - 1, Revision, 0, 0, Index.Length, [], true); OnChanged();
    }

    private void SetStorySelections(ImmutableDictionary<Guid, TextSelection> selections)
    {
        if (ReferenceEquals(_storySelections, selections)) return;
        _retained.Add(selections, current: true); _retained.Remove(_storySelections, current: true);
        _storySelections = selections;
    }

    public void ReturnToBody() => SwitchStory(Guid.Empty);

    public void ActivateHeaderFooter(Guid sectionId, bool footer, HeaderFooterVariant variant = HeaderFooterVariant.Primary)
    {
        var sections = Document.Sections.IsEmpty ? ImmutableArray.Create(new DocumentSection()) : Document.Sections;
        var at = SectionPosition(sections, sectionId);
        var document = Document with { Sections = sections };
        var story = document.ResolveHeaderFooter(at, footer, variant);
        if (story is null)
        {
            if (IsReadOnly) return;
            story = new DocumentStory { Kind = footer ? DocumentStoryKind.Footer : DocumentStoryKind.Header };
            var settings = sections[at].HeaderFooter.WithReference(footer, variant, new() { LinkToPrevious = false, StoryId = story.Id });
            document = document with { Stories = document.Stories.Add(story.Id, story), Sections = sections.SetItem(at, sections[at] with { HeaderFooter = settings }) };
            document.Validate(); Commit(document, Selection, wholeDocument: true);
        }
        SwitchStory(story.Id);
    }

    public void SetHeaderFooterSettings(Guid sectionId, HeaderFooterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (IsReadOnly) return;
        var sections = Document.Sections.IsEmpty ? ImmutableArray.Create(new DocumentSection()) : Document.Sections;
        var at = SectionPosition(sections, sectionId);
        var document = Document with { Sections = sections.SetItem(at, sections[at] with { HeaderFooter = settings }) };
        document.Validate(); Commit(document, Selection, wholeDocument: true);
    }

    /// <summary>Unlinking clones the inherited rich story and every descendant identity.</summary>
    public void SetHeaderFooterLink(Guid sectionId, bool footer, HeaderFooterVariant variant, bool linked)
    {
        if (IsReadOnly) return;
        var sections = Document.Sections.IsEmpty ? ImmutableArray.Create(new DocumentSection()) : Document.Sections;
        var at = SectionPosition(sections, sectionId);
        var document = Document with { Sections = sections };
        var settings = sections[at].HeaderFooter;
        if (settings.GetReference(footer, variant).LinkToPrevious == linked) return;
        StoryReference reference;
        if (linked) reference = new();
        else
        {
            var inherited = document.ResolveHeaderFooter(at, footer, variant);
            var story = new DocumentStory { Kind = footer ? DocumentStoryKind.Footer : DocumentStoryKind.Header,
                Blocks = inherited is null ? [new Paragraph()] : BlockOperations.Clone(inherited.Blocks) };
            document = document with { Stories = document.Stories.Add(story.Id, story) };
            reference = new() { LinkToPrevious = false, StoryId = story.Id };
        }
        document = document with { Sections = sections.SetItem(at, sections[at] with { HeaderFooter = settings.WithReference(footer, variant, reference) }) };
        document.Validate(); Commit(document, Selection, wholeDocument: true);
    }

    public DocumentNote? InsertNote(DocumentNoteKind kind, string? customMark = null)
    {
        if (IsReadOnly) return null;
        if (ActiveStoryId != Guid.Empty) throw new InvalidOperationException("Notes can only be inserted in the main story.");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var story = new DocumentStory { Kind = kind == DocumentNoteKind.Footnote ? DocumentStoryKind.Footnote : DocumentStoryKind.Endnote };
        var note = new DocumentNote { StoryId = story.Id, Kind = kind, CustomMark = customMark };
        var descriptor = InlineDescriptor.Note(note.Id, customMark ?? (Document.Notes.Count(n => n.Kind == kind) +
            (kind == DocumentNoteKind.Footnote ? Document.FootnoteSettings.Start : Document.EndnoteSettings.Start)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var source = Document with { Stories = Document.Stories.Add(story.Id, story), Notes = Document.Notes.Add(note) };
        var paragraph = new Paragraph([new RichRun(descriptor, TypingStyle)]) { Style = Index.At(Selection.Start).Paragraph.Style };
        var (document, caret) = ReplaceRange(source, Selection, [paragraph]);
        document.Validate(); Commit(document, new(caret, caret), editedRange: Selection);
        SwitchStory(story.Id); return note;
    }

    public void ActivateNote(Guid noteId)
    {
        var note = Document.Notes.FirstOrDefault(n => n.Id == noteId) ?? throw new ArgumentException("The note does not exist.", nameof(noteId));
        SwitchStory(note.StoryId);
    }

    public void RemoveNote(Guid noteId)
    {
        if (IsReadOnly) return;
        var note = Document.Notes.FirstOrDefault(n => n.Id == noteId) ?? throw new ArgumentException("The note does not exist.", nameof(noteId));
        var document = Document.RewriteParagraphs(p => [p with { Runs = p.Runs.Where(r => r.Inline?.Payload is not NoteInlinePayload reference || reference.NoteId != noteId).ToImmutableArray() }]);
        document = document with { Notes = document.Notes.Remove(note), Stories = document.Stories.Remove(note.StoryId) };
        if (ActiveStoryId == note.StoryId) ReturnToBody();
        document.Validate(); Commit(document.PruneUnusedResources(), Selection, wholeDocument: true);
    }

    public void SetNoteSettings(DocumentNoteKind kind, NoteSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (IsReadOnly) return;
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        settings.Validate(kind);
        var document = kind == DocumentNoteKind.Footnote ? Document with { FootnoteSettings = settings } : Document with { EndnoteSettings = settings };
        Commit(document, Selection, wholeDocument: true);
    }

    private int SectionPosition(ImmutableArray<DocumentSection> sections, Guid id)
    {
        if (id == Guid.Empty) id = CurrentSection?.Id ?? sections[0].Id;
        for (var i = 0; i < sections.Length; i++) if (sections[i].Id == id) return i;
        throw new ArgumentException("The physical section does not exist.", nameof(id));
    }

    private static FlowDocument PruneDetachedNotes(FlowDocument document)
    {
        var referenced = new HashSet<Guid>();
        void Visit(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks) switch (block)
            {
                case Paragraph p: foreach (var run in p.Runs) if (run.Inline?.Payload is NoteInlinePayload note) referenced.Add(note.NoteId); break;
                case Section s: Visit(s.Blocks); break;
                case Table t: foreach (var row in t.Rows) foreach (var cell in row) { Visit(cell.Blocks); Visit(cell.MergeOriginalBlocks); } break;
            }
        }
        Visit(document.Blocks);
        var removed = document.Notes.Where(n => !referenced.Contains(n.Id)).ToArray();
        return removed.Length == 0 ? document : document with
        { Notes = document.Notes.Where(n => referenced.Contains(n.Id)).ToImmutableArray(), Stories = document.Stories.RemoveRange(removed.Select(n => n.StoryId)) };
    }
}
