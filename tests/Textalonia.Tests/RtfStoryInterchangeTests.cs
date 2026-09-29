using System.Collections.Immutable;
using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class RtfStoryInterchangeTests
{
    [Fact]
    public void Independent_rtf_destinations_preserve_sections_variants_notes_and_page_fields()
    {
        const string rtf = "{\\rtf1\\ansi\\facingp\\ftnstart3\\ftnnrlc\\ftnrestart\\aendnotes\\sectd\\titlepg\\headery300\\footery450" +
            "{\\header Primary {\\field{\\*\\fldinst PAGE}{\\fldrslt 1}}}{\\headerf First}{\\headerl Even}{\\footer Footer}" +
            "\\pard Body\\chftn{\\footnote\\pard\\chftn {\\b Rich} note\\par}\\par\\sect\\sectd\\pard Second\\chftn{\\footnote\\ftnalt\\pard\\chftn End note\\par}\\par}";
        var document = DocumentFormats.Rtf.Parse(rtf);
        Assert.Equal("Body\uFFFC\nSecond\uFFFC", document.Text);
        Assert.Equal(2, document.Sections.Length);
        var first = document.Sections[0].HeaderFooter;
        Assert.True(first.DifferentFirstPage);
        Assert.True(first.DifferentOddEvenPages);
        Assert.Equal(20, first.HeaderDistance);
        Assert.Equal(30, first.FooterDistance);
        Assert.True(document.Sections[1].HeaderFooter.PrimaryHeader.LinkToPrevious);
        var header = document.Stories[first.PrimaryHeader.StoryId!.Value];
        var page = Assert.Single(Assert.IsType<Paragraph>(header.Blocks[0]).Runs.Where(r => r.Inline is not null));
        Assert.Equal(PageFieldKind.Page, Assert.IsType<PageFieldInlinePayload>(page.Inline!.Payload).Field);
        Assert.Equal("First", new FlowDocument(document.Stories[first.FirstHeader.StoryId!.Value].Blocks).Text);
        Assert.Equal("Even", new FlowDocument(document.Stories[first.EvenHeader.StoryId!.Value].Blocks).Text);
        Assert.Equal(3, document.FootnoteSettings.Start);
        Assert.Equal(PageNumberFormat.LowerRoman, document.FootnoteSettings.NumberFormat);
        Assert.Equal(NoteRestartPolicy.EachSection, document.FootnoteSettings.Restart);
        Assert.Equal(NotePlacement.SectionEnd, document.EndnoteSettings.Placement);
        Assert.Equal(new[] { DocumentNoteKind.Footnote, DocumentNoteKind.Endnote }, document.Notes.Select(n => n.Kind));
        var footnote = Assert.IsType<Paragraph>(document.Stories[document.Notes[0].StoryId].Blocks[0]);
        Assert.Equal("Rich note", footnote.Text);
        Assert.True(footnote.Runs[0].Style.Bold);
        document.Validate();
    }

    [Fact]
    public void Rich_stories_custom_marks_and_linked_sections_survive_two_round_trips()
    {
        var imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jB2kAAAAASUVORK5CYII=");
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph([new RichRun("Page "),
            new RichRun(new InlineDescriptor { Payload = new PageFieldInlinePayload(PageFieldKind.Page), AltText = "1" })]), Table.Create(1, 1)] };
        var even = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("Even", new() { FontFamily = "Courier New", Foreground = "#112233" })] };
        var footer = new DocumentStory { Kind = DocumentStoryKind.Footer, Blocks = [new Paragraph("Footer")] };
        var noteStory = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [new Paragraph([new RichRun("Rich note", new() { Bold = true }),
            new RichRun(new InlineDescriptor { Payload = new ImageInlinePayload("image"), Width = 32, Height = 20 })]), Table.Create(1, 1)] };
        var note = new DocumentNote { StoryId = noteStory.Id, CustomMark = "*" };
        var first = new Paragraph([new RichRun("First"), new RichRun(InlineDescriptor.Note(note.Id, "*"))]);
        var second = new Paragraph("Second");
        var document = new FlowDocument([first, second])
        {
            Stories = new[] { header, even, footer, noteStory }.ToImmutableDictionary(s => s.Id), Notes = [note],
            Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", new() { MediaType = "image/png", Data = imageBytes.ToImmutableArray() }),
            FootnoteSettings = new() { Start = 5, NumberFormat = PageNumberFormat.UpperLetter, Restart = NoteRestartPolicy.EachPage, Placement = NotePlacement.BelowText, SeparatorText = "---", ContinuationSeparatorText = "continued" },
            Sections = [new() { HeaderFooter = new() { DifferentOddEvenPages = true, HeaderDistance = 24, FooterDistance = 18,
                PrimaryHeader = new() { StoryId = header.Id, LinkToPrevious = false }, EvenHeader = new() { StoryId = even.Id, LinkToPrevious = false }, PrimaryFooter = new() { StoryId = footer.Id, LinkToPrevious = false } } },
                new() { StartParagraphId = second.Id, HeaderFooter = new() { DifferentOddEvenPages = true } }]
        };
        var originalText = document.Text;
        for (var pass = 0; pass < 2; pass++)
        {
            document = DocumentFormats.Rtf.Parse(DocumentFormats.Rtf.Serialize(document));
            Assert.Equal(originalText, document.Text);
            Assert.Equal("*", Assert.Single(document.Notes).CustomMark);
            Assert.True(document.Sections[1].HeaderFooter.PrimaryHeader.LinkToPrevious);
            Assert.Equal(24, document.Sections[0].HeaderFooter.HeaderDistance);
            Assert.Equal(18, document.Sections[0].HeaderFooter.FooterDistance);
            Assert.Equal("continued", document.FootnoteSettings.ContinuationSeparatorText);
            Assert.Equal(NoteRestartPolicy.EachPage, document.FootnoteSettings.Restart);
            Assert.Equal(NotePlacement.BelowText, document.FootnoteSettings.Placement);
            Assert.Equal(PageNumberFormat.UpperLetter, document.FootnoteSettings.NumberFormat);
            var restoredNote = document.Stories[document.Notes[0].StoryId];
            Assert.IsType<Table>(restoredNote.Blocks[1]);
            var noteParagraph = Assert.IsType<Paragraph>(restoredNote.Blocks[0]);
            Assert.Equal("Rich note\uFFFC", noteParagraph.Text);
            Assert.True(noteParagraph.Runs[0].Style.Bold);
            var image = Assert.IsType<ImageInlinePayload>(Assert.Single(noteParagraph.Runs.Where(r => r.Inline is not null)).Inline!.Payload);
            Assert.Equal(imageBytes, document.Resources[image.ResourceId].Data.ToArray());
            var restoredHeader = document.Stories[document.Sections[0].HeaderFooter.PrimaryHeader.StoryId!.Value];
            Assert.IsType<Table>(restoredHeader.Blocks[1]);
            Assert.Equal("Courier New", Assert.IsType<Paragraph>(document.Stories[document.Sections[0].HeaderFooter.EvenHeader.StoryId!.Value].Blocks[0]).Runs[0].Style.FontFamily);
            document.Validate();
        }
    }

    [Fact]
    public async Task Unsupported_nested_notes_are_diagnosed_in_tolerant_and_strict_modes()
    {
        const string rtf = "{\\rtf1{\\header Header{\\footnote nested note}}Body}";
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
        var result = await DocumentFormats.Rtf.LoadWithReportAsync(stream);
        Assert.Equal("Body", result.Document.Text);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "rtf.nested-note");
        stream.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Rtf.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public void Independent_custom_mark_is_consumed_once_and_removed_from_note_content()
    {
        const string rtf = "{\\rtf1 Body{\\super *}{\\footnote{\\super *}\\pard Custom note\\par} tail}";
        var document = DocumentFormats.Rtf.Parse(rtf);
        Assert.Equal("Body\uFFFC tail", document.Text);
        var note = Assert.Single(document.Notes);
        Assert.Equal("*", note.CustomMark);
        Assert.Equal("Custom note", new FlowDocument(document.Stories[note.StoryId].Blocks).Text);
    }

    [Fact]
    public void Sections_without_explicit_reset_inherit_headers_and_keep_distinct_identities()
    {
        var document = DocumentFormats.Rtf.Parse("{\\rtf1{\\header Header}First\\par\\sect Second\\par}");
        Assert.Equal(2, document.Sections.Length);
        Assert.NotEqual(document.Sections[0].Id, document.Sections[1].Id);
        Assert.True(document.Sections[1].HeaderFooter.PrimaryHeader.LinkToPrevious);
        document.Validate();
    }

    [Fact]
    public void Page_field_cached_formatting_survives_round_trip()
    {
        var document = DocumentFormats.Rtf.Parse("{\\rtf1{\\header{\\field{\\*\\fldinst NUMPAGES}{\\fldrslt{\\b\\fs30 12}}}}Body}");
        document = DocumentFormats.Rtf.Parse(DocumentFormats.Rtf.Serialize(document));
        var header = document.Stories[document.Sections[0].HeaderFooter.PrimaryHeader.StoryId!.Value];
        var run = Assert.Single(Assert.IsType<Paragraph>(header.Blocks[0]).Runs);
        Assert.True(run.Style.Bold);
        Assert.Equal(20, run.Style.FontSize);
        Assert.Equal("12", run.Inline!.AltText);
        Assert.Equal(PageFieldKind.NumPages, Assert.IsType<PageFieldInlinePayload>(run.Inline.Payload).Field);
    }

    [Fact]
    public void Document_page_defaults_and_section_overrides_import_separately()
    {
        const string rtf = "{\\rtf1\\paperw12000\\paperh16000\\margl1200\\margr1500\\margt1800\\margb2100\\gutter300\\margmirror" +
            "\\sectd\\pgwsxn14000\\pghsxn18000\\marglsxn900\\guttersxn450\\lndscpsxn First\\par\\sect\\sectd Second\\par}";
        var document = DocumentFormats.Rtf.Parse(rtf);
        Assert.Equal(2, document.Sections.Length);
        var first = document.Sections[0].PageSettings;
        Assert.Equal(14000 / 15d, first.Width);
        Assert.Equal(18000 / 15d, first.Height);
        Assert.Equal(900 / 15d, first.Margins.Left);
        Assert.Equal(1500 / 15d, first.Margins.Right);
        Assert.Equal(450 / 15d, first.Gutter);
        Assert.Equal(PageOrientation.Landscape, first.Orientation);
        Assert.True(first.MirrorMargins);
        var second = document.Sections[1].PageSettings;
        Assert.Equal(12000 / 15d, second.Width);
        Assert.Equal(16000 / 15d, second.Height);
        Assert.Equal(1200 / 15d, second.Margins.Left);
        Assert.Equal(1800 / 15d, second.Margins.Top);
        Assert.Equal(2100 / 15d, second.Margins.Bottom);
        Assert.Equal(300 / 15d, second.Gutter);
        Assert.Equal(PageOrientation.Portrait, second.Orientation);
        Assert.True(second.MirrorMargins);
    }

    [Fact]
    public async Task Supported_page_geometry_round_trips_without_conversion_loss()
    {
        var first = new Paragraph("First");
        var second = new Paragraph("Second");
        var document = new FlowDocument([first, second])
        {
            Sections = [new() { PageSettings = new() { Width = 760, Height = 1120, Orientation = PageOrientation.Landscape,
                Margins = new(54, 60, 72, 78), Gutter = 18, MirrorMargins = true } },
                new() { StartParagraphId = second.Id, PageSettings = new() { Width = 816, Height = 1056,
                    Margins = new(90, 96, 102, 108), Gutter = 12 } }]
        };
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Rtf.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(saved.Report.Diagnostics);
        stream.Position = 0;
        var restored = await DocumentFormats.Rtf.LoadAsync(stream);
        Assert.Equal(document.Sections.Select(s => s.PageSettings), restored.Sections.Select(s => s.PageSettings));
    }

    [Fact]
    public async Task Unsupported_page_decoration_is_reported_and_strict_export_rejects_it()
    {
        var document = new FlowDocument([new Paragraph("Body")])
        {
            Sections = [new() { PageSettings = new() { Background = "#FFEEDD" } }]
        };
        using var stream = new MemoryStream();
        var saved = await DocumentFormats.Rtf.SaveWithReportAsync(document, stream);
        Assert.Contains(saved.Report.Diagnostics, diagnostic => diagnostic.Code == "rtf.physical-page-settings");
        using var strict = new MemoryStream();
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Rtf.SaveWithReportAsync(document, strict,
            new() { Mode = ConversionMode.Strict }));
    }
}
