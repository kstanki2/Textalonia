using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class DotxInterchangeTests
{
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string TemplateMain = "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml";
    private const string DocumentMain = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    [Fact]
    public async Task Dotx_is_registered_and_round_trips_as_a_template_package()
    {
        Assert.Same(DocumentFormats.Dotx, DocumentFormats.ForPath("letter.DOTX"));
        Assert.Contains(DocumentFormats.Dotx, DocumentFormats.BuiltIn);
        Assert.Equal("Word template", DocumentFormats.Dotx.Name);

        var document = FlowDocument.FromText("Reusable letter");
        using var stream = new MemoryStream();
        await DocumentFormats.Dotx.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        Assert.True(stream.CanWrite);

        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        using (var source = archive.GetEntry("[Content_Types].xml")!.Open())
        {
            var types = XDocument.Load(source);
            Assert.Equal(TemplateMain, (string?)types.Root!.Elements(ContentTypes + "Override")
                .Single(part => (string?)part.Attribute("PartName") == "/word/document.xml")
                .Attribute("ContentType"));
            Assert.NotNull(archive.GetEntry("word/document.xml"));
        }

        stream.Position = 0;
        var loaded = await DocumentFormats.Dotx.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Equal(document.PlainText, loaded.Document.PlainText);
        Assert.Empty(loaded.Report.Diagnostics);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task Dotx_rejects_a_docx_main_part_instead_of_treating_it_as_a_template()
    {
        using var source = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(FlowDocument.FromText("ordinary document"), source);
        source.Position = 0;
        using (var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
        using (var part = archive.GetEntry("[Content_Types].xml")!.Open())
        {
            var types = XDocument.Load(part);
            Assert.Equal(DocumentMain, (string?)types.Root!.Elements(ContentTypes + "Override")
                .Single(item => (string?)item.Attribute("PartName") == "/word/document.xml")
                .Attribute("ContentType"));
        }

        source.Position = 0;
        await Assert.ThrowsAsync<FormatException>(() => DocumentFormats.Dotx.LoadAsync(source));
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Dotx_strict_export_reports_loss_before_touching_output()
    {
        var document = DocumentFormats.Markdown.Parse("> Quoted text");
        using var destination = new MemoryStream(Encoding.UTF8.GetBytes("untouched"), writable: true);
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Dotx.SaveWithReportAsync(
            document, destination, new() { Mode = ConversionMode.Strict }));
        Assert.Equal("untouched", Encoding.UTF8.GetString(destination.ToArray()));
    }
}
