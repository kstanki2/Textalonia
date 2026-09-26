using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>A deliberately limited, non-executing HTML/CSS importer. No scripts or remote resources are loaded.</summary>
public sealed class HtmlDocumentFormat : TextDocumentFormat
{
    public override string Name => "HTML";
    public override IReadOnlyList<string> Extensions => [".html", ".htm"];
    private static readonly HashSet<string> BlockTags = ["p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "pre", "blockquote", "section"];
    private static readonly HashSet<string> IgnoredTags = ["script", "style", "head", "iframe", "object", "embed", "template", "noscript"];

    public override FlowDocument Parse(string text)
    {
        if (text.Length > 32 * 1024 * 1024) throw new FormatException("HTML is too large.");
        var html = new HtmlParser().ParseDocument(text);
        var pending = new Stack<(INode Node, int Depth)>();
        pending.Push((html, 0));
        var nodes = 0;
        while (pending.TryPop(out var item))
        {
            if (item.Depth > 64 || ++nodes > 100_000) throw new FormatException("HTML structure exceeds the import limits.");
            foreach (var child in item.Node.ChildNodes) pending.Push((child, item.Depth + 1));
        }
        var blocks = ReadBlocks(html.Body!, TextStyle.Default, ListKind.None, 0);
        var document = new FlowDocument(blocks);
        document.Validate();
        return document;
    }

    private static IEnumerable<Block> ReadBlocks(INode parent, TextStyle inherited, ListKind list, int level)
    {
        var pending = new List<RichRun>();
        var result = new List<Block>();
        void Flush()
        {
            if (pending.Any(r => !string.IsNullOrWhiteSpace(r.Text))) result.Add(new Paragraph(pending));
            pending.Clear();
        }
        foreach (var node in parent.ChildNodes)
        {
            if (node is IElement element)
            {
                var tag = element.LocalName;
                if (IgnoredTags.Contains(tag)) continue;
                if (tag is "ul" or "ol")
                {
                    Flush();
                    result.AddRange(ReadBlocks(element, ReadStyle(element, inherited), tag == "ul" ? ListKind.Bullet : ListKind.Numbered, Math.Min(8, level + 1)));
                }
                else if (tag == "table")
                {
                    Flush(); result.Add(ReadTable(element, inherited));
                }
                else if (BlockTags.Contains(tag))
                {
                    Flush();
                    var style = ReadStyle(element, inherited);
                    if (tag is "section" or "blockquote")
                    {
                        var css = Css(element);
                        result.Add(new Section
                        {
                            Blocks = FlowDocument.EnsureBlocks(ReadBlocks(element, style, list, level).ToImmutableArray()),
                            Background = Color(css.GetValueOrDefault("background-color")),
                            BorderColor = tag == "blockquote" ? "#629BEA" : null
                        });
                    }
                    else if (element.Children.Any(c => BlockTags.Contains(c.LocalName) || c.LocalName is "ul" or "ol" or "table"))
                        result.AddRange(ReadBlocks(element, style, tag == "li" ? list : ListKind.None, level));
                    else
                    {
                        var heading = tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1]) ? tag[1] - '0' : 0;
                        if (heading > 0 && !Css(element).ContainsKey("font-size"))
                            style = style with { Bold = true, FontSize = heading switch { 1 => 32, 2 => 26, 3 => 22, _ => 18 } };
                        var css = Css(element);
                        var runs = ReadInline(element.ChildNodes, style, tag == "pre" || css.GetValueOrDefault("white-space") is "pre" or "pre-wrap" or "break-spaces").ToArray();
                        if (element.Children.Length == 1 && element.Children[0].LocalName == "br" && string.IsNullOrWhiteSpace(element.TextContent)) runs = [];
                        result.Add(new Paragraph(runs)
                        {
                            DefaultStyle = style,
                            Style = new ParagraphStyle
                            {
                                HeadingLevel = heading,
                                List = tag == "li" ? list : ListKind.None,
                                ListLevel = Math.Max(0, level - 1),
                                Alignment = css.GetValueOrDefault("text-align") switch
                                {
                                    "center" => ParagraphAlignment.Center, "right" => ParagraphAlignment.Right,
                                    "justify" => ParagraphAlignment.Justify, _ => ParagraphAlignment.Left
                                },
                                RightToLeft = element.GetAttribute("dir") == "rtl" || css.GetValueOrDefault("direction") == "rtl"
                            }
                        });
                    }
                }
                else pending.AddRange(ReadInline([node], inherited, false));
            }
            else pending.AddRange(ReadInline([node], inherited, false));
        }
        Flush();
        return result;
    }

    private static IEnumerable<RichRun> ReadInline(IEnumerable<INode> nodes, TextStyle inherited, bool preserve)
    {
        foreach (var node in nodes)
        {
            if (node is IText text)
            {
                var value = preserve ? FlowDocument.NormalizeNewlines(text.Data).Replace('\n', '\u2028')
                    : Regex.Replace(text.Data, @"[\t\r\n ]+", " ");
                if (value.Length > 0) yield return new(value, inherited);
            }
            else if (node is IElement e)
            {
                if (IgnoredTags.Contains(e.LocalName)) continue;
                if (e.LocalName == "br") { yield return new("\u2028", inherited); continue; }
                if (e.LocalName == "img")
                {
                    var alt = e.GetAttribute("alt");
                    if (!string.IsNullOrEmpty(alt)) yield return new(alt, inherited);
                    continue;
                }
                foreach (var run in ReadInline(e.ChildNodes, ReadStyle(e, inherited), preserve)) yield return run;
            }
        }
    }

    private static TextStyle ReadStyle(IElement e, TextStyle style)
    {
        style = e.LocalName switch
        {
            "b" or "strong" => style with { Bold = true },
            "i" or "em" => style with { Italic = true },
            "u" => style with { Underline = true },
            "s" or "strike" or "del" => style with { Strikethrough = true },
            "sub" => style with { Baseline = Baseline.Subscript },
            "sup" => style with { Baseline = Baseline.Superscript },
            "code" or "pre" => style with { FontFamily = "monospace" },
            "a" when FlowDocument.IsSafeHyperlink(e.GetAttribute("href") ?? "") =>
                style with { Hyperlink = e.GetAttribute("href"), Underline = true },
            _ => style
        };
        var css = Css(e);
        foreach (var (key, value) in css)
        {
            switch (key)
            {
                case "font-weight": style = style with { Bold = value is "bold" or "bolder" || int.TryParse(value, out var weight) && weight >= 600 }; break;
                case "font-style": style = style with { Italic = value is "italic" or "oblique" }; break;
                case "font-family": style = style with { FontFamily = value.Split(',')[0].Trim(' ', '\'', '"') }; break;
                case "font-size":
                    var number = Regex.Match(value, @"^[0-9]+(?:\.[0-9]+)?").Value;
                    if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
                        style = style with { FontSize = Math.Clamp(size * (value.EndsWith("pt") ? 4d / 3 : 1), 1, 512) };
                    break;
                case "color": style = style with { Foreground = Color(value) ?? style.Foreground }; break;
                case "background":
                case "background-color": style = style with { Background = Color(value) ?? style.Background }; break;
                case "text-decoration":
                case "text-decoration-line":
                    style = style with { Underline = value.Contains("underline"), Strikethrough = value.Contains("line-through") }; break;
                case "vertical-align":
                    style = style with { Baseline = value switch { "sub" => Baseline.Subscript, "super" => Baseline.Superscript, _ => Baseline.Normal } }; break;
            }
        }
        return style;
    }

    private static Dictionary<string, string> Css(IElement e)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (e.GetAttribute("style") ?? "").Split(';'))
        {
            var colon = pair.IndexOf(':');
            if (colon > 0) result[pair[..colon].Trim().ToLowerInvariant()] = pair[(colon + 1)..].Trim();
        }
        return result;
    }

    private static string? Color(string? value)
    {
        if (value is null) return null;
        if (Avalonia.Media.Color.TryParse(value, out var color))
            return color.A == 255 ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        var rgb = Regex.Match(value, @"^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
        return rgb.Success ? $"#{Math.Clamp(int.Parse(rgb.Groups[1].Value), 0, 255):X2}{Math.Clamp(int.Parse(rgb.Groups[2].Value), 0, 255):X2}{Math.Clamp(int.Parse(rgb.Groups[3].Value), 0, 255):X2}" : null;
    }

    private static Table ReadTable(IElement element, TextStyle inherited)
    {
        // Only direct rows and rows in a row group; nested tables are not folded into this grid.
        var rows = element.Children.SelectMany<IElement, IElement>(e => e.LocalName is "thead" or "tbody" or "tfoot" ? e.Children : new[] { e })
            .Where(e => e.LocalName == "tr").ToArray();
        if (rows.Length == 0) return Table.Create(1, 1);
        if (rows.Length > 1000) throw new FormatException("Table is too large.");
        var cells = new Dictionary<(int, int), TableCell>();
        var occupied = new HashSet<(int, int)>();
        var columns = 1;
        for (var r = 0; r < rows.Length; r++)
        {
            var c = 0;
            foreach (var e in rows[r].Children.Where(e => e.LocalName is "td" or "th"))
            {
                while (occupied.Contains((r, c))) c++;
                var colSpan = int.TryParse(e.GetAttribute("colspan"), out var cs) ? Math.Clamp(cs, 1, 100) : 1;
                var rowSpan = int.TryParse(e.GetAttribute("rowspan"), out var rs) ? Math.Clamp(rs, 1, rows.Length - r) : 1;
                if (c + colSpan > 100) throw new FormatException("Table is too wide.");
                var blocks = ReadBlocks(e, ReadStyle(e, inherited) with { Bold = inherited.Bold || e.LocalName == "th" }, ListKind.None, 0);
                var paragraphs = new DocumentIndex(new FlowDocument(blocks)).Paragraphs.Select(p => p.Paragraph).ToImmutableArray();
                cells[(r, c)] = new TableCell
                {
                    Paragraphs = FlowDocument.EnsureParagraphs(paragraphs), ColumnSpan = colSpan, RowSpan = rowSpan,
                    Background = Color(Css(e).GetValueOrDefault("background-color"))
                };
                for (var y = r; y < r + rowSpan; y++)
                    for (var x = c; x < c + colSpan; x++) occupied.Add((y, x));
                c += colSpan; columns = Math.Max(columns, c);
            }
        }
        return new Table
        {
            Rows = Enumerable.Range(0, rows.Length).Select(r => Enumerable.Range(0, columns)
                .Select(c => cells.GetValueOrDefault((r, c)) ?? new TableCell()).ToImmutableArray()).ToImmutableArray()
        };
    }

    public override string Serialize(FlowDocument document)
    {
        var builder = new StringBuilder("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>");
        WriteBlocks(builder, document.Blocks);
        return builder.Append("</body></html>").ToString();
    }

    private static void WriteBlocks(StringBuilder b, IEnumerable<Block> blocks)
    {
        ListKind openList = ListKind.None;
        void CloseList()
        {
            if (openList != ListKind.None) b.Append(openList == ListKind.Bullet ? "</ul>" : "</ol>");
            openList = ListKind.None;
        }
        foreach (var block in blocks)
        {
            if (block is Paragraph p)
            {
                if (p.Style.List != openList)
                {
                    CloseList();
                    openList = p.Style.List;
                    if (openList != ListKind.None) b.Append(openList == ListKind.Bullet ? "<ul>" : "<ol>");
                }
                var tag = openList != ListKind.None ? "li" : p.Style.HeadingLevel > 0 ? $"h{p.Style.HeadingLevel}" : "p";
                b.Append('<').Append(tag).Append(" dir=\"").Append(p.Style.RightToLeft ? "rtl" : "ltr")
                    .Append("\" style=\"white-space:pre-wrap;text-align:").Append(p.Style.Alignment.ToString().ToLowerInvariant())
                    .Append(";margin-left:").Append((p.Style.Indent + 24 * p.Style.ListLevel).ToString(CultureInfo.InvariantCulture)).Append("px\">");
                foreach (var run in p.Runs) WriteRun(b, run);
                if (p.Runs.IsEmpty) b.Append("<br>");
                b.Append("</").Append(tag).Append('>');
            }
            else
            {
                CloseList();
                if (block is Section s)
                {
                    b.Append("<section style=\"background-color:").Append(s.Background ?? "transparent").Append("\">");
                    WriteBlocks(b, s.Blocks); b.Append("</section>");
                }
                else if (block is Table t)
                {
                    b.Append("<table style=\"border-collapse:collapse\" border=\"1\">");
                    for (var r = 0; r < t.Rows.Length; r++)
                    {
                        b.Append("<tr>");
                        for (var c = 0; c < t.ColumnCount; c++)
                        {
                            if (t.IsCovered(r, c)) continue;
                            var cell = t.Rows[r][c];
                            b.Append("<td rowspan=\"").Append(cell.RowSpan).Append("\" colspan=\"").Append(cell.ColumnSpan)
                                .Append("\" style=\"background-color:").Append(cell.Background ?? "transparent").Append("\">");
                            WriteBlocks(b, cell.Paragraphs); b.Append("</td>");
                        }
                        b.Append("</tr>");
                    }
                    b.Append("</table>");
                }
            }
        }
        CloseList();
    }

    private static void WriteRun(StringBuilder b, RichRun run)
    {
        var s = run.Style;
        if (s.Hyperlink is not null) b.Append("<a href=\"").Append(WebUtility.HtmlEncode(s.Hyperlink)).Append("\">");
        b.Append("<span style=\"font-size:").Append(s.FontSize.ToString(CultureInfo.InvariantCulture))
            .Append("px;font-weight:").Append(s.Bold ? "bold" : "normal").Append(";font-style:").Append(s.Italic ? "italic" : "normal");
        if (s.FontFamily is not null) b.Append(";font-family:").Append(WebUtility.HtmlEncode(s.FontFamily.Replace(";", "")));
        if (s.Foreground is not null) b.Append(";color:").Append(CssColor(s.Foreground));
        if (s.Background is not null) b.Append(";background-color:").Append(CssColor(s.Background));
        if (s.Underline || s.Strikethrough) b.Append(";text-decoration:").Append(s.Underline ? "underline " : "").Append(s.Strikethrough ? "line-through" : "");
        if (s.Baseline != Baseline.Normal) b.Append(";vertical-align:").Append(s.Baseline == Baseline.Subscript ? "sub" : "super");
        b.Append("\">").Append(WebUtility.HtmlEncode(run.PlainText).Replace("\u2028", "<br>")).Append("</span>");
        if (s.Hyperlink is not null) b.Append("</a>");
    }
    private static string CssColor(string value) => value.Length == 9 ? "#" + value[3..] + value[1..3] : value;
}
