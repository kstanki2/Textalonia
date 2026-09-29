using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class FlatOpcInterchangeTests
{
    private const string PackageNamespace = "http://schemas.microsoft.com/office/2006/xmlPackage";
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly FlatOpcDocumentFormat Format = new();

    private static MemoryStream Input(string xml) => new(Encoding.UTF8.GetBytes(xml));

    private static string FlatOpc(string parts) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <pkg:package xmlns:pkg="{PackageNamespace}">{parts}</pkg:package>
        """;

    private static string DocumentPart(string body) => $"""
        <pkg:part pkg:name="/word/document.xml" pkg:contentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml">
          <pkg:xmlData><w:document xmlns:w="{WordNamespace}"><w:body>{body}</w:body></w:document></pkg:xmlData>
        </pkg:part>
        """;

    [Fact]
    public async Task Loads_word_flat_opc_xml_and_preserves_caller_stream()
    {
        using var input = Input(FlatOpc(DocumentPart("<w:p><w:r><w:t>Flat OPC</w:t></w:r></w:p>")));
        var document = await Format.LoadAsync(input);
        Assert.Equal("Flat OPC", document.Text);
        Assert.True(input.CanRead);
        Assert.Equal(input.Length, input.Position);
    }

    [Fact]
    public async Task Saves_xml_and_binary_parts_and_round_trips_an_image()
    {
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        var inline = new InlineDescriptor { AltText = "pixel", Width = 12, Height = 12, Payload = new ImageInlinePayload("pixel") };
        var original = new FlowDocument([new Paragraph([new RichRun("before"), new RichRun(inline), new RichRun("after")])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("pixel", new() { MediaType = "image/png", Data = ImmutableArray.CreateRange(image) }) };

        using var output = new MemoryStream();
        await Format.SaveAsync(original, output);
        Assert.True(output.CanWrite);
        output.Position = 0;
        var package = XDocument.Load(output);
        XNamespace pkg = PackageNamespace;
        Assert.Equal(pkg + "package", package.Root!.Name);
        Assert.Contains(package.Root.Elements(pkg + "part"), p =>
            (string?)p.Attribute(pkg + "name") == "/word/document.xml" && p.Element(pkg + "xmlData") is not null);
        var binary = Assert.Single(package.Root.Elements(pkg + "part"), p => p.Element(pkg + "binaryData") is not null);
        Assert.StartsWith("/word/media/", (string?)binary.Attribute(pkg + "name"));
        Assert.Equal(image, Convert.FromBase64String(binary.Element(pkg + "binaryData")!.Value));

        output.Position = 0;
        var loaded = await Format.LoadAsync(output);
        Assert.Equal(original.Text, loaded.Text);
        Assert.Single(loaded.Resources);
    }

    [Theory]
    [InlineData("<notPackage/>")]
    [InlineData("<!DOCTYPE pkg:package [<!ENTITY x 'bad'>]><pkg:package xmlns:pkg='http://schemas.microsoft.com/office/2006/xmlPackage'>&x;</pkg:package>")]
    public async Task Rejects_wrong_root_and_dtd(string xml)
    {
        using var input = Input(xml);
        await Assert.ThrowsAsync<FormatException>(() => Format.LoadAsync(input));
    }

    [Fact]
    public async Task Rejects_duplicate_unsafe_and_malformed_parts()
    {
        foreach (var parts in new[]
        {
            DocumentPart("<w:p/>") + DocumentPart("<w:p/>"),
            DocumentPart("<w:p/>").Replace("/word/document.xml", "/word/../document.xml"),
            DocumentPart("<w:p/>") + "<pkg:part pkg:name='/word/image.png' pkg:contentType='image/png'><pkg:binaryData>???</pkg:binaryData></pkg:part>",
            DocumentPart("<w:p/>").Replace("</pkg:xmlData>", "<w:extra xmlns:w='" + WordNamespace + "'/></pkg:xmlData>")
        })
        {
            using var input = Input(FlatOpc(parts));
            await Assert.ThrowsAsync<FormatException>(() => Format.LoadAsync(input));
        }
    }

    [Fact]
    public async Task Docx_loss_diagnostics_flow_through_flat_opc_and_strict_load_rejects()
    {
        var xml = FlatOpc(DocumentPart("<w:ins><w:p><w:r><w:t>new</w:t></w:r></w:p></w:ins>"));
        using var tolerant = Input(xml);
        var loaded = await Format.LoadWithReportAsync(tolerant);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "docx.revision");
        using var strict = Input(xml);
        await Assert.ThrowsAsync<DocumentConversionException>(() =>
            Format.LoadWithReportAsync(strict, new() { Mode = ConversionMode.Strict }));
    }
}
