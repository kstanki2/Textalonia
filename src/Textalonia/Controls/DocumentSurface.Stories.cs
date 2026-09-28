using System.Collections.Immutable;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Textalonia.Model;
using Textalonia.Layout;

namespace Textalonia.Controls;

public partial class DocumentSurface
{
    private FlowDocument? _noteProjectionSource, _noteProjection;
    private FlowDocument DisplayNoteMarks(FlowDocument source)
    {
        if (source.Notes.IsEmpty) { _noteProjectionSource = _noteProjection = null; return source; }
        if (ReferenceEquals(source, _noteProjectionSource)) return _noteProjection!;
        var marks = source.Notes.ToDictionary(note => note.Id, note => DocumentNoteNumbering.GetMark(source, note.Id));
        _noteProjection = source.RewriteParagraphs(paragraph => [paragraph with
        {
            Runs = paragraph.Runs.Select(run => run.Inline is { Payload: NoteInlinePayload note } inline && marks.TryGetValue(note.NoteId, out var mark)
                ? run with { Inline = inline with { AltText = mark } } : run).ToImmutableArray()
        }]);
        _noteProjectionSource = source;
        return _noteProjection;
    }

    private FlowDocument? _storyProjectionSource, _storyProjection;
    private Guid _storyProjectionId;
    private FlowDocument ProjectStory(FlowDocument source, Guid storyId)
    {
        if (!ReferenceEquals(_storyProjectionSource, source) || _storyProjectionId != storyId)
        {
            _storyProjectionSource = source; _storyProjectionId = storyId;
            _storyProjection = source.GetStoryDocument(storyId);
        }
        return _storyProjection!;
    }

    internal void ClearStoryProjections()
    {
        _noteProjectionSource = _noteProjection = _storyProjectionSource = _storyProjection = null;
    }

    private bool HandleStoryPointerPress(PointerPressedEventArgs e)
    {
        if (Editor is null || Editor.ViewMode != DocumentViewMode.PrintLayout ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        EnsureLayout(Bounds.Width);
        if (_pagedLayout is not { } pages) return false;
        var point = ToDocument(e.GetPosition(this));
        var region = pages.StoryRegions.LastOrDefault(r => r.Bounds.Contains(point));
        if (e.ClickCount < 2)
        {
            if (region is not null && region.StoryId == Editor.ActiveStoryId)
            {
                SetStoryInstanceContext(region);
            }
            return false;
        }
        var marker = pages.InlineVisuals().FirstOrDefault(v => v.Descriptor.Payload is NoteInlinePayload && v.Bounds.Contains(point));
        if (marker?.Descriptor.Payload is NoteInlinePayload noteMarker)
        {
            Editor.EditNote(noteMarker.NoteId); Focus(); e.Handled = true; return true;
        }
        if (region is not null)
        {
            if (region.Kind is DocumentStoryKind.Header or DocumentStoryKind.Footer)
            {
                var page = pages.Pages[region.PageIndex];
                var section = Editor.Document.Sections.FirstOrDefault(s => s.Id == page.SectionId);
                var first = pages.Pages.First(p => p.SectionId == page.SectionId).Index == page.Index;
                var variant = section?.HeaderFooter.DifferentFirstPage == true && first ? HeaderFooterVariant.First :
                    section?.HeaderFooter.DifferentOddEvenPages == true && (page.Index + 1) % 2 == 0 ? HeaderFooterVariant.Even : HeaderFooterVariant.Primary;
                Editor.EditHeaderFooter(region.Kind == DocumentStoryKind.Footer, variant, section?.Id ?? Guid.Empty);
            }
            Editor.ActivateStoryInstance(region.StoryId, region.PageIndex);
            SelectVisualCaret(pages.HitTestStoryCaret(region.StoryId, point, region.PageIndex), false);
            Focus(); e.Handled = true; return true;
        }
        var sheet = pages.Pages.FirstOrDefault(p => p.Bounds.Contains(point));
        if (sheet is null) return false;
        if (point.Y < sheet.ContentBounds.Top || point.Y > sheet.ContentBounds.Bottom)
        {
            var section = Editor.Document.Sections.FirstOrDefault(s => s.Id == sheet.SectionId);
            var first = pages.Pages.First(p => p.SectionId == sheet.SectionId).Index == sheet.Index;
            var variant = section?.HeaderFooter.DifferentFirstPage == true && first ? HeaderFooterVariant.First :
                section?.HeaderFooter.DifferentOddEvenPages == true && (sheet.Index + 1) % 2 == 0 ? HeaderFooterVariant.Even : HeaderFooterVariant.Primary;
            Editor.EditHeaderFooter(point.Y > sheet.ContentBounds.Bottom, variant, section?.Id ?? Guid.Empty);
            Editor.ActiveStoryPageIndex = sheet.Index;
            EnsureLayout(Bounds.Width);
            if (Editor.ActiveStoryId != Guid.Empty) SelectVisualCaret(GeometryHitTestCaret(e.GetPosition(this)), false);
            Focus(); e.Handled = true; return true;
        }
        if (Editor.ActiveStoryId != Guid.Empty) Editor.CloseStory();
        var position = pages.HitTest(point);
        var at = Editor.Session.Index.At(position);
        var offset = at.Start;
        foreach (var run in at.Paragraph.Runs)
        {
            if (offset == position && run.Inline?.Payload is NoteInlinePayload note)
            {
                Editor.EditNote(note.NoteId); Focus(); e.Handled = true; return true;
            }
            offset += run.Storage.Length;
        }
        return false;
    }

    private void SetStoryInstanceContext(StoryRegion region)
    {
        if (Editor is null || _pagedLayout is not { } pages) return;
        Editor.ActiveStoryPageIndex = region.PageIndex;
        if (region.Kind is not (DocumentStoryKind.Header or DocumentStoryKind.Footer)) return;
        var page = pages.Pages[region.PageIndex];
        var section = Editor.Document.Sections.FirstOrDefault(s => s.Id == page.SectionId);
        var first = pages.Pages.First(p => p.SectionId == page.SectionId).Index == page.Index;
        var variant = section?.HeaderFooter.DifferentFirstPage == true && first ? HeaderFooterVariant.First :
            section?.HeaderFooter.DifferentOddEvenPages == true && (page.Index + 1) % 2 == 0 ? HeaderFooterVariant.Even : HeaderFooterVariant.Primary;
        Editor.SetHeaderFooterInstance(section?.Id ?? Guid.Empty, region.Kind == DocumentStoryKind.Footer, variant, region.PageIndex);
    }

    private void DrawStoryOverlay(DrawingContext context)
    {
        if (Editor is null || Editor.ActiveStoryId == Guid.Empty) return;
        var pen = new Pen(Brushes.SlateGray, 1, DashStyle.Dash);
        foreach (var region in _pagedLayout?.StoryRegions.Where(r => r.StoryId == Editor.ActiveStoryId &&
                     (GeometryStoryPage < 0 || r.PageIndex == GeometryStoryPage)) ?? [])
            context.DrawRectangle(null, pen, region.Bounds.Inflate(2));
    }
}
