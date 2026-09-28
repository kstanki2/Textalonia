using System.Collections.Immutable;
using Avalonia;
using Textalonia.Controls;
using Textalonia.Model;

namespace Textalonia.Layout;

public sealed partial class PaginationEngine
{
    private sealed partial class Builder
    {
        private sealed record StoryMeasure(DocumentStory Story, CellContent Content, double Height);
        private sealed class PendingNote(DocumentNote note, Guid sectionId, StoryMeasure content)
        {
            public DocumentNote Note = note;
            public Guid SectionId = sectionId;
            public StoryMeasure Content = content;
            public double Offset;
        }
        private sealed record NotePart(PendingNote Note, int Page, double From, double To, double Height, bool Continued);
        private readonly List<LineFragment> _storyFragments = [];
        private readonly List<StoryRegion> _storyRegions = [];
        private readonly List<(Guid StoryId, int Page, TableCellVisual Cell)> _storyCells = [];
        private readonly List<string> _layoutDiagnostics = [];
        private readonly Queue<PendingNote> _pendingNotes = [];
        private readonly List<NotePart> _noteParts = [];
        private readonly Dictionary<int, double> _noteHeights = [];
        private readonly Dictionary<int, double> _noteSeparatorHeights = [];
        private readonly Dictionary<Guid, (Guid Section, int Page)> _noteOwners = [];
        private readonly Dictionary<Guid, DocumentNote> _notesById = document.Notes.ToDictionary(n => n.Id);
        private readonly HashSet<Guid> _placedEndnotes = [];
        private PageContext? _storyContext;
        private Guid _displayStoryId;
        private bool _placingEndnotes;
        private const double SeparatorHeight = 20;

        private void ResetStories()
        {
            _storyFragments.Clear(); _storyRegions.Clear(); _storyCells.Clear(); _layoutDiagnostics.Clear();
            _pendingNotes.Clear(); _noteParts.Clear(); _noteHeights.Clear(); _noteSeparatorHeights.Clear(); _noteOwners.Clear(); _placedEndnotes.Clear();
            _storyContext = null; _displayStoryId = Guid.Empty; _placingEndnotes = false;
        }

        private PageContext Context(int page) => new(page, _pages[page].Number, _pages.Count,
            _pages[page].Section.Id, _pages.Count(p => p.Section.Id == _pages[page].Section.Id));

        private StoryMeasure MeasureStory(DocumentStory story, double width, int page)
        {
            var oldIndex = _index; var oldContext = _storyContext; var oldStoryId = _displayStoryId;
            _index = new(document.GetStoryDocument(story.Id)); _storyContext = Context(page); _displayStoryId = story.Id;
            try
            {
                var content = new CellContent { Table = null!, Width = width };
                var height = MeasureLocal(story.Blocks, 0, 0, width, content);
                return new(story, content, Math.Max(1, height));
            }
            finally { _index = oldIndex; _storyContext = oldContext; _displayStoryId = oldStoryId; }
        }

        private Paragraph DisplayReferenceMarks(Paragraph paragraph)
        {
            var changed = false;
            var runs = paragraph.Runs.Select(run =>
            {
                if (run.Inline is not { Payload: NoteInlinePayload reference } inline || !_noteOwners.ContainsKey(reference.NoteId)) return run;
                var mark = Mark(_notesById[reference.NoteId]);
                if (inline.AltText == mark) return run;
                changed = true; return run with { Inline = inline with { AltText = mark } };
            }).ToImmutableArray();
            return changed ? paragraph with { Runs = runs } : paragraph;
        }

        private Paragraph DisplayParagraph(Paragraph paragraph, Guid? storyId = null)
        {
            if (_storyContext is not { } context || !paragraph.Runs.Any(r => r.Inline?.Payload is PageFieldInlinePayload)) return paragraph;
            var owner = storyId ?? _displayStoryId;
            var index = storyId is null ? _index : document.GetStoryIndex(owner);
            var paragraphStart = index.ById(paragraph.Id).Start;
            var locked = document.Fields.Where(f => f.IsLocked && f.Start.StoryId == owner)
                .Select(f => (Start: f.Start.Resolve(document) - paragraphStart, End: f.End.Resolve(document) - paragraphStart))
                .Where(range => range.Start < paragraph.Length && range.End > 0).ToArray();
            var localOffset = 0;
            return paragraph with { Runs = paragraph.Runs.Select(run =>
            {
                var offset = localOffset; localOffset += run.Text.Length;
                return run.Inline is { Payload: PageFieldInlinePayload field } inline && !locked.Any(r => offset >= r.Start && offset < r.End) ?
                run with { Inline = inline with { AltText = field.Field switch
                {
                    PageFieldKind.Page => NumberText(context.PageNumber, _pages[context.PageIndex].Section.PageNumberFormat),
                    PageFieldKind.NumPages => context.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    PageFieldKind.SectionPages => context.SectionPageCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => inline.AltText
                } } } : run;
            }).ToImmutableArray() };
        }

        private static LineFragment ShiftStoryFragment(LineFragment f, Vector shift) => f with
        {
            Bounds = f.Bounds.Translate(shift), Clip = f.Clip.Translate(shift), Origin = f.Origin + shift,
            Baseline = f.Baseline + shift.Y, ColumnBounds = f.ColumnBounds.Translate(shift)
        };

        private void EmitStory(StoryMeasure measure, int page, Rect region, double from = 0,
            double to = double.PositiveInfinity, bool continued = false, string? marker = null, string? separator = null)
        {
            var content = measure.Content;
            var lines = content.Lines.Where(l => l.Y >= from - .01 && l.Y < to - .01).ToArray();
            var start = lines.Select(l => l.Position.Start + l.Line.Start).DefaultIfEmpty(0).Min();
            var end = lines.Select(l => l.Position.Start + l.Line.End).DefaultIfEmpty(start).Max();
            _storyRegions.Add(new(measure.Story.Id, measure.Story.Kind, page, region, start, end, continued, marker, Context(page))
                { SeparatorText = separator });
            var shift = new Vector(region.Left, region.Top - from);
            var storyDocument = document.GetStoryDocument(measure.Story.Id);
            foreach (var local in lines)
            {
                var list = local.Line.Start == 0 && (local != lines[0] || marker is null)
                    ? ListNumbering.GetMarker(storyDocument, local.Position.Paragraph.Id) : null;
                var origin = new Point(local.X + shift.X, local.Y + shift.Y);
                _storyFragments.Add(new()
                {
                    StoryKey = measure.Story.Id, ParagraphId = local.Position.Paragraph.Id,
                    SectionId = _pages[page].Section.Id, PageIndex = page, ColumnIndex = 0,
                    TextStart = local.Position.Start + local.Line.Start, TextEnd = local.Position.Start + local.Line.End,
                    Bounds = new(origin, new Size(Math.Max(1, local.Width), local.Line.Height)),
                    Clip = local.Clip is { } clip ? region.Intersect(clip.Translate(shift)) : region,
                    Baseline = origin.Y + local.Line.Baseline, Measurement = local.Measurement, Line = local.Line,
                    Position = local.Position, Origin = origin, ColumnBounds = region,
                    SourceStart = local.Position.Start + local.Measurement.Offset + local.Line.Window.Start,
                    Marker = list?.Text ?? (local == lines[0] ? marker : null),
                    MarkerDefinition = list?.LevelDefinition,
                    MarkerStyle = list is null ? null : _resolver.ResolveListMarkerStyle(local.Position.Paragraph, list.LevelDefinition)
                });
            }
            foreach (var decoration in content.Decorations)
            {
                var shifted = decoration with { Bounds = decoration.Bounds.Translate(shift), Clip = region };
                if (shifted.Bounds.Intersects(region)) _decorations.Add((page, shifted));
            }
            foreach (var cell in content.Cells)
            {
                var shifted = cell with { Bounds = cell.Bounds.Translate(shift), Clip = region };
                if (shifted.Bounds.Intersects(region)) _storyCells.Add((measure.Story.Id, page, shifted));
            }
        }

        private NoteSettings Settings(DocumentNote note) => note.Kind == DocumentNoteKind.Footnote ? document.FootnoteSettings : document.EndnoteSettings;
        private string Mark(DocumentNote note)
        {
            if (note.CustomMark is { } mark) return mark;
            var settings = Settings(note);
            var owner = _noteOwners[note.Id];
            var number = settings.Start;
            foreach (var pair in _noteOwners)
            {
                if (pair.Key == note.Id) break;
                var preceding = _notesById[pair.Key];
                if (preceding.Kind == note.Kind && preceding.CustomMark is null &&
                    (settings.Restart == NoteRestartPolicy.Continuous ||
                    settings.Restart == NoteRestartPolicy.EachPage && pair.Value.Page == owner.Page ||
                    settings.Restart == NoteRestartPolicy.EachSection && pair.Value.Section == owner.Section)) number++;
            }
            return NumberText(number, settings.NumberFormat);
        }

        private IEnumerable<DocumentNote> NotesIn(Paragraph paragraph, int start, int end)
        {
            var position = 0;
            foreach (var run in paragraph.Runs)
            {
                if (position >= start && position < end && run.Inline?.Payload is NoteInlinePayload reference && !_noteOwners.ContainsKey(reference.NoteId))
                    yield return _notesById[reference.NoteId];
                position += run.Storage.Length;
            }
        }

        private double EstimateNoteReservation(IEnumerable<LocalLine> lines)
        {
            var notes = lines.SelectMany(l => NotesIn(l.Position.Paragraph, l.Line.Start, l.Line.End))
                .Where(n => Settings(n).Placement is NotePlacement.PageBottom or NotePlacement.BelowText).DistinctBy(n => n.Id).ToArray();
            if (notes.Length == 0) return 0;
            var height = _noteHeights.GetValueOrDefault(_pages.Count - 1) == 0 ? SeparatorHeight : 0;
            foreach (var note in notes)
            {
                var measure = MeasureStory(document.Stories[note.StoryId], Page.Body.Width, _pages.Count - 1);
                height += Math.Min(measure.Height, Math.Max(FirstBandHeight(measure, 0), Page.Body.Height * .45 / notes.Length));
            }
            return height;
        }

        // References are committed once, after their owning line has a physical page. Footnote
        // capacity only decreases while filling a sheet, so no fixpoint or oscillating retry exists.
        private void PrepareNotes(Paragraph paragraph, int start, int end, double lineHeight)
        {
            var notes = NotesIn(paragraph, start, end).ToArray();
            if (notes.Length == 0) return;
            if (options.Draft)
            { foreach (var note in notes) _noteOwners[note.Id] = (_section.Id, _pages.Count - 1); return; }
            var measured = notes.Select(note => new PendingNote(note, _section.Id,
                MeasureStory(document.Stories[note.StoryId], Page.Body.Width, _pages.Count - 1))).ToArray();
            double Desired() => measured.Where(n => Settings(n.Note).Placement is NotePlacement.PageBottom or NotePlacement.BelowText)
                .Sum(n => Math.Min(n.Content.Height, Math.Max(FirstBandHeight(n.Content, 0), Page.Body.Height * .45 / Math.Max(1, notes.Length)))) +
                (measured.Any(n => Settings(n.Note).Placement is NotePlacement.PageBottom or NotePlacement.BelowText) && _noteHeights.GetValueOrDefault(_pages.Count - 1) == 0 ? SeparatorHeight : 0);
            var occupiedBottom = _fragments.Where(f => f.PageIndex == _pages.Count - 1).Select(f => f.Bounds.Bottom).DefaultIfEmpty(_y).Max();
            if (!options.Draft && Math.Max(_y + lineHeight, occupiedBottom) + Desired() > Column.Bottom + .01 &&
                (_y > Column.Top + .01 || occupiedBottom > Column.Top + .01))
            {
                Advance(true);
                occupiedBottom = _y;
                // Different sections can change note width; a physical page keeps its section's width.
                measured = notes.Select(note => new PendingNote(note, _section.Id,
                    MeasureStory(document.Stories[note.StoryId], Page.Body.Width, _pages.Count - 1))).ToArray();
            }
            foreach (var note in notes) _noteOwners[note.Id] = (_section.Id, _pages.Count - 1);
            foreach (var note in measured)
                if (Settings(note.Note).Placement is NotePlacement.PageBottom or NotePlacement.BelowText)
                {
                    var available = Math.Max(1, Page.Body.Bottom - Math.Max(_y + lineHeight, occupiedBottom) - _noteHeights.GetValueOrDefault(_pages.Count - 1));
                    var separator = _noteHeights.GetValueOrDefault(_pages.Count - 1) == 0 ? SeparatorHeight : 0;
                    var budget = Math.Max(FirstBandHeight(note.Content, note.Offset) + separator, Page.Body.Height * .45 / Math.Max(1, notes.Length));
                    AddNotePart(note, Math.Min(available, budget));
                    if (note.Offset + .01 < note.Content.Height) _pendingNotes.Enqueue(note);
                }
        }

        // Split only at complete line bands, including across adjacent table cells. An oversized
        // atomic line is clipped once and consumed, which guarantees progress even on tiny paper.
        private static double FirstBandHeight(StoryMeasure measure, double from)
        {
            var lines = measure.Content.Lines.Where(l => l.Y >= from - .01).ToArray();
            if (lines.Length == 0) return Math.Max(1, measure.Height - from);
            var firstY = lines.Min(l => l.Y);
            return Math.Max(1, lines.Where(l => Math.Abs(l.Y - firstY) < .01).Max(l => l.Y + l.Line.Height) - from);
        }

        private static double Cut(StoryMeasure measure, double from, double budget)
        {
            var cut = Math.Min(measure.Height, from + Math.Max(1, budget));
            bool changed;
            do
            {
                changed = false;
                foreach (var line in measure.Content.Lines)
                    if (line.Y >= from - .01 && line.Y < cut - .01 && line.Y + line.Line.Height > cut + .01)
                    { cut = Math.Max(from, line.Y); changed = true; }
            } while (changed && cut > from + .01);
            if (cut <= from + .01)
                cut = Math.Min(measure.Height, measure.Content.Lines.Where(l => l.Y >= from - .01)
                    .Select(l => l.Y + l.Line.Height).DefaultIfEmpty(measure.Height).Min());
            return Math.Max(from + .01, cut);
        }

        private void AddNotePart(PendingNote note, double budget)
        {
            var page = _pages.Count - 1;
            var oldHeight = _noteHeights.GetValueOrDefault(page);
            var separator = oldHeight == 0 ? Math.Min(SeparatorHeight, Math.Max(0, budget - 1)) : 0;
            if (oldHeight == 0) _noteSeparatorHeights[page] = separator;
            var cut = Cut(note.Content, note.Offset, Math.Max(1, budget - separator));
            var height = Math.Max(1, Math.Min(cut - note.Offset, Math.Max(1, budget - separator)));
            if (cut - note.Offset > height + .01)
                _layoutDiagnostics.Add($"An oversized atomic line in note {note.Note.Id} on page {page + 1} is clipped once to guarantee pagination progress.");
            _noteParts.Add(new(note, page, note.Offset, cut, height, note.Offset > 0));
            note.Offset = cut;
            var reserved = _noteHeights[page] = Math.Min(Math.Max(1, Page.Body.Height - 1), oldHeight + separator + height);
            if (!options.Draft)
                for (var i = 0; i < Page.Columns.Length; i++) Page.Columns[i] = Page.Columns[i].WithHeight(Math.Max(1, Page.Body.Bottom - reserved - Page.Columns[i].Top));
        }

        private void ContinueFootnotes()
        {
            if (_pendingNotes.Count == 0 || Page.Blank) return;
            var first = _pendingNotes.Peek();
            var capacity = Math.Min(Math.Max(1, Page.Body.Height - 20), Math.Max(Page.Body.Height * .45,
                FirstBandHeight(first.Content, first.Offset) + SeparatorHeight));
            while (_pendingNotes.TryPeek(out var note) && capacity > 0)
            {
                var before = _noteHeights.GetValueOrDefault(_pages.Count - 1);
                AddNotePart(note, capacity);
                capacity -= _noteHeights.GetValueOrDefault(_pages.Count - 1) - before;
                if (note.Offset + .01 >= note.Content.Height) _pendingNotes.Dequeue(); else break;
            }
        }

        private void CompleteNotes()
        {
            PlaceEndnotes(_section.Id, sectionEnd: true);
            PlaceEndnotes(Guid.Empty, sectionEnd: false);
            while (_pendingNotes.Count != 0) NewPage();
        }

        private void PlaceEndnotes(Guid sectionId, bool sectionEnd)
        {
            if (options.Draft || _placingEndnotes) return;
            var notes = _noteOwners.Keys.Select(id => _notesById[id]).Where(n => _noteOwners.TryGetValue(n.Id, out var owner) && !_placedEndnotes.Contains(n.Id) &&
                (sectionEnd ? Settings(n).Placement == NotePlacement.SectionEnd && owner.Section == sectionId :
                    Settings(n).Placement == NotePlacement.DocumentEnd)).ToArray();
            if (notes.Length == 0) return;
            _placingEndnotes = true;
            try
            {
                foreach (var note in notes)
                {
                    _placedEndnotes.Add(note.Id);
                    var measure = MeasureStory(document.Stories[note.StoryId], Column.Width, _pages.Count - 1);
                    var offset = 0d; var continued = false;
                    while (offset + .01 < measure.Height)
                    {
                        if (Remaining <= SeparatorHeight + 1 && !AtTop) Advance();
                        var separator = continued ? Settings(note).ContinuationSeparatorText : Settings(note).SeparatorText;
                        _y += Math.Min(SeparatorHeight, Math.Max(0, Remaining - 1));
                        var available = Math.Max(1, Remaining);
                        var cut = Cut(measure, offset, available);
                        var height = Math.Min(cut - offset, available);
                        EmitStory(measure, _pages.Count - 1, new Rect(Column.Left, _y, Column.Width, height), offset, cut, continued,
                            continued ? null : Mark(note), separator);
                        _y += height; offset = cut; continued = true;
                        if (offset + .01 < measure.Height) Advance();
                    }
                }
            }
            finally { _placingEndnotes = false; }
        }

        private void CompleteStories()
        {
            if (!options.Draft && !document.Notes.IsEmpty && document.Sections.Any(s => s.PageSettings.BalanceColumns && s.PageSettings.Columns.Length > 1))
                _layoutDiagnostics.Add("Column balancing is disabled for sections in documents containing notes; forward note reservation determines column breaks.");
            foreach (var grouping in _noteParts.GroupBy(p => p.Page))
            {
                var sheet = _pages[grouping.Key];
                var belowText = grouping.All(part => Settings(part.Note.Note).Placement == NotePlacement.BelowText);
                var top = belowText ? _fragments.Concat(_storyFragments).Where(f => f.PageIndex == grouping.Key).Select(f => f.Bounds.Bottom).DefaultIfEmpty(sheet.Body.Top).Max() :
                    sheet.Body.Bottom - _noteHeights[grouping.Key];
                top += _noteSeparatorHeights.GetValueOrDefault(grouping.Key);
                var first = true;
                foreach (var part in grouping)
                {
                    EmitStory(part.Note.Content, part.Page, new Rect(sheet.Body.Left, top, sheet.Body.Width, part.Height),
                        part.From, part.To, part.Continued, part.Continued ? null : Mark(part.Note.Note),
                        first && _noteSeparatorHeights.GetValueOrDefault(grouping.Key) >= 12 ? part.Continued ? Settings(part.Note.Note).ContinuationSeparatorText : Settings(part.Note.Note).SeparatorText : null);
                    first = false; top += part.Height;
                }
            }
            for (var i = 0; i < _pages.Count; i++)
            {
                if (options.Draft) break;
                var sheet = _pages[i];
                if (sheet.Blank) continue;
                var sectionIndex = Array.FindIndex(document.Sections.ToArray(), s => s.Id == sheet.Section.Id);
                if (sectionIndex < 0) continue;
                var settings = sheet.Section.HeaderFooter;
                var first = !_pages.Take(i).Any(p => p.Section.Id == sheet.Section.Id);
                var variant = first && settings.DifferentFirstPage ? HeaderFooterVariant.First :
                    settings.DifferentOddEvenPages && (i + 1) % 2 == 0 ? HeaderFooterVariant.Even : HeaderFooterVariant.Primary;
                for (var footer = 0; footer < 2; footer++)
                {
                    var story = document.ResolveHeaderFooter(sectionIndex, footer != 0, variant);
                    if (story is null) continue;
                    var measure = MeasureStory(story, sheet.Body.Width, i);
                    var available = footer == 0 ? Math.Max(0, sheet.Body.Top - settings.HeaderDistance) :
                        Math.Max(0, sheet.Height - settings.FooterDistance - sheet.Body.Bottom);
                    var height = Math.Min(measure.Height, available);
                    var top = footer == 0 ? settings.HeaderDistance : sheet.Height - settings.FooterDistance - height;
                    EmitStory(measure, i, new Rect(sheet.Body.Left, top, sheet.Body.Width, height));
                    if (measure.Height > available + .01)
                        _layoutDiagnostics.Add($"{(footer == 0 ? "Header" : "Footer")} {story.Id} on page {i + 1} is clipped to its page margin.");
                }
            }
            RefreshDisplay(_fragments); RefreshDisplay(_storyFragments);
            for (var i = 0; i < _storyRegions.Count; i++)
                _storyRegions[i] = _storyRegions[i] with { Context = Context(_storyRegions[i].PageIndex) };
        }

        private void RefreshDisplay(List<LineFragment> fragments)
        {
            // Page fields and note markers occupy fixed atomic slots. Updating their display
            // after pagination cannot change line breaks or feed page-count oscillation.
            for (var i = 0; i < fragments.Count; i++)
            {
                var fragment = fragments[i];
                _storyContext = Context(fragment.PageIndex);
                var source = DisplayParagraph(DisplayReferenceMarks(fragment.Measurement.Source), fragment.StoryKey);
                if (source == fragment.Measurement.Source) continue;
                var measurement = Measure(source, fragment.Measurement.Width, fragment.Measurement.Offset);
                var line = measurement.Lines.FirstOrDefault(l => l.Start == fragment.Line.Start && l.End == fragment.Line.End);
                if (line is null)
                {
                    _layoutDiagnostics.Add("An atomic field changed line geometry; its original measured display was retained.");
                    continue;
                }
                fragments[i] = fragment with { Measurement = measurement, Line = line,
                    SourceStart = fragment.Position.Start + measurement.Offset + line.Window.Start };
            }
            _storyContext = null;
        }

    }
}
