using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>A bounded, non-executing HTML/CSS importer. Scripts and remote resources are never loaded.</summary>
public sealed class HtmlDocumentFormat : TextDocumentFormat
{
    public override string Name => "HTML";
    public override IReadOnlyList<string> Extensions => [".html", ".htm"];
    private static readonly HashSet<string> BlockTags = ["p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "pre", "blockquote", "section"];
    private static readonly HashSet<string> IgnoredTags = ["script", "style", "iframe", "object", "embed", "template", "noscript", "link"];
    private static readonly HashSet<string> SupportedTags = ["html", "head", "body", "meta", "title", "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "pre", "blockquote", "section", "ul", "ol", "table", "thead", "tbody", "tfoot", "tr", "td", "th", "colgroup", "col", "span", "b", "strong", "i", "em", "u", "s", "strike", "del", "sub", "sup", "code", "a", "br", "img"];
    private static readonly HashSet<string> SupportedStyles = ["font-weight", "font-style", "font-family", "font-size", "font-stretch", "color", "background", "background-color", "text-decoration", "text-decoration-line", "vertical-align", "text-align", "direction", "margin", "margin-top", "margin-bottom", "margin-left", "margin-right", "text-indent", "line-height", "letter-spacing", "white-space", "padding", "padding-left", "padding-top", "padding-right", "padding-bottom", "border", "border-left", "border-top", "border-right", "border-bottom", "border-collapse", "width", "height", "min-height", "list-style-type", "table-layout", "break-inside"];
    private static readonly string[] StretchNames = ["ultra-condensed", "extra-condensed", "condensed", "semi-condensed", "normal", "semi-expanded", "expanded", "extra-expanded", "ultra-expanded"];
    private const string Metadata = "data-textalonia-";
    private sealed class ImportContext
    {
        public readonly Dictionary<string, DocumentResource> Resources = new(StringComparer.Ordinal);
        public long EmbeddedBytes;
    }
    private sealed record ListContext(ListKind Kind, Guid? Id, int Level, ListDefinition Definition, int? Start);
    private sealed class WhitespaceState
    {
        public bool AtLineStart = true;
        public RichRun? PendingSpace;
        public void Reset() { AtLineStart = true; PendingSpace = null; }
    }

    public override FlowDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 32 * 1024 * 1024) throw new FormatException("HTML is too large.");
        var html = new HtmlParser().ParseDocument(text);
        var pending = new Stack<(INode Node, int Depth)>();
        pending.Push((html, 0));
        var nodes = 0;
        while (pending.TryPop(out var item))
        {
            if (item.Depth > 64 || ++nodes > 100_000) throw new FormatException("HTML structure exceeds the import limits.");
            if (item.Node is IElement element) Inspect(element);
            foreach (var child in item.Node.ChildNodes.Reverse()) pending.Push((child, item.Depth + 1));
        }
        var context = new ImportContext();
        var document = new FlowDocument(ReadBlocks(html.Body!, ReadStyle(html.Body!, TextStyle.Default), context))
        { Resources = context.Resources.ToImmutableDictionary(StringComparer.Ordinal) };
        document.Validate();
        return document;
    }

    private static void Inspect(IElement element)
    {
        var tag = element.LocalName;
        if (IgnoredTags.Contains(tag)) Report("html.unsupported-element", tag, "Element and its contents were omitted.", element);
        else if (!SupportedTags.Contains(tag)) Report("html.unsupported-element", tag, "Child content was retained without the element's semantics.", element);
        foreach (var (name, value) in Css(element))
        {
            var mapped = SupportedStyles.Contains(name);
            if (name is "width") mapped = tag is "img" or "col" or "table" or "td" or "th";
            if (name is "height" or "min-height") mapped = tag == "tr" || tag == "img" && name == "height";
            if (name == "border-collapse") mapped = tag == "table" && value == "collapse";
            if (name == "list-style-type") mapped = tag is "ul" or "ol";
            if (name == "padding" || name.StartsWith("padding-", StringComparison.Ordinal) || name == "border" || name.StartsWith("border-", StringComparison.Ordinal) && name != "border-collapse")
                mapped = tag is "section" or "blockquote" or "td" or "th";
            if (name is "text-align" or "direction" or "text-indent" or "line-height" or "letter-spacing" || name == "margin" || name.StartsWith("margin-", StringComparison.Ordinal))
                mapped = BlockTags.Contains(tag) || tag is "body" or "td" or "th" or "table";
            if (name == "font-style" && value is not ("normal" or "italic" or "oblique")) mapped = false;
            if (name == "vertical-align" && value is not ("baseline" or "sub" or "super") && !(tag is "td" or "th" && value is "top" or "middle" or "bottom")) mapped = false;
            if (name == "table-layout") mapped = tag == "table" && value is "auto" or "fixed";
            if (name == "break-inside") mapped = tag == "tr" && value is "auto" or "avoid";
            if (name == "text-align" && value is not ("left" or "center" or "right" or "justify" or "start" or "end")) mapped = false;
            if (name == "direction" && value is not ("ltr" or "rtl")) mapped = false;
            if (name == "white-space" && value is not ("normal" or "pre" or "pre-wrap" or "break-spaces")) mapped = false;
            if (name is "text-decoration" or "text-decoration-line" && value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part is not ("none" or "underline" or "line-through"))) mapped = false;
            if (name == "font-family" && value.Contains(',')) Report("html.font-family", "CSS font fallback list", "The first font family was retained.", element);
            if (!mapped) Report("html.unsupported-style", $"{name}: {value}", "Unsupported style or component was ignored; supported components were retained.", element);
        }
        if ((element.HasAttribute("width") && tag is not ("img" or "col" or "table" or "td" or "th")) || (element.HasAttribute("height") && tag is not ("img" or "tr")))
            Report("html.unsupported-style", "Element width or height attribute", "Container size was determined by document flow.", element);
        if (element.HasAttribute("class")) Report("html.unsupported-style", "CSS class rules", "Only inline CSS and semantic HTML tags were applied.", element);
        if (tag == "a" && element.GetAttribute("href") is { } href && !FlowDocument.IsSafeHyperlink(href))
            Report("html.unsafe-link", "Unsafe or relative hyperlink", "Link text was retained without the hyperlink.", element);
        if (tag == "ol" && element.HasAttribute("reversed"))
            Report("html.list-reversed", "Reversed ordered list", "List was imported using ascending numbering.", element);
    }

    private static IEnumerable<Block> ReadBlocks(INode parent, TextStyle inherited, ImportContext context, ListContext? list = null)
    {
        var pending = new List<RichRun>();
        var whitespace = new WhitespaceState();
        var result = new List<Block>();
        var listApplied = false;
        void AddParagraph(IElement? element, IEnumerable<RichRun> runs, TextStyle style)
        {
            result.Add(ReadParagraph(element, runs, style, listApplied ? null : list));
            listApplied = true;
        }
        void Flush()
        {
            var emptyListItem = list is not null && parent is IElement item && item.Children.Where(c => c.LocalName is not ("ul" or "ol")).ToArray() is [{ LocalName: "br" }];
            if (emptyListItem && !listApplied) AddParagraph(parent as IElement, [], inherited);
            else if (pending.Any(r => r.Inline is not null || !string.IsNullOrWhiteSpace(r.Text) || parent is IElement preserving && PreservesWhitespace(preserving) && r.Text.Length > 0))
                AddParagraph(parent as IElement, pending, inherited);
            pending.Clear(); whitespace.Reset();
        }
        foreach (var node in parent.ChildNodes)
        {
            if (node is IElement element)
            {
                var tag = element.LocalName;
                if (IgnoredTags.Contains(tag) || tag == "head") continue;
                if (tag is "ul" or "ol")
                {
                    Flush();
                    result.AddRange(ReadList(element, ReadStyle(element, inherited), context, list));
                }
                else if (tag == "table")
                {
                    Flush(); result.Add(ReadTable(element, inherited, context));
                }
                else if (tag is "section" or "blockquote")
                {
                    Flush();
                    var css = Css(element);
                    result.Add(new Section
                    {
                        Blocks = FlowDocument.EnsureBlocks(ReadBlocks(element, ReadStyle(element, inherited), context).ToImmutableArray()),
                        Background = ReadColor(css, "background-color", element) ?? ReadColor(css, "background", element),
                        BorderColor = element.GetAttribute(Metadata + "border-color") is { } color ? Color(color) : tag == "blockquote" ? "#629BEA" : null,
                        Padding = MetadataNumber(element, "padding", 12),
                        PaddingEdges = !MetadataBoolean(element, "padding-edges", true) ? null : ReadEdges(css, "padding", element),
                        Borders = !MetadataBoolean(element, "borders", true) ? null : ReadBorders(css, element)
                    });
                }
                else if (BlockTags.Contains(tag))
                {
                    Flush();
                    var style = ReadStyle(element, inherited);
                    if (element.Children.Any(c => BlockTags.Contains(c.LocalName) || c.LocalName is "ul" or "ol" or "table"))
                    {
                        result.AddRange(ReadBlocks(element, style, context, listApplied ? null : list));
                        listApplied = true;
                    }
                    else
                    {
                        var heading = Heading(element);
                        if (heading > 0 && !Css(element).ContainsKey("font-size"))
                            style = style with { Bold = true, FontSize = heading switch { 1 => 32, 2 => 26, 3 => 22, _ => 18 } };
                        var runs = ReadInline(element.ChildNodes, style, PreservesWhitespace(element), context).ToArray();
                        if (element.Children.Length == 1 && element.Children[0].LocalName == "br" && string.IsNullOrWhiteSpace(element.TextContent)) runs = [];
                        AddParagraph(element, runs, style);
                    }
                }
                else pending.AddRange(ReadInline([node], inherited, parent is IElement p && PreservesWhitespace(p), context, whitespace));
            }
            else pending.AddRange(ReadInline([node], inherited, parent is IElement p && PreservesWhitespace(p), context, whitespace));
        }
        Flush();
        if (list is not null && !listApplied && parent is IElement item && !item.HasAttribute(Metadata + "list-container"))
            result.Insert(0, ReadParagraph(item, [], inherited, list));
        return result;
    }

    private static IEnumerable<Block> ReadList(IElement element, TextStyle inherited, ImportContext context, ListContext? parent)
    {
        var kind = element.LocalName == "ol" ? ListKind.Numbered : ListKind.Bullet;
        var level = Math.Min(8, parent is null ? 0 : parent.Level + 1);
        if (parent?.Level == 8) Report("html.list-depth", "List nesting beyond nine levels", "Nesting was clamped to level nine.", element);
        Guid? id = element.HasAttribute(Metadata + "list-id") ? ParseId(element.GetAttribute(Metadata + "list-id")) : parent?.Id ?? Guid.NewGuid();
        var marker = Css(element).GetValueOrDefault("list-style-type") ?? element.GetAttribute("type");
        var format = new ListLevelDefinition
        {
            Kind = kind, Marker = marker switch
            {
                "a" or "lower-alpha" or "lower-latin" => ListMarkerStyle.LowerLetter,
                "A" or "upper-alpha" or "upper-latin" => ListMarkerStyle.UpperLetter,
                "i" or "lower-roman" => ListMarkerStyle.LowerRoman,
                "I" or "upper-roman" => ListMarkerStyle.UpperRoman,
                _ => kind == ListKind.Bullet ? ListMarkerStyle.Bullet : ListMarkerStyle.Decimal
            },
            Text = marker switch { "circle" => "\u25e6", "square" => "\u25aa", _ => null }
        };
        if (marker is not null && marker is not ("a" or "A" or "i" or "I" or "1" or "lower-alpha" or "lower-latin" or "upper-alpha" or "upper-latin" or "lower-roman" or "upper-roman" or "decimal" or "disc" or "circle" or "square"))
            Report("html.unsupported-style", "list-style-type: " + marker, "Default list marker was used.", element);
        var definitions = (parent?.Definition.Levels ?? []).ToBuilder();
        while (definitions.Count <= level) definitions.Add(new());
        definitions[level] = format;
        var definition = new ListDefinition { Levels = definitions.ToImmutable() };
        int? start = PositiveInteger(element.GetAttribute("start"), element);
        foreach (var child in element.Children)
        {
            if (child.LocalName != "li") continue;
            var childList = new ListContext(kind, id, level, definition, PositiveInteger(child.GetAttribute("value"), child) ?? start);
            foreach (var block in ReadBlocks(child, ReadStyle(child, inherited), context, childList)) yield return block;
            start = null;
        }
    }

    private static Paragraph ReadParagraph(IElement? element, IEnumerable<RichRun> runs, TextStyle textStyle, ListContext? list)
    {
        var css = element is null ? new Dictionary<string, string>() : Css(element);
        if (!css.ContainsKey("direction") && element?.GetAttribute("dir") is { } ownDirection) css["direction"] = ownDirection;
        // These CSS properties inherit through containers; margins do not.
        for (var ancestor = element?.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            var inheritedCss = Css(ancestor);
            foreach (var name in new[] { "text-align", "direction", "line-height", "letter-spacing", "text-indent" })
                if (!css.ContainsKey(name) && inheritedCss.TryGetValue(name, out var value)) css[name] = value;
            if (!css.ContainsKey("direction") && ancestor.GetAttribute("dir") is { } direction) css["direction"] = direction;
        }
        var margin = ReadEdges(css, "margin", element);
        var own = element?.HasAttribute(Metadata + "list") == true;
        var definition = list?.Definition;
        if (own)
        {
            definition = null;
            if (element!.GetAttribute(Metadata + "list-definition") is { Length: > 0 } value)
            {
                if (value.Length > 16_384) throw new FormatException("List metadata is too large.");
                try { definition = JsonSerializer.Deserialize<ListDefinition>(value); definition?.Validate(); }
                catch (JsonException ex) { throw new FormatException("Invalid HTML list metadata.", ex); }
            }
        }
        var rightToLeft = element?.GetAttribute("dir") == "rtl" || css.GetValueOrDefault("direction") == "rtl";
        return new Paragraph(runs)
        {
            DefaultStyle = textStyle,
            Style = new ParagraphStyle
            {
                HeadingLevel = element is null ? 0 : MetadataInteger(element, "heading", Heading(element)),
                List = list?.Kind ?? ListKind.None,
                ListLevel = own ? MetadataInteger(element!, "list-level", 0) : list?.Level ?? 0,
                ListId = own ? ParseId(element!.GetAttribute(Metadata + "list-id")) : list?.Id,
                ListDefinition = definition,
                ListStart = own ? MetadataListStart(element!) : list?.Start,
                ListRestart = own && MetadataBoolean(element!, "list-restart", false),
                Alignment = css.GetValueOrDefault("text-align") switch
                { "center" => ParagraphAlignment.Center, "right" => ParagraphAlignment.Right, "justify" => ParagraphAlignment.Justify,
                    "start" when rightToLeft => ParagraphAlignment.Right, "end" when !rightToLeft => ParagraphAlignment.Right, _ => ParagraphAlignment.Left },
                RightToLeft = rightToLeft,
                Indent = Bound(margin?.Left ?? 0, 0, 1000, element, "margin-left"), RightIndent = Bound(margin?.Right ?? 0, 0, 100000, element, "margin-right"),
                SpaceBefore = Bound(margin?.Top ?? 0, 0, 1000, element, "margin-top"), SpaceAfter = Bound(margin?.Bottom ?? 8, 0, 1000, element, "margin-bottom"),
                FirstLineIndent = Bound(Length(css.GetValueOrDefault("text-indent"), 0, element), -100000, 100000, element, "text-indent"),
                LetterSpacing = Bound(Length(css.GetValueOrDefault("letter-spacing"), 0, element), -1000, 1000, element, "letter-spacing"),
                LineHeight = ReadLineHeight(css.GetValueOrDefault("line-height"), textStyle.FontSize, element)
            }
        };
    }

    private static IEnumerable<RichRun> ReadInline(IEnumerable<INode> nodes, TextStyle inherited, bool preserve, ImportContext context, WhitespaceState? whitespace = null)
    {
        whitespace ??= new WhitespaceState();
        foreach (var node in nodes)
        {
            if (node is IText text)
            {
                var value = preserve ? FlowDocument.NormalizeNewlines(text.Data).Replace('\n', '\u2028') : Regex.Replace(text.Data, @"[\t\r\n ]+", " ");
                if (value.Length == 0) continue;
                if (preserve)
                {
                    if (whitespace.PendingSpace is { } before) { yield return before; whitespace.PendingSpace = null; }
                    yield return new RichRun(value, inherited); whitespace.AtLineStart = value.EndsWith('\u2028');
                }
                else
                {
                    var content = value.Trim(' ');
                    if (content.Length > 0)
                    {
                        if (whitespace.PendingSpace is { } before) yield return before;
                        else if (value[0] == ' ' && !whitespace.AtLineStart) yield return new RichRun(" ", inherited);
                        yield return new RichRun(content, inherited);
                        whitespace.AtLineStart = false;
                        whitespace.PendingSpace = value[^1] == ' ' ? new RichRun(" ", inherited) : null;
                    }
                    else if (!whitespace.AtLineStart) whitespace.PendingSpace ??= new RichRun(" ", inherited);
                }
            }
            else if (node is IElement element)
            {
                if (IgnoredTags.Contains(element.LocalName)) continue;
                if (element.LocalName == "br")
                { whitespace.Reset(); yield return new RichRun("\u2028", inherited); continue; }
                var style = ReadStyle(element, inherited);
                if (element.LocalName == "img")
                {
                    foreach (var image in ReadImage(element, style, context))
                    {
                        if (whitespace.PendingSpace is { } before) { yield return before; whitespace.PendingSpace = null; }
                        yield return image; whitespace.AtLineStart = false;
                    }
                    continue;
                }
                var childPreserve = Css(element).GetValueOrDefault("white-space") switch
                { "normal" => false, "pre" or "pre-wrap" or "break-spaces" => true, _ => preserve || element.LocalName == "pre" };
                foreach (var run in ReadInline(element.ChildNodes, style, childPreserve, context, whitespace)) yield return run;
            }
        }
    }
    private static IEnumerable<RichRun> ReadImage(IElement element, TextStyle style, ImportContext context)
    {
        var source = element.GetAttribute("src") ?? "";
        var alt = element.GetAttribute("alt") ?? "";
        var match = Regex.Match(source, @"^data:(image/(?:png|jpeg|gif|webp|bmp));base64,([\s\S]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            var encoded = match.Groups[2].Value;
            if (encoded.Length > (DocumentResource.MaximumEmbeddedBytes + 2L) / 3 * 4 + 1024) throw new FormatException("Embedded HTML image is too large.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException ex) { throw new FormatException("Invalid embedded HTML image data.", ex); }
            if (bytes.Length > DocumentResource.MaximumEmbeddedBytes) throw new FormatException("Embedded HTML image is too large.");
            var resource = new DocumentResource { MediaType = match.Groups[1].Value.ToLowerInvariant(), Data = ImmutableArray.CreateRange(bytes) };
            var key = "html-image-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) + "-" + resource.MediaType.Replace('/', '-');
            if (!context.Resources.ContainsKey(key))
            {
                if ((context.EmbeddedBytes += bytes.Length) > DocumentResource.MaximumDocumentEmbeddedBytes) throw new FormatException("Embedded HTML images exceed the resource limits.");
                if (context.Resources.Count >= 4096) throw new FormatException("Too many embedded HTML images.");
                context.Resources.Add(key, resource);
            }
            var css = Css(element);
            yield return new RichRun(new InlineDescriptor
            {
                AltText = alt, Width = Bound(Length(css.GetValueOrDefault("width") ?? element.GetAttribute("width"), 32, element), 1, 10000, element, "image width"),
                Height = Bound(Length(css.GetValueOrDefault("height") ?? element.GetAttribute("height"), 32, element), 1, 10000, element, "image height"),
                Payload = new ImageInlinePayload(key)
            }, style);
        }
        else
        {
            Report("html.resource", "External or unsupported image source", "Image was replaced with alternative text; no resource was fetched.", element);
            if (alt.Length > 0) yield return new RichRun(alt, style);
        }
    }

    private static TextStyle ReadStyle(IElement element, TextStyle style)
    {
        style = element.LocalName switch
        {
            "b" or "strong" => style with { Bold = true, FontWeight = null },
            "i" or "em" => style with { Italic = true }, "u" => style with { Underline = true },
            "s" or "strike" or "del" => style with { Strikethrough = true },
            "sub" => style with { Baseline = Baseline.Subscript }, "sup" => style with { Baseline = Baseline.Superscript },
            "code" or "pre" => style with { FontFamily = "monospace" },
            "a" when FlowDocument.IsSafeHyperlink(element.GetAttribute("href") ?? "") => style with { Hyperlink = element.GetAttribute("href"), Underline = true },
            _ => style
        };
        foreach (var (key, value) in Css(element))
        {
            switch (key)
            {
                case "font-weight":
                    if (int.TryParse(value, out var weight) && weight is >= 1 and <= 1000) style = style with { FontWeight = weight, Bold = weight >= 600 };
                    else if (value is "normal" or "bold" or "bolder") style = style with { FontWeight = null, Bold = value != "normal" };
                    else Report("html.unsupported-style", key + ": " + value, "Inherited font weight was retained.", element);
                    break;
                case "font-style": style = style with { Italic = value is "italic" or "oblique" }; break;
                case "font-family": style = style with { FontFamily = value == "initial" ? null : value.Split(',')[0].Trim(' ', '\'', '"') }; break;
                case "font-size": style = style with { FontSize = Bound(Length(value, style.FontSize, element), 1, 512, element, "font-size") }; break;
                case "font-stretch":
                    var stretch = Array.IndexOf(StretchNames, value);
                    if (stretch >= 0) style = style with { FontStretch = stretch + 1 };
                    else Report("html.unsupported-style", key + ": " + value, "Inherited font stretch was retained.", element);
                    break;
                case "color": style = style with { Foreground = value == "initial" ? null : ReadColor(value, element) ?? style.Foreground }; break;
                case "background": case "background-color": style = style with { Background = value == "initial" ? null : ReadColor(value, element) ?? style.Background }; break;
                case "text-decoration": case "text-decoration-line": style = style with { Underline = value.Contains("underline"), Strikethrough = value.Contains("line-through") }; break;
                case "vertical-align": style = style with { Baseline = value switch { "sub" => Baseline.Subscript, "super" => Baseline.Superscript, _ => Baseline.Normal } }; break;
            }
        }
        if (element.HasAttribute(Metadata + "bold")) style = style with { Bold = MetadataBoolean(element, "bold", false) };
        if (element.GetAttribute(Metadata + "hyperlink") is { } hyperlink)
        {
            if (hyperlink.Length > 0 && !FlowDocument.IsSafeHyperlink(hyperlink)) throw new FormatException("Unsafe hyperlink in HTML metadata.");
            style = style with { Hyperlink = hyperlink.Length == 0 ? null : hyperlink };
        }
        return style;
    }

    private static Dictionary<string, string> Css(IElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (element.GetAttribute("style") ?? "").Split(';'))
        {
            var colon = pair.IndexOf(':');
            if (colon > 0) result[pair[..colon].Trim().ToLowerInvariant()] = pair[(colon + 1)..].Trim();
        }
        return result;
    }
    private static int Heading(IElement element) => element.LocalName is { Length: 2 } tag && tag[0] == 'h' && tag[1] is >= '1' and <= '6' ? tag[1] - '0' : 0;
    private static bool PreservesWhitespace(IElement element)
    {
        for (IElement? current = element; current is not null; current = current.ParentElement)
        {
            var mode = Css(current).GetValueOrDefault("white-space");
            if (mode == "normal") return false;
            if (mode is "pre" or "pre-wrap" or "break-spaces" || current.LocalName == "pre") return true;
        }
        return false;
    }
    private static double Number(string? value, double fallback) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : fallback;
    private static Guid? ParseId(string? value) => string.IsNullOrEmpty(value) ? null : Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new FormatException("Invalid list identity in HTML metadata.");
    private static double MetadataNumber(IElement element, string name, double fallback)
    {
        var value = element.GetAttribute(Metadata + name);
        if (value is null) return fallback;
        var number = Number(value, double.NaN);
        if (!double.IsFinite(number)) throw new FormatException("Invalid numeric HTML metadata.");
        return number;
    }
    private static int MetadataInteger(IElement element, string name, int fallback)
    {
        var number = MetadataNumber(element, name, fallback);
        if (number < int.MinValue || number > int.MaxValue || Math.Truncate(number) != number) throw new FormatException("Invalid integer HTML metadata.");
        return (int)number;
    }
    private static bool MetadataBoolean(IElement element, string name, bool fallback)
    {
        var value = element.GetAttribute(Metadata + name);
        if (value is null) return fallback;
        return bool.TryParse(value, out var result) ? result : throw new FormatException("Invalid Boolean HTML metadata.");
    }
    private static int? MetadataListStart(IElement element)
    {
        var value = element.GetAttribute(Metadata + "list-start");
        if (string.IsNullOrEmpty(value)) return null;
        return int.TryParse(value, out var start) && start is >= 1 and <= 1_000_000 ? start : throw new FormatException("Invalid list start in HTML metadata.");
    }
    private static int? PositiveInteger(string? value, IElement element)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (int.TryParse(value, out var integer) && integer is >= 1 and <= 1_000_000) return integer;
        Report("html.list-start", "Unsupported list counter: " + value, "Default positive list numbering was used.", element); return null;
    }
    private static double Bound(double value, double minimum, double maximum, IElement? element, string feature)
    {
        if (value < minimum || value > maximum)
            Report("html.value-range", feature + ": " + N(value), "Value was clamped to the supported range " + N(minimum) + " through " + N(maximum) + ".", element);
        return Math.Clamp(value, minimum, maximum);
    }
    private static double Length(string? value, double fallback, IElement? element)
    {
        if (value is null or "normal" or "auto" or "initial") return fallback;
        var match = Regex.Match(value, @"^([+-]?(?:\d+(?:\.\d*)?|\.\d+))(px|pt|in|cm|mm)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            number *= match.Groups[2].Value.ToLowerInvariant() switch { "pt" => 4d / 3, "in" => 96, "cm" => 96 / 2.54, "mm" => 96 / 25.4, _ => 1 };
            if (double.IsFinite(number)) return number;
        }
        Report("html.unsupported-style", "Unsupported CSS length: " + value, "Default or inherited length was used.", element); return fallback;
    }
    private static double? ReadLineHeight(string? value, double fontSize, IElement? element)
    {
        if (value is null or "normal" or "initial") return null;
        var number = Number(value, double.NaN);
        var length = double.IsNaN(number) ? Length(value, 0, element) : number * fontSize;
        if (length <= 0) { Report("html.value-range", "Nonpositive line-height", "Natural line height was used.", element); return null; }
        return Bound(length, 1, 10000, element, "line-height");
    }
    private static EdgeInsets? ReadEdges(Dictionary<string, string> css, string name, IElement? element)
    {
        if (!css.Keys.Any(key => key == name || key.StartsWith(name + "-", StringComparison.Ordinal))) return null;
        double top = 0, right = 0, bottom = name == "margin" ? 8 : 0, left = 0;
        if (css.TryGetValue(name, out var shorthand))
        {
            var values = shorthand.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => Length(value, 0, element)).ToArray();
            if (values.Length is >= 1 and <= 4)
            { top = values[0]; right = values.Length > 1 ? values[1] : top; bottom = values.Length > 2 ? values[2] : top; left = values.Length > 3 ? values[3] : right; }
            else Report("html.unsupported-style", name + ": " + shorthand, "Default insets were used.", element);
        }
        var maximum = name == "padding" ? 1000 : 100000;
        return new(Bound(Length(css.GetValueOrDefault(name + "-left"), left, element), 0, maximum, element, name + "-left"), Bound(Length(css.GetValueOrDefault(name + "-top"), top, element), 0, maximum, element, name + "-top"),
            Bound(Length(css.GetValueOrDefault(name + "-right"), right, element), 0, maximum, element, name + "-right"), Bound(Length(css.GetValueOrDefault(name + "-bottom"), bottom, element), 0, maximum, element, name + "-bottom"));
    }
    private static BlockBorders? ReadBorders(Dictionary<string, string> css, IElement element)
    {
        if (!css.Keys.Any(key => key == "border" || key is "border-left" or "border-top" or "border-right" or "border-bottom")) return null;
        BorderSide? Side(string name)
        {
            var value = css.GetValueOrDefault(name) ?? css.GetValueOrDefault("border");
            if (value is null or "none" or "0" or "0px") return null;
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => p is "groove" or "ridge" or "inset" or "outset"))
                Report("html.unsupported-style", name + ": " + value, "Border was approximated as solid.", element);
            var width = parts.FirstOrDefault(p => Regex.IsMatch(p, @"^\d"));
            var color = parts.Select(Color).FirstOrDefault(c => c is not null);
            return new(Bound(Length(width, 1, element), 0, 1000, element, name), color)
            { Kind = parts.Contains("none") ? BorderKind.None : parts.Contains("dashed") ? BorderKind.Dashed : parts.Contains("dotted") ? BorderKind.Dotted : parts.Contains("double") ? BorderKind.Double : BorderKind.Solid };
        }
        return new(Side("border-left"), Side("border-top"), Side("border-right"), Side("border-bottom"));
    }
    private static string? ReadColor(Dictionary<string, string> css, string key, IElement? element) => css.TryGetValue(key, out var value) ? ReadColor(value, element) : null;
    private static string? ReadColor(string value, IElement? element)
    {
        if (value == "initial") return null;
        var color = Color(value);
        if (color is null) Report("html.unsupported-style", "Unsupported color: " + value, "Inherited color was retained.", element);
        return color;
    }
    private static string? Color(string? value)
    {
        if (value is null) return null;
        if (value == "transparent") return "#00000000";
        if (Regex.IsMatch(value, "^#[0-9a-fA-F]{8}$")) return "#" + value[7..9].ToUpperInvariant() + value[1..7].ToUpperInvariant();
        if (Regex.IsMatch(value, "^#[0-9a-fA-F]{4}$")) return $"#{value[4]}{value[4]}{value[1]}{value[1]}{value[2]}{value[2]}{value[3]}{value[3]}".ToUpperInvariant();
        var rgb = Regex.Match(value, @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*([0-9.]+))?\s*\)$", RegexOptions.IgnoreCase);
        if (rgb.Success)
        {
            var alpha = rgb.Groups[4].Success ? (int)Math.Round(Math.Clamp(Number(rgb.Groups[4].Value, 1), 0, 1) * 255) : 255;
            return "#" + (alpha == 255 ? "" : alpha.ToString("X2")) + string.Concat(Enumerable.Range(1, 3).Select(i => Math.Clamp(int.Parse(rgb.Groups[i].Value), 0, 255).ToString("X2")));
        }
        return Avalonia.Media.Color.TryParse(value, out var color) ? color.A == 255 ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}" : null;
    }

    private static Table ReadTable(IElement element, TextStyle inherited, ImportContext context)
    {
        var rows = element.Children.SelectMany<IElement, IElement>(e => e.LocalName is "thead" or "tbody" or "tfoot" ? e.Children : new[] { e }).Where(e => e.LocalName == "tr").ToArray();
        if (rows.Length == 0) { Report("html.empty-table", "Table without rows", "An empty one-cell table was used.", element); return Table.Create(1, 1); }
        if (rows.Length > 1000) throw new FormatException("Table is too large.");
        var cells = new Dictionary<(int, int), TableCell>();
        var occupied = new HashSet<(int, int)>();
        var columns = 1;
        for (var r = 0; r < rows.Length; r++)
        {
            var c = 0;
            foreach (var cell in rows[r].Children.Where(e => e.LocalName is "td" or "th"))
            {
                while (occupied.Contains((r, c))) c++;
                var colSpan = ReadSpan(cell, "colspan", 100);
                var rowSpan = ReadSpan(cell, "rowspan", rows.Length - r);
                if (c + colSpan > 100) throw new FormatException("Table is too wide.");
                for (var y = r; y < r + rowSpan; y++)
                    for (var x = c; x < c + colSpan; x++) if (!occupied.Add((y, x))) throw new FormatException("HTML contains overlapping table spans.");
                var css = Css(cell);
                var rowParent = rows[r].ParentElement;
                var rowStyle = rowParent is not null && rowParent != element ? ReadStyle(rowParent, ReadStyle(element, inherited)) : ReadStyle(element, inherited);
                var cellStyle = ReadStyle(cell, ReadStyle(rows[r], rowStyle));
                if (cell.LocalName == "th") cellStyle = cellStyle with { Bold = true };
                cells[(r, c)] = new TableCell
                {
                    Blocks = FlowDocument.EnsureBlocks(ReadBlocks(cell, cellStyle, context).ToImmutableArray()),
                    ColumnSpan = colSpan, RowSpan = rowSpan, Background = ReadColor(css, "background-color", cell) ?? ReadColor(css, "background", cell),
                    Padding = ReadEdges(css, "padding", cell), Borders = ReadBorders(css, cell), PreferredWidth = ReadHtmlWidth(cell),
                    VerticalAlignment = css.GetValueOrDefault("vertical-align") switch { "middle" => TableCellVerticalAlignment.Center, "bottom" => TableCellVerticalAlignment.Bottom, _ => TableCellVerticalAlignment.Top },
                    TextDirection = css.GetValueOrDefault("direction") switch { "rtl" => TableCellTextDirection.RightToLeft, "ltr" => TableCellTextDirection.LeftToRight, _ => TableCellTextDirection.Inherit }
                };
                c += colSpan; columns = Math.Max(columns, c);
            }
        }
        var columnElements = element.Children.SelectMany<IElement, IElement>(e => e.LocalName == "colgroup" ? e.Children : new[] { e }).Where(e => e.LocalName == "col").ToArray();
        var widths = new List<double>();
        foreach (var column in columnElements)
        {
            var width = Css(column).GetValueOrDefault("width") ?? column.GetAttribute("width");
            var parsed = column.HasAttribute(Metadata + "width") ? MetadataNumber(column, "width", 1) : width?.EndsWith('%') == true ? Number(width[..^1], 1) : Length(width, 1, column);
            if (column.HasAttribute(Metadata + "width") && parsed is <= 0 or > 100000) throw new FormatException("Invalid HTML column width metadata.");
            var span = ReadSpan(column, "span", 100);
            widths.AddRange(Enumerable.Repeat(Bound(parsed, 0.001, 100000, column, "column width"), span));
        }
        if (widths.Count != 0 && widths.Count != columns) Report("html.table-widths", "Column widths do not match the table grid", "Equal column widths were used.", element);
        var sizing = rows.Select(row =>
        {
            var css = Css(row);
            var height = Bound(Length(css.GetValueOrDefault("height") ?? css.GetValueOrDefault("min-height") ?? row.GetAttribute("height"), 0, row), 0, 100000, row, "row height");
            return new TableRowSizing { AllowSplit = css.GetValueOrDefault("break-inside") != "avoid", Height = height, Mode = height == 0 ? TableRowHeightMode.Auto : row.GetAttribute(Metadata + "height-mode") == "Exact" ? TableRowHeightMode.Exact : TableRowHeightMode.AtLeast };
        }).ToImmutableArray();
        return new Table
        {
            Rows = Enumerable.Range(0, rows.Length).Select(r => Enumerable.Range(0, columns).Select(c => cells.GetValueOrDefault((r, c)) ?? new TableCell()).ToImmutableArray()).ToImmutableArray(),
            ColumnWidths = widths.Count == columns ? widths.ToImmutableArray() : [], RowSizing = sizing.Any(s => s.Mode != TableRowHeightMode.Auto || !s.AllowSplit) ? sizing : [],
            PreferredWidth = ReadHtmlWidth(element),
            AutoFit = ReadTableEnum(element, "autofit", Css(element).GetValueOrDefault("table-layout") == "fixed" ? TableAutoFit.Fixed : TableAutoFit.Legacy),
            Alignment = ReadTableEnum(element, "alignment", Css(element).GetValueOrDefault("margin-left") == "auto" ? Css(element).GetValueOrDefault("margin-right") == "auto" ? TableAlignment.Center : TableAlignment.Right : TableAlignment.Left),
            Indent = MetadataNumber(element, "indent", 0), RightToLeft = Css(element).GetValueOrDefault("direction") == "rtl",
            RepeatHeaderRows = rows.TakeWhile(row => row.ParentElement?.LocalName == "thead").Count()
        };
    }

    public override string Serialize(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        StyleConversion.ReportLosses(this, document);
        TableConversion.ReportLosses("html", document);
        document = new DocumentStyleResolver(document).ResolveDocument();
        document.Validate();
        var builder = new StringBuilder("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>");
        WriteBlocks(builder, document.Blocks, document, ListNumbering.Compute(document));
        return builder.Append("</body></html>").ToString();
    }

    private static int ReadSpan(IElement cell, string name, int maximum)
    {
        var value = cell.GetAttribute(name);
        if (value is null) return 1;
        if (!int.TryParse(value, out var span) || span < 0 || span > maximum || span == 0 && name == "colspan")
            throw new FormatException("Invalid HTML table span.");
        return span == 0 ? maximum : span;
    }
    private sealed class OpenList(ListKind kind, Guid? id, ListDefinition? definition)
    {
        public ListKind Kind { get; } = kind;
        public Guid? Id { get; } = id;
        public ListDefinition? Definition { get; } = definition;
        public bool ItemOpen { get; set; }
    }
    private static void WriteBlocks(StringBuilder builder, IEnumerable<Block> blocks, FlowDocument document, IReadOnlyDictionary<Guid, ListMarker> markers)
    {
        var lists = new List<OpenList>();
        void CloseList()
        {
            var list = lists[^1]; if (list.ItemOpen) builder.Append("</li>");
            builder.Append(list.Kind == ListKind.Bullet ? "</ul>" : "</ol>"); lists.RemoveAt(lists.Count - 1);
        }
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph && paragraph.Style.List != ListKind.None)
            {
                var style = paragraph.Style;
                while (lists.Count > style.ListLevel + 1) CloseList();
                if (lists.Count == style.ListLevel + 1 && (lists[^1].Kind != style.List || lists[^1].Id != style.ListId || lists[^1].Definition != style.ListDefinition)) CloseList();
                while (lists.Count <= style.ListLevel)
                {
                    if (lists.Count > 0 && !lists[^1].ItemOpen) { builder.Append("<li data-textalonia-list-container=\"true\">"); lists[^1].ItemOpen = true; }
                    builder.Append(style.List == ListKind.Bullet ? "<ul" : "<ol"); Attribute(builder, Metadata + "list-id", style.ListId?.ToString() ?? "");
                    if (style.List == ListKind.Numbered) Attribute(builder, "start", markers[paragraph.Id].Number.ToString(CultureInfo.InvariantCulture));
                    var format = style.ListDefinition?.Level(lists.Count, style.List);
                    Attribute(builder, "style", "list-style-type:" + MarkerCss(format, style.List)); builder.Append('>');
                    lists.Add(new(style.List, style.ListId, style.ListDefinition));
                }
                if (lists[^1].ItemOpen) builder.Append("</li>");
                WriteParagraph(builder, paragraph, "li", document, markers);
                lists[^1].ItemOpen = true;
            }
            else
            {
                while (lists.Count > 0) CloseList();
                switch (block)
                {
                    case Paragraph ordinary: WriteParagraph(builder, ordinary, ordinary.Style.HeadingLevel > 0 ? $"h{ordinary.Style.HeadingLevel}" : "p", document, markers); break;
                    case Section section:
                        builder.Append("<section");
                        Attribute(builder, Metadata + "padding", N(section.Padding));
                        Attribute(builder, Metadata + "padding-edges", section.PaddingEdges is null ? "false" : "true");
                        Attribute(builder, Metadata + "borders", section.Borders is null ? "false" : "true");
                        if (section.BorderColor is not null) Attribute(builder, Metadata + "border-color", CssColor(section.BorderColor));
                        var sectionBorders = section.Borders ?? (section.BorderColor is null ? null : new BlockBorders(new(1, section.BorderColor), new(1, section.BorderColor), new(1, section.BorderColor), new(1, section.BorderColor)));
                        Attribute(builder, "style", BackgroundCss(section.Background) + EdgesCss(section.PaddingEdges ?? new(section.Padding, section.Padding, section.Padding, section.Padding), "padding") + BordersCss(sectionBorders));
                        builder.Append('>'); WriteBlocks(builder, section.Blocks, document, markers); builder.Append("</section>"); break;
                    case Table table: WriteTable(builder, table, document, markers); break;
                }
            }
        }
        while (lists.Count > 0) CloseList();
    }
    private static void WriteParagraph(StringBuilder builder, Paragraph paragraph, string tag, FlowDocument document, IReadOnlyDictionary<Guid, ListMarker> markers)
    {
        var style = paragraph.Style;
        ReportFontFamily(paragraph.DefaultStyle, paragraph.Id);
        builder.Append('<').Append(tag); Attribute(builder, "dir", style.RightToLeft ? "rtl" : "ltr");
        Attribute(builder, Metadata + "bold", paragraph.DefaultStyle.Bold ? "true" : "false");
        if (paragraph.DefaultStyle.Hyperlink is { } hyperlink) Attribute(builder, Metadata + "hyperlink", hyperlink);
        if (tag == "li")
        {
            Attribute(builder, Metadata + "list", "true"); Attribute(builder, Metadata + "list-id", style.ListId?.ToString() ?? "");
            Attribute(builder, Metadata + "list-level", style.ListLevel.ToString(CultureInfo.InvariantCulture));
            Attribute(builder, Metadata + "list-start", style.ListStart?.ToString(CultureInfo.InvariantCulture) ?? "");
            Attribute(builder, Metadata + "list-restart", style.ListRestart ? "true" : "false");
            Attribute(builder, Metadata + "list-definition", style.ListDefinition is null ? "" : JsonSerializer.Serialize(style.ListDefinition));
            Attribute(builder, Metadata + "heading", style.HeadingLevel.ToString(CultureInfo.InvariantCulture));
            if (style.List == ListKind.Numbered) Attribute(builder, "value", markers[paragraph.Id].Number.ToString(CultureInfo.InvariantCulture));
            if (style.ListDefinition?.Levels.Any(level => level.IncludeAncestors || level.Prefix != "" || level.Suffix != "." || level.Text is not (null or "\u2022" or "\u25e6" or "\u25aa")) == true)
                ConversionDiagnostics.Report("html.list-marker", "Custom list marker pattern", "Standard HTML markers were used; exact definitions are retained in Textalonia metadata.", paragraph.Id);
        }
        Attribute(builder, "style", "white-space:pre-wrap;text-align:" + style.Alignment.ToString().ToLowerInvariant() + ";" + TextCss(paragraph.DefaultStyle) +
            "margin:" + N(style.SpaceBefore) + "px " + N(style.RightIndent) + "px " + N(style.SpaceAfter) + "px " + N(style.Indent) + "px;" +
            "text-indent:" + N(style.FirstLineIndent) + "px;letter-spacing:" + N(style.LetterSpacing) + "px;" + (style.LineHeight is { } height ? "line-height:" + N(height) + "px;" : ""));
        builder.Append('>');
        foreach (var run in paragraph.Runs) WriteRun(builder, run, document);
        if (paragraph.Runs.IsEmpty) builder.Append("<br>");
        if (tag != "li") builder.Append("</").Append(tag).Append('>');
    }
    private static void WriteTable(StringBuilder builder, Table table, FlowDocument document, IReadOnlyDictionary<Guid, ListMarker> markers)
    {
        builder.Append("<table border=\"1\"");
        Attribute(builder, "style", "border-collapse:collapse;" + HtmlWidthCss(table.PreferredWidth) + "table-layout:" + (table.AutoFit == TableAutoFit.Fixed ? "fixed" : "auto") + ";direction:" + (table.RightToLeft ? "rtl" : "ltr") + ";" +
            (table.Alignment == TableAlignment.Center ? "margin-left:auto;margin-right:auto;" : table.Alignment == TableAlignment.Right ? "margin-left:auto;margin-right:0;" : "margin-left:" + N(table.Indent) + "px;"));
        Attribute(builder, Metadata + "autofit", table.AutoFit.ToString()); Attribute(builder, Metadata + "alignment", table.Alignment.ToString()); Attribute(builder, Metadata + "indent", N(table.Indent)); builder.Append('>');
        if (!table.ColumnWidths.IsEmpty)
        {
            builder.Append("<colgroup>"); var sum = table.ColumnWidths.Sum();
            foreach (var width in table.ColumnWidths) { builder.Append("<col"); Attribute(builder, Metadata + "width", N(width)); Attribute(builder, "style", "width:" + N(width / sum * 100) + "%"); builder.Append('>'); }
            builder.Append("</colgroup>");
        }
        builder.Append(table.RepeatHeaderRows > 0 ? "<thead>" : "<tbody>");
        for (var row = 0; row < table.Rows.Length; row++)
        {
            if (row > 0 && row == table.RepeatHeaderRows) builder.Append("</thead><tbody>");
            builder.Append("<tr");
            if (!table.RowSizing.IsEmpty)
            {
                var sizing = table.RowSizing[row]; Attribute(builder, "style", (sizing.Mode == TableRowHeightMode.Auto ? "" : "height:" + N(sizing.Height) + "px;") + "break-inside:" + (sizing.AllowSplit ? "auto" : "avoid") + ";"); Attribute(builder, Metadata + "height-mode", sizing.Mode.ToString());
                if (sizing.Mode == TableRowHeightMode.Exact) ConversionDiagnostics.Report("html.row-height", "Exact table row height", "Browser row height is a minimum; exact sizing is retained in Textalonia metadata.", table.Id);
            }
            builder.Append('>');
            for (var column = 0; column < table.ColumnCount; column++)
            {
                if (table.IsCovered(row, column)) continue;
                var cell = table.Rows[row][column]; builder.Append("<td");
                Attribute(builder, "rowspan", cell.RowSpan.ToString(CultureInfo.InvariantCulture)); Attribute(builder, "colspan", cell.ColumnSpan.ToString(CultureInfo.InvariantCulture));
                Attribute(builder, "style", BackgroundCss(cell.Background) + EdgesCss(cell.Padding, "padding") + BordersCss(cell.Borders) + HtmlWidthCss(cell.PreferredWidth) +
                    "vertical-align:" + (cell.VerticalAlignment switch { TableCellVerticalAlignment.Center => "middle", TableCellVerticalAlignment.Bottom => "bottom", _ => "top" }) + ";" +
                    (cell.TextDirection == TableCellTextDirection.Inherit ? "" : "direction:" + (cell.TextDirection == TableCellTextDirection.RightToLeft ? "rtl" : "ltr") + ";"));
                builder.Append('>'); WriteBlocks(builder, cell.Blocks, document, markers); builder.Append("</td>");
            }
            builder.Append("</tr>");
        }
        builder.Append(table.RepeatHeaderRows == table.Rows.Length ? "</thead></table>" : "</tbody></table>");
    }
    private static void WriteRun(StringBuilder builder, RichRun run, FlowDocument document)
    {
        ReportFontFamily(run.Style, run.Inline?.Id);
        if (run.Style.Hyperlink is { } link) { builder.Append("<a"); Attribute(builder, "href", link); builder.Append('>'); }
        builder.Append("<span"); Attribute(builder, "style", TextCss(run.Style)); Attribute(builder, Metadata + "bold", run.Style.Bold ? "true" : "false");
        Attribute(builder, Metadata + "hyperlink", run.Style.Hyperlink ?? ""); builder.Append('>');
        if (run.Inline is { } inline)
        {
            if (inline.Payload is ImageInlinePayload image && document.Resources.TryGetValue(image.ResourceId, out var resource) &&
                resource.Kind == DocumentResourceKind.Embedded && resource.MediaType.ToLowerInvariant() is "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp")
            {
                builder.Append("<img"); Attribute(builder, "src", "data:" + resource.MediaType.ToLowerInvariant() + ";base64," + Convert.ToBase64String(resource.Data.AsSpan()));
                Attribute(builder, "alt", inline.AltText); Attribute(builder, "width", N(inline.Width)); Attribute(builder, "height", N(inline.Height)); builder.Append('>');
            }
            else
            {
                if (inline.Payload is not MergeFieldInlinePayload)
                    ConversionDiagnostics.Report("html.resource", inline.Payload is ImageInlinePayload ? "Unavailable, external, or unsupported image resource" : "Host inline control", "Inline object was replaced with alternative text; no resource was fetched.", inline.Id);
                builder.Append(WebUtility.HtmlEncode(inline.AltText).Replace("\u2028", "<br>"));
            }
        }
        else builder.Append(WebUtility.HtmlEncode(run.Text).Replace("\u2028", "<br>"));
        builder.Append("</span>"); if (run.Style.Hyperlink is not null) builder.Append("</a>");
    }
    private static void ReportFontFamily(TextStyle style, Guid? modelId)
    {
        if (style.FontFamily?.IndexOfAny([',', ';']) >= 0)
            ConversionDiagnostics.Report("html.font-family", "Font family containing a fallback list or CSS delimiter", "The first family is imported and CSS delimiters are removed on export.", modelId);
    }
    private static string TextCss(TextStyle style) => "font-size:" + N(style.FontSize) + "px;font-weight:" + (style.FontWeight?.ToString(CultureInfo.InvariantCulture) ?? (style.Bold ? "bold" : "normal")) +
        ";font-style:" + (style.Italic ? "italic" : "normal") + ";font-family:" + (style.FontFamily?.Replace(";", "") ?? "initial") + ";font-stretch:" + StretchNames[style.FontStretch - 1] +
        ";color:" + (style.Foreground is { } foreground ? CssColor(foreground) : "initial") + ";background-color:" + (style.Background is { } background ? CssColor(background) : "initial") +
        ";text-decoration:" + (style.Underline || style.Strikethrough ? (style.Underline ? "underline " : "") + (style.Strikethrough ? "line-through" : "") : "none") +
        ";vertical-align:" + (style.Baseline switch { Baseline.Subscript => "sub", Baseline.Superscript => "super", _ => "baseline" }) + ";";
    private static string MarkerCss(ListLevelDefinition? level, ListKind kind) => level?.Marker switch
    {
        ListMarkerStyle.LowerLetter => "lower-alpha", ListMarkerStyle.UpperLetter => "upper-alpha", ListMarkerStyle.LowerRoman => "lower-roman", ListMarkerStyle.UpperRoman => "upper-roman",
        ListMarkerStyle.Bullet => level.Text switch { "\u25e6" => "circle", "\u25aa" => "square", _ => "disc" }, _ => kind == ListKind.Bullet ? "disc" : "decimal"
    };
    private static string BackgroundCss(string? value) => value is null ? "" : "background-color:" + CssColor(value) + ";";
    private static string EdgesCss(EdgeInsets? value, string name) => value is null ? "" : name + ":" + N(value.Top) + "px " + N(value.Right) + "px " + N(value.Bottom) + "px " + N(value.Left) + "px;";
    private static string BordersCss(BlockBorders? value)
    {
        if (value is null) return "";
        var builder = new StringBuilder("border:none;");
        foreach (var (name, side) in new[] { ("left", value.Left), ("top", value.Top), ("right", value.Right), ("bottom", value.Bottom) })
            if (side is not null) builder.Append("border-").Append(name).Append(':').Append(N(side.Width)).Append("px ").Append(side.Kind.ToString().ToLowerInvariant()).Append(' ').Append(side.Color is null ? "currentColor" : CssColor(side.Color)).Append(';');
        return builder.ToString();
    }
    private static TablePreferredWidth ReadHtmlWidth(IElement element)
    {
        var value = Css(element).GetValueOrDefault("width") ?? element.GetAttribute("width");
        if (value is null or "auto") return new();
        return value.EndsWith('%') ? new(TableWidthUnit.Percentage, Bound(Number(value[..^1], 100), .001, 100, element, "preferred width"))
            : new(TableWidthUnit.Absolute, Bound(Length(value, 1, element), .001, 100000, element, "preferred width"));
    }
    private static T ReadTableEnum<T>(IElement element, string name, T fallback) where T : struct, Enum
    {
        var value = element.GetAttribute(Metadata + name);
        if (value is null) return fallback;
        if (!Enum.TryParse<T>(value, out var parsed) || !Enum.IsDefined(parsed)) throw new FormatException("Invalid HTML table metadata: " + name);
        return parsed;
    }
    private static string HtmlWidthCss(TablePreferredWidth width) => "width:" + (width.Unit switch { TableWidthUnit.Percentage => N(width.Value) + "%", TableWidthUnit.Absolute => N(width.Value) + "px", _ => "auto" }) + ";";
    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string CssColor(string value) => value.Length == 9 ? "#" + value[3..] + value[1..3] : value;
    private static void Attribute(StringBuilder builder, string name, string value) => builder.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
    private static void Report(string code, string feature, string fallback, IElement? element) => ConversionDiagnostics.Report(code, feature, fallback, sourceLocation: element is null ? null : "<" + element.LocalName + ">");
}
