using System.IO.Compression;
using System.Text;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class DocxPackageValidationTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static MemoryStream Package(string body = "<w:p><w:r><w:t>body</w:t></w:r></w:p>",
        string? relationships = null, params (string Path, string Content)[] otherParts)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string name, string content)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
            Part("word/document.xml", $"<w:document xmlns:w='{W}' xmlns:r='{R}'><w:body>{body}</w:body></w:document>");
            if (relationships is not null)
                Part("word/_rels/document.xml.rels", $"<Relationships xmlns='{Rel}'>{relationships}</Relationships>");
            foreach (var (path, content) in otherParts) Part(path, content);
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public async Task Excluded_parts_and_invalidated_signature_are_reported_without_copying()
    {
        using var input = Package(otherParts:
        [
            ("word/activeX/activeX1.bin", "opaque"),
            ("word/vbaProject.bin", "opaque"),
            ("word/charts/chart1.xml", "<chart/>"),
            ("word/comments.xml", "<comments/>"),
            ("_xmlsignatures/sig1.xml", "<signature/>")
        ]);
        var result = await DocumentFormats.Docx.LoadWithReportAsync(input);
        Assert.Equal("body", result.Document.Text);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.excluded-part" && d.SourceLocation == "word/activeX/activeX1.bin");
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.excluded-part" && d.SourceLocation == "word/vbaProject.bin");
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.signature-invalidated");
        using var output = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(result.Document, output);
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.Contains("activeX", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unknown_relationship_and_opaque_target_have_explicit_losses()
    {
        using var input = Package(relationships:
            "<Relationship Id='opaque' Type='urn:example:unsupported' Target='../opaque/item.bin'/>",
            otherParts: [("opaque/item.bin", "opaque")]);
        var result = await DocumentFormats.Docx.LoadWithReportAsync(input);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.unsupported-relationship");
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.unsupported-part" && d.SourceLocation == "opaque/item.bin");
    }

    [Fact]
    public async Task Missing_known_target_is_a_strict_conversion_loss()
    {
        const string relationships = $"<Relationship Id='image1' Type='{R}/image' Target='media/missing.png'/>";
        using var tolerant = Package(relationships: relationships);
        var result = await DocumentFormats.Docx.LoadWithReportAsync(tolerant);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.relationship-target-missing");
        using var strict = Package(relationships: relationships);
        await Assert.ThrowsAsync<DocumentConversionException>(() =>
            DocumentFormats.Docx.LoadWithReportAsync(strict, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public async Task Duplicate_relationship_ids_are_rejected()
    {
        const string relationships = $"<Relationship Id='same' Type='{R}/styles' Target='styles.xml'/>" +
            $"<Relationship Id='same' Type='{R}/numbering' Target='numbering.xml'/>";
        using var input = Package(relationships: relationships);
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Docx.LoadAsync(input));
    }

    [Fact]
    public async Task Unrecognized_dangling_relationship_id_is_reported()
    {
        using var input = Package("<w:p><w:unknown r:id='missing'/></w:p>");
        var result = await DocumentFormats.Docx.LoadWithReportAsync(input);
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "docx.relationship-id-missing" &&
            d.SourceLocation == "word/document.xml");
    }

    [Fact]
    public async Task Writer_passes_package_graph_validation()
    {
        using var output = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(FlowDocument.FromText("saved"), output);
        output.Position = 0;
        Assert.Equal("saved", (await DocumentFormats.Docx.LoadAsync(output)).Text);
    }
}
