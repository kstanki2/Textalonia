using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class EquationInterchangeTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string M = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    [Fact]
    public async Task Office_math_survives_import_adjacent_edit_native_storage_and_docx_reopen()
    {
        using var input = new MemoryStream();
        using (var archive = new ZipArchive(input, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false)))
            writer.Write($"<w:document xmlns:w='{W}' xmlns:m='{M}'><w:body><w:p><w:r><w:t>Before </w:t></w:r>" +
                "<m:oMath><m:r><m:t>x</m:t></m:r><m:r><m:t>+1</m:t></m:r></m:oMath>" +
                "<w:r><w:t> after</w:t></w:r></w:p><w:p><m:oMathPara><m:oMath><m:r><m:t>y</m:t></m:r></m:oMath></m:oMathPara></w:p></w:body></w:document>");
        input.Position = 0;
        var imported = await DocumentFormats.Docx.LoadAsync(input);
        var original = Equations(imported);
        Assert.Equal(2, original.Length);
        Assert.Equal("Before x+1 after\ny", imported.PlainText);
        Assert.Equal("oMath", XElement.Parse(original[0].Xml).Name.LocalName);
        Assert.Equal("oMathPara", XElement.Parse(original[1].Xml).Name.LocalName);

        var session = new EditorSession(imported);
        session.Select(0, 0);
        session.InsertText("Edited ");
        Assert.Equal(original.Select(e => e.Xml), Equations(session.Document).Select(e => e.Xml));
        Assert.Equal("Edited Before x+1 after\ny", session.Document.PlainText);

        var json = DocumentFormats.Json.Parse(DocumentFormats.Json.Serialize(session.Document));
        var xaml = DocumentFormats.Xaml.Parse(DocumentFormats.Xaml.Serialize(json));
        Assert.Equal(original.Select(e => e.Xml), Equations(xaml).Select(e => e.Xml));

        using var output = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(xaml, output);
        output.Position = 0;
        using (var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
        using (var reader = archive.GetEntry("word/document.xml")!.Open())
        {
            var xml = XDocument.Load(reader);
            Assert.Single(xml.Descendants(XName.Get("oMathPara", M)));
            Assert.Equal(2, xml.Descendants(XName.Get("oMath", M)).Count());
        }
        output.Position = 0;
        var reopened = await DocumentFormats.Docx.LoadAsync(output);
        Assert.Equal(xaml.PlainText, reopened.PlainText);
        foreach (var (before, after) in original.Zip(Equations(reopened)))
            Assert.True(XNode.DeepEquals(WithoutNamespaceDeclarations(XElement.Parse(before.Xml)),
                WithoutNamespaceDeclarations(XElement.Parse(after.Xml))));
    }

    [Fact]
    public async Task Other_codecs_report_math_xml_loss_before_strict_output()
    {
        var document = new FlowDocument([new Paragraph([new RichRun(new InlineDescriptor
        {
            AltText = "x", Payload = new EquationInlinePayload($"<m:oMath xmlns:m='{M}'><m:r><m:t>x</m:t></m:r></m:oMath>")
        })])]);
        using var tolerant = new MemoryStream();
        var result = await DocumentFormats.Rtf.SaveWithReportAsync(document, tolerant);
        Assert.Contains(result.Report.Diagnostics, diagnostic => diagnostic.Code == "conversion.equation");
        using var strict = new MemoryStream();
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() =>
            DocumentFormats.Rtf.SaveWithReportAsync(document, strict, new() { Mode = ConversionMode.Strict }));
        Assert.Contains(error.Report.Diagnostics, diagnostic => diagnostic.Code == "conversion.equation");
        Assert.Equal(0, strict.Length);
    }

    private static EquationInlinePayload[] Equations(FlowDocument document) =>
        new DocumentIndex(document).Paragraphs.SelectMany(entry => entry.Paragraph.Runs)
            .Select(run => run.Inline?.Payload).OfType<EquationInlinePayload>().ToArray();

    private static XElement WithoutNamespaceDeclarations(XElement element) => new(element.Name,
        element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration),
        element.Nodes().Select(node => node is XElement child ? WithoutNamespaceDeclarations(child) : node));
}
