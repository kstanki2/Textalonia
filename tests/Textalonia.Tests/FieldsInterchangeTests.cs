using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class FieldsInterchangeTests
{
    private static FlowDocument Sample()
    {
        var first = new Paragraph([new RichRun("aB", new() { Bold = true }), new RichRun("Cd", new() { Italic = true })]);
        var last = new Paragraph("EFgh");
        DocumentAnchor Anchor(Paragraph p, int offset, AnchorAffinity affinity = AnchorAffinity.After) => new() { ParagraphId = p.Id, Offset = offset, Affinity = affinity };
        return new FlowDocument([first, last])
        {
            Fields = [new() { Instruction = "DOCVARIABLE customer", Start = Anchor(first, 1, AnchorAffinity.Before), End = Anchor(last, 2), IsLocked = true, ShowCode = true },
                new() { Instruction = "DATE \\@ yyyy", Start = Anchor(first, 2), End = Anchor(first, 3), IsDirty = false }],
            Bookmarks = [new() { Name = "customer", Start = Anchor(first, 2, AnchorAffinity.Before), End = Anchor(last, 3) }],
            Properties = ImmutableDictionary<string, string>.Empty.Add("Title", "Document title")
        };
    }
    private static IDocumentFormat Format(string name) => name switch { "json" => DocumentFormats.Json, "xaml" => DocumentFormats.Xaml, "docx" => DocumentFormats.Docx, _ => DocumentFormats.Rtf };

    [Theory]
    [InlineData("json")]
    [InlineData("xaml")]
    [InlineData("docx")]
    [InlineData("rtf")]
    public async Task Rich_nested_ranges_and_bookmarks_survive_round_trip(string name)
    {
        var original = Sample();
        using var stream = new MemoryStream();
        await Format(name).SaveAsync(original, stream);
        if (name == "docx")
        {
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();
            Assert.Contains("bookmarkStart", xml); Assert.Contains("instrText", xml); Assert.Contains("fldChar", xml);
        }
        stream.Position = 0;
        var loaded = await Format(name).LoadAsync(stream);
        Assert.Equal(original.Text, loaded.Text);
        Assert.Equal(2, loaded.Fields.Length);
        foreach (var expected in original.Fields)
        {
            var actual = loaded.Fields.Single(f => f.Id == expected.Id);
            Assert.Equal(expected.Instruction, actual.Instruction);
            Assert.Equal(expected.Start.Resolve(original), actual.Start.Resolve(loaded));
            Assert.Equal(expected.End.Resolve(original), actual.End.Resolve(loaded));
            Assert.Equal(expected.IsLocked, actual.IsLocked);
            Assert.Equal(expected.IsDirty, actual.IsDirty);
            Assert.Equal(expected.ShowCode, actual.ShowCode);
            Assert.Equal(expected.Start.Affinity, actual.Start.Affinity);
        }
        var bookmark = Assert.Single(loaded.Bookmarks);
        Assert.Equal("customer", bookmark.Name);
        Assert.Equal(original.Bookmarks[0].Id, bookmark.Id);
        Assert.Equal(2, bookmark.Start.Resolve(loaded));
        Assert.Equal(8, bookmark.End.Resolve(loaded));
        Assert.Contains(new DocumentIndex(loaded).Paragraphs[0].Paragraph.Runs, r => r.Style.Bold && r.Text.Contains('B'));
        Assert.Contains(new DocumentIndex(loaded).Paragraphs[0].Paragraph.Runs, r => r.Style.Italic && r.Text.Contains('C'));
        Assert.Equal("Document title", loaded.Properties["Title"]);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("rtf")]
    public async Task Unknown_fields_keep_rich_results_with_explicit_diagnostics(string name)
    {
        var original = Sample();
        original = original with { Fields = [original.Fields[0] with { Instruction = "VENDORFIELD secret" }] };
        using var stream = new MemoryStream();
        var saved = await Format(name).SaveWithReportAsync(original, stream);
        Assert.Contains(saved.Report.Diagnostics, d => d.Code == name + ".unsupported-field");
        stream.Position = 0;
        var loaded = await Format(name).LoadWithReportAsync(stream);
        Assert.Equal("VENDORFIELD secret", Assert.Single(loaded.Document.Fields).Instruction);
        Assert.Equal(original.Text, loaded.Document.Text);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == name + ".unsupported-field");
        stream.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => Format(name).LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Imports_standard_docx_bookmarks_and_cross_paragraph_complex_fields()
    {
        using var stream = Package("""
            <w:p><w:bookmarkStart w:id="1" w:name="named"/><w:r><w:fldChar w:fldCharType="begin" w:fldLock="1"/></w:r><w:r><w:instrText>DOCVARIABLE customer</w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>Rich</w:t></w:r></w:p>
            <w:p><w:r><w:t>result</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r><w:bookmarkEnd w:id="1"/></w:p>
            """);
        var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        Assert.Equal("Rich\nresult", loaded.Text);
        var field = Assert.Single(loaded.Fields);
        Assert.True(field.IsLocked); Assert.Equal(0, field.Start.Resolve(loaded)); Assert.Equal(11, field.End.Resolve(loaded));
        Assert.Equal("named", Assert.Single(loaded.Bookmarks).Name);
    }

    [Fact]
    public void Imports_standard_rtf_general_fields_and_bookmarks()
    {
        var loaded = DocumentFormats.Rtf.Parse(@"{\rtf1 {\*\bkmkstart named}{\field\fldlock{\*\fldinst DOCVARIABLE customer}{\fldrslt {\b Rich} result}}{\*\bkmkend named}}");
        Assert.Equal("Rich result", loaded.Text);
        var field = Assert.Single(loaded.Fields);
        Assert.True(field.IsLocked); Assert.Equal(0, field.Start.Resolve(loaded)); Assert.Equal(11, field.End.Resolve(loaded));
        Assert.Equal("named", Assert.Single(loaded.Bookmarks).Name);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xaml")]
    [InlineData("docx")]
    [InlineData("rtf")]
    public async Task Secondary_story_ranges_and_empty_fields_keep_their_story_owner(string name)
    {
        var paragraph = new Paragraph("header");
        var story = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [paragraph] };
        var anchor = new DocumentAnchor { StoryId = story.Id, ParagraphId = paragraph.Id, Offset = 2 };
        var document = new FlowDocument([new Paragraph("body")])
        {
            Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story),
            Sections = [new() { HeaderFooter = new() { PrimaryHeader = new() { StoryId = story.Id, LinkToPrevious = false } } }],
            Fields = [new() { Instruction = "DOCVARIABLE Empty", Start = anchor, End = anchor }],
            Bookmarks = [new() { Name = "empty", Start = anchor with { Affinity = AnchorAffinity.Before }, End = anchor }]
        };
        using var stream = new MemoryStream();
        await Format(name).SaveAsync(document, stream); stream.Position = 0;
        var loaded = await Format(name).LoadAsync(stream);
        var field = Assert.Single(loaded.Fields);
        var bookmark = Assert.Single(loaded.Bookmarks);
        Assert.NotEqual(Guid.Empty, field.Start.StoryId);
        Assert.Equal(field.Start.StoryId, bookmark.Start.StoryId);
        Assert.Equal(2, field.Start.Resolve(loaded)); Assert.Equal(2, field.End.Resolve(loaded));
        Assert.Equal("header", loaded.GetStoryDocument(field.Start.StoryId).Text);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xaml")]
    [InlineData("docx")]
    [InlineData("rtf")]
    public async Task Internal_link_metadata_survives_round_trip(string name)
    {
        var destination = new InternalLinkDestination { BookmarkName = "target", Tooltip = "Go to target", Activation = InternalLinkActivation.Click };
        var paragraph = new Paragraph("linked", new() { InternalLink = destination });
        var anchor = new DocumentAnchor { ParagraphId = paragraph.Id, Offset = 0 };
        var document = new FlowDocument([paragraph]) { Bookmarks = [new() { Name = "target", Start = anchor, End = anchor }] };
        using var stream = new MemoryStream();
        await Format(name).SaveAsync(document, stream); stream.Position = 0;
        var loaded = await Format(name).LoadAsync(stream);
        Assert.Equal(destination, Assert.Single(Assert.IsType<Paragraph>(loaded.Blocks[0]).Runs).Style.InternalLink);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Empty_external_field_instructions_and_unclosed_bookmarks_preserve_text_and_report_loss(bool docx)
    {
        using var stream = docx
            ? Package("<w:p><w:bookmarkStart w:id='1' w:name='unfinished'/><w:fldSimple w:instr=' '><w:r><w:t>cached</w:t></w:r></w:fldSimple></w:p>")
            : new MemoryStream(Encoding.ASCII.GetBytes(@"{\rtf1{\*\bkmkstart unfinished}{\field{\*\fldinst }{\fldrslt cached}}}"));
        var format = Format(docx ? "docx" : "rtf");
        var loaded = await format.LoadWithReportAsync(stream);
        Assert.Equal("cached", loaded.Document.Text);
        Assert.Empty(loaded.Document.Fields); Assert.Empty(loaded.Document.Bookmarks);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code.EndsWith(".unsupported-field", StringComparison.Ordinal));
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code.EndsWith(".bookmark", StringComparison.Ordinal));
        stream.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => format.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict }));
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("rtf")]
    public async Task Nested_instruction_expressions_use_standard_nested_field_markup(string name)
    {
        var paragraph = new Paragraph("Yes");
        var anchor = new DocumentAnchor { ParagraphId = paragraph.Id, Offset = 0 };
        const string instruction = "IF { MERGEFIELD Name } = \"Alice\" \"Yes\" \"No\"";
        var document = new FlowDocument([paragraph]) { Fields = [new() { Instruction = instruction, Start = anchor, End = anchor with { Offset = 3 } }] };
        using var stream = new MemoryStream();
        await Format(name).SaveAsync(document, stream); stream.Position = 0;
        if (name == "docx")
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
            using var source = zip.GetEntry("word/document.xml")!.Open();
            var xml = XDocument.Load(source); XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            Assert.Equal(2, xml.Descendants(w + "fldChar").Count(e => (string?)e.Attribute(w + "fldCharType") == "begin"));
        }
        else
        {
            var encoded = Encoding.UTF8.GetString(stream.ToArray());
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(encoded, @"\\field\b").Count);
        }
        stream.Position = 0;
        var loaded = await Format(name).LoadAsync(stream);
        Assert.Equal(instruction, Assert.Single(loaded.Fields).Instruction);
        Assert.Equal("Yes", loaded.Text);
    }

    [Fact]
    public void Standard_rtf_nested_instruction_keeps_code_and_excludes_its_cached_text()
    {
        var document = DocumentFormats.Rtf.Parse(@"{\rtf1{\field{\*\fldinst IF {\field{\*\fldinst MERGEFIELD Name}{\fldrslt Alice}} = ""Alice"" ""Yes"" ""No""}{\fldrslt Yes}}}");
        var field = Assert.Single(document.Fields);
        Assert.Contains("{ MERGEFIELD Name }", field.Instruction);
        Assert.Equal("Yes", document.Text);
    }

    private static MemoryStream Package(string body)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false)))
            writer.Write("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body>" + body + "</w:body></w:document>");
        stream.Position = 0; return stream;
    }
}
