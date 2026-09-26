using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>WordprocessingML interchange for paragraphs, inline formatting, links, lists and tables.</summary>
public sealed class DocxDocumentFormat : IDocumentFormat
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    public string Name => "Word document";
    public IReadOnlyList<string> Extensions => [".docx"];

    public async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        return await Task.Run(() => Read(bytes, cancellationToken), cancellationToken);
    }
    public async Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default)
    {
        document.Validate();
        var bytes = await Task.Run(() => Write(document, cancellationToken), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static FlowDocument Read(byte[] bytes, CancellationToken token)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count > 4096 || archive.Entries.Sum(e => e.Length) > 128L * 1024 * 1024)
            throw new FormatException("DOCX package is too large.");
        XDocument? Xml(string path, bool required = false)
        {
            var entry = archive.GetEntry(path);
            if (entry is null)
            {
                if (required) throw new FormatException($"Missing DOCX part: {path}");
                return null;
            }
            if (entry.Length > 32 * 1024 * 1024) throw new FormatException("DOCX XML part is too large.");
            using var source = entry.Open();
            using var reader = XmlReader.Create(source, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024
            });
            return XDocument.Load(reader);
        }
        var links = Xml("word/_rels/document.xml.rels")?.Root?.Elements(Rel + "Relationship")
            .Where(e => ((string?)e.Attribute("Type"))?.EndsWith("/hyperlink", StringComparison.Ordinal) == true)
            .ToDictionary(e => (string)e.Attribute("Id")!, e => (string?)e.Attribute("Target") ?? "") ?? [];
        var styles = Xml("word/styles.xml")?.Root?.Elements(W + "style")
            .Where(e => e.Attribute(W + "styleId") is not null)
            .ToDictionary(e => (string)e.Attribute(W + "styleId")!, e => e) ?? [];
        var numbering = Xml("word/numbering.xml");
        var listKinds = new Dictionary<string, ListKind>();
        if (numbering?.Root is { } numberingRoot)
            foreach (var num in numberingRoot.Elements(W + "num"))
            {
                var abstractId = Value(num.Element(W + "abstractNumId"));
                var definition = numberingRoot.Elements(W + "abstractNum").FirstOrDefault(e => (string?)e.Attribute(W + "abstractNumId") == abstractId);
                var format = Value(definition?.Element(W + "lvl")?.Element(W + "numFmt"));
                listKinds[(string?)num.Attribute(W + "numId") ?? ""] = format == "bullet" ? ListKind.Bullet : ListKind.Numbered;
            }
        (TextStyle Text, ParagraphStyle Paragraph) ResolveStyle(string? id, int depth = 0)
        {
            if (id is null || depth > 16 || !styles.TryGetValue(id, out var style)) return (TextStyle.Default, ParagraphStyle.Default);
            var basis = ResolveStyle(Value(style.Element(W + "basedOn")), depth + 1);
            var ps = ReadParagraphStyle(style.Element(W + "pPr"), basis.Paragraph, listKinds);
            return (ReadTextStyle(style.Element(W + "rPr"), basis.Text), ps);
        }
        Paragraph ReadParagraph(XElement element)
        {
            token.ThrowIfCancellationRequested();
            var pp = element.Element(W + "pPr");
            var styleId = Value(pp?.Element(W + "pStyle"));
            var basis = ResolveStyle(styleId);
            var paragraphStyle = ReadParagraphStyle(pp, basis.Paragraph, listKinds);
            if (styleId?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true &&
                int.TryParse(styleId[7..], out var heading) && heading is >= 1 and <= 6)
                paragraphStyle = paragraphStyle with { HeadingLevel = heading };
            var runs = new List<RichRun>();
            foreach (var run in element.Descendants(W + "r").Where(e => !e.Ancestors(W + "del").Any()))
            {
                var style = ReadTextStyle(run.Element(W + "rPr"), basis.Text);
                var hyperlink = run.Ancestors(W + "hyperlink").FirstOrDefault();
                if (hyperlink is not null && links.TryGetValue((string?)hyperlink.Attribute(R + "id") ?? "", out var link) && FlowDocument.IsSafeHyperlink(link))
                    style = style with { Hyperlink = link };
                var text = string.Concat(run.Elements().Select(e => e.Name.LocalName switch
                {
                    "t" => e.Value, "tab" => "\t", "br" or "cr" => "\u2028", _ => ""
                }));
                if (text.Length > 0) runs.Add(new(text.Replace('\n', '\u2028').Replace("\r", ""), style));
            }
            return new Paragraph(runs) { Style = paragraphStyle, DefaultStyle = basis.Text };
        }
        Table ReadTable(XElement element)
        {
            var rows = element.Elements(W + "tr").ToArray();
            var columns = Math.Max(element.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() ?? 0,
                rows.Select(r => r.Elements(W + "tc").Sum(c => Number(c.Element(W + "tcPr")?.Element(W + "gridSpan"), 1))).DefaultIfEmpty(1).Max());
            var table = Table.Create(Math.Max(1, rows.Length), Math.Max(1, columns));
            var vertical = new Dictionary<int, (int Row, int Column)>();
            for (var r = 0; r < rows.Length; r++)
            {
                var column = 0;
                var nextVertical = new Dictionary<int, (int, int)>();
                foreach (var cell in rows[r].Elements(W + "tc"))
                {
                    var properties = cell.Element(W + "tcPr");
                    var span = Math.Clamp(Number(properties?.Element(W + "gridSpan"), 1), 1, columns - column);
                    var merge = properties?.Element(W + "vMerge");
                    if (merge is not null && Value(merge) != "restart" && vertical.TryGetValue(column, out var owner))
                    {
                        var anchor = table.Rows[owner.Row][owner.Column];
                        table = table.SetCell(owner.Row, owner.Column, anchor with { RowSpan = r - owner.Row + 1 });
                        nextVertical[column] = owner;
                    }
                    else
                    {
                        var paragraphs = cell.Elements(W + "p").Select(ReadParagraph).ToImmutableArray();
                        var fill = (string?)properties?.Element(W + "shd")?.Attribute(W + "fill");
                        table = table.SetCell(r, column, new TableCell
                        {
                            Paragraphs = FlowDocument.EnsureParagraphs(paragraphs), ColumnSpan = span,
                            Background = ReadColor(fill)
                        });
                        if (merge is not null) nextVertical[column] = (r, column);
                    }
                    column += span;
                }
                vertical = nextVertical;
            }
            return table;
        }
        var body = Xml("word/document.xml", true)?.Root?.Element(W + "body") ?? throw new FormatException("Missing DOCX document body.");
        var blocks = body.Elements().SelectMany<XElement, Block>(e =>
            e.Name == W + "p" ? [ReadParagraph(e)] : e.Name == W + "tbl" ? [ReadTable(e)] : []);
        var document = new FlowDocument(blocks);
        document.Validate();
        return document;
    }

    private static string? Value(XElement? element) => (string?)element?.Attribute(W + "val");
    private static int Number(XElement? element, int fallback = 0) => int.TryParse(Value(element), out var number) ? number : fallback;
    private static bool On(XElement? element) => element is not null && Value(element) is not ("false" or "0" or "off" or "none");
    private static string? ReadColor(string? value) => value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-fA-F]{6}$") ? "#" + value : null;
    private static TextStyle ReadTextStyle(XElement? properties, TextStyle style)
    {
        if (properties is null) return style;
        foreach (var property in properties.Elements())
            switch (property.Name.LocalName)
            {
                case "b": style = style with { Bold = On(property) }; break;
                case "i": style = style with { Italic = On(property) }; break;
                case "u": style = style with { Underline = On(property) }; break;
                case "strike": style = style with { Strikethrough = On(property) }; break;
                case "sz": style = style with { FontSize = Math.Clamp(Number(property, 24) * 2d / 3, 1, 512) }; break;
                case "rFonts": style = style with { FontFamily = (string?)property.Attribute(W + "ascii") ?? (string?)property.Attribute(W + "hAnsi") ?? style.FontFamily }; break;
                case "color": style = style with { Foreground = ReadColor(Value(property)) }; break;
                case "shd": style = style with { Background = ReadColor((string?)property.Attribute(W + "fill")) }; break;
                case "highlight":
                    if (Avalonia.Media.Color.TryParse(Value(property), out var color))
                        style = style with { Background = $"#{color.R:X2}{color.G:X2}{color.B:X2}" };
                    break;
                case "vertAlign": style = style with { Baseline = Value(property) switch { "subscript" => Baseline.Subscript, "superscript" => Baseline.Superscript, _ => Baseline.Normal } }; break;
            }
        return style;
    }
    private static ParagraphStyle ReadParagraphStyle(XElement? properties, ParagraphStyle style, Dictionary<string, ListKind> listKinds)
    {
        if (properties is null) return style;
        foreach (var p in properties.Elements())
            switch (p.Name.LocalName)
            {
                case "jc": style = style with { Alignment = Value(p) switch { "center" => ParagraphAlignment.Center, "right" or "end" => ParagraphAlignment.Right, "both" or "distribute" => ParagraphAlignment.Justify, _ => ParagraphAlignment.Left } }; break;
                case "bidi": style = style with { RightToLeft = On(p) }; break;
                case "ind":
                    if (double.TryParse((string?)p.Attribute(W + "left"), NumberStyles.Float, CultureInfo.InvariantCulture, out var indent))
                        style = style with { Indent = Math.Clamp(indent / 15, 0, 1000) };
                    break;
                case "spacing":
                    if (double.TryParse((string?)p.Attribute(W + "before"), out var before)) style = style with { SpaceBefore = Math.Clamp(before / 15, 0, 1000) };
                    if (double.TryParse((string?)p.Attribute(W + "after"), out var after)) style = style with { SpaceAfter = Math.Clamp(after / 15, 0, 1000) };
                    break;
                case "numPr":
                    var numId = Value(p.Element(W + "numId")) ?? "0";
                    style = style with { List = numId == "0" ? ListKind.None : listKinds.GetValueOrDefault(numId, ListKind.Numbered), ListLevel = Math.Clamp(Number(p.Element(W + "ilvl")), 0, 8) };
                    break;
                case "outlineLvl": style = style with { HeadingLevel = Math.Clamp(Number(p) + 1, 0, 6) }; break;
            }
        return style;
    }

    private static byte[] Write(FlowDocument document, CancellationToken token)
    {
        using var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, true))
        {
            void Part(string name, XElement element)
            {
                token.ThrowIfCancellationRequested();
                using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                new XDocument(new XDeclaration("1.0", "utf-8", "yes"), element).Save(stream);
            }
            var relationships = new List<XElement>
            {
                new(Rel + "Relationship", new XAttribute("Id", "styles"), new XAttribute("Type", R.NamespaceName + "/styles"), new XAttribute("Target", "styles.xml")),
                new(Rel + "Relationship", new XAttribute("Id", "numbering"), new XAttribute("Type", R.NamespaceName + "/numbering"), new XAttribute("Target", "numbering.xml"))
            };
            XElement Val(string name, object value) => new(W + name, new XAttribute(W + "val", value));
            XElement WriteParagraph(Paragraph paragraph)
            {
                var ps = paragraph.Style;
                var pp = new XElement(W + "pPr",
                    Val("jc", ps.Alignment switch { ParagraphAlignment.Center => "center", ParagraphAlignment.Right => "right", ParagraphAlignment.Justify => "both", _ => "left" }),
                    new XElement(W + "spacing", new XAttribute(W + "before", (int)(ps.SpaceBefore * 15)), new XAttribute(W + "after", (int)(ps.SpaceAfter * 15))),
                    new XElement(W + "ind", new XAttribute(W + "left", (int)(ps.Indent * 15))),
                    ps.RightToLeft ? new XElement(W + "bidi") : null,
                    ps.HeadingLevel > 0 ? Val("outlineLvl", ps.HeadingLevel - 1) : null,
                    ps.List != ListKind.None ? new XElement(W + "numPr", Val("ilvl", ps.ListLevel), Val("numId", ps.List == ListKind.Bullet ? 1 : 2)) : null);
                var p = new XElement(W + "p", pp);
                foreach (var run in paragraph.Runs)
                {
                    var s = run.Style;
                    var rp = new XElement(W + "rPr",
                        s.Bold ? new XElement(W + "b") : null,
                        s.Italic ? new XElement(W + "i") : null,
                        s.Underline ? Val("u", "single") : null,
                        s.Strikethrough ? new XElement(W + "strike") : null,
                        Val("sz", (int)Math.Round(s.FontSize * 1.5)),
                        s.FontFamily is not null ? new XElement(W + "rFonts", new XAttribute(W + "ascii", s.FontFamily), new XAttribute(W + "hAnsi", s.FontFamily)) : null,
                        s.Foreground is not null ? Val("color", s.Foreground[^6..]) : null,
                        s.Background is not null ? new XElement(W + "shd", new XAttribute(W + "fill", s.Background[^6..])) : null,
                        s.Baseline != Baseline.Normal ? Val("vertAlign", s.Baseline == Baseline.Subscript ? "subscript" : "superscript") : null);
                    var r = new XElement(W + "r", rp);
                    var segments = System.Text.RegularExpressions.Regex.Split(run.Text, "([\t\u2028])");
                    foreach (var segment in segments)
                        if (segment == "\t") r.Add(new XElement(W + "tab"));
                        else if (segment == "\u2028") r.Add(new XElement(W + "br"));
                        else if (segment.Length > 0) r.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), segment));
                    if (s.Hyperlink is { } link)
                    {
                        var id = $"link{relationships.Count}";
                        relationships.Add(new(Rel + "Relationship", new XAttribute("Id", id),
                            new XAttribute("Type", R.NamespaceName + "/hyperlink"), new XAttribute("Target", link), new XAttribute("TargetMode", "External")));
                        p.Add(new XElement(W + "hyperlink", new XAttribute(R + "id", id), r));
                    }
                    else p.Add(r);
                }
                return p;
            }
            IEnumerable<XElement> WriteBlocks(IEnumerable<Block> blocks)
            {
                foreach (var block in blocks)
                {
                    if (block is Paragraph p) yield return WriteParagraph(p);
                    else if (block is Section s)
                    {
                        foreach (var child in WriteBlocks(s.Blocks)) yield return child;
                    }
                    else if (block is Table table)
                    {
                        var t = new XElement(W + "tbl",
                            new XElement(W + "tblPr", new XElement(W + "tblW", new XAttribute(W + "w", 0), new XAttribute(W + "type", "auto")),
                                new XElement(W + "tblBorders", new[] { "top", "left", "bottom", "right", "insideH", "insideV" }
                                    .Select(side => new XElement(W + side, new XAttribute(W + "val", "single"), new XAttribute(W + "sz", 4), new XAttribute(W + "color", "B8C5D6"))))),
                            new XElement(W + "tblGrid", Enumerable.Range(0, table.ColumnCount).Select(_ => new XElement(W + "gridCol", new XAttribute(W + "w", 9000 / table.ColumnCount)))));
                        for (var row = 0; row < table.Rows.Length; row++)
                        {
                            var tr = new XElement(W + "tr");
                            for (var col = 0; col < table.ColumnCount; col++)
                            {
                                var owner = table.OwnerOf(row, col);
                                if (owner.Column != col) continue;
                                var cell = table.Rows[owner.Row][owner.Column];
                                var continuation = owner.Row != row;
                                var tc = new XElement(W + "tc",
                                    new XElement(W + "tcPr",
                                        new XElement(W + "tcW", new XAttribute(W + "w", 9000 / table.ColumnCount * cell.ColumnSpan), new XAttribute(W + "type", "dxa")),
                                        cell.ColumnSpan > 1 ? Val("gridSpan", cell.ColumnSpan) : null,
                                        cell.RowSpan > 1 ? Val("vMerge", continuation ? "continue" : "restart") : null,
                                        cell.Background is not null ? new XElement(W + "shd", new XAttribute(W + "fill", cell.Background[^6..])) : null));
                                tc.Add(continuation ? [new XElement(W + "p")] : cell.Paragraphs.Select(WriteParagraph));
                                tr.Add(tc);
                            }
                            t.Add(tr);
                        }
                        yield return t;
                    }
                }
            }
            var body = new XElement(W + "body", WriteBlocks(document.Blocks));
            body.Add(new XElement(W + "sectPr",
                new XElement(W + "pgSz", new XAttribute(W + "w", 12240), new XAttribute(W + "h", 15840)),
                new XElement(W + "pgMar", new XAttribute(W + "top", 1440), new XAttribute(W + "bottom", 1440), new XAttribute(W + "left", 1440), new XAttribute(W + "right", 1440))));
            Part("word/document.xml", new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "r", R), body));
            Part("_rels/.rels", new XElement(Rel + "Relationships", new XElement(Rel + "Relationship", new XAttribute("Id", "document"),
                new XAttribute("Type", R.NamespaceName + "/officeDocument"), new XAttribute("Target", "word/document.xml"))));
            Part("word/_rels/document.xml.rels", new XElement(Rel + "Relationships", relationships));
            Part("word/styles.xml", new XElement(W + "styles",
                new XElement(W + "docDefaults", new XElement(W + "rPrDefault", new XElement(W + "rPr", Val("sz", 24))))));
            var numbering = new XElement(W + "numbering");
            for (var id = 1; id <= 2; id++)
            {
                numbering.Add(new XElement(W + "abstractNum", new XAttribute(W + "abstractNumId", id),
                    Enumerable.Range(0, 9).Select(level => new XElement(W + "lvl", new XAttribute(W + "ilvl", level),
                        Val("start", 1), Val("numFmt", id == 1 ? "bullet" : "decimal"), Val("lvlText", id == 1 ? "\u2022" : $"%{level + 1}.")))));
                numbering.Add(new XElement(W + "num", new XAttribute(W + "numId", id), Val("abstractNumId", id)));
            }
            Part("word/numbering.xml", numbering);
            Part("[Content_Types].xml", new XElement(Ct + "Types",
                new XElement(Ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(Ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new[] { ("document", "document.main"), ("styles", "styles"), ("numbering", "numbering") }.Select(p =>
                    new XElement(Ct + "Override", new XAttribute("PartName", $"/word/{p.Item1}.xml"),
                        new XAttribute("ContentType", $"application/vnd.openxmlformats-officedocument.wordprocessingml.{p.Item2}+xml")))));
        }
        return result.ToArray();
    }
}

