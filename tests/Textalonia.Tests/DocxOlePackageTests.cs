using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class DocxOlePackageTests
{
    private static readonly byte[] Preview = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
    private static readonly byte[] Workbook = [0x50, 0x4b, 0x03, 0x04, 1, 2, 3, 4];
    private const string PackageType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static FlowDocument Document(OleRelationshipKind kind = OleRelationshipKind.Package)
    {
        var inline = new InlineDescriptor { AltText = "Embedded workbook", Width = 48, Height = 32,
            Payload = new OleInlinePayload("workbook", "preview") { ProgramId = "Excel.Sheet.12", FileName = "quarterly.xlsx", RelationshipKind = kind } };
        return new FlowDocument([new Paragraph([new RichRun(inline)])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty
            .Add("workbook", new DocumentResource { MediaType = PackageType, Data = ImmutableArray.CreateRange(Workbook) })
            .Add("preview", new DocumentResource { MediaType = "image/png", Data = ImmutableArray.CreateRange(Preview) }) };
    }

    [Fact]
    public async Task Embedded_package_relationship_round_trips_with_owner_and_preview()
    {
        var source = Document();
        using var output = new MemoryStream();
        var saved = await DocumentFormats.Docx.SaveWithReportAsync(source, output, new() { Mode = ConversionMode.Strict });
        Assert.Empty(saved.Report.Diagnostics);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
        {
            var relation = PackageRelation(zip);
            Assert.EndsWith("/package", (string?)relation.Attribute("Type"));
            var target = "word/" + (string?)relation.Attribute("Target");
            Assert.Equal(Workbook, ReadBytes(zip.GetEntry(target)!));
            Assert.Single(zip.Entries, entry => entry.FullName.StartsWith("word/embeddings/", StringComparison.Ordinal));
        }
        output.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadWithReportAsync(output, new() { Mode = ConversionMode.Strict });
        Assert.Empty(loaded.Report.Diagnostics);
        var payload = Assert.IsType<OleInlinePayload>(((Paragraph)loaded.Document.Blocks[0]).Runs[0].Inline!.Payload);
        Assert.Equal(OleRelationshipKind.Package, payload.RelationshipKind);
        Assert.Equal("Excel.Sheet.12", payload.ProgramId);
        Assert.Equal("quarterly.xlsx", payload.FileName);
        Assert.Equal(Workbook, loaded.Document.Resources[payload.ResourceId].Data.ToArray());
        Assert.Equal(Preview, loaded.Document.Resources[payload.PreviewResourceId].Data.ToArray());
        using var edited = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(loaded.Document with { Blocks = [new Paragraph("Object removed")] }, edited);
        using var editedZip = new ZipArchive(edited, ZipArchiveMode.Read, leaveOpen: true);
        Assert.DoesNotContain(editedZip.Entries, entry => entry.FullName.StartsWith("word/embeddings/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xaml")]
    public void Native_formats_keep_package_relationship_kind(string format)
    {
        var codec = format == "json" ? (TextDocumentFormat)DocumentFormats.Json : DocumentFormats.Xaml;
        var restored = codec.Parse(codec.Serialize(Document()));
        var payload = Assert.IsType<OleInlinePayload>(((Paragraph)restored.Blocks[0]).Runs[0].Inline!.Payload);
        Assert.Equal(OleRelationshipKind.Package, payload.RelationshipKind);
    }

    [Fact]
    public async Task Missing_preview_does_not_write_ownerless_embedding_part()
    {
        var source = Document() with { Resources = Document().Resources.Remove("preview") };
        using var output = new MemoryStream();
        var result = await DocumentFormats.Docx.SaveWithReportAsync(source, output);
        Assert.Contains(result.Report.Diagnostics, diagnostic => diagnostic.Code == "docx.ole-unavailable");
        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("word/embeddings/", StringComparison.Ordinal));
        Assert.DoesNotContain(Relationships(zip), relationship => ((string?)relationship.Attribute("Type"))?.EndsWith("/package", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Missing_package_relationship_drops_orphan_preview_and_never_follows_external_target()
    {
        using var original = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(Document(), original);
        using var modified = RewriteRelations(original.ToArray(), xml =>
        {
            var relation = xml.Root!.Elements(Rel + "Relationship").Single(e => ((string?)e.Attribute("Type"))!.EndsWith("/package", StringComparison.Ordinal));
            relation.SetAttributeValue("TargetMode", "External");
            relation.SetAttributeValue("Target", "file:///private/workbook.xlsx");
        });
        var imported = await DocumentFormats.Docx.LoadWithReportAsync(modified);
        Assert.Contains(imported.Report.Diagnostics, diagnostic => diagnostic.Code == "docx.package-unavailable");
        Assert.Equal("Embedded workbook", imported.Document.PlainText);
        Assert.Empty(imported.Document.Resources);
        using var output = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(imported.Document, output);
        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("word/embeddings/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unowned_package_relationship_and_part_are_diagnosed_and_not_copied()
    {
        using var original = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(Document(), original);
        using var modified = RewriteRelations(original.ToArray(), xml =>
        {
            var relation = xml.Root!.Elements(Rel + "Relationship").Single(e => ((string?)e.Attribute("Type"))!.EndsWith("/package", StringComparison.Ordinal));
            relation.SetAttributeValue("Id", "unownedPackage");
        });
        var imported = await DocumentFormats.Docx.LoadWithReportAsync(modified);
        Assert.Contains(imported.Report.Diagnostics, diagnostic => diagnostic.Code == "docx.ole-unowned");
        Assert.Empty(imported.Document.Resources);
        using var output = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(imported.Document, output);
        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("word/embeddings/", StringComparison.Ordinal));
    }

    private static XElement PackageRelation(ZipArchive archive) => Relationships(archive)
        .Single(element => ((string?)element.Attribute("Type"))?.EndsWith("/package", StringComparison.Ordinal) == true);

    private static XElement[] Relationships(ZipArchive archive)
    {
        using var part = archive.GetEntry("word/_rels/document.xml.rels")!.Open();
        return XDocument.Load(part).Root!.Elements(Rel + "Relationship").ToArray();
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var source = entry.Open(); using var target = new MemoryStream(); source.CopyTo(target); return target.ToArray();
    }

    private static MemoryStream RewriteRelations(byte[] bytes, Action<XDocument> edit)
    {
        var result = new MemoryStream();
        using (var input = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        using (var output = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in input.Entries)
            {
                var contents = ReadBytes(entry);
                if (entry.FullName == "word/_rels/document.xml.rels")
                {
                    using var source = new MemoryStream(contents);
                    var xml = XDocument.Load(source); edit(xml);
                    contents = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
                }
                using var target = output.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
                target.Write(contents);
            }
        result.Position = 0;
        return result;
    }
}
