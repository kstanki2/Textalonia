using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class StoryInterchangeTests
{
    private static FlowDocument Example()
    {
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph([new RichRun("Page "), new RichRun(InlineDescriptor.PageField(PageFieldKind.Page))]), Table.Create(1, 1)] };
        var first = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph("first")] };
        var even = new DocumentStory { Kind = DocumentStoryKind.Footer, Blocks = [new Paragraph("even")] };
        var foot = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [new Paragraph([new RichRun("rich note", new TextStyle { Bold = true })]), Table.Create(1, 1)] };
        var end = new DocumentStory { Kind = DocumentStoryKind.Endnote, Blocks = [new Paragraph("end note")] };
        var note = new DocumentNote { StoryId = foot.Id, CustomMark = "*" };
        var endnote = new DocumentNote { StoryId = end.Id, Kind = DocumentNoteKind.Endnote };
        var p = new Paragraph([new RichRun("body "), new RichRun(InlineDescriptor.Note(note.Id, "*")), new RichRun(InlineDescriptor.Note(endnote.Id))]);
        var p2 = new Paragraph("second section");
        return new FlowDocument([p, p2])
        {
            Stories = new[] { header, first, even, foot, end }.ToImmutableDictionary(s => s.Id), Notes = [note, endnote],
            Sections = [new() { HeaderFooter = new() {
                PrimaryHeader = new() { StoryId = header.Id, LinkToPrevious = false },
                FirstHeader = new() { StoryId = first.Id, LinkToPrevious = false },
                EvenFooter = new() { StoryId = even.Id, LinkToPrevious = false },
                DifferentFirstPage = true, DifferentOddEvenPages = true, HeaderDistance = 24, FooterDistance = 30 } },
                new() { StartParagraphId = p2.Id, HeaderFooter = new() { DifferentOddEvenPages = true } }],
            FootnoteSettings = new() { NumberFormat = PageNumberFormat.LowerRoman, Start = 3, Restart = NoteRestartPolicy.EachSection, Placement = NotePlacement.BelowText, SeparatorText = "separator", ContinuationSeparatorText = "continued" },
            EndnoteSettings = new() { Placement = NotePlacement.SectionEnd, NumberFormat = PageNumberFormat.UpperLetter, Start = 2 }
        };
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Native_and_xaml_preserve_all_stories_and_references(bool xaml)
    {
        var original = Example();
        TextDocumentFormat format = xaml ? DocumentFormats.Xaml : DocumentFormats.Json;
        var encoded = format.Serialize(original);
        var loaded = format.Parse(encoded);
        Assert.Equal(DocumentFormats.Json.Serialize(original), DocumentFormats.Json.Serialize(loaded));
        Assert.Contains(xaml ? "Version=\"6\"" : "\"version\": 11", encoded);
    }

    [Fact]
    public async Task Docx_preserves_rich_stories_links_notes_settings_and_page_fields()
    {
        var original = Example();
        using var stream = new MemoryStream();
        var exported = await DocumentFormats.Docx.SaveWithReportAsync(original, stream);
        Assert.DoesNotContain(exported.Report.Diagnostics, d => d.Code is "conversion.stories" or "conversion.page-sections" or "docx.inline-fallback");
        stream.Position = 0;
        var result = await DocumentFormats.Docx.LoadWithReportAsync(stream);
        var loaded = result.Document;
        Assert.DoesNotContain(result.Report.Diagnostics, d => d.Code.StartsWith("docx.note") || d.Code is "docx.page-layout" or "docx.run-content" or "docx.field");
        Assert.Equal(2, loaded.Sections.Length);
        Assert.True(loaded.Sections[1].HeaderFooter.PrimaryHeader.LinkToPrevious);
        Assert.Equal(loaded.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary)!.Id, loaded.ResolveHeaderFooter(1, false, HeaderFooterVariant.Primary)!.Id);
        Assert.Equal(24, loaded.Sections[0].HeaderFooter.HeaderDistance);
        Assert.Equal(30, loaded.Sections[0].HeaderFooter.FooterDistance);
        Assert.True(loaded.Sections[0].HeaderFooter.DifferentFirstPage);
        Assert.True(loaded.Sections[0].HeaderFooter.DifferentOddEvenPages);
        Assert.Equal(original.FootnoteSettings, loaded.FootnoteSettings);
        Assert.Equal(original.EndnoteSettings, loaded.EndnoteSettings);
        Assert.Equal(2, loaded.Notes.Length);
        var note = Assert.Single(loaded.Notes, n => n.Kind == DocumentNoteKind.Footnote);
        Assert.Equal("*", note.CustomMark);
        var noteBlocks = loaded.Stories[note.StoryId].Blocks;
        Assert.Equal("rich note", Assert.IsType<Paragraph>(noteBlocks[0]).Text);
        Assert.True(Assert.IsType<Paragraph>(noteBlocks[0]).Runs[0].Style.Bold);
        Assert.IsType<Table>(noteBlocks[1]);
        var header = loaded.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary)!;
        Assert.Contains(Assert.IsType<Paragraph>(header.Blocks[0]).Runs, r => r.Inline?.Payload is PageFieldInlinePayload { Field: PageFieldKind.Page });
        Assert.IsType<Table>(header.Blocks[1]);
    }

    [Fact]
    public async Task Docx_secondary_image_relationships_are_owned_by_the_story_part()
    {
        var image = new InlineDescriptor { AltText = "story image", Payload = new ImageInlinePayload("image") };
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [new Paragraph([new RichRun(image)])] };
        var document = FlowDocument.FromText("body") with
        {
            Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(header.Id, header),
            Sections = [new() { HeaderFooter = new() { PrimaryHeader = new() { StoryId = header.Id, LinkToPrevious = false } } }],
            Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("image", new() { MediaType = "image/png", Data = [1, 2, 3] })
        };
        using var stream = new MemoryStream();
        var exported = await DocumentFormats.Docx.SaveWithReportAsync(document, stream);
        Assert.DoesNotContain(exported.Report.Diagnostics, d => d.Code == "conversion.unused-resource");
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true)) Assert.NotNull(archive.GetEntry("word/_rels/header1.xml.rels"));
        stream.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        var story = loaded.ResolveHeaderFooter(0, false, HeaderFooterVariant.Primary)!;
        var imported = Assert.IsType<ImageInlinePayload>(Assert.IsType<Paragraph>(story.Blocks[0]).Runs[0].Inline!.Payload);
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded.Resources[imported.ResourceId].Data.ToArray());
    }

    [Fact]
    public async Task Section_after_table_retains_boundary_without_an_extra_visible_paragraph()
    {
        var paragraph = new Paragraph("next");
        var original = new FlowDocument([Table.Create(1, 1), paragraph]) { Sections = [new(), new() { StartParagraphId = paragraph.Id }] };
        using var stream = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(original, stream); stream.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        Assert.Equal(2, loaded.Blocks.Length);
        Assert.Equal(2, loaded.Sections.Length);
        Assert.Equal(loaded.Blocks[1].Id, loaded.Sections[1].StartParagraphId);
    }

    [Fact]
    public async Task Formats_without_stories_diagnose_and_strict_export_is_atomic()
    {
        using var stream = new MemoryStream();
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Html.SaveWithReportAsync(Example(), stream, new() { Mode = ConversionMode.Strict }));
        Assert.Contains(error.Report.Diagnostics, d => d.Code == "conversion.stories");
        Assert.Equal(0, stream.Length);
    }
    private static MemoryStream Package(params (string Path, string Xml)[] parts)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var part in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Path).Open(), new UTF8Encoding(false));
                writer.Write(part.Xml);
            }
        stream.Position = 0; return stream;
    }

    [Fact]
    public async Task External_section_starting_with_table_retains_ownership_with_diagnosed_boundary_paragraph()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var stream = Package(("word/document.xml", $"<w:document xmlns:w='{w}'><w:body><w:p><w:pPr><w:sectPr/></w:pPr><w:r><w:t>before</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>table</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:sectPr/></w:body></w:document>"));
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(stream);
        Assert.Equal(2, loaded.Document.Sections.Length);
        Assert.Equal(loaded.Document.Blocks[1].Id, loaded.Document.Sections[1].StartParagraphId);
        Assert.IsType<Table>(loaded.Document.Blocks[2]);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.section-table-boundary");
    }

    [Fact]
    public async Task External_note_relationships_resolve_relative_parts_and_unsupported_marks_are_diagnosed()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var mark = new string('*', 33);
        using var stream = Package(
            ("word/document.xml", $"<w:document xmlns:w='{w}'><w:body><w:p><w:r><w:footnoteReference w:id='4' w:customMarkFollows='1'/><w:t>{mark}</w:t></w:r></w:p></w:body></w:document>"),
            ("word/_rels/document.xml.rels", $"<Relationships xmlns='{rel}'><Relationship Id='notes' Type='{r}/footnotes' Target='notes/footnotes.xml'/></Relationships>"),
            ("word/notes/footnotes.xml", $"<w:footnotes xmlns:w='{w}' xmlns:r='{r}'><w:footnote w:id='4'><w:p><w:r><w:footnoteRef/></w:r><w:hyperlink r:id='link'><w:r><w:t>source</w:t></w:r></w:hyperlink></w:p></w:footnote></w:footnotes>"),
            ("word/notes/_rels/footnotes.xml.rels", $"<Relationships xmlns='{rel}'><Relationship Id='link' Type='{r}/hyperlink' Target='https://example.com/source' TargetMode='External'/></Relationships>"));
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(stream);
        var note = Assert.Single(loaded.Document.Notes);
        Assert.Null(note.CustomMark);
        var paragraph = Assert.IsType<Paragraph>(loaded.Document.Stories[note.StoryId].Blocks[0]);
        Assert.Equal("source", paragraph.Text);
        Assert.Equal("https://example.com/source", paragraph.Runs[0].Style.Hyperlink);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.note-custom-mark");
    }

    [Fact]
    public async Task Notes_beginning_with_tables_keep_their_structure_and_reference_marker()
    {
        var table = Table.Create(1, 1); table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Paragraph("first table")] });
        var story = new DocumentStory { Kind = DocumentStoryKind.Footnote, Blocks = [table, new Paragraph("after table")] };
        var note = new DocumentNote { StoryId = story.Id, CustomMark = "*" };
        var document = new FlowDocument([new Paragraph([new RichRun(InlineDescriptor.Note(note.Id, "*"))])])
        { Notes = [note], Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story) };
        using var stream = new MemoryStream(); await DocumentFormats.Docx.SaveAsync(document, stream); stream.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        var noteStory = loaded.Stories[loaded.Notes[0].StoryId];
        Assert.Equal(2, noteStory.Blocks.Length);
        Assert.Equal("first table", Assert.IsType<Paragraph>(Assert.IsType<Table>(noteStory.Blocks[0]).Rows[0][0].Blocks[0]).Text);
        Assert.Equal("after table", Assert.IsType<Paragraph>(noteStory.Blocks[1]).Text);
    }

    [Fact]
    public async Task Page_fields_accept_mergeformat_and_preserve_the_cached_character_style()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var stream = Package(("word/document.xml", $"<w:document xmlns:w='{w}'><w:body><w:p><w:fldSimple w:instr=' PAGE \\* MERGEFORMAT '><w:r><w:rPr><w:b/></w:rPr><w:t>12</w:t></w:r></w:fldSimple></w:p></w:body></w:document>"));
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(stream);
        var run = Assert.Single(Assert.IsType<Paragraph>(loaded.Document.Blocks[0]).Runs);
        Assert.IsType<PageFieldInlinePayload>(run.Inline!.Payload);
        Assert.Equal("12", run.PlainText); Assert.True(run.Style.Bold);
        Assert.DoesNotContain(loaded.Report.Diagnostics, d => d.Code == "docx.field");
        var rtf = DocumentFormats.Rtf.Parse(@"{\rtf1{\field{\*\fldinst NUMPAGES \\* MERGEFORMAT}{\fldrslt\b 12}}}");
        var rtfRun = Assert.Single(Assert.IsType<Paragraph>(rtf.Blocks[0]).Runs);
        Assert.Equal(PageFieldKind.NumPages, Assert.IsType<PageFieldInlinePayload>(rtfRun.Inline!.Payload).Field);
        Assert.Equal("12", rtfRun.PlainText); Assert.True(rtfRun.Style.Bold);
    }

}
