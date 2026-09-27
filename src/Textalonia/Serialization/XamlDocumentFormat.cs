using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>
/// Textalonia's versioned XML data vocabulary. This is not an Avalonia/WPF XAML loader:
/// names select only the explicit model records below and never CLR types or live controls.
/// </summary>
public sealed class XamlDocumentFormat : TextDocumentFormat
{
    public const string NamespaceUri = "urn:textalonia:document:1";
    public const int MaximumCharacters = 32 * 1024 * 1024;
    // Every model nesting level can add Blocks/Table/Rows/Row/Cell wrappers. Allow
    // the full model depth (32), including style and descriptor children.
    public const int MaximumDepth = 256;
    private static readonly XNamespace Ns = NamespaceUri;
    public override string Name => "Textalonia XAML data";
    public override IReadOnlyList<string> Extensions => [".txaml", ".xaml"];

    public override FlowDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumCharacters) throw new FormatException("XAML exceeds the 32 MB character limit.");
        var settings = ReaderSettings();
        // Check the forward-only reader before allocating an XML tree. In particular, deeply
        // nested unknown content must not evade limits simply because it will be omitted.
        ValidateXmlBounds(text, settings);
        using var input = XmlReader.Create(new StringReader(text), settings);
        var xml = XDocument.Load(input, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        var root = xml.Root ?? throw new FormatException("Missing Document element.");
        if (root.Name != Ns + "Document") throw new FormatException("Expected a Textalonia Document in " + NamespaceUri + ".");
        if (Required(root, "Version") is not ("1" or "2" or "3")) throw new NotSupportedException("Only Textalonia XAML data versions 1, 2 and 3 are supported.");
        foreach (var instruction in xml.DescendantNodes().OfType<XProcessingInstruction>())
            Report("xaml.processing-instruction", instruction.Target, "Processing instruction was ignored.", instruction);
        Check(root, "Version", "Resources Blocks Styles Defaults Theme Fonts Sections Stories Notes FootnoteSettings EndnoteSettings Bookmarks Fields Properties");
        var document = new FlowDocument(ReadBlocks(Child(root, "Blocks")))
        {
            Resources = ReadResources(Child(root, "Resources")),
            Styles = ReadData<DocumentStyleCatalog>(Child(root, "Styles")) ?? new(),
            Defaults = ReadData<DocumentDefaults>(Child(root, "Defaults")) ?? new(),
            Theme = ReadData<DocumentTheme>(Child(root, "Theme")) ?? new(),
            Fonts = (ReadData<DocumentFontDefinition[]>(Child(root, "Fonts")) ?? []).ToImmutableArray(),
            Sections = (ReadData<DocumentSection[]>(Child(root, "Sections")) ?? []).ToImmutableArray(),
            Stories = ReadData<ImmutableDictionary<Guid, DocumentStory>>(Child(root, "Stories")) ?? ImmutableDictionary<Guid, DocumentStory>.Empty,
            Notes = (ReadData<DocumentNote[]>(Child(root, "Notes")) ?? []).ToImmutableArray(),
            Bookmarks = (ReadData<DocumentBookmark[]>(Child(root, "Bookmarks")) ?? []).ToImmutableArray(),
            Fields = (ReadData<DocumentField[]>(Child(root, "Fields")) ?? []).ToImmutableArray(),
            Properties = ReadData<ImmutableDictionary<string, string>>(Child(root, "Properties")) ?? ImmutableDictionary<string, string>.Empty,
            FootnoteSettings = ReadData<NoteSettings>(Child(root, "FootnoteSettings")) ?? new(),
            EndnoteSettings = ReadData<NoteSettings>(Child(root, "EndnoteSettings")) ?? new() { Placement = NotePlacement.DocumentEnd }
        };
        document.Validate();
        return document;
    }

    public override string Serialize(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var root = Element("Document", Attr("Version", 3),
            WriteData("Styles", document.Styles), WriteData("Defaults", document.Defaults), WriteData("Theme", document.Theme), WriteData("Fonts", document.Fonts), WriteData("Sections", document.Sections),
            WriteData("Stories", document.Stories), WriteData("Notes", document.Notes), WriteData("FootnoteSettings", document.FootnoteSettings), WriteData("EndnoteSettings", document.EndnoteSettings),
            WriteData("Bookmarks", document.Bookmarks), WriteData("Fields", document.Fields), WriteData("Properties", document.Properties),
            Element("Resources", document.Resources.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
                Element("Resource", Attr("Key", p.Key), Attr("Kind", p.Value.Kind), Attr("MediaType", p.Value.MediaType),
                    Attr("Location", p.Value.Location), Element("Data", Convert.ToBase64String(p.Value.Data.AsSpan()))))),
            WriteBlocks("Blocks", document.Blocks));
        var text = new StringBuilder();
        try
        {
            using var writer = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.Entitize });
            root.WriteTo(writer);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Document contains text not representable in XML 1.0.", exception);
        }
        var result = text.ToString();
        if (result.Length > MaximumCharacters || Encoding.UTF8.GetByteCount(result) > MaximumCharacters)
            throw new FormatException("XAML output exceeds the 32 MiB UTF-8 document limit.");
        // Export must not produce an artifact that its own bounded reader cannot load.
        ValidateXmlBounds(result, ReaderSettings());
        return result;
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
        MaxCharactersInDocument = MaximumCharacters, MaxCharactersFromEntities = 1024, IgnoreComments = true
    };

    private static void ValidateXmlBounds(string text, XmlReaderSettings settings)
    {
        using var reader = XmlReader.Create(new StringReader(text), settings);
        var nodes = 0;
        while (reader.Read())
            if (reader.Depth > MaximumDepth || ++nodes > 1_000_000)
                throw new FormatException("XAML structure exceeds the import limits.");
    }

    private static ImmutableDictionary<string, DocumentResource> ReadResources(XElement? parent)
    {
        if (parent is null) return ImmutableDictionary<string, DocumentResource>.Empty;
        Check(parent, "", "Resource");
        var result = ImmutableDictionary.CreateBuilder<string, DocumentResource>(StringComparer.Ordinal);
        long total = 0;
        foreach (var element in parent.Elements(Ns + "Resource"))
        {
            Check(element, "Key Kind MediaType Location", "Data");
            var data = Child(element, "Data");
            if (data is not null) Check(data, "", "", text: true);
            var encoded = data is null ? "" : DirectText(data);
            if (encoded.Length > (DocumentResource.MaximumEmbeddedBytes + 2L) / 3 * 4)
                throw new FormatException("Embedded XAML resource exceeds the size limit.");
            var bytes = Convert.FromBase64String(encoded);
            if ((total += bytes.Length) > DocumentResource.MaximumDocumentEmbeddedBytes)
                throw new FormatException("Embedded XAML resources exceed the document size limit.");
            if (!result.TryAdd(Required(element, "Key"), new DocumentResource
            {
                Kind = EnumValue(element, "Kind", DocumentResourceKind.Embedded),
                MediaType = Value(element, "MediaType") ?? "application/octet-stream",
                Location = Value(element, "Location"), Data = ImmutableArray.CreateRange(bytes)
            })) throw new FormatException("Duplicate resource key.");
            if (result.Count > 4096) throw new FormatException("Too many document resources.");
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<Block> ReadBlocks(XElement? parent, bool ensureNonempty = true)
    {
        if (parent is null) return ensureNonempty ? [new Paragraph()] : [];
        Check(parent, "", "Paragraph Section Table");
        var blocks = parent.Elements().Where(e => e.Name.Namespace == Ns).Select<XElement, Block?>(element => element.Name.LocalName switch
        {
            "Paragraph" => ReadParagraph(element), "Section" => ReadSection(element), "Table" => ReadTable(element), _ => null
        }).OfType<Block>().ToImmutableArray();
        return ensureNonempty ? FlowDocument.EnsureBlocks(blocks) : blocks;
    }

    private static Paragraph ReadParagraph(XElement element)
    {
        Check(element, "Id", "ParagraphStyle DefaultStyle Runs");
        var runs = Child(element, "Runs");
        if (runs is not null) Check(runs, "", "Run");
        return new Paragraph
        {
            Id = Identity(element), Style = ReadParagraphStyle(Child(element, "ParagraphStyle")),
            DefaultStyle = ReadTextStyle(Child(element, "DefaultStyle")),
            Runs = runs?.Elements(Ns + "Run").Select(ReadRun).ToImmutableArray() ?? []
        };
    }

    private static Section ReadSection(XElement element)
    {
        Check(element, "Id Background BorderColor Padding Semantic CodeLanguage", "PaddingEdges Borders Blocks");
        return new Section
        {
            Id = Identity(element), Background = Value(element, "Background"), BorderColor = Value(element, "BorderColor"),
            Padding = Number(element, "Padding", 12), PaddingEdges = ReadEdges(Child(element, "PaddingEdges")),
            Borders = ReadBorders(Child(element, "Borders")), Blocks = ReadBlocks(Child(element, "Blocks")),
            Semantic = EnumValue(element, "Semantic", SectionSemantic.None), CodeLanguage = Value(element, "CodeLanguage")
        };
    }

    private static Table ReadTable(XElement element)
    {
        Check(element, "Id StyleId", "ColumnWidths RowSizing Rows StyleOverrides");
        var columns = Child(element, "ColumnWidths");
        if (columns is not null) Check(columns, "", "Column");
        var sizes = Child(element, "RowSizing");
        if (sizes is not null) Check(sizes, "", "RowSize");
        var rows = Child(element, "Rows");
        if (rows is not null) Check(rows, "", "Row");
        return new Table
        {
            Id = Identity(element), StyleId = Value(element, "StyleId"),
            StyleOverrides = ReadData<TableStyleOverrides>(Child(element, "StyleOverrides")),
            ColumnWidths = columns?.Elements(Ns + "Column").Select(column =>
            { Check(column, "Width", ""); return Number(column, "Width", 1); }).ToImmutableArray() ?? [],
            RowSizing = sizes?.Elements(Ns + "RowSize").Select(size =>
            {
                Check(size, "Mode Height", "");
                return new TableRowSizing { Mode = EnumValue(size, "Mode", TableRowHeightMode.Auto), Height = Number(size, "Height", 0) };
            }).ToImmutableArray() ?? [],
            Rows = rows?.Elements(Ns + "Row").Select(row =>
            { Check(row, "", "Cell"); return row.Elements(Ns + "Cell").Select(ReadCell).ToImmutableArray(); }).ToImmutableArray() ?? []
        };
    }

    private static TableCell ReadCell(XElement element)
    {
        Check(element, "Id ColumnSpan RowSpan Background", "Padding Borders Blocks MergeOriginalBlocks");
        return new TableCell
        {
            Id = Identity(element), ColumnSpan = Integer(element, "ColumnSpan", 1), RowSpan = Integer(element, "RowSpan", 1),
            Background = Value(element, "Background"), Padding = ReadEdges(Child(element, "Padding")), Borders = ReadBorders(Child(element, "Borders")),
            Blocks = ReadBlocks(Child(element, "Blocks")), MergeOriginalBlocks = ReadBlocks(Child(element, "MergeOriginalBlocks"), false)
        };
    }

    private static RichRun ReadRun(XElement element)
    {
        Check(element, "Text", "Style Inline");
        var style = ReadTextStyle(Child(element, "Style"));
        var inline = Child(element, "Inline");
        if (inline is null) return new RichRun(Value(element, "Text") ?? "", style);
        Check(inline, "Id AltText Width Height", "Image Control MergeField Note PageField");
        var image = Child(inline, "Image");
        var control = Child(inline, "Control");
        var mergeField = Child(inline, "MergeField");
        var note = Child(inline, "Note");
        var pageField = Child(inline, "PageField");
        if ((image is not null ? 1 : 0) + (control is not null ? 1 : 0) + (mergeField is not null ? 1 : 0) + (note is not null ? 1 : 0) + (pageField is not null ? 1 : 0) > 1)
            throw new FormatException("Inline must contain exactly one payload.");
        InlinePayload payload;
        if (image is not null)
        {
            Check(image, "ResourceId", "");
            payload = new ImageInlinePayload(Required(image, "ResourceId"));
        }
        else if (control is not null)
        {
            Check(control, "Type", "Property");
            var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var property in control.Elements(Ns + "Property"))
            {
                Check(property, "Name Value", "");
                if (!properties.TryAdd(Required(property, "Name"), Required(property, "Value")))
                    throw new FormatException("Duplicate inline property name.");
                if (properties.Count > 128) throw new FormatException("Too many inline properties.");
            }
            payload = new ControlInlinePayload(Required(control, "Type")) { Properties = properties.ToImmutable() };
        }
        else if (note is not null)
        {
            Check(note, "NoteId", "");
            payload = new NoteInlinePayload(Guid.Parse(Required(note, "NoteId")));
        }
        else if (pageField is not null)
        {
            Check(pageField, "Field", "");
            payload = new PageFieldInlinePayload(EnumValue(pageField, "Field", PageFieldKind.Page));
        }
        else if (mergeField is not null)
        {
            Check(mergeField, "Name Format FallbackText", "");
            payload = new MergeFieldInlinePayload(Required(mergeField, "Name"))
            {
                Format = Value(mergeField, "Format"), FallbackText = Value(mergeField, "FallbackText")
            };
        }
        else
        {
            Report("xaml.inline-payload", "Missing or unsupported inline payload", "Alternative text was retained.", inline);
            return new RichRun(Value(inline, "AltText") ?? "", style);
        }
        if (Value(element, "Text") is { } text && text != "\uFFFC")
            Report("xaml.inline-text", "Text alongside an inline descriptor", "The inline descriptor's atomic position was retained.", element);
        return new RichRun(new InlineDescriptor
        {
            Id = Identity(inline), AltText = Value(inline, "AltText") ?? (payload is MergeFieldInlinePayload field ? "\u00AB" + field.Name + "\u00BB" : ""), Width = Number(inline, "Width", 32),
            Height = Number(inline, "Height", 32), Payload = payload
        }, style);
    }

    private static TextStyle ReadTextStyle(XElement? element)
    {
        if (element is null) return TextStyle.Default;
        if (Child(element, "Data") is { } data)
        {
            if (element.Attributes().Any(a => !a.IsNamespaceDeclaration) || element.Elements().Count() != 1)
                throw new FormatException("Style Data cannot coexist with legacy formatting attributes.");
            return ReadData<TextStyle>(data)!;
        }
        Check(element, "FontFamily FontSize Bold FontWeight FontStretch Italic Underline Strikethrough Foreground Background Hyperlink Baseline IsCode", "");
        return new TextStyle
        {
            FontFamily = Value(element, "FontFamily"), FontSize = Number(element, "FontSize", 16), Bold = Boolean(element, "Bold"),
            FontWeight = NullableInteger(element, "FontWeight"), FontStretch = Integer(element, "FontStretch", 5),
            Italic = Boolean(element, "Italic"), Underline = Boolean(element, "Underline"), Strikethrough = Boolean(element, "Strikethrough"),
            Foreground = Value(element, "Foreground"), Background = Value(element, "Background"), Hyperlink = Value(element, "Hyperlink"),
            Baseline = EnumValue(element, "Baseline", Baseline.Normal), IsCode = Boolean(element, "IsCode")
        };
    }

    private static ParagraphStyle ReadParagraphStyle(XElement? element)
    {
        if (element is null) return ParagraphStyle.Default;
        if (Child(element, "Data") is { } data)
        {
            if (element.Attributes().Any(a => !a.IsNamespaceDeclaration) || element.Elements().Count() != 1)
                throw new FormatException("Style Data cannot coexist with legacy formatting attributes.");
            return ReadData<ParagraphStyle>(data)!;
        }
        Check(element, "Alignment List ListLevel ListId ListStart ListRestart HeadingLevel SpaceBefore SpaceAfter Indent RightIndent FirstLineIndent LineHeight LetterSpacing RightToLeft", "ListDefinition");
        var definition = Child(element, "ListDefinition");
        if (definition is not null) Check(definition, "", "Level");
        return new ParagraphStyle
        {
            Alignment = EnumValue(element, "Alignment", ParagraphAlignment.Left), List = EnumValue(element, "List", ListKind.None),
            ListLevel = Integer(element, "ListLevel", 0), ListId = NullableIdentity(element, "ListId"), ListStart = NullableInteger(element, "ListStart"),
            ListRestart = Boolean(element, "ListRestart"), HeadingLevel = Integer(element, "HeadingLevel", 0),
            SpaceBefore = Number(element, "SpaceBefore", 0), SpaceAfter = Number(element, "SpaceAfter", 8), Indent = Number(element, "Indent", 0),
            RightIndent = Number(element, "RightIndent", 0), FirstLineIndent = Number(element, "FirstLineIndent", 0),
            LineHeight = NullableNumber(element, "LineHeight"), LetterSpacing = Number(element, "LetterSpacing", 0), RightToLeft = Boolean(element, "RightToLeft"),
            ListDefinition = definition is null ? null : new ListDefinition
            {
                Levels = definition.Elements(Ns + "Level").Select(level =>
                {
                    Check(level, "Start Kind Marker Text Prefix Suffix IncludeAncestors", "");
                    return new ListLevelDefinition
                    {
                        Start = Integer(level, "Start", 1), Kind = EnumValue(level, "Kind", ListKind.Numbered), Marker = EnumValue(level, "Marker", ListMarkerStyle.Decimal),
                        Text = Value(level, "Text"), Prefix = Value(level, "Prefix") ?? "", Suffix = Value(level, "Suffix") ?? ".", IncludeAncestors = Boolean(level, "IncludeAncestors")
                    };
                }).ToImmutableArray()
            }
        };
    }

    private static EdgeInsets? ReadEdges(XElement? element)
    {
        if (element is null) return null;
        Check(element, "Left Top Right Bottom", "");
        return new(Number(element, "Left", 0), Number(element, "Top", 0), Number(element, "Right", 0), Number(element, "Bottom", 0));
    }

    private static BlockBorders? ReadBorders(XElement? element)
    {
        if (element is null) return null;
        Check(element, "", "Left Top Right Bottom");
        BorderSide? Side(string name)
        {
            var side = Child(element, name);
            if (side is null) return null;
            Check(side, "Width Color", "");
            return new(Number(side, "Width", 0), Value(side, "Color"));
        }
        return new(Side("Left"), Side("Top"), Side("Right"), Side("Bottom"));
    }

    private static XElement WriteBlocks(string name, IEnumerable<Block> blocks) => Element(name, blocks.Select(WriteBlock));
    private static XElement WriteBlock(Block block) => block switch
    {
        Paragraph p => Element("Paragraph", Attr("Id", p.Id), WriteParagraphStyle(p.Style), WriteTextStyle("DefaultStyle", p.DefaultStyle),
            Element("Runs", p.Runs.Select(run => Element("Run", Attr("Text", run.Text), WriteTextStyle("Style", run.Style), WriteInline(run.Inline))))),
        Section s => Element("Section", Attr("Id", s.Id), Attr("Background", s.Background), Attr("BorderColor", s.BorderColor), Attr("Padding", s.Padding),
            Attr("Semantic", s.Semantic), Attr("CodeLanguage", s.CodeLanguage), WriteEdges("PaddingEdges", s.PaddingEdges), WriteBorders(s.Borders), WriteBlocks("Blocks", s.Blocks)),
        Table t => Element("Table", Attr("Id", t.Id), Attr("StyleId", t.StyleId),
            t.StyleOverrides is null ? null : WriteData("StyleOverrides", t.StyleOverrides), Element("ColumnWidths", t.ColumnWidths.Select(width => Element("Column", Attr("Width", width)))),
            Element("RowSizing", t.RowSizing.Select(size => Element("RowSize", Attr("Mode", size.Mode), Attr("Height", size.Height)))),
            Element("Rows", t.Rows.Select(row => Element("Row", row.Select(cell => Element("Cell", Attr("Id", cell.Id), Attr("ColumnSpan", cell.ColumnSpan),
                Attr("RowSpan", cell.RowSpan), Attr("Background", cell.Background), WriteEdges("Padding", cell.Padding), WriteBorders(cell.Borders),
                WriteBlocks("Blocks", cell.Blocks), WriteBlocks("MergeOriginalBlocks", cell.MergeOriginalBlocks))))))),
        _ => throw new FormatException("Unsupported block type.")
    };

    private static XElement? WriteInline(InlineDescriptor? inline)
    {
        if (inline is null) return null;
        return Element("Inline", Attr("Id", inline.Id), Attr("AltText", inline.AltText), Attr("Width", inline.Width), Attr("Height", inline.Height), inline.Payload switch
        {
            ImageInlinePayload image => Element("Image", Attr("ResourceId", image.ResourceId)),
            NoteInlinePayload note => Element("Note", Attr("NoteId", note.NoteId)),
            PageFieldInlinePayload page => Element("PageField", Attr("Field", page.Field)),
            MergeFieldInlinePayload field => Element("MergeField", Attr("Name", field.Name), Attr("Format", field.Format), Attr("FallbackText", field.FallbackText)),
            ControlInlinePayload control => Element("Control", Attr("Type", control.Type), control.Properties.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => Element("Property", Attr("Name", p.Key), Attr("Value", p.Value)))),
            _ => throw new FormatException("Unsupported inline payload.")
        });
    }

    private static XElement WriteTextStyle(string name, TextStyle style) =>
        HasExtended(style, TextStyle.Default, "fontFamily fontSize bold fontWeight fontStretch italic underline strikethrough foreground background hyperlink baseline isCode")
        ? Element(name, WriteData("Data", style)) : Element(name,
        Attr("FontFamily", style.FontFamily), Attr("FontSize", style.FontSize), Attr("Bold", style.Bold), Attr("FontWeight", style.FontWeight), Attr("FontStretch", style.FontStretch),
        Attr("Italic", style.Italic), Attr("Underline", style.Underline), Attr("Strikethrough", style.Strikethrough), Attr("Foreground", style.Foreground), Attr("Background", style.Background),
        Attr("Hyperlink", style.Hyperlink), Attr("Baseline", style.Baseline), Attr("IsCode", style.IsCode));

    private static XElement WriteParagraphStyle(ParagraphStyle style) =>
        HasExtended(style, ParagraphStyle.Default, "alignment list listLevel listId listStart listRestart headingLevel spaceBefore spaceAfter indent rightIndent firstLineIndent lineHeight letterSpacing rightToLeft listDefinition")
        ? Element("ParagraphStyle", WriteData("Data", style)) : Element("ParagraphStyle",
        Attr("Alignment", style.Alignment), Attr("List", style.List), Attr("ListLevel", style.ListLevel), Attr("ListId", style.ListId), Attr("ListStart", style.ListStart),
        Attr("ListRestart", style.ListRestart), Attr("HeadingLevel", style.HeadingLevel), Attr("SpaceBefore", style.SpaceBefore), Attr("SpaceAfter", style.SpaceAfter),
        Attr("Indent", style.Indent), Attr("RightIndent", style.RightIndent), Attr("FirstLineIndent", style.FirstLineIndent), Attr("LineHeight", style.LineHeight),
        Attr("LetterSpacing", style.LetterSpacing), Attr("RightToLeft", style.RightToLeft), style.ListDefinition is null ? null : Element("ListDefinition", style.ListDefinition.Levels.Select(level =>
            Element("Level", Attr("Start", level.Start), Attr("Kind", level.Kind), Attr("Marker", level.Marker), Attr("Text", level.Text), Attr("Prefix", level.Prefix),
                Attr("Suffix", level.Suffix), Attr("IncludeAncestors", level.IncludeAncestors)))));

    private static XElement? WriteEdges(string name, EdgeInsets? edges) => edges is null ? null :
        Element(name, Attr("Left", edges.Left), Attr("Top", edges.Top), Attr("Right", edges.Right), Attr("Bottom", edges.Bottom));
    private static XElement? WriteBorders(BlockBorders? borders)
    {
        XElement? Side(string name, BorderSide? side) => side is null ? null : Element(name, Attr("Width", side.Width), Attr("Color", side.Color));
        return borders is null ? null : Element("Borders", Side("Left", borders.Left), Side("Top", borders.Top), Side("Right", borders.Right), Side("Bottom", borders.Bottom));
    }

    // Typed JSON payloads carry the extensible style vocabulary; no CLR type names or
    // executable XAML are accepted. Legacy attributes continue to read and write unchanged.
    private static XElement WriteData<T>(string name, T value) => Element(name,
        JsonSerializer.Serialize(value, JsonDocumentFormat.Options));

    private static T? ReadData<T>(XElement? element) where T : class
    {
        if (element is null) return null;
        Check(element, "", "", text: true);
        using var json = JsonDocument.Parse(DirectText(element), new JsonDocumentOptions { MaxDepth = 256 });
        JsonDocumentFormat.ValidateUniqueMembers(json.RootElement);
        return json.RootElement.Deserialize<T>(JsonDocumentFormat.Options) ?? throw new FormatException("Missing formatting data.");
    }

    private static bool HasExtended<T>(T value, T defaults, string legacy)
    {
        var properties = legacy.Split(' ').ToHashSet(StringComparer.Ordinal);
        var data = JsonSerializer.SerializeToElement(value, JsonDocumentFormat.Options);
        var baseline = JsonSerializer.SerializeToElement(defaults, JsonDocumentFormat.Options);
        return data.EnumerateObject().Any(p => !properties.Contains(p.Name) &&
            p.Value.GetRawText() != baseline.GetProperty(p.Name).GetRawText());
    }

    private static XElement Element(string name, params object?[] content) => new(Ns + name, content);
    private static XAttribute? Attr(string name, object? value) => value is null ? null : new XAttribute(name, value);
    private static string? Value(XElement element, string name) => (string?)element.Attribute(name);
    private static string Required(XElement element, string name) => Value(element, name) ?? throw new FormatException($"{element.Name.LocalName} requires {name}.");
    private static Guid Identity(XElement element) => NullableIdentity(element, "Id") ?? Guid.NewGuid();
    private static Guid? NullableIdentity(XElement element, string name) => Value(element, name) is not { } value ? null :
        Guid.TryParse(value, out var result) ? result : throw new FormatException($"Invalid {name} identifier.");
    private static int Integer(XElement element, string name, int fallback) => NullableInteger(element, name) ?? fallback;
    private static int? NullableInteger(XElement element, string name) => Value(element, name) is not { } value ? null :
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new FormatException($"Invalid {name} integer.");
    private static double Number(XElement element, string name, double fallback) => NullableNumber(element, name) ?? fallback;
    private static double? NullableNumber(XElement element, string name) => Value(element, name) is not { } value ? null :
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : throw new FormatException($"Invalid {name} number.");
    private static bool Boolean(XElement element, string name) => Value(element, name) is not { } value ? false : value switch
    { "true" or "1" => true, "false" or "0" => false, _ => throw new FormatException($"Invalid {name} boolean.") };
    private static T EnumValue<T>(XElement element, string name, T fallback) where T : struct, Enum => Value(element, name) is not { } value ? fallback :
        Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) && Enum.TryParse<T>(value, out var result) ? result : throw new FormatException($"Invalid {name} value.");
    private static XElement? Child(XElement parent, string name)
    {
        var elements = parent.Elements(Ns + name).Take(2).ToArray();
        return elements.Length > 1 ? throw new FormatException($"Duplicate {name} element.") : elements.SingleOrDefault();
    }
    private static string DirectText(XElement element) => string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value));

    private static void Check(XElement element, string attributes, string children, bool text = false)
    {
        var allowedAttributes = attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allowedChildren = children.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var attribute in element.Attributes())
            if (!attribute.IsNamespaceDeclaration && (attribute.Name.Namespace != XNamespace.None || !allowedAttributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal)))
                Report("xaml.unsupported-attribute", attribute.Name.ToString(), "Attribute was ignored; no behavior was attached.", attribute);
        foreach (var child in element.Elements())
            if (child.Name.Namespace != Ns || !allowedChildren.Contains(child.Name.LocalName, StringComparer.Ordinal))
                Report("xaml.unsupported-element", child.Name.ToString(), "Element and its contents were omitted.", child);
        if (!text && element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            Report("xaml.unexpected-text", element.Name.LocalName + " text content", "Text outside the documented data fields was omitted.", element);
    }

    private static void Report(string code, string feature, string fallback, XObject source)
    {
        var line = (IXmlLineInfo)source;
        ConversionDiagnostics.Report(code, feature, fallback, sourceLocation: line.HasLineInfo() ? $"line {line.LineNumber}, column {line.LinePosition}" : null);
    }
}
