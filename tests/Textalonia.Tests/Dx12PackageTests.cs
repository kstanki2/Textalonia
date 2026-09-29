using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class Dx12PackageTests
{
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Fact]
    public async Task Core_typed_custom_xml_and_compatibility_metadata_round_trip_through_docx_and_native_formats()
    {
        var created = new DateTimeOffset(2024, 3, 2, 12, 34, 56, TimeSpan.Zero);
        var original = new FlowDocument([new Paragraph("Body")])
        {
            CoreProperties = new DocumentCoreProperties { Title = "A report", Creator = "Author", Created = created },
            Properties = ImmutableDictionary<string, string>.Empty.Add("Count", "42"),
            CustomProperties = [new DocumentCustomProperty { Name = "Count", Type = DocumentPropertyType.Integer, Value = "42" }],
            CustomXmlParts = [new DocumentCustomXmlPart
            {
                PartName = "customXml/item1.xml", Xml = "<data xmlns='urn:test'><value>42</value></data>",
                PropertiesPartName = "customXml/itemProps1.xml",
                PropertiesXml = "<ds:datastoreItem xmlns:ds='http://schemas.openxmlformats.org/officeDocument/2006/customXml' ds:itemID='{12345678-1234-1234-1234-123456789012}'/>"
            }],
            CompatibilitySettings = new DocumentCompatibilitySettings
            {
                Xml = $"<w:compat xmlns:w='{WordNamespace}'><w:compatSetting w:name='compatibilityMode' w:uri='http://schemas.microsoft.com/office/word' w:val='15'/></w:compat>"
            }
        };
        original.Validate();
        using var docx = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(original, docx);
        using (var zip = new ZipArchive(new MemoryStream(docx.ToArray()), ZipArchiveMode.Read))
        {
            Assert.NotNull(zip.GetEntry("docProps/core.xml"));
            Assert.NotNull(zip.GetEntry("docProps/custom.xml"));
            Assert.NotNull(zip.GetEntry("customXml/item1.xml"));
            Assert.NotNull(zip.GetEntry("customXml/itemProps1.xml"));
            Assert.NotNull(zip.GetEntry("customXml/_rels/item1.xml.rels"));
            var custom = XDocument.Load(zip.GetEntry("docProps/custom.xml")!.Open());
            Assert.Equal("i8", Assert.Single(custom.Descendants(), e => e.Name.LocalName == "property").Elements().Single().Name.LocalName);
        }
        docx.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(docx);
        Assert.Equal(original.CoreProperties, loaded.CoreProperties);
        Assert.Equal(DocumentPropertyType.Integer, Assert.Single(loaded.CustomProperties).Type);
        Assert.Equal("42", loaded.Properties["Count"]);
        Assert.Equal("42", XDocument.Parse(Assert.Single(loaded.CustomXmlParts).Xml).Descendants().Single(e => e.Name.LocalName == "value").Value);
        Assert.Contains("compatibilityMode", loaded.CompatibilitySettings.Xml);
        foreach (var format in new TextDocumentFormat[] { DocumentFormats.Json, DocumentFormats.Xaml })
        {
            var reopened = format.Parse(format.Serialize(loaded));
            Assert.Equal(loaded.CoreProperties, reopened.CoreProperties);
            Assert.Equal(loaded.CustomProperties.ToArray(), reopened.CustomProperties.ToArray());
            Assert.Equal(loaded.CustomXmlParts.ToArray(), reopened.CustomXmlParts.ToArray());
            Assert.Equal(loaded.CompatibilitySettings, reopened.CompatibilitySettings);
        }
    }

    [Fact]
    public async Task Section_tag_without_Textalonia_marker_is_a_real_content_control()
    {
        using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false));
            writer.Write($"<w:document xmlns:w='{WordNamespace}'><w:body><w:sdt><w:sdtPr><w:tag w:val='Textalonia.Section'/></w:sdtPr><w:sdtContent><w:p><w:r><w:t>text</w:t></w:r></w:p></w:sdtContent></w:sdt></w:body></w:document>");
        }
        package.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(package);
        Assert.IsType<Paragraph>(Assert.Single(loaded.Blocks));
        Assert.Equal("Textalonia.Section", Assert.Single(loaded.ContentControls).Tag);
    }

    [Fact]
    public async Task Editing_a_typed_property_through_legacy_catalog_reports_type_loss_before_strict_export()
    {
        var document = FlowDocument.FromText("text") with
        {
            Properties = ImmutableDictionary<string, string>.Empty.Add("Count", "not a number"),
            CustomProperties = [new DocumentCustomProperty { Name = "Count", Type = DocumentPropertyType.Integer, Value = "42" }]
        };
        using var rejected = new MemoryStream();
        var error = await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Docx.SaveWithReportAsync(document, rejected,
            new ConversionOptions { Mode = ConversionMode.Strict }));
        Assert.Contains(error.Report.Diagnostics, diagnostic => diagnostic.Code == "docx.property-type");
        Assert.Equal(0, rejected.Length);
        using var tolerated = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(document, tolerated);
        tolerated.Position = 0;
        var loaded = await DocumentFormats.Docx.LoadAsync(tolerated);
        Assert.Equal("not a number", loaded.Properties["Count"]);
        Assert.Equal(DocumentPropertyType.Text, Assert.Single(loaded.CustomProperties).Type);
    }

    [Fact]
    public void Custom_xml_catalog_rejects_traversal_dtd_and_duplicate_paths()
    {
        var part = new DocumentCustomXmlPart { PartName = "customXml/item1.xml", Xml = "<item/>" };
        Assert.Throws<FormatException>(() => (new FlowDocument { CustomXmlParts = [part, part] }).Validate());
        Assert.Throws<FormatException>(() => (new FlowDocument { CustomXmlParts = [part with { PartName = "customXml/../bad.xml" }] }).Validate());
        Assert.Throws<FormatException>(() => (new FlowDocument { CustomXmlParts = [part with { Xml = "<!DOCTYPE x><x/>" }] }).Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n  ")]
    [InlineData("<first/><second/>")]
    public void Custom_xml_catalog_requires_one_root_element(string xml)
    {
        var part = new DocumentCustomXmlPart { PartName = "customXml/item1.xml", Xml = xml };
        Assert.Throws<FormatException>(() => (new FlowDocument { CustomXmlParts = [part] }).Validate());
        Assert.Throws<FormatException>(() => (new FlowDocument { CustomXmlParts =
            [part with { Xml = "<item/>", PropertiesPartName = "customXml/itemProps1.xml", PropertiesXml = xml }] }).Validate());
    }

    [Fact]
    public void Null_typed_metadata_values_fail_validation_as_format_errors()
    {
        Assert.Throws<FormatException>(() => (new FlowDocument
        { CustomProperties = [new DocumentCustomProperty { Name = "Null", Value = null! }] }).Validate());
        Assert.Throws<FormatException>(() => (new FlowDocument
        { CustomXmlParts = [new DocumentCustomXmlPart { PartName = "customXml/item1.xml", Xml = null! }] }).Validate());
        Assert.Throws<FormatException>(() => (new FlowDocument
        { CustomXmlParts = [new DocumentCustomXmlPart { PartName = null!, Xml = "<item/>" }] }).Validate());
    }
}
