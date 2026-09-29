using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class OdtDocumentFormat
{
    private static byte[] Write(FlowDocument source, CancellationToken token)
    {
        StyleConversion.ReportLosses(new OdtDocumentFormat(), source);
        TableConversion.ReportLosses("odt", source);
        if (source.Sections.Length > 1)
            ConversionDiagnostics.Report("odt.page-sections", "Multiple physical page sections", "The first page layout was retained; later section boundaries and settings were omitted.");
        if (!source.Sections.IsEmpty)
        {
            var page = source.Sections[0];
            if (page.PageNumberStart is not null || page.PageNumberFormat != PageNumberFormat.Decimal ||
                page.BreakKind != SectionBreakKind.NextPage || page.PageSettings.Columns.Length > 0 ||
                page.PageSettings.Gutter != 0 || page.PageSettings.MirrorMargins || page.PageSettings.Borders is not null ||
                page.PageSettings.LineNumbering is not null || page.PageSettings.Grid is not null ||
                page.PageSettings.Background is not null and not "#FFFFFF")
                ConversionDiagnostics.Report("odt.page-setting", "Page numbering, columns or decoration outside the ODT subset",
                    "Page size, orientation and margins were retained; other settings were omitted.", page.Id);
        }
        var document = new DocumentStyleResolver(source).ResolveDocument();
        var writer = new WriteContext(document, token);
        var content = writer.Content();
        var styles = writer.Styles();
        var manifest = new XDocument(new XElement(Manifest + "manifest",
            new XAttribute(XNamespace.Xmlns + "manifest", Manifest), new XAttribute(Manifest + "version", "1.2"),
            new XElement(Manifest + "file-entry", new XAttribute(Manifest + "full-path", "/"), new XAttribute(Manifest + "media-type", MimeType)),
            new XElement(Manifest + "file-entry", new XAttribute(Manifest + "full-path", "content.xml"), new XAttribute(Manifest + "media-type", "text/xml")),
            new XElement(Manifest + "file-entry", new XAttribute(Manifest + "full-path", "styles.xml"), new XAttribute(Manifest + "media-type", "text/xml"))));
        foreach (var image in writer.Images)
            manifest.Root!.Add(new XElement(Manifest + "file-entry", new XAttribute(Manifest + "full-path", image.Path),
                new XAttribute(Manifest + "media-type", image.MediaType)));
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Put(archive, "mimetype", Encoding.ASCII.GetBytes(MimeType), CompressionLevel.NoCompression);
            Put(archive, "content.xml", Encoding.UTF8.GetBytes(content.ToString(SaveOptions.DisableFormatting)));
            Put(archive, "styles.xml", Encoding.UTF8.GetBytes(styles.ToString(SaveOptions.DisableFormatting)));
            Put(archive, "META-INF/manifest.xml", Encoding.UTF8.GetBytes(manifest.ToString(SaveOptions.DisableFormatting)));
            foreach (var image in writer.Images) Put(archive, image.Path, image.Data);
        }
        if (output.Length > 32 * 1024 * 1024) throw new FormatException("ODT output exceeds the 32 MB package limit.");
        return output.ToArray();
    }

    private static void Put(ZipArchive archive, string path, byte[] bytes, CompressionLevel compression = CompressionLevel.Optimal)
    {
        using var stream = archive.CreateEntry(path, compression).Open();
        stream.Write(bytes);
    }

    private sealed partial class WriteContext(FlowDocument document, CancellationToken token)
    {
        private readonly XElement _automatic = new(Office + "automatic-styles");
        private readonly Dictionary<(ParagraphStyle Paragraph, TextStyle Text), string> _paragraphStyles = [];
        private readonly Dictionary<TextStyle, string> _textStyles = [];
        private readonly Dictionary<(ListKind Kind, ListDefinition? Definition), string> _listStyles = [];
        private readonly Dictionary<string, (string Path, string MediaType, byte[] Data)> _images = new(StringComparer.Ordinal);
        public IReadOnlyCollection<(string Path, string MediaType, byte[] Data)> Images => _images.Values;

        public XDocument Content()
        {
            var body = new XElement(Office + "text");
            WriteBlocks(body, document.Blocks);
            return new XDocument(new XElement(Office + "document-content",
                new XAttribute(XNamespace.Xmlns + "office", Office), new XAttribute(XNamespace.Xmlns + "text", Text),
                new XAttribute(XNamespace.Xmlns + "style", Style), new XAttribute(XNamespace.Xmlns + "fo", Fo),
                new XAttribute(XNamespace.Xmlns + "table", TableNs), new XAttribute(XNamespace.Xmlns + "xlink", XLink),
                new XAttribute(XNamespace.Xmlns + "draw", Draw), new XAttribute(XNamespace.Xmlns + "svg", Svg),
                new XAttribute(Office + "version", "1.2"), _automatic, new XElement(Office + "body", body)));
        }

        public XDocument Styles()
        {
            var root = new XElement(Office + "document-styles",
                new XAttribute(XNamespace.Xmlns + "office", Office), new XAttribute(XNamespace.Xmlns + "style", Style),
                new XAttribute(XNamespace.Xmlns + "fo", Fo), new XAttribute(Office + "version", "1.2"),
                new XElement(Office + "styles"));
            if (!document.Sections.IsEmpty)
            {
                var page = document.Sections[0].PageSettings;
                root.Add(new XElement(Office + "automatic-styles", new XElement(Style + "page-layout",
                    new XAttribute(Style + "name", "TextaloniaPage"), new XElement(Style + "page-layout-properties",
                        new XAttribute(Fo + "page-width", Inches(page.EffectiveWidth)),
                        new XAttribute(Fo + "page-height", Inches(page.EffectiveHeight)),
                        new XAttribute(Fo + "margin-left", Inches(page.Margins.Left)),
                        new XAttribute(Fo + "margin-top", Inches(page.Margins.Top)),
                        new XAttribute(Fo + "margin-right", Inches(page.Margins.Right)),
                        new XAttribute(Fo + "margin-bottom", Inches(page.Margins.Bottom)),
                        new XAttribute(Style + "print-orientation", page.Orientation == PageOrientation.Landscape ? "landscape" : "portrait")))),
                    new XElement(Office + "master-styles", new XElement(Style + "master-page",
                        new XAttribute(Style + "name", "Standard"), new XAttribute(Style + "page-layout-name", "TextaloniaPage"))));
            }
            return new XDocument(root);
        }

        private void WriteBlocks(XElement parent, IEnumerable<Block> blocks)
        {
            var items = blocks.ToArray();
            for (var i = 0; i < items.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                switch (items[i])
                {
                    case Paragraph paragraph when paragraph.Style.List != ListKind.None && paragraph.Style.ListLevel == 0:
                    {
                        var group = new List<Paragraph> { paragraph };
                        while (i + 1 < items.Length && items[i + 1] is Paragraph next && next.Style.List != ListKind.None && next.Style.ListLevel == 0 &&
                            next.Style.List == paragraph.Style.List && next.Style.ListId == paragraph.Style.ListId && !next.Style.ListRestart)
                        { group.Add(next); i++; }
                        parent.Add(WriteList(group));
                        break;
                    }
                    case Paragraph paragraph:
                        if (paragraph.Style.List != ListKind.None)
                            ConversionDiagnostics.Report("odt.list-level", "List paragraph outside the supported flat-list export subset",
                                "Paragraph text was retained without a list marker.", paragraph.Id);
                        parent.Add(WriteParagraph(paragraph));
                        break;
                    case Table table: parent.Add(WriteTable(table)); break;
                    case Section section:
                        ConversionDiagnostics.Report("odt.decorative-section", "Decorative or semantic block section",
                            "Contained content was retained in normal flow; section decoration and identity were omitted.", section.Id);
                        WriteBlocks(parent, section.Blocks);
                        break;
                }
            }
        }

        private XElement WriteList(IReadOnlyList<Paragraph> paragraphs)
        {
            var first = paragraphs[0];
            var name = ListStyle(first.Style.List, first.Style.ListDefinition);
            var list = new XElement(Text + "list", new XAttribute(Text + "style-name", name));
            for (var i = 0; i < paragraphs.Count; i++)
            {
                var paragraph = paragraphs[i];
                var item = new XElement(Text + "list-item");
                if (paragraph.Style.ListStart is { } start) item.SetAttributeValue(Text + "start-value", start);
                item.Add(WriteParagraph(paragraph));
                list.Add(item);
            }
            return list;
        }

        private string ListStyle(ListKind kind, ListDefinition? definition)
        {
            var key = (kind, definition);
            if (_listStyles.TryGetValue(key, out var name)) return name;
            name = "L" + (_listStyles.Count + 1).ToString(CultureInfo.InvariantCulture);
            _listStyles.Add(key, name);
            var level = definition?.Levels.FirstOrDefault() ?? new ListLevelDefinition { Kind = kind, Marker = kind == ListKind.Bullet ? ListMarkerStyle.Bullet : ListMarkerStyle.Decimal };
            var bullet = kind == ListKind.Bullet;
            var element = new XElement(Text + "list-style", new XAttribute(Style + "name", name));
            if (bullet) element.Add(new XElement(Text + "list-level-style-bullet", new XAttribute(Text + "level", 1),
                new XAttribute(Text + "bullet-char", level.Text ?? "•")));
            else element.Add(new XElement(Text + "list-level-style-number", new XAttribute(Text + "level", 1),
                new XAttribute(Style + "num-format", level.Marker switch
                { ListMarkerStyle.LowerLetter => "a", ListMarkerStyle.UpperLetter => "A", ListMarkerStyle.LowerRoman => "i", ListMarkerStyle.UpperRoman => "I", _ => "1" }),
                new XAttribute(Text + "start-value", level.Start), new XAttribute(Style + "num-prefix", level.Prefix), new XAttribute(Style + "num-suffix", level.Suffix)));
            if (definition is { Levels.Length: > 1 } || level.Marker is not (ListMarkerStyle.Decimal or ListMarkerStyle.Bullet or
                ListMarkerStyle.LowerLetter or ListMarkerStyle.UpperLetter or ListMarkerStyle.LowerRoman or ListMarkerStyle.UpperRoman) ||
                level.MarkerFormatting != new TextStyleOverrides() || level.CharacterStyleId is not null || level.ParagraphStyleId is not null ||
                level.TextIndent is not null || level.MarkerIndent is not null || level.TabPosition is not null)
                ConversionDiagnostics.Report("odt.list-marker", "Advanced list marker formatting or nested level definition",
                    "Basic number or bullet marker and first-level prefix, suffix and start were retained.");
            _automatic.Add(element);
            return name;
        }

        private XElement WriteTable(Table table)
        {
            if (table.StyleId is not null || table.StyleOverrides is not null || table.Position is not null || table.RightToLeft ||
                table.Indent != 0 || table.RepeatHeaderRows != 0 || !table.ColumnWidths.IsEmpty || !table.RowSizing.IsEmpty ||
                table.PreferredWidth != new TablePreferredWidth() || table.AutoFit != TableAutoFit.Legacy || table.Alignment != TableAlignment.Left)
                ConversionDiagnostics.Report("odt.table-format", "Table width, placement or advanced table style",
                    "Table cells, text and spans were retained; unsupported geometry and formatting were omitted.", table.Id);
            var result = new XElement(TableNs + "table", new XAttribute(TableNs + "name", "Table" + table.Id.ToString("N")));
            result.Add(new XElement(TableNs + "table-column", new XAttribute(TableNs + "number-columns-repeated", table.ColumnCount)));
            for (var row = 0; row < table.Rows.Length; row++)
            {
                var rowElement = new XElement(TableNs + "table-row");
                for (var column = 0; column < table.ColumnCount; column++)
                {
                    if (table.IsCovered(row, column)) { rowElement.Add(new XElement(TableNs + "covered-table-cell")); continue; }
                    var cell = table.Rows[row][column];
                    var element = new XElement(TableNs + "table-cell", new XAttribute(Office + "value-type", "string"));
                    if (cell.ColumnSpan > 1) element.SetAttributeValue(TableNs + "number-columns-spanned", cell.ColumnSpan);
                    if (cell.RowSpan > 1) element.SetAttributeValue(TableNs + "number-rows-spanned", cell.RowSpan);
                    if (cell.Background is not null || cell.Padding is not null || cell.Borders is not null || cell.StyleOverrides is not null ||
                        cell.VerticalAlignment != TableCellVerticalAlignment.Top || cell.TextDirection != TableCellTextDirection.Inherit ||
                        cell.PreferredWidth != new TablePreferredWidth())
                        ConversionDiagnostics.Report("odt.cell-format", "Cell appearance or sizing outside the ODT subset",
                            "Cell content and spans were retained; unsupported formatting was omitted.", cell.Id);
                    WriteBlocks(element, cell.Blocks);
                    rowElement.Add(element);
                }
                result.Add(rowElement);
            }
            return result;
        }

        private XElement WriteParagraph(Paragraph paragraph)
        {
            var result = new XElement(paragraph.Style.HeadingLevel > 0 ? Text + "h" : Text + "p");
            result.SetAttributeValue(Text + "style-name", ParagraphStyleName(paragraph.Style, paragraph.DefaultStyle));
            if (paragraph.Style.HeadingLevel > 0) result.SetAttributeValue(Text + "outline-level", paragraph.Style.HeadingLevel);
            foreach (var run in paragraph.Runs)
            {
                token.ThrowIfCancellationRequested();
                var content = new XElement(Text + "span", new XAttribute(Text + "style-name", TextStyleName(run.Style)));
                if (run.Inline is { } inline)
                {
                    if (inline.Payload is ImageInlinePayload image)
                    {
                        if (!TryWriteImage(content, inline, image)) AppendText(content, inline.AltText);
                    }
                    else { ConversionDiagnostics.Report("odt.inline", "Object, field or host control",
                        "Inline object was replaced by its alternative text.", inline.Id); AppendText(content, inline.AltText); }
                }
                else AppendText(content, run.Text);
                if (run.Style.Hyperlink is { } link && FlowDocument.IsSafeHyperlink(link))
                    result.Add(new XElement(Text + "a", new XAttribute(XLink + "href", link), content));
                else result.Add(content);
            }
            return result;
        }

        private string ParagraphStyleName(ParagraphStyle paragraph, TextStyle text)
        {
            var key = (paragraph, text);
            if (_paragraphStyles.TryGetValue(key, out var name)) return name;
            name = "P" + (_paragraphStyles.Count + 1).ToString(CultureInfo.InvariantCulture);
            _paragraphStyles.Add(key, name);
            var style = new XElement(Style + "style", new XAttribute(Style + "name", name), new XAttribute(Style + "family", "paragraph"),
                new XElement(Style + "paragraph-properties", new XAttribute(Fo + "text-align", paragraph.Alignment switch
                { ParagraphAlignment.Center => "center", ParagraphAlignment.Right => "end", ParagraphAlignment.Justify => "justify", _ => "start" }),
                    new XAttribute(Fo + "margin-left", Inches(paragraph.Indent)), new XAttribute(Fo + "margin-right", Inches(paragraph.RightIndent)),
                    new XAttribute(Fo + "margin-top", Inches(paragraph.SpaceBefore)), new XAttribute(Fo + "margin-bottom", Inches(paragraph.SpaceAfter)),
                    new XAttribute(Fo + "text-indent", Inches(paragraph.FirstLineIndent))), TextProperties(text));
            _automatic.Add(style);
            return name;
        }

        private string TextStyleName(TextStyle text)
        {
            if (_textStyles.TryGetValue(text, out var name)) return name;
            name = "T" + (_textStyles.Count + 1).ToString(CultureInfo.InvariantCulture);
            _textStyles.Add(text, name);
            _automatic.Add(new XElement(Style + "style", new XAttribute(Style + "name", name), new XAttribute(Style + "family", "text"), TextProperties(text)));
            return name;
        }

        private static XElement TextProperties(TextStyle text)
        {
            var properties = new XElement(Style + "text-properties",
                new XAttribute(Fo + "font-size", Points(text.FontSize)),
                new XAttribute(Fo + "font-weight", text.Bold ? "bold" : "normal"),
                new XAttribute(Fo + "font-style", text.Italic ? "italic" : "normal"),
                new XAttribute(Style + "text-underline-style", text.Underline ? "solid" : "none"),
                new XAttribute(Style + "text-line-through-style", text.Strikethrough ? "solid" : "none"));
            if (text.FontFamily is not null) properties.SetAttributeValue(Fo + "font-family", text.FontFamily);
            if (text.Foreground is not null) properties.SetAttributeValue(Fo + "color", text.Foreground);
            if (text.Background is not null) properties.SetAttributeValue(Fo + "background-color", text.Background);
            return properties;
        }

        private static void AppendText(XElement parent, string value)
        {
            var start = 0;
            for (var i = 0; i < value.Length;)
            {
                var ch = value[i];
                if (ch is not (' ' or '\t' or '\n' or '\u2028')) { i++; continue; }
                if (i > start) parent.Add(new XText(value[start..i]));
                if (ch == ' ')
                {
                    var end = i + 1;
                    while (end < value.Length && value[end] == ' ') end++;
                    parent.Add(new XElement(Text + "s", new XAttribute(Text + "c", end - i)));
                    i = end;
                }
                else { parent.Add(new XElement(ch == '\t' ? Text + "tab" : Text + "line-break")); i++; }
                start = i;
            }
            if (start < value.Length) parent.Add(new XText(value[start..]));
        }
    }

    private static string Inches(double value) => DocumentUnits.ToInches(value).ToString("0.######", CultureInfo.InvariantCulture) + "in";
    private static string Points(double value) => DocumentUnits.ToPoints(value).ToString("0.######", CultureInfo.InvariantCulture) + "pt";
}
