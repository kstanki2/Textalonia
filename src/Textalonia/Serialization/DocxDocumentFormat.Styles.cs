using System.Collections.Immutable;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class DocxDocumentFormat
{
    private static FlowDocument MapStyleIdentifiers(FlowDocument document)
    {
        var catalog = document.Styles;
        var all = catalog.Characters.Keys.Concat(catalog.Paragraphs.Keys).Concat(catalog.Tables.Keys).ToArray();
        var collisions = all.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1 || group.Key == CellEndStyle).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        if (collisions.Count == 0) return document;
        Loss("style-id-collision", "Style identifiers shared by different kinds or reserved by DOCX", "Conflicting identifiers were remapped; style relationships and formatting retained.");
        var used = all.Append(CellEndStyle).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> Map(IEnumerable<string> ids, string kind)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in ids.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (!collisions.Contains(id)) { map[id] = id; continue; }
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..16];
                var candidate = "Textalonia" + kind + digest; var suffix = 0;
                while (!used.Add(candidate)) candidate = "Textalonia" + kind + digest + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture);
                map[id] = candidate;
            }
            return map;
        }
        var characters = Map(catalog.Characters.Keys, "Character");
        var paragraphs = Map(catalog.Paragraphs.Keys, "Paragraph");
        var tables = Map(catalog.Tables.Keys, "Table");
        static string? Id(Dictionary<string, string> map, string? id) => id is null ? null : map.GetValueOrDefault(id, id);
        TextStyle Text(TextStyle style) => style with { StyleId = Id(characters, style.StyleId) };
        ParagraphStyle Paragraph(ParagraphStyle style) => style with { StyleId = Id(paragraphs, style.StyleId) };
        ImmutableArray<Block> Blocks(ImmutableArray<Block> blocks) => blocks.Select<Block, Block>(block => block switch
        {
            Textalonia.Model.Paragraph paragraph => paragraph with { Style = Paragraph(paragraph.Style), DefaultStyle = Text(paragraph.DefaultStyle), Runs = paragraph.Runs.Select(run => run with { Style = Text(run.Style) }).ToImmutableArray() },
            Section section => section with { Blocks = Blocks(section.Blocks) },
            Table table => table with { StyleId = Id(tables, table.StyleId), Rows = table.Rows.Select(row => row.Select(cell => cell with { Blocks = Blocks(cell.Blocks), MergeOriginalBlocks = Blocks(cell.MergeOriginalBlocks) }).ToImmutableArray()).ToImmutableArray() },
            _ => block
        }).ToImmutableArray();
        return document with
        {
            Blocks = Blocks(document.Blocks), Defaults = document.Defaults with { Text = Text(document.Defaults.Text), Paragraph = Paragraph(document.Defaults.Paragraph) },
            Styles = catalog with
            {
                DefaultCharacterStyleId = Id(characters, catalog.DefaultCharacterStyleId), DefaultParagraphStyleId = Id(paragraphs, catalog.DefaultParagraphStyleId), DefaultTableStyleId = Id(tables, catalog.DefaultTableStyleId),
                Characters = catalog.Characters.Values.Select(style => style with { Id = characters[style.Id], BasedOn = Id(characters, style.BasedOn), LinkedStyle = Id(paragraphs, style.LinkedStyle) }).ToImmutableDictionary(style => style.Id),
                Paragraphs = catalog.Paragraphs.Values.Select(style => style with { Id = paragraphs[style.Id], BasedOn = Id(paragraphs, style.BasedOn), LinkedStyle = Id(characters, style.LinkedStyle), NextStyle = Id(paragraphs, style.NextStyle) }).ToImmutableDictionary(style => style.Id),
                Tables = catalog.Tables.Values.Select(style => style with { Id = tables[style.Id], BasedOn = Id(tables, style.BasedOn) }).ToImmutableDictionary(style => style.Id)
            }
        };
    }

    private static DocumentStyleCatalog ReadStyleCatalog(Dictionary<string, XElement> source, Dictionary<string, NumberingInfo> numbering)
    {
        var characters = ImmutableDictionary.CreateBuilder<string, CharacterStyleDefinition>();
        var paragraphs = ImmutableDictionary.CreateBuilder<string, ParagraphStyleDefinition>();
        var tables = ImmutableDictionary.CreateBuilder<string, TableStyleDefinition>();
        string Kind(XElement value) => (string?)value.Attribute(W + "type") ?? "paragraph";
        string? Reference(string id, string property, string expectedKind)
        {
            var target = Value(source[id].Element(W + property));
            if (target is null) return null;
            if (!source.TryGetValue(target, out var referenced) || Kind(referenced) != expectedKind)
            { Loss("style-missing", "Missing or incompatible style reference", "Invalid reference omitted.", source[id]); return null; }
            if (property != "basedOn") return target;
            var visited = new HashSet<string> { id };
            var current = target;
            while (current is not null && source.TryGetValue(current, out var ancestor))
            {
                if (!visited.Add(current) || visited.Count > 32)
                { Loss("style-cycle", "Cyclic or excessively deep style inheritance", "Invalid parent reference omitted.", source[id]); return null; }
                current = Value(ancestor.Element(W + "basedOn"));
            }
            return target;
        }
        ParagraphStyleOverrides ParagraphFormatting(string id, XElement element)
        {
            var properties = element.Element(W + "pPr");
            var formatting = ReadParagraphOverrides(properties, numbering);
            if (properties?.Element(W + "tabs") is null) return formatting;
            var chain = new Stack<XElement>(); var visited = new HashSet<string>();
            var current = id;
            while (current is not null && visited.Add(current) && source.TryGetValue(current, out var ancestor))
            { chain.Push(ancestor); current = Value(ancestor.Element(W + "basedOn")); }
            var effective = ParagraphStyle.Default;
            while (chain.Count > 0) effective = ReadParagraphStyle(chain.Pop().Element(W + "pPr"), effective, numbering);
            return formatting with { TabStops = effective.TabStops };
        }
        foreach (var (id, element) in source)
        {
            if (id == CellEndStyle) continue;
            var name = Value(element.Element(W + "name")) ?? id;
            var kind = Kind(element);
            if (kind == "character") characters.Add(id, new CharacterStyleDefinition
            {
                Id = id, Name = name, BasedOn = Reference(id, "basedOn", kind), LinkedStyle = Reference(id, "link", "paragraph"),
                Formatting = ReadTextOverrides(element.Element(W + "rPr"))
            });
            else if (kind == "paragraph") paragraphs.Add(id, new ParagraphStyleDefinition
            {
                Id = id, Name = name, BasedOn = Reference(id, "basedOn", kind), LinkedStyle = Reference(id, "link", "character"), NextStyle = Reference(id, "next", "paragraph"),
                Formatting = ParagraphFormatting(id, element), TextFormatting = ReadTextOverrides(element.Element(W + "rPr"))
            });
            else if (kind == "table")
            {
                var properties = element.Element(W + "tblPr");
                var cell = element.Element(W + "tcPr");
                var formatting = ReadTableFormatting(properties, cell);
                tables.Add(id, new TableStyleDefinition { Id = id, Name = name, BasedOn = Reference(id, "basedOn", kind), Formatting = formatting });
                if (element.Elements(W + "tblStylePr").Any()) Loss("table-conditional-style", "Conditional table style regions", "Base named table style retained.", element);
                if (element.Element(W + "rPr") is not null || element.Element(W + "pPr") is not null)
                    Loss("table-text-style", "Table style character and paragraph formatting", "Base table cell decoration retained.", element);
            }
            else Loss("style-type", "Unsupported named style kind", "Recognized style definitions retained.", element);
        }
        string? DefaultId(string kind) => source.FirstOrDefault(p => Kind(p.Value) == kind && OnAttribute(p.Value, "default")).Key;
        return new DocumentStyleCatalog { Characters = characters.ToImmutable(), Paragraphs = paragraphs.ToImmutable(), Tables = tables.ToImmutable(),
            DefaultCharacterStyleId = DefaultId("character"), DefaultParagraphStyleId = DefaultId("paragraph"), DefaultTableStyleId = DefaultId("table") };
    }

    private static DocumentTheme ReadTheme(XElement? root)
    {
        if (root is null) return new DocumentTheme();
        var colors = ImmutableDictionary.CreateBuilder<string, string>();
        var fonts = ImmutableDictionary.CreateBuilder<string, string>();
        var elements = root.Element(A + "themeElements");
        foreach (var item in elements?.Element(A + "clrScheme")?.Elements() ?? [])
        {
            var color = item.Elements().FirstOrDefault();
            var value = ReadColor((string?)color?.Attribute(color.Name == A + "sysClr" ? "lastClr" : "val"));
            if (value is not null) colors[item.Name.LocalName] = value;
        }
        foreach (var group in elements?.Element(A + "fontScheme")?.Elements() ?? [])
        {
            var prefix = group.Name == A + "majorFont" ? "major" : "minor";
            foreach (var (tag, suffix) in new[] { ("latin", "Ascii"), ("latin", "HAnsi"), ("ea", "EastAsia"), ("cs", "Bidi") })
                if ((string?)group.Element(A + tag)?.Attribute("typeface") is { Length: > 0 } family) fonts[prefix + suffix] = family;
        }
        foreach (var (alias, slot) in new[] { ("text1", "dk1"), ("background1", "lt1"), ("text2", "dk2"), ("background2", "lt2"), ("hyperlink", "hlink"), ("followedHyperlink", "folHlink") })
            if (colors.TryGetValue(slot, out var color)) colors[alias] = color;
        return new DocumentTheme { Name = (string?)root.Attribute("name") ?? "Office", Colors = colors.ToImmutable(), Fonts = fonts.ToImmutable() };
    }

    private static XElement WriteTheme(DocumentTheme theme)
    {
        string Font(string name) => theme.Fonts.GetValueOrDefault(name) ?? (name.EndsWith("Ascii", StringComparison.Ordinal) ? theme.Fonts.GetValueOrDefault(name.Replace("Ascii", "HAnsi", StringComparison.Ordinal)) : null) ?? "";
        var colorAliases = new Dictionary<string, string> { ["dk1"] = "text1", ["lt1"] = "background1", ["dk2"] = "text2", ["lt2"] = "background2", ["hlink"] = "hyperlink", ["folHlink"] = "followedHyperlink" };
        string ThemeColorValue(string name) => theme.Colors.GetValueOrDefault(name) ??
            (colorAliases.TryGetValue(name, out var alias) ? theme.Colors.GetValueOrDefault(alias) : null) ?? (name.StartsWith("lt", StringComparison.Ordinal) ? "#FFFFFF" : "#000000");
        if (colorAliases.Any(pair => theme.Colors.TryGetValue(pair.Key, out var primary) && theme.Colors.TryGetValue(pair.Value, out var alias) && primary != alias) ||
            new[] { "major", "minor" }.Any(prefix => theme.Fonts.TryGetValue(prefix + "Ascii", out var ascii) && theme.Fonts.TryGetValue(prefix + "HAnsi", out var ansi) && ascii != ansi))
            Loss("theme-slot-alias", "Conflicting values for equivalent Office theme slots", "Canonical color and ASCII font slots retained.");
        var colorNames = new HashSet<string>(new[] { "dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" }.Concat(colorAliases.Values), StringComparer.Ordinal);
        var fontNames = new HashSet<string>(new[] { "major", "minor" }.SelectMany(prefix => new[] { "Ascii", "HAnsi", "EastAsia", "Bidi" }.Select(suffix => prefix + suffix)), StringComparer.Ordinal);
        if (theme.Colors.Keys.Any(name => !colorNames.Contains(name)) || theme.Fonts.Keys.Any(name => !fontNames.Contains(name)))
            Loss("theme-slot", "Custom document theme slots", "Supported Office theme slots retained; custom slots omitted.");
        return new XElement(A + "theme", new XAttribute("name", theme.Name ?? "Textalonia"), new XElement(A + "themeElements",
            new XElement(A + "clrScheme", new XAttribute("name", theme.Name ?? "Textalonia"),
                new[] { "dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" }
                    .Select(name => new XElement(A + name, new XElement(A + "srgbClr", new XAttribute("val", Color(ThemeColorValue(name), Guid.Empty)))))),
            new XElement(A + "fontScheme", new XAttribute("name", theme.Name ?? "Textalonia"), new[] { "major", "minor" }.Select(prefix =>
                new XElement(A + (prefix + "Font"), new XElement(A + "latin", new XAttribute("typeface", Font(prefix + "Ascii"))),
                    new XElement(A + "ea", new XAttribute("typeface", Font(prefix + "EastAsia"))), new XElement(A + "cs", new XAttribute("typeface", Font(prefix + "Bidi")))))),
            new XElement(A + "fmtScheme", new XAttribute("name", theme.Name ?? "Textalonia"),
                new XElement(A + "fillStyleLst", Enumerable.Range(0, 3).Select(_ => new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr"))))),
                new XElement(A + "lnStyleLst", Enumerable.Range(1, 3).Select(width => new XElement(A + "ln", new XAttribute("w", width * 9525),
                    new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr"))), new XElement(A + "prstDash", new XAttribute("val", "solid"))))),
                new XElement(A + "effectStyleLst", Enumerable.Range(0, 3).Select(_ => new XElement(A + "effectStyle", new XElement(A + "effectLst")))),
                new XElement(A + "bgFillStyleLst", Enumerable.Range(0, 3).Select(_ => new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr"))))))));
    }

    private static XElement WriteStyles(FlowDocument document, Func<ParagraphStyle, int> numberFor)
    {
        XElement Definition(string kind, string id, string? name, string? basedOn, string? linked, string? next, bool isDefault, params XElement[] formatting) =>
            new(W + "style", new XAttribute(W + "type", kind), new XAttribute(W + "styleId", id), isDefault ? new XAttribute(W + "default", 1) : null,
                Val("name", name ?? id), basedOn is null ? null : Val("basedOn", basedOn), linked is null ? null : Val("link", linked), next is null ? null : Val("next", next), formatting);
        var catalog = document.Styles;
        var resolver = new DocumentStyleResolver(document);
        XElement ParagraphFormatting(ParagraphStyleDefinition definition)
        {
            var source = new ParagraphStyle { Overrides = definition.Formatting };
            var inheritedTabs = definition.Formatting.TabStops.IsSet ? new DocumentStyleResolver(document with
            {
                Styles = catalog with { Paragraphs = catalog.Paragraphs.SetItem(definition.Id, definition with { Formatting = definition.Formatting with { TabStops = default } }) }
            }).ResolveParagraphStyle(ParagraphStyle.ForStyle(definition.Id)).TabStops : [];
            var element = WriteParagraphProperties(source, Guid.Empty, inheritedTabs, document.Defaults.Paragraph.DefaultTabWidth > 0 ? document.Defaults.Paragraph.DefaultTabWidth : 48);
            var effective = resolver.ResolveParagraphStyle(ParagraphStyle.ForStyle(definition.Id));
            if (definition.Formatting.List.IsSet || definition.Formatting.ListId.IsSet || definition.Formatting.ListLevel.IsSet)
                element.Add(effective.List == ListKind.None ? new XElement(W + "numPr", Val("numId", 0)) :
                    new XElement(W + "numPr", Val("ilvl", effective.ListLevel), Val("numId", numberFor(effective))));
            return element;
        }
        return new XElement(W + "styles", new XElement(W + "docDefaults", new XElement(W + "rPrDefault", WriteTextStyle(document.Defaults.Text, Guid.Empty)),
                new XElement(W + "pPrDefault", WriteParagraphProperties(document.Defaults.Paragraph with { DefaultTabWidth = 0 }, Guid.Empty))),
            Definition("paragraph", CellEndStyle, "Textalonia cell terminator", null, null, null, false),
            catalog.Characters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Definition("character", p.Key, p.Value.Name, p.Value.BasedOn, p.Value.LinkedStyle, null,
                catalog.DefaultCharacterStyleId == p.Key, WriteTextStyle(new TextStyle { Overrides = p.Value.Formatting }, Guid.Empty))),
            catalog.Paragraphs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Definition("paragraph", p.Key, p.Value.Name, p.Value.BasedOn, p.Value.LinkedStyle, p.Value.NextStyle,
                catalog.DefaultParagraphStyleId == p.Key, ParagraphFormatting(p.Value), WriteTextStyle(new TextStyle { Overrides = p.Value.TextFormatting }, Guid.Empty))),
            catalog.Tables.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Definition("table", p.Key, p.Value.Name, p.Value.BasedOn, null, null,
                catalog.DefaultTableStyleId == p.Key, WriteTableStyle(p.Value.Formatting))));
    }

    private static TableStyleOverrides ReadTableFormatting(XElement? properties, XElement? cell = null)
    {
        var formatting = new TableStyleOverrides();
        if ((cell?.Element(W + "shd") ?? properties?.Element(W + "shd")) is { } shading) formatting = formatting with { Background = new(ReadColor((string?)shading.Attribute(W + "fill"))) };
        if ((cell?.Element(W + "tcMar") ?? properties?.Element(W + "tblCellMar")) is { } padding) formatting = formatting with { Padding = new(ReadPadding(padding)) };
        if ((cell?.Element(W + "tcBorders") ?? properties?.Element(W + "tblBorders")) is { } borders) formatting = formatting with { Borders = new(ReadBorders(borders)) };
        return formatting;
    }

    private static XElement WriteTableStyle(TableStyleOverrides formatting) => new(W + "tblPr",
        formatting.Background.IsSet ? new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "fill", formatting.Background.Value is { } color ? Color(color, Guid.Empty) : "auto")) : null,
        formatting.Padding.IsSet ? WritePadding("tblCellMar", formatting.Padding.Value ?? new EdgeInsets()) : null,
        formatting.Borders.IsSet ? new XElement(W + "tblBorders", WriteBorders(formatting.Borders.Value ?? new BlockBorders(), Guid.Empty).Elements()) : null);
}
