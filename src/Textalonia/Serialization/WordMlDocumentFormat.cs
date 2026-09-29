using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>
/// A bounded Word 2003 XML (WordML) text codec. It retains body paragraphs, their
/// alignment, and basic run formatting; unsupported WordML is reported as loss.
/// This vocabulary is distinct from OOXML, Flat OPC, and Textalonia XAML.
/// </summary>
public sealed class WordMlDocumentFormat : TextDocumentFormat
{
    public const string NamespaceUri = "http://schemas.microsoft.com/office/word/2003/wordml";
    private const string AuxiliaryNamespaceUri = "http://schemas.microsoft.com/office/word/2003/auxHint";
    private const int MaximumCharacters = 32 * 1024 * 1024;
    private const int MaximumNodes = 100_000;
    private const int MaximumDepth = 128;
    private static readonly XNamespace W = NamespaceUri;
    private static readonly XNamespace Wx = AuxiliaryNamespaceUri;

    public override string Name => "Word 2003 XML";
    public override IReadOnlyList<string> Extensions => [".wordml", ".xml"];

    public override FlowDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumCharacters) throw new FormatException("WordML exceeds the 32 MB character limit.");
        try
        {
            // Scan before building an XML tree, including unknown content that will be dropped.
            using (var scan = XmlReader.Create(new StringReader(text), ReaderSettings())) ValidateBounds(scan);
            using var reader = XmlReader.Create(new StringReader(text), ReaderSettings());
            return ReadDocument(reader);
        }
        catch (XmlException error) { throw new FormatException("Invalid WordML XML.", error); }
    }

    public override async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // XmlReader honors the XML encoding declaration as well as Unicode BOMs.
                using (var scan = XmlReader.Create(new MemoryStream(bytes, false), ReaderSettings()))
                    ValidateBounds(scan, cancellationToken);
                using var reader = XmlReader.Create(new MemoryStream(bytes, false), ReaderSettings());
                var result = ReadDocument(reader);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (XmlException error) { throw new FormatException("Invalid WordML XML.", error); }
        }, cancellationToken);
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumCharacters,
        MaxCharactersFromEntities = MaximumCharacters,
        IgnoreWhitespace = false
    };

    private static void ValidateBounds(XmlReader reader, CancellationToken token = default)
    {
        var nodes = 0;
        while (reader.Read())
        {
            if ((++nodes & 1023) == 0) token.ThrowIfCancellationRequested();
            if (nodes > MaximumNodes || reader.Depth > MaximumDepth)
                throw new FormatException("WordML structure exceeds the import limits.");
        }
        token.ThrowIfCancellationRequested();
    }

    private static FlowDocument ReadDocument(XmlReader reader)
    {
        var xml = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        var root = xml.Root ?? throw new FormatException("Missing WordML document root.");
        if (root.Name != W + "wordDocument")
            throw new FormatException("Expected a Word 2003 XML wordDocument root.");
        CheckAttributes(root);
        CheckUnexpectedText(root);
        var bodies = root.Elements(W + "body").ToArray();
        if (bodies.Length != 1) throw new FormatException("WordML must contain exactly one body.");

        foreach (var child in root.Elements())
            if (child != bodies[0]) Unsupported(child);
        foreach (var instruction in xml.DescendantNodes().OfType<XProcessingInstruction>())
            Report("wordml.processing-instruction", "Processing instruction", "Processing instruction ignored.", instruction);

        var blocks = ImmutableArray.CreateBuilder<Block>();
        ReadBlocks(bodies[0], blocks);
        var document = new FlowDocument(FlowDocument.EnsureBlocks(blocks.ToImmutable()));
        document.Validate();
        return document;
    }

    private static void ReadBlocks(XElement container, ImmutableArray<Block>.Builder blocks)
    {
        CheckAttributes(container);
        CheckUnexpectedText(container);
        foreach (var child in container.Elements())
        {
            if (child.Name == W + "p") blocks.Add(ReadParagraph(child));
            else if (child.Name == Wx + "sect")
            {
                Report("wordml.section", "WordML section boundary and page properties",
                    "Visible paragraphs retained in order; section settings omitted.", child);
                ReadBlocks(child, blocks);
            }
            else Unsupported(child);
        }
    }

    private static Paragraph ReadParagraph(XElement element)
    {
        CheckAttributes(element);
        CheckUnexpectedText(element);
        var style = ParagraphStyle.Default;
        var runs = new List<RichRun>();
        var propertySeen = false;
        foreach (var child in element.Elements())
        {
            if (child.Name == W + "pPr")
            {
                if (propertySeen) Report("wordml.duplicate-property", "Repeated paragraph properties", "Last supported value retained.", child);
                style = ReadParagraphStyle(child);
                propertySeen = true;
            }
            else if (child.Name == W + "r") ReadRun(child, runs);
            else Unsupported(child);
        }
        return new Paragraph(runs) { Style = style };
    }

    private static ParagraphStyle ReadParagraphStyle(XElement element)
    {
        CheckAttributes(element);
        CheckUnexpectedText(element);
        var style = ParagraphStyle.Default;
        foreach (var child in element.Elements())
        {
            if (child.Name != W + "jc") { Unsupported(child); continue; }
            CheckAttributes(child, W + "val");
            CheckLeafContent(child);
            var value = (string?)child.Attribute(W + "val");
            var alignment = value switch
            {
                "left" or "start" => ParagraphAlignment.Left,
                "center" => ParagraphAlignment.Center,
                "right" or "end" => ParagraphAlignment.Right,
                "both" => ParagraphAlignment.Justify,
                _ => (ParagraphAlignment?)null
            };
            if (alignment is null) Report("wordml.alignment", "Unsupported paragraph alignment",
                "Paragraph uses left alignment.", child);
            else style = style with { Alignment = alignment.Value };
        }
        return style;
    }

    private static void ReadRun(XElement element, List<RichRun> runs)
    {
        CheckAttributes(element);
        CheckUnexpectedText(element);
        var style = TextStyle.Default;
        var properties = element.Elements(W + "rPr").ToArray();
        if (properties.Length > 1)
            Report("wordml.duplicate-property", "Repeated run properties", "Last supported value retained.", properties[1]);
        foreach (var property in properties) style = ReadRunStyle(property);
        foreach (var child in element.Elements())
        {
            if (child.Name == W + "rPr") continue;
            if (child.Name == W + "t")
            {
                CheckAttributes(child, XNamespace.Xml + "space");
                if (child.HasElements) { Unsupported(child); continue; }
                runs.Add(new RichRun(child.Value, style));
            }
            else if (child.Name == W + "tab")
            {
                CheckAttributes(child);
                CheckLeafContent(child);
                runs.Add(new RichRun("\t", style));
            }
            else if (child.Name == W + "br" || child.Name == W + "cr")
            {
                CheckAttributes(child);
                CheckLeafContent(child);
                runs.Add(new RichRun("\u2028", style));
            }
            else Unsupported(child);
        }
    }

    private static TextStyle ReadRunStyle(XElement element)
    {
        CheckAttributes(element);
        CheckUnexpectedText(element);
        var style = TextStyle.Default;
        foreach (var child in element.Elements())
        {
            if (child.Name == W + "b") style = style with { Bold = ReadToggle(child) };
            else if (child.Name == W + "i") style = style with { Italic = ReadToggle(child) };
            else if (child.Name == W + "u")
            {
                CheckAttributes(child, W + "val");
                CheckLeafContent(child);
                var value = (string?)child.Attribute(W + "val") ?? "single";
                var kind = value switch
                {
                    "none" => UnderlineKind.None,
                    "single" => UnderlineKind.Single,
                    "double" => UnderlineKind.Double,
                    _ => (UnderlineKind?)null
                };
                if (kind is null) Report("wordml.underline", "Unsupported underline style",
                    "Underline style omitted.", child);
                else style = style with { Underline = kind != UnderlineKind.None, UnderlineKind = kind.Value };
            }
            else Unsupported(child);
        }
        return style;
    }

    private static bool ReadToggle(XElement element)
    {
        CheckAttributes(element, W + "val");
        CheckLeafContent(element);
        return ((string?)element.Attribute(W + "val")) switch
        {
            null or "1" or "true" or "on" => true,
            "0" or "false" or "off" => false,
            _ => UnsupportedToggle(element)
        };
    }

    private static bool UnsupportedToggle(XElement element)
    {
        Report("wordml.toggle", "Unsupported boolean property value", "Property omitted.", element);
        return false;
    }

    public override string Serialize(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        ReportDocumentLosses(document);
        var resolved = new DocumentStyleResolver(document).ResolveDocument();
        var body = new XElement(W + "body");
        WriteBlocks(resolved.Blocks, body);
        var root = new XElement(W + "wordDocument",
            new XAttribute(XNamespace.Xmlns + "w", NamespaceUri),
            new XAttribute(XNamespace.Xmlns + "wx", AuxiliaryNamespaceUri), body);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + root.ToString(SaveOptions.DisableFormatting);
    }

    private static void WriteBlocks(IEnumerable<Block> blocks, XElement body)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph: body.Add(WriteParagraph(paragraph)); break;
                case Section section:
                    Report("wordml.section", "Document section structure and decoration",
                        "Visible paragraphs retained in order; section boundaries and decoration omitted.", section.Id);
                    WriteBlocks(section.Blocks, body);
                    break;
                case Table table:
                    Report("wordml.table", "Table structure and cells", "Visible cell paragraphs retained in row order; table geometry omitted.", table.Id);
                    for (var row = 0; row < table.Rows.Length; row++)
                        for (var column = 0; column < table.ColumnCount; column++)
                            if (!table.IsCovered(row, column)) WriteBlocks(table.Rows[row][column].Blocks, body);
                    break;
            }
        }
    }

    private static XElement WriteParagraph(Paragraph paragraph)
    {
        if (paragraph.Style != (ParagraphStyle.Default with { Alignment = paragraph.Style.Alignment }))
            Report("wordml.paragraph-style", "Paragraph formatting beyond alignment", "Only alignment retained.", paragraph.Id);
        if (paragraph.DefaultStyle != TextStyle.Default)
            Report("wordml.paragraph-default-style", "Paragraph default character formatting", "Existing run formatting retained; typing defaults omitted.", paragraph.Id);
        var result = new XElement(W + "p");
        if (paragraph.Style.Alignment != ParagraphAlignment.Left)
            result.Add(new XElement(W + "pPr", new XElement(W + "jc", new XAttribute(W + "val",
                paragraph.Style.Alignment switch
                {
                    ParagraphAlignment.Center => "center", ParagraphAlignment.Right => "right",
                    ParagraphAlignment.Justify => "both", _ => "left"
                }))));
        foreach (var run in paragraph.Runs) result.Add(WriteRun(run, paragraph.Id));
        return result;
    }

    private static XElement WriteRun(RichRun run, Guid paragraphId)
    {
        var style = run.Style;
        var underline = style.UnderlineKind != UnderlineKind.None ? style.UnderlineKind :
            style.Underline ? UnderlineKind.Single : UnderlineKind.None;
        var unsupported = style with { Bold = false, Italic = false, Underline = false, UnderlineKind = UnderlineKind.None };
        if (unsupported != TextStyle.Default || underline is not (UnderlineKind.None or UnderlineKind.Single or UnderlineKind.Double))
            Report("wordml.run-style", "Character formatting beyond bold, italic and single/double underline",
                "Only supported emphasis retained.", paragraphId);
        var element = new XElement(W + "r");
        var properties = new XElement(W + "rPr");
        if (style.Bold) properties.Add(new XElement(W + "b"));
        if (style.Italic) properties.Add(new XElement(W + "i"));
        if (underline != UnderlineKind.None)
            properties.Add(new XElement(W + "u", new XAttribute(W + "val",
                underline == UnderlineKind.Double ? "double" : "single")));
        if (properties.HasElements) element.Add(properties);
        var value = run.Inline is { } inline ? inline.AltText : run.Text;
        if (run.Inline is { } descriptor)
            Report("wordml.inline", "Inline object", "Alternative text retained; object payload and behavior omitted.", descriptor.Id);
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is not ('\t' or '\u2028')) continue;
            if (i > start) element.Add(WriteText(value[start..i]));
            element.Add(new XElement(W + (value[i] == '\t' ? "tab" : "br")));
            start = i + 1;
        }
        if (start < value.Length) element.Add(WriteText(value[start..]));
        return element;
    }

    private static XElement WriteText(string text) => new(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text);

    private static void ReportDocumentLosses(FlowDocument document)
    {
        if (document.Styles.Characters.Count + document.Styles.Paragraphs.Count + document.Styles.Tables.Count != 0 ||
            document.Theme != new DocumentTheme() || document.Defaults != new DocumentDefaults())
            Report("wordml.named-styles", "Named styles, inheritance and theme", "Effective appearance retained only within the supported run and paragraph subset.");
        if (!document.Sections.IsEmpty)
            Report("wordml.page-sections", "Physical page sections", "Page settings and section boundaries omitted.");
        if (!document.Stories.IsEmpty || !document.Notes.IsEmpty)
            Report("wordml.stories", "Headers, footers and notes", "Only the main body retained.");
        if (!document.Fields.IsEmpty || !document.Bookmarks.IsEmpty)
            Report("wordml.fields", "Field instructions and bookmark ranges", "Visible main-body text retained; instructions and ranges omitted.");
        if (document.Resources.Count != 0 || !document.Fonts.IsEmpty)
            Report("wordml.resources", "Document resources and fonts", "Embedded resource bytes omitted.");
        if (!document.Properties.IsEmpty)
            Report("wordml.properties", "Document properties", "Property metadata omitted.");
        if (!document.ContentControls.IsEmpty || !document.PermissionRanges.IsEmpty || document.Protection != new DocumentProtection())
            Report("wordml.controls-protection", "Content controls and edit protection", "Visible text retained; controls and restrictions omitted.");
    }

    private static void CheckAttributes(XElement element, params XName[] allowed)
    {
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration || allowed.Contains(attribute.Name)) continue;
            Report("wordml.unsupported-attribute", "Unsupported WordML attribute " + attribute.Name,
                "Attribute omitted.", attribute);
        }
    }

    private static void CheckUnexpectedText(XElement element)
    {
        foreach (var text in element.Nodes().OfType<XText>().Where(node => !string.IsNullOrWhiteSpace(node.Value)))
            Report("wordml.unexpected-text", "Text outside a WordML text run", "Text omitted.", text);
    }

    private static void CheckLeafContent(XElement element)
    {
        CheckUnexpectedText(element);
        foreach (var child in element.Elements()) Unsupported(child);
    }

    private static void Unsupported(XElement element) =>
        Report("wordml.unsupported-element", "Unsupported WordML element " + element.Name,
            "Element and its content omitted.", element);

    private static void Report(string code, string feature, string fallback, XObject source) =>
        ConversionDiagnostics.Report(code, feature, fallback, sourceLocation: Location(source));

    private static void Report(string code, string feature, string fallback, Guid? modelId = null) =>
        ConversionDiagnostics.Report(code, feature, fallback, modelId);

    private static string? Location(XObject source) => source is IXmlLineInfo line && line.HasLineInfo()
        ? $"line {line.LineNumber}, column {line.LinePosition}" : null;
}

