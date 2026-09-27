using System.Collections.Immutable;
using System.Text;
using System.Xml;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class XamlSerializationTests
{
    private static readonly XamlDocumentFormat Format = new();
    private static string Document(string blocks, string attributes = "") =>
        $"<Document xmlns='{XamlDocumentFormat.NamespaceUri}' Version='1' {attributes}><Blocks>{blocks}</Blocks></Document>";

    [Fact]
    public async Task All_native_model_data_round_trips_with_a_clean_strict_report()
    {
        var style = new TextStyle
        {
            FontFamily = "Test & <font>", FontSize = 21.5, Bold = true, FontWeight = 640, FontStretch = 7,
            Italic = true, Underline = true, Strikethrough = true, Foreground = "#112233", Background = "#AABBCCDD",
            Hyperlink = "https://example.test/a?b=1&c=2", Baseline = Baseline.Superscript, IsCode = true
        };
        var paragraph = new Paragraph
        {
            Runs = [new RichRun(" \tA & <B> {Binding} \u2028\U0001F600 ", style), new RichRun("", style),
                new RichRun(new InlineDescriptor { AltText = "image", Width = 80, Height = 30, Payload = new ImageInlinePayload("embedded") }, style),
                new RichRun(new InlineDescriptor { AltText = "control\tvalue", Width = 42, Height = 20,
                    Payload = new ControlInlinePayload("custom-widget") { Properties = ImmutableDictionary<string, string>.Empty.Add("text", "literal {Binding}\r\n\t<value>") } })],
            DefaultStyle = style,
            Style = new ParagraphStyle
            {
                Alignment = ParagraphAlignment.Justify, List = ListKind.Numbered, ListLevel = 1, ListId = Guid.NewGuid(), ListStart = 5, ListRestart = true,
                HeadingLevel = 3, SpaceBefore = 2.5, SpaceAfter = 6, Indent = 10, RightIndent = 9, FirstLineIndent = -3, LineHeight = 28, LetterSpacing = 1.5, RightToLeft = true,
                ListDefinition = new ListDefinition { Levels = [new() { Start = 3, Marker = ListMarkerStyle.UpperRoman, Prefix = "(", Suffix = ")", IncludeAncestors = true },
                    new() { Kind = ListKind.Bullet, Marker = ListMarkerStyle.Bullet, Text = "\u25A0" }] }
            }
        };
        var table = Table.Create(2, 2) with { ColumnWidths = [1.5, 3], RowSizing = [new() { Mode = TableRowHeightMode.AtLeast, Height = 40 }, new() { Mode = TableRowHeightMode.Exact, Height = 20 }] };
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [paragraph], Padding = new(1, 2, 3, 4), Background = "#AABBCC", Borders = new(new(1, "#112233"), new(2), new(3), new(4)) });
        table = table.SetCell(1, 1, table.Rows[1][1] with { Blocks = [new Section { Blocks = [new Paragraph("hidden")] }] });
        table = table.MergeCells(0, 0, 2, 2);
        var document = new FlowDocument([new Section
        {
            Background = "#001122", BorderColor = "#334455", Padding = 9, PaddingEdges = new(3, 4, 5, 6), Borders = new(Top: new(2, "#334455")),
            Semantic = SectionSemantic.Quote, Blocks = [table, new Section { Semantic = SectionSemantic.CodeBlock, CodeLanguage = "csharp", Blocks = [new Paragraph("return 42;", style)] }]
        }])
        {
            Resources = ImmutableDictionary<string, DocumentResource>.Empty
                .Add("embedded", new() { MediaType = "image/png", Data = [0, 1, 2, 255] })
                .Add("local", new() { Kind = DocumentResourceKind.Local, Location = "assets/a.png", MediaType = "image/png" })
                .Add("host", new() { Kind = DocumentResourceKind.Host, Location = "https://example.test/image.png", MediaType = "image/png" })
        };
        using var stream = new MemoryStream();
        var saved = await Format.SaveWithReportAsync(document, stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(saved.Report.Diagnostics);
        Assert.True(stream.CanWrite);
        stream.Position = 0;
        var loaded = await Format.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict });
        Assert.Empty(loaded.Report.Diagnostics);
        Assert.True(stream.CanRead);
        Assert.Equal(DocumentFormats.Json.Serialize(document), DocumentFormats.Json.Serialize(loaded.Document));
        Assert.Equal(Format.Serialize(document), Format.Serialize(loaded.Document));
    }

    [Fact]
    public async Task Unknown_elements_attributes_events_and_instructions_are_reported_and_never_activated()
    {
        var xml = "<?run do-something?>" + Document("""
            <Paragraph Loaded="RunCode" x:Class="Arbitrary.Type"><Runs><Run Text="safe" /></Runs></Paragraph>
            <Button xmlns="https://github.com/avaloniaui" Click="Execute" />
            <ObjectDataProvider xmlns="clr-namespace:System.Windows.Data;assembly=PresentationFramework" MethodName="Start" />
            <Unknown><Paragraph><Runs><Run Text="omitted" /></Runs></Paragraph></Unknown>
            """, "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = await Format.LoadWithReportAsync(stream);
        Assert.Equal("safe", result.Document.Text);
        Assert.Equal(3, result.Report.Diagnostics.Count(d => d.Code == "xaml.unsupported-element"));
        Assert.Equal(2, result.Report.Diagnostics.Count(d => d.Code == "xaml.unsupported-attribute"));
        Assert.Contains(result.Report.Diagnostics, d => d.Code == "xaml.processing-instruction");
        Assert.All(result.Report.Diagnostics, d => Assert.StartsWith("line ", d.SourceLocation));
        stream.Position = 0;
        await Assert.ThrowsAsync<DocumentConversionException>(() => Format.LoadWithReportAsync(stream, new() { Mode = ConversionMode.Strict }));
    }

    [Fact]
    public void Inline_names_and_markup_extension_syntax_remain_inert_data()
    {
        var xml = Document($$"""
            <Paragraph><Runs><Run><Style FontFamily="{x:Static Dangerous.Type}" /><Inline AltText="fallback">
              <Control Type="{{typeof(ConstructorProbe).FullName}}">
                <Property Name="Text" Value="{Binding Path=Secrets}" />
                <Property Name="Click" Value="Execute" />
              </Control>
            </Inline></Run></Runs></Paragraph>
            """);
        ConstructorProbe.Constructions = 0;
        var document = Format.Parse(xml);
        var run = Assert.IsType<Paragraph>(document.Blocks[0]).Runs[0];
        var payload = Assert.IsType<ControlInlinePayload>(run.Inline!.Payload);
        Assert.Equal(typeof(ConstructorProbe).FullName, payload.Type);
        Assert.Equal("{Binding Path=Secrets}", payload.Properties["Text"]);
        Assert.Equal("Execute", payload.Properties["Click"]);
        Assert.Equal("{x:Static Dangerous.Type}", run.Style.FontFamily);
        Assert.Equal(0, ConstructorProbe.Constructions);
        Assert.Equal("fallback", document.PlainText);
    }

    public sealed class ConstructorProbe : Avalonia.Controls.Control
    {
        public static int Constructions;
        public ConstructorProbe() => Constructions++;
    }

    [Theory]
    [InlineData("<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///never-read'>]>")]
    [InlineData("<!DOCTYPE Document [<!ENTITY x 'expanded'>]>")]
    public void Dtd_and_entities_are_prohibited(string declaration) =>
        Assert.Throws<XmlException>(() => Format.Parse(declaration + Document("<Paragraph />")));

    [Fact]
    public void Limits_apply_even_to_unknown_content()
    {
        var deep = string.Concat(Enumerable.Repeat("<Unknown>", XamlDocumentFormat.MaximumDepth + 1)) +
            string.Concat(Enumerable.Repeat("</Unknown>", XamlDocumentFormat.MaximumDepth + 1));
        Assert.Throws<FormatException>(() => Format.Parse(Document(deep)));
        Assert.Throws<FormatException>(() => Format.Parse(new string(' ', XamlDocumentFormat.MaximumCharacters + 1)));
    }

    [Theory]
    [InlineData("<Document xmlns='https://github.com/avaloniaui' Version='1' />")]
    [InlineData("<Document xmlns='urn:textalonia:document:1' Version='1'><Blocks/><Blocks/></Document>")]
    [InlineData("<Document xmlns='urn:textalonia:document:1' Version='1'><Blocks><Paragraph Id='bad'/></Blocks></Document>")]
    public void Malformed_vocabulary_is_rejected(string xml) => Assert.Throws<FormatException>(() => Format.Parse(xml));

    [Fact]
    public void Unsupported_version_and_unsafe_hyperlink_are_rejected()
    {
        Assert.Throws<NotSupportedException>(() => Format.Parse(Document("").Replace("Version='1'", "Version='99'")));
        Assert.Throws<FormatException>(() => Format.Parse(Document("<Paragraph><DefaultStyle Hyperlink='javascript:alert(1)'/></Paragraph>")));
    }

    [Fact]
    public async Task Unknown_inline_payload_falls_back_to_alternative_text_with_a_report()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Document("<Paragraph><Runs><Run><Inline AltText='fallback'><LiveControl /></Inline></Run></Runs></Paragraph>")));
        var loaded = await Format.LoadWithReportAsync(stream);
        Assert.Equal("fallback", loaded.Document.Text);
        Assert.Contains(loaded.Report.Diagnostics, d => d.Code == "xaml.inline-payload");
    }

    [Fact]
    public void Full_model_depth_with_table_wrappers_round_trips()
    {
        Block block = new Paragraph("deep");
        for (var depth = 0; depth < 32; depth++)
            block = new Table { Rows = [[new TableCell { Blocks = [block] }]] };
        var document = new FlowDocument([block]);
        var xml = Format.Serialize(document);
        Assert.Equal(DocumentFormats.Json.Serialize(document), DocumentFormats.Json.Serialize(Format.Parse(xml)));
    }

    [Theory]
    [InlineData("before\0after")]
    [InlineData("before\u0001after")]
    public void Xml_unrepresentable_text_is_rejected_on_export(string text) =>
        Assert.Throws<FormatException>(() => Format.Serialize(FlowDocument.FromText(text)));
}
