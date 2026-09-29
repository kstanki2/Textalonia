using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>Bounded OpenDocument Text interchange for flow text, basic styles, lists, tables and one page layout.</summary>
public sealed partial class OdtDocumentFormat : IDocumentFormat
{
    private const string MimeType = "application/vnd.oasis.opendocument.text";
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(index =>
    {
        var value = (uint)index;
        for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0);
        return value;
    }).ToArray();

    public string Name => "OpenDocument Text";
    public IReadOnlyList<string> Extensions => [".odt"];

    public async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        return await Task.Run(() => Read(bytes, cancellationToken), cancellationToken);
    }

    public async Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var bytes = await Task.Run(() => Write(document, cancellationToken), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static FlowDocument Read(byte[] bytes, CancellationToken token)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count > 4096 || archive.Entries.Sum(entry => entry.Length) > 128L * 1024 * 1024)
            throw new FormatException("ODT package exceeds its limits.");
        if (archive.Entries.Any(entry => !SafePartName(entry.FullName)) ||
            archive.Entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new FormatException("ODT package has an unsafe or duplicate part name.");
        long packageBytes = 0;
        byte[] Part(string path, bool required = false)
        {
            token.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(path);
            if (entry is null)
            {
                if (required) throw new FormatException("Missing ODT part: " + path);
                return [];
            }
            if (entry.Length > 32 * 1024 * 1024) throw new FormatException("ODT part exceeds the size limit.");
            using var input = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            var crc = uint.MaxValue;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (output.Length + read > 32 * 1024 * 1024 || packageBytes + read > 128L * 1024 * 1024)
                    throw new FormatException("ODT decompressed content exceeds its limits.");
                for (var i = 0; i < read; i++) crc = CrcTable[(int)((crc ^ buffer[i]) & 255)] ^ (crc >> 8);
                output.Write(buffer, 0, read);
                packageBytes += read;
            }
            if (output.Length != entry.Length || ~crc != entry.Crc32) throw new FormatException("ODT part has an inconsistent size or checksum.");
            return output.ToArray();
        }
        XDocument Xml(string path, bool required)
        {
            var data = Part(path, required);
            if (data.Length == 0 && !required) return new XDocument();
            XDocument result;
            try
            {
                using var source = new MemoryStream(data);
                using var reader = XmlReader.Create(source, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
                result = XDocument.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (XmlException ex) { throw new FormatException("Invalid ODT XML part: " + path, ex); }
            result.AddAnnotation(path);
            if (result.Descendants().Take(200001).Count() > 200000 || result.Descendants().Any(e => e.Ancestors().Take(129).Count() > 128))
                throw new FormatException("ODT XML structure exceeds its limits.");
            return result;
        }
        if (Encoding.ASCII.GetString(Part("mimetype", true)) != MimeType) throw new FormatException("Invalid ODT mimetype.");
        var manifest = Xml("META-INF/manifest.xml", true);
        if (manifest.Root?.Name != Manifest + "manifest") throw new FormatException("Invalid ODT manifest.");
        if (manifest.Descendants(Manifest + "encryption-data").Any()) throw new NotSupportedException("Encrypted ODT packages require a password provider.");
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Root.Elements(Manifest + "file-entry"))
        {
            var path = (string?)entry.Attribute(Manifest + "full-path") ?? throw new FormatException("ODT manifest entry has no path.");
            if (path == "/")
            {
                if ((string?)entry.Attribute(Manifest + "media-type") != MimeType) throw new FormatException("ODT manifest has the wrong root media type.");
                continue;
            }
            if (!SafePartName(path) || !declared.TryAdd(path, (string?)entry.Attribute(Manifest + "media-type") ?? ""))
                throw new FormatException("ODT manifest has an unsafe or duplicate path.");
            if (!path.EndsWith('/') && archive.GetEntry(path) is null) throw new FormatException("ODT manifest references a missing part: " + path);
        }
        var content = Xml("content.xml", true);
        if (content.Root?.Name != Office + "document-content") throw new FormatException("Invalid ODT content root.");
        var stylesXml = Xml("styles.xml", false);
        var context = new ReadContext(content, stylesXml, path => Part(path), declared, token);
        var body = content.Root.Element(Office + "body")?.Element(Office + "text") ?? throw new FormatException("ODT text body is missing.");
        var blocks = context.ReadBlocks(body, 0);
        var document = new FlowDocument(FlowDocument.EnsureBlocks(blocks.ToImmutableArray()))
        { Resources = context.Resources };
        if (context.ReadPageSettings() is { } page) document = document with { Sections = [new DocumentSection { PageSettings = page }] };
        document.Validate();
        foreach (var entry in archive.Entries.Where(entry => entry.FullName is not ("mimetype" or "content.xml" or "styles.xml" or "META-INF/manifest.xml") &&
            !entry.FullName.EndsWith('/') && !context.ImportedParts.Contains(entry.FullName)))
            Loss("package-part", "Unmapped ODT package part", "Part was omitted.", location: entry.FullName);
        return document;
    }

    private sealed partial class ReadContext
    {
        private readonly Dictionary<string, XElement> _styles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _listStyles = new(StringComparer.Ordinal);
        private readonly XDocument _stylesXml;
        private readonly CancellationToken _token;
        private int _blocks;

        public ReadContext(XDocument content, XDocument stylesXml, Func<string, byte[]> part,
            IReadOnlyDictionary<string, string> mediaTypes, CancellationToken token)
        {
            _stylesXml = stylesXml;
            _part = part;
            _mediaTypes = mediaTypes;
            _token = token;
            foreach (var source in new[] { stylesXml, content })
            {
                foreach (var style in source.Descendants(Style + "style"))
                    if ((string?)style.Attribute(Style + "name") is { Length: > 0 } name &&
                        (string?)style.Attribute(Style + "family") is { Length: > 0 } family)
                    {
                        _styles[family + ":" + name] = style;
                        if (style.Parent?.Name == Office + "styles")
                            Loss("named-style", "Named ODT style identity and inheritance", "Effective formatting was retained as direct formatting.", style);
                    }
                foreach (var style in source.Descendants(Text + "list-style"))
                    if ((string?)style.Attribute(Style + "name") is { Length: > 0 } name) _listStyles[name] = style;
            }
            foreach (var style in stylesXml.Descendants(Style + "default-style"))
                Loss("default-style", "ODT document default style", "Default style definition was omitted; explicit paragraph formatting was retained.", style);
            if (_styles.Count + _listStyles.Count > 4096) throw new FormatException("ODT style count exceeds the limit.");
        }

        public List<Block> ReadBlocks(XElement parent, int depth)
        {
            if (depth > 32) throw new FormatException("ODT block nesting exceeds the limit.");
            var blocks = new List<Block>();
            foreach (var child in parent.Elements())
            {
                _token.ThrowIfCancellationRequested();
                if (++_blocks > 100000) throw new FormatException("ODT block count exceeds the limit.");
                if (child.Name == Text + "p" || child.Name == Text + "h") blocks.Add(ReadParagraph(child));
                else if (child.Name == Text + "list") blocks.AddRange(ReadList(child, depth + 1));
                else if (child.Name == TableNs + "table") blocks.Add(ReadTable(child, depth + 1));
                else if (child.Name == Text + "section")
                {
                    Loss("section", "ODT text section boundary", "Content was placed in the main flow.", child);
                    blocks.AddRange(ReadBlocks(child, depth + 1));
                }
                else if (child.Name is not null && child.Name != Text + "sequence-decls" && child.Name != Text + "user-field-decls")
                    Loss("block", child.Name.LocalName, "Unsupported block was omitted.", child);
            }
            return blocks;
        }

        private Paragraph ReadParagraph(XElement element, Guid? listId = null, int level = 0, ListDefinition? definition = null, int? start = null)
        {
            ReportAttributes(element, Text + "style-name", Text + "outline-level");
            var (paragraphStyle, defaultText) = ParagraphStyleFor((string?)element.Attribute(Text + "style-name"), 0);
            if (element.Name == Text + "h")
            {
                var heading = (int?)element.Attribute(Text + "outline-level") ?? 1;
                if (heading is < 1 or > 6) { Loss("heading-level", "Heading level outside 1-6", "Level was clamped.", element); heading = Math.Clamp(heading, 1, 6); }
                paragraphStyle = paragraphStyle with { HeadingLevel = heading };
            }
            if (listId is not null)
            {
                var kind = definition?.Level(level, ListKind.Numbered).Kind ?? ListKind.Numbered;
                paragraphStyle = paragraphStyle with { List = kind, ListId = listId, ListLevel = level, ListDefinition = definition, ListStart = start, ListRestart = start is not null };
            }
            var runs = new List<RichRun>();
            foreach (var node in element.Nodes()) ReadInline(node, defaultText, runs, 0);
            return new Paragraph(runs) { Style = paragraphStyle, DefaultStyle = defaultText };
        }

        private void ReadInline(XNode node, TextStyle inherited, List<RichRun> runs, int depth)
        {
            if (depth > 32) throw new FormatException("ODT inline nesting exceeds the limit.");
            if (node is XText value) { if (value.Value.Length > 0) runs.Add(new RichRun(value.Value, inherited)); return; }
            if (node is not XElement element) return;
            if (element.Name == Text + "s")
            {
                var count = (int?)element.Attribute(Text + "c") ?? 1;
                if (count is < 1 or > 100000) throw new FormatException("Invalid ODT repeated space count.");
                runs.Add(new RichRun(new string(' ', count), inherited)); return;
            }
            if (element.Name == Text + "tab") { runs.Add(new RichRun("\t", inherited)); return; }
            if (element.Name == Text + "line-break") { runs.Add(new RichRun("\u2028", inherited)); return; }
            if (element.Name == Draw + "frame") { ReadFrame(element, inherited, runs); return; }
            if (element.Name == Text + "span") inherited = TextStyleFor((string?)element.Attribute(Text + "style-name"), inherited, 0);
            else if (element.Name == Text + "a")
            {
                var href = (string?)element.Attribute(XLink + "href");
                if (href is not null && FlowDocument.IsSafeHyperlink(href)) inherited = inherited with { Hyperlink = href };
                else Loss("hyperlink", "Unsafe or relative hyperlink", "Link text was retained without a destination.", element);
            }
            else if (element.Name != Text + "span") Loss("inline", element.Name.LocalName, "Visible child text was retained where possible.", element);
            foreach (var child in element.Nodes()) ReadInline(child, inherited, runs, depth + 1);
        }

        private List<Block> ReadList(XElement source, int depth, Guid? identity = null, int level = 0, ListDefinition? inherited = null)
        {
            if (level > 8) throw new FormatException("ODT list depth exceeds nine levels.");
            ReportAttributes(source, Text + "style-name");
            identity ??= Guid.NewGuid();
            var definition = inherited ?? ListDefinitionFor((string?)source.Attribute(Text + "style-name"));
            var result = new List<Block>();
            foreach (var item in source.Elements(Text + "list-item"))
            {
                var first = true;
                var start = (int?)item.Attribute(Text + "start-value");
                if (start is < 1 or > 1_000_000) throw new FormatException("Invalid ODT list start.");
                foreach (var child in item.Elements())
                {
                    if (child.Name == Text + "p" || child.Name == Text + "h")
                    {
                        if (first) result.Add(ReadParagraph(child, identity, level, definition, start));
                        else { Loss("list-item-block", "Multiple paragraphs in a list item", "Additional paragraphs were retained without a second marker.", child); result.Add(ReadParagraph(child)); }
                        first = false;
                    }
                    else if (child.Name == Text + "list") result.AddRange(ReadList(child, depth + 1, identity, level + 1, definition));
                    else if (child.Name == TableNs + "table") result.Add(ReadTable(child, depth + 1));
                    else Loss("list-item-block", child.Name.LocalName, "Unsupported list item content was omitted.", child);
                }
            }
            return result;
        }

        private ListDefinition ListDefinitionFor(string? name)
        {
            if (name is null || !_listStyles.TryGetValue(name, out var style))
            {
                Loss("list-style", "Missing ODT list style", "Decimal numbering was used.");
                return new ListDefinition { Levels = [new ListLevelDefinition()] };
            }
            var levels = new List<ListLevelDefinition>();
            foreach (var child in style.Elements().Where(e => e.Name == Text + "list-level-style-number" || e.Name == Text + "list-level-style-bullet"))
            {
                var level = (int?)child.Attribute(Text + "level") ?? 1;
                if (level is < 1 or > 9) throw new FormatException("Invalid ODT list level.");
                while (levels.Count < level) levels.Add(new ListLevelDefinition());
                var bullet = child.Name == Text + "list-level-style-bullet";
                var format = (string?)child.Attribute(Style + "num-format");
                var marker = format switch { "a" => ListMarkerStyle.LowerLetter, "A" => ListMarkerStyle.UpperLetter,
                    "i" => ListMarkerStyle.LowerRoman, "I" => ListMarkerStyle.UpperRoman, _ => bullet ? ListMarkerStyle.Bullet : ListMarkerStyle.Decimal };
                if (!bullet && format is not (null or "1" or "a" or "A" or "i" or "I"))
                    Loss("list-format", "Unsupported ODT number format", "Decimal numbering was used.", child);
                levels[level - 1] = new ListLevelDefinition { Kind = bullet ? ListKind.Bullet : ListKind.Numbered, Marker = marker,
                    Text = bullet ? (string?)child.Attribute(Text + "bullet-char") : null,
                    Start = (int?)child.Attribute(Text + "start-value") ?? 1,
                    Prefix = (string?)child.Attribute(Style + "num-prefix") ?? "", Suffix = (string?)child.Attribute(Style + "num-suffix") ?? "." };
            }
            if (levels.Count == 0) levels.Add(new ListLevelDefinition());
            return new ListDefinition { Levels = levels.ToImmutableArray() };
        }

        private Table ReadTable(XElement source, int depth)
        {
            if (source.Attribute(TableNs + "style-name") is not null)
                Loss("table-style", "ODT table style", "Cell content and spans were retained; table appearance was omitted.", source);
            var rows = new List<List<XElement>>();
            foreach (var parent in source.Elements())
            {
                var rowElements = parent.Name == TableNs + "table-row" ? [parent] :
                    parent.Name == TableNs + "table-header-rows" ? parent.Elements(TableNs + "table-row").ToArray() : [];
                foreach (var row in rowElements)
                {
                    if (row.Attribute(TableNs + "style-name") is not null)
                        Loss("row-style", "ODT row style", "Row appearance was omitted.", row);
                    var repeat = Repetition(row, TableNs + "number-rows-repeated", 1000);
                    var cells = new List<XElement>();
                    foreach (var cell in row.Elements().Where(e => e.Name == TableNs + "table-cell" || e.Name == TableNs + "covered-table-cell"))
                    {
                        if (cell.Attribute(TableNs + "style-name") is not null)
                            Loss("cell-style", "ODT cell style", "Cell content and spans were retained; appearance was omitted.", cell);
                        for (var i = 0; i < Repetition(cell, TableNs + "number-columns-repeated", 100); i++) cells.Add(cell);
                    }
                    for (var i = 0; i < repeat; i++)
                    {
                        if (rows.Count >= 1000) throw new FormatException("ODT table has too many rows.");
                        rows.Add(cells);
                    }
                }
            }
            var columns = Math.Max(rows.Select(row => row.Count).DefaultIfEmpty(0).Max(),
                source.Elements(TableNs + "table-column").Sum(column => Repetition(column, TableNs + "number-columns-repeated", 100)));
            if (rows.Count == 0 || columns is < 1 or > 100) throw new FormatException("Invalid ODT table grid.");
            var table = Table.Create(rows.Count, columns);
            for (var row = 0; row < rows.Count; row++)
                for (var column = 0; column < rows[row].Count; column++)
                {
                    var cell = rows[row][column];
                    if (cell.Name == TableNs + "covered-table-cell") continue;
                    var columnSpan = Repetition(cell, TableNs + "number-columns-spanned", columns);
                    var rowSpan = Repetition(cell, TableNs + "number-rows-spanned", rows.Count);
                    var content = ReadBlocks(cell, depth + 1);
                    table = table.SetCell(row, column, new TableCell { Blocks = FlowDocument.EnsureBlocks(content.ToImmutableArray()), ColumnSpan = columnSpan, RowSpan = rowSpan });
                }
            if (rows.Any(row => row.Count != columns)) Loss("table-grid", "Ragged ODT table grid", "Missing cells were filled with empty cells.", source);
            return table;
        }

        private static int Repetition(XElement element, XName attribute, int maximum)
        {
            var value = (int?)element.Attribute(attribute) ?? 1;
            if (value is < 1 || value > maximum) throw new FormatException("ODT table repeat or span exceeds its limit.");
            return value;
        }

        private (ParagraphStyle Paragraph, TextStyle Text) ParagraphStyleFor(string? name, int depth)
        {
            if (name is null) return (ParagraphStyle.Default, TextStyle.Default);
            if (depth > 32) throw new FormatException("ODT style inheritance exceeds its limit.");
            if (!_styles.TryGetValue("paragraph:" + name, out var source))
            { Loss("style-reference", "Missing ODT paragraph style", "Default formatting was used."); return (ParagraphStyle.Default, TextStyle.Default); }
            var parent = ParagraphStyleFor((string?)source.Attribute(Style + "parent-style-name"), depth + 1);
            return (ApplyParagraph(source.Element(Style + "paragraph-properties"), parent.Paragraph), ApplyText(source.Element(Style + "text-properties"), parent.Text));
        }

        private TextStyle TextStyleFor(string? name, TextStyle inherited, int depth)
        {
            if (name is null) return inherited;
            if (depth > 32) throw new FormatException("ODT style inheritance exceeds its limit.");
            if (!_styles.TryGetValue("text:" + name, out var source))
            { Loss("style-reference", "Missing ODT text style", "Inherited formatting was used."); return inherited; }
            inherited = TextStyleFor((string?)source.Attribute(Style + "parent-style-name"), inherited, depth + 1);
            return ApplyText(source.Element(Style + "text-properties"), inherited);
        }

        public PageSettings? ReadPageSettings()
        {
            var master = _stylesXml.Descendants(Style + "master-page").FirstOrDefault();
            if (_stylesXml.Descendants(Style + "master-page").Skip(1).Any())
                Loss("page-master", "Multiple ODT master pages", "Only the first master page layout was used.", master);
            var layoutName = (string?)master?.Attribute(Style + "page-layout-name");
            var layout = _stylesXml.Descendants(Style + "page-layout").FirstOrDefault(e => (string?)e.Attribute(Style + "name") == layoutName);
            var properties = layout?.Element(Style + "page-layout-properties");
            if (properties is null) return null;
            ReportAttributes(properties, Fo + "page-width", Fo + "page-height", Fo + "margin-left", Fo + "margin-top",
                Fo + "margin-right", Fo + "margin-bottom", Style + "print-orientation");
            var orientation = (string?)properties.Attribute(Style + "print-orientation") == "landscape" ? PageOrientation.Landscape : PageOrientation.Portrait;
            var width = Measure((string?)properties.Attribute(Fo + "page-width"), 816);
            var height = Measure((string?)properties.Attribute(Fo + "page-height"), 1056);
            var margins = new EdgeInsets(
                Measure((string?)properties.Attribute(Fo + "margin-left"), 96),
                Measure((string?)properties.Attribute(Fo + "margin-top"), 96),
                Measure((string?)properties.Attribute(Fo + "margin-right"), 96),
                Measure((string?)properties.Attribute(Fo + "margin-bottom"), 96));
            return new PageSettings { Width = orientation == PageOrientation.Landscape ? height : width,
                Height = orientation == PageOrientation.Landscape ? width : height, Orientation = orientation, Margins = margins };
        }
    }

    private static ParagraphStyle ApplyParagraph(XElement? properties, ParagraphStyle style)
    {
        if (properties is null) return style;
        ReportAttributes(properties, Fo + "text-align", Fo + "margin-left", Fo + "margin-right", Fo + "margin-top",
            Fo + "margin-bottom", Fo + "text-indent");
        style = style with
        {
            Alignment = (string?)properties.Attribute(Fo + "text-align") switch
            { "center" => ParagraphAlignment.Center, "end" or "right" => ParagraphAlignment.Right, "justify" => ParagraphAlignment.Justify, _ => ParagraphAlignment.Left },
            Indent = Measure((string?)properties.Attribute(Fo + "margin-left"), style.Indent),
            RightIndent = Measure((string?)properties.Attribute(Fo + "margin-right"), style.RightIndent),
            SpaceBefore = Measure((string?)properties.Attribute(Fo + "margin-top"), style.SpaceBefore),
            SpaceAfter = Measure((string?)properties.Attribute(Fo + "margin-bottom"), style.SpaceAfter),
            FirstLineIndent = Measure((string?)properties.Attribute(Fo + "text-indent"), style.FirstLineIndent)
        };
        return style;
    }

    private static TextStyle ApplyText(XElement? properties, TextStyle style)
    {
        if (properties is null) return style;
        ReportAttributes(properties, Style + "font-name", Fo + "font-family", Fo + "font-size", Fo + "font-weight",
            Fo + "font-style", Style + "text-underline-style", Style + "text-line-through-style",
            Fo + "color", Fo + "background-color");
        var font = (string?)properties.Attribute(Style + "font-name") ?? (string?)properties.Attribute(Fo + "font-family");
        return style with
        {
            FontFamily = font is null ? style.FontFamily : font.Trim('"', '\''),
            FontSize = Measure((string?)properties.Attribute(Fo + "font-size"), style.FontSize),
            Bold = (string?)properties.Attribute(Fo + "font-weight") switch { "bold" => true, "normal" => false, _ => style.Bold },
            Italic = (string?)properties.Attribute(Fo + "font-style") switch { "italic" or "oblique" => true, "normal" => false, _ => style.Italic },
            Underline = (string?)properties.Attribute(Style + "text-underline-style") switch { null => style.Underline, "none" => false, _ => true },
            Strikethrough = (string?)properties.Attribute(Style + "text-line-through-style") switch { null => style.Strikethrough, "none" => false, _ => true },
            Foreground = (string?)properties.Attribute(Fo + "color") ?? style.Foreground,
            Background = (string?)properties.Attribute(Fo + "background-color") ?? style.Background
        };
    }

    private static double Measure(string? value, double fallback)
    {
        if (value is null) return fallback;
        var unit = new string(value.SkipWhile(c => char.IsDigit(c) || c is '.' or '-' or '+').ToArray());
        var digits = value[..(value.Length - unit.Length)];
        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new FormatException("Invalid ODT measurement.");
        return unit switch
        {
            "in" => DocumentUnits.FromInches(number), "cm" => DocumentUnits.FromMillimeters(number * 10),
            "mm" => DocumentUnits.FromMillimeters(number), "pt" => DocumentUnits.FromPoints(number),
            "pc" => DocumentUnits.FromPoints(number * 12), "px" => number,
            _ => throw new FormatException("Unsupported ODT measurement unit: " + unit)
        };
    }

    private static bool SafePartName(string path) => path.Length is > 0 and <= 1024 && !path.StartsWith('/') &&
        !path.Contains('\\') && !path.Contains(':') && !path.Split('/').Any(part => part == ".." || part == ".");

    private static void ReportAttributes(XElement element, params XName[] supported)
    {
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration && !supported.Contains(attribute.Name)))
            Loss("attribute", "Unsupported ODT attribute " + attribute.Name.LocalName,
                "Supported content and formatting were retained; this attribute was omitted.", element);
    }

    private static void Loss(string code, string feature, string fallback, XElement? source = null, string? location = null)
    {
        if (source is IXmlLineInfo info && info.HasLineInfo())
            location = $"{source.Document?.Annotation<string>() ?? "content.xml"}:{info.LineNumber}:{info.LinePosition}";
        ConversionDiagnostics.Report("odt." + code, feature, fallback, sourceLocation: location);
    }
}
