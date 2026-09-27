using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class PageModelTests
{
    private static FlowDocument TwoSections()
    {
        var first = new Paragraph("first"); var second = new Paragraph("second");
        return new FlowDocument([first, second]) { Sections = [new DocumentSection(), new DocumentSection
        {
            StartParagraphId = second.Id, BreakKind = SectionBreakKind.OddPage, PageNumberStart = 7,
            PageNumberFormat = PageNumberFormat.LowerRoman,
            PageSettings = new() { Width = 600, Height = 800, Orientation = PageOrientation.Landscape,
                Margins = new(25, 35, 45, 55), MirrorMargins = true, Gutter = 12,
                Columns = [new(1), new(2)], ColumnSpacing = 18, BalanceColumns = false,
                Background = "#FFFEEE", Borders = new(Left: new(2, "#123456")),
                LineNumbering = new(3, 2, 15, LineNumberRestart.EachSection), Grid = new(16, 24) }
        }] };
    }

    [Fact]
    public void Physical_units_orientation_and_validation_are_explicit()
    {
        Assert.Equal(96, DocumentUnits.FromInches(1));
        Assert.Equal(96, DocumentUnits.FromPoints(72));
        Assert.Equal(96, DocumentUnits.FromTwips(1440));
        Assert.Equal(96, DocumentUnits.FromMillimeters(25.4), 8);
        var settings = new PageSettings { Width = 600, Height = 800, Orientation = PageOrientation.Landscape };
        Assert.Equal(800, settings.EffectiveWidth); Assert.Equal(600, settings.EffectiveHeight);
        Assert.Throws<FormatException>(() => (settings with { Width = double.NaN }).Validate());
        Assert.Throws<FormatException>(() => (settings with { Gutter = 900 }).Validate());
        Assert.Throws<FormatException>(() => (settings with { Columns = [new(0)] }).Validate());
    }

    [Fact]
    public void Physical_section_partitions_reject_detached_duplicate_and_table_boundaries()
    {
        var document = TwoSections(); document.Validate();
        Assert.Throws<FormatException>(() => (document with { Sections = [document.Sections[1]] }).Validate());
        Assert.Throws<FormatException>(() => (document with { Sections = document.Sections.Add(document.Sections[1]) }).Validate());
        Assert.Throws<FormatException>(() => (document with { Sections = document.Sections.SetItem(1,
            document.Sections[1] with { StartParagraphId = Guid.NewGuid() }) }).Validate());
        var table = Table.Create(1, 1);
        var inTable = new DocumentIndex(new FlowDocument([table])).Paragraphs[0].Paragraph.Id;
        Assert.Throws<FormatException>(() => (document with { Blocks = document.Blocks.Add(table), Sections = document.Sections.Add(
            new DocumentSection { StartParagraphId = inTable }) }).Validate());
    }

    [Fact]
    public void Native_xaml_and_clipboard_preserve_page_settings_breaks_frames_and_numbering()
    {
        var document = TwoSections();
        document = document.ReplaceBlock(document.Blocks[1].Id, ((Paragraph)document.Blocks[1]) with
        { Style = new() { ColumnBreakBefore = true, Frame = new(10, 20, 160, 240) } });
        foreach (var format in new TextDocumentFormat[] { DocumentFormats.Json, DocumentFormats.Xaml })
        {
            var restored = format.Parse(format.Serialize(document));
            Assert.Equal(DocumentFormats.Json.Serialize(document), DocumentFormats.Json.Serialize(restored));
        }
        var payload = ClipboardInterchange.Serialize(new() { Document = document });
        Assert.Equal(DocumentFormats.Json.Serialize(document), DocumentFormats.Json.Serialize(ClipboardInterchange.Parse(payload).Document));
        Assert.Contains("\"version\": 6", DocumentFormats.Json.Serialize(document));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Prior_native_versions_default_to_one_implicit_page_section(int version)
    {
        var document = DocumentFormats.Json.Parse("{\"version\":" + version + ",\"document\":{}}");
        Assert.Empty(document.Sections);
    }

    [Fact]
    public void Page_setup_section_edits_and_numbering_are_one_undo_step_and_readonly_safe()
    {
        var session = new EditorSession(FlowDocument.FromText("abcdef"));
        session.Select(3, 3); session.InsertSectionBreak(SectionBreakKind.NextPage, new() { Width = 700 });
        Assert.Equal("abc\ndef", session.Document.Text); Assert.Equal(2, session.Document.Sections.Length);
        session.Document.Validate(); session.Undo(); Assert.Equal("abcdef", session.Document.Text); Assert.Empty(session.Document.Sections);
        session.Redo(); var boundary = session.Document.Sections[1].Id;
        session.SetSectionNumbering(12, PageNumberFormat.UpperRoman);
        Assert.Equal(12, session.CurrentSection!.PageNumberStart);
        session.RemoveSection(boundary); Assert.Single(session.Document.Sections);
        session.Undo(); Assert.Equal(boundary, session.Document.Sections[1].Id);
        session.IsReadOnly = true; var before = session.Document;
        session.SetPageSettings(new() { Width = 900 }); session.InsertPageBreak(); session.InsertSectionBreak(SectionBreakKind.Continuous);
        Assert.Same(before, session.Document);
    }

    [Fact]
    public void Explicit_page_and_column_breaks_split_paragraphs_without_creating_sections()
    {
        var session = new EditorSession(FlowDocument.FromText("abcdef"));
        session.Select(3, 3); session.InsertPageBreak();
        Assert.Equal("abc\ndef", session.Document.Text); Assert.Empty(session.Document.Sections);
        Assert.True(((Paragraph)session.Document.Blocks[1]).Style.PageBreakBefore);
        session.Undo(); session.InsertColumnBreak();
        Assert.True(((Paragraph)session.Document.Blocks[1]).Style.ColumnBreakBefore);
        Assert.False(((Paragraph)session.Document.Blocks[1]).Style.PageBreakBefore);
    }

    [Fact]
    public void Repeated_inserted_breaks_at_start_occupy_distinct_paragraph_boundaries()
    {
        var session = new EditorSession(FlowDocument.FromText("body"));
        session.InsertPageBreak(); session.InsertPageBreak();
        Assert.Equal("\n\nbody", session.Document.Text);
        Assert.Equal(3, session.Document.Blocks.Length);
        Assert.True(((Paragraph)session.Document.Blocks[1]).Style.PageBreakBefore);
        Assert.True(((Paragraph)session.Document.Blocks[2]).Style.PageBreakBefore);
        session.Undo(); Assert.Equal("\nbody", session.Document.Text);
    }

    [Fact]
    public void Boundary_deletion_joins_preceding_section_and_undo_restores_it()
    {
        var session = new EditorSession(TwoSections());
        session.Select(5, 6); session.InsertText("");
        Assert.Equal("firstsecond", session.Document.Text); Assert.Single(session.Document.Sections);
        session.Document.Validate(); session.Undo(); Assert.Equal(2, session.Document.Sections.Length);
        session.Select(0, 0); session.InsertText("prefix "); session.Document.Validate();
        Assert.Equal(session.Index.Paragraphs[1].Paragraph.Id, session.Document.Sections[1].StartParagraphId);
        session.SelectAll(); session.InsertText("replacement"); session.Document.Validate(); Assert.Single(session.Document.Sections);
    }

    [Fact]
    public void Clipboard_remaps_section_boundaries_and_preserves_destination_initial_settings()
    {
        var source = new EditorSession(TwoSections()); source.SelectAll(); var fragment = source.CopyFragment();
        fragment.Validate(); Assert.Equal(2, fragment.Document.Sections.Length);
        Assert.NotEqual(source.Document.Sections[1].StartParagraphId, fragment.Document.Sections[1].StartParagraphId);
        var destination = new EditorSession(FlowDocument.FromText("target")); destination.SetPageSettings(new() { Width = 900 });
        destination.Select(3, 3); destination.InsertFragment(fragment); destination.Document.Validate();
        Assert.Equal(900, destination.Document.Sections[0].PageSettings.Width); Assert.Equal(2, destination.Document.Sections.Length);
        var empty = new EditorSession(); empty.InsertFragment(fragment); empty.Document.Validate();
        Assert.Equal(2, empty.Document.Sections.Length);
    }

    [Fact]
    public void Frame_and_column_break_support_sparse_override_inheritance()
    {
        var frame = new ParagraphFrame(10, 20, 160);
        var overrides = new ParagraphStyleOverrides { Frame = frame, ColumnBreakBefore = true };
        var resolved = overrides.Apply(ParagraphStyle.Default);
        Assert.Equal(frame, resolved.Frame); Assert.True(resolved.ColumnBreakBefore);
        var cleared = overrides with { Frame = new((ParagraphFrame?)null), ColumnBreakBefore = false };
        Assert.Null(cleared.Apply(resolved).Frame); Assert.False(cleared.Apply(resolved).ColumnBreakBefore);
    }

    [Fact]
    public async Task Unsupported_export_reports_physical_sections_before_strict_output_write()
    {
        foreach (var format in new IDocumentFormat[] { DocumentFormats.Html, DocumentFormats.Docx, DocumentFormats.Rtf, DocumentFormats.Markdown, DocumentFormats.PlainText })
        {
            using var output = new MemoryStream();
            var error = await Assert.ThrowsAsync<DocumentConversionException>(() => format.SaveWithReportAsync(TwoSections(), output,
                new() { Mode = ConversionMode.Strict }));
            Assert.Contains(error.Report.Diagnostics, item => item.Code == "conversion.page-sections"); Assert.Equal(0, output.Length);
        }
    }
}
