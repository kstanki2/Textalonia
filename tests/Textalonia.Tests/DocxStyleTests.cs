using System.Collections.Immutable;
using System.IO.Compression;
using System.Xml.Linq;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class DocxStyleTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static async Task<(FlowDocument Document, XDocument Styles, XDocument Body)> RoundTrip(FlowDocument original)
    {
        using var stream = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(original, stream);
        stream.Position = 0;
        XDocument styles, body;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
        {
            using var stylePart = archive.GetEntry("word/styles.xml")!.Open(); styles = XDocument.Load(stylePart);
            using var bodyPart = archive.GetEntry("word/document.xml")!.Open(); body = XDocument.Load(bodyPart);
        }
        stream.Position = 0;
        return (await DocumentFormats.Docx.LoadAsync(stream), styles, body);
    }

    [Fact]
    public async Task Named_styles_retain_sparse_false_zero_links_defaults_and_live_inheritance()
    {
        var catalog = new DocumentStyleCatalog
        {
            DefaultParagraphStyleId = "Normal",
            Characters = ImmutableDictionary<string, CharacterStyleDefinition>.Empty.Add("Emphasis", new()
            { Id = "Emphasis", Name = "Emphasis", LinkedStyle = "Derived", Formatting = new() { Italic = true } }),
            Paragraphs = ImmutableDictionary<string, ParagraphStyleDefinition>.Empty
                .Add("Normal", new() { Id = "Normal", Name = "Normal", Formatting = new() { SpaceAfter = 14 }, TextFormatting = new() { Bold = true, FontSize = 20 } })
                .Add("Derived", new() { Id = "Derived", Name = "Derived", BasedOn = "Normal", LinkedStyle = "Emphasis", NextStyle = "Normal", Formatting = new() { SpaceAfter = 0 } })
        };
        var paragraph = new Paragraph([new RichRun("styled", TextStyle.ForStyle("Emphasis") with { Overrides = new() { Bold = false } })])
        { Style = ParagraphStyle.ForStyle("Derived"), DefaultStyle = TextStyle.ForStyle(null) };
        var original = new FlowDocument([paragraph]) { Styles = catalog };
        var (document, styles, body) = await RoundTrip(original);
        Assert.Equal("Normal", document.Styles.DefaultParagraphStyleId);
        var derived = document.Styles.Paragraphs["Derived"];
        Assert.Equal("Normal", derived.BasedOn); Assert.Equal("Normal", derived.NextStyle); Assert.Equal("Emphasis", derived.LinkedStyle);
        Assert.True(derived.Formatting.SpaceAfter.IsSet); Assert.Equal(0, derived.Formatting.SpaceAfter.Value);
        Assert.False(derived.TextFormatting.Bold.IsSet);
        var actual = Assert.IsType<Paragraph>(document.Blocks[0]);
        Assert.Equal("Derived", actual.Style.StyleId); Assert.Equal("Emphasis", actual.Runs[0].Style.StyleId);
        Assert.True(actual.Runs[0].Style.Overrides!.Bold.IsSet); Assert.False(actual.Runs[0].Style.Overrides!.Bold.Value);
        Assert.Null(body.Descendants(W + "pPr").Single().Element(W + "spacing"));
        Assert.Null(body.Descendants(W + "rPr").Last().Element(W + "sz"));
        var changed = document with { Styles = document.Styles with { Paragraphs = document.Styles.Paragraphs.SetItem("Normal", document.Styles.Paragraphs["Normal"] with { TextFormatting = new() { Bold = true, FontSize = 28 } }) } };
        var resolved = new DocumentStyleResolver(changed).ResolveParagraph(actual);
        Assert.Equal(28, resolved.Runs[0].Style.FontSize); Assert.False(resolved.Runs[0].Style.Bold); Assert.True(resolved.Runs[0].Style.Italic);
        Assert.Equal(0, resolved.Style.SpaceAfter);
        Assert.Equal("Emphasis", (string?)styles.Descendants(W + "style").Single(e => (string?)e.Attribute(W + "styleId") == "Derived").Element(W + "link")?.Attribute(W + "val"));
    }

    [Fact]
    public async Task Typography_tabs_and_paragraph_rules_round_trip()
    {
        var style = new TextStyle { UnderlineKind = UnderlineKind.Wave, UnderlineColor = "#CC3300", StrikeKind = StrikeKind.Double,
            AllCaps = true, SmallCaps = true, Language = "tr-TR", NoProof = true, Tracking = 2, HorizontalScale = 1.25, BaselineOffset = 4, KerningThreshold = 12 };
        var paragraph = new Paragraph([new RichRun("text\t12.4", style)]) { Style = new()
        {
            TabStops = [new(72, TabAlignment.Center, TabLeader.Dots), new(144, TabAlignment.Decimal, TabLeader.Line)],
            LineSpacingMode = LineSpacingMode.AtLeast, LineSpacing = 24, ContextualSpacing = true, OutlineLevel = 8,
            KeepWithNext = true, KeepTogether = true, PageBreakBefore = true, WidowControl = false, SnapToGrid = true,
            Shading = "#EEEEEE", Borders = new(new(1, "#123456"), new(2, "#123456"), new(1, "#123456"), new(2, "#123456"))
        } };
        var (document, _, _) = await RoundTrip(new FlowDocument([paragraph]));
        var actual = Assert.IsType<Paragraph>(document.Blocks[0]);
        var text = actual.Runs[0].Style;
        Assert.Equal(style.UnderlineKind, text.UnderlineKind); Assert.Equal(style.UnderlineColor, text.UnderlineColor); Assert.Equal(style.StrikeKind, text.StrikeKind);
        Assert.True(text.AllCaps); Assert.True(text.SmallCaps); Assert.True(text.NoProof); Assert.Equal("tr-TR", text.Language);
        Assert.Equal(2, text.Tracking); Assert.Equal(1.25, text.HorizontalScale); Assert.Equal(4, text.BaselineOffset); Assert.Equal(12, text.KerningThreshold);
        Assert.Equal(paragraph.Style.TabStops.ToArray(), actual.Style.TabStops.ToArray());
        Assert.Equal(LineSpacingMode.AtLeast, actual.Style.LineSpacingMode); Assert.Equal(24, actual.Style.LineSpacing);
        Assert.Equal(8, actual.Style.OutlineLevel); Assert.Equal(0, actual.Style.HeadingLevel); Assert.True(actual.Style.ContextualSpacing);
        Assert.True(actual.Style.KeepWithNext); Assert.True(actual.Style.KeepTogether); Assert.True(actual.Style.PageBreakBefore);
        Assert.False(actual.Style.WidowControl); Assert.True(actual.Style.SnapToGrid); Assert.Equal(paragraph.Style.Borders, actual.Style.Borders); Assert.Equal("#EEEEEE", actual.Style.Shading);
    }

    [Fact]
    public async Task Theme_slots_and_tints_remain_references_after_edit_and_reopen()
    {
        var paragraph = new Paragraph([new RichRun("themed", TextStyle.ForStyle(null) with
        { Overrides = new() { ThemeFont = new ThemeFontReference("minorAscii"), ThemeForeground = new ThemeColorReference("accent1", .2) } })])
        { Style = ParagraphStyle.ForStyle(null), DefaultStyle = TextStyle.ForStyle(null) };
        var original = new FlowDocument([paragraph]) { Theme = new()
        {
            Name = "Test theme", Colors = ImmutableDictionary<string, string>.Empty.Add("accent1", "#336699"),
            Fonts = ImmutableDictionary<string, string>.Empty.Add("minorAscii", "Arial")
        } };
        var (document, _, body) = await RoundTrip(original);
        Assert.Equal("Test theme", document.Theme.Name); Assert.Equal("#336699", document.Theme.Colors["accent1"]); Assert.Equal("Arial", document.Theme.Fonts["minorAscii"]);
        var actual = Assert.IsType<Paragraph>(document.Blocks[0]);
        Assert.Equal("minorAscii", actual.Runs[0].Style.Overrides!.ThemeFont.Value!.Name);
        Assert.Equal("accent1", actual.Runs[0].Style.Overrides!.ThemeForeground.Value!.Name);
        Assert.Equal(.2, actual.Runs[0].Style.Overrides!.ThemeForeground.Value!.Tint, 8);
        Assert.Equal("accent1", (string?)body.Descendants(W + "color").First(e => e.Attribute(W + "themeColor") is not null).Attribute(W + "themeColor"));
        var resolved = new DocumentStyleResolver(document).ResolveParagraph(actual);
        Assert.Equal("Arial", resolved.Runs[0].Style.FontFamily); Assert.Equal("#5C85AD", resolved.Runs[0].Style.Foreground);
    }

    [Fact]
    public async Task Table_style_identity_and_explicit_null_color_survive()
    {
        var paragraph = new Paragraph([new RichRun("clear", TextStyle.ForStyle("Red") with { Overrides = new() { Foreground = new(null) } })]);
        var table = Table.Create(1, 1) with { StyleId = "Shaded" };
        var original = new FlowDocument([paragraph, table]) { Styles = new()
        {
            Characters = ImmutableDictionary<string, CharacterStyleDefinition>.Empty.Add("Red", new() { Id = "Red", Formatting = new() { Foreground = "#FF0000" } }),
            Tables = ImmutableDictionary<string, TableStyleDefinition>.Empty.Add("Shaded", new() { Id = "Shaded", Formatting = new() { Background = "#EEEEEE", Padding = new EdgeInsets(2, 2, 2, 2) } })
        } };
        var (document, _, _) = await RoundTrip(original);
        Assert.Equal("Shaded", Assert.IsType<Table>(document.Blocks[1]).StyleId);
        Assert.Equal("#EEEEEE", document.Styles.Tables["Shaded"].Formatting.Background.Value);
        var actual = Assert.IsType<Paragraph>(document.Blocks[0]);
        Assert.Null(new DocumentStyleResolver(document).ResolveParagraph(actual).Runs[0].Style.Foreground);
    }
    [Fact]
    public async Task Direct_empty_tabs_and_list_none_cancel_named_style_values()
    {
        var paragraph = new Paragraph("plain") { Style = ParagraphStyle.ForStyle("List") with
        { Overrides = new() { List = ListKind.None, TabStops = ImmutableArray<TabStop>.Empty } } };
        var document = new FlowDocument([paragraph]) { Styles = new()
        {
            Paragraphs = ImmutableDictionary<string, ParagraphStyleDefinition>.Empty.Add("List", new()
            { Id = "List", Formatting = new() { List = ListKind.Numbered, ListId = Guid.NewGuid(), TabStops = ImmutableArray.Create(new TabStop(72)) } })
        } };
        var (loaded, _, body) = await RoundTrip(document);
        var resolved = new DocumentStyleResolver(loaded).ResolveParagraph(Assert.IsType<Paragraph>(loaded.Blocks[0]));
        Assert.Equal(ListKind.None, resolved.Style.List); Assert.Empty(resolved.Style.TabStops);
        Assert.Contains(body.Descendants(W + "tab"), element => (string?)element.Attribute(W + "val") == "clear");
        Assert.Contains(body.Descendants(W + "numId"), element => (string?)element.Attribute(W + "val") == "0");
    }
    [Fact]
    public async Task Identifiers_shared_by_style_kinds_are_remapped_without_dangling_references()
    {
        var paragraph = new Paragraph([new RichRun("shared", TextStyle.ForStyle("same"))]) { Style = ParagraphStyle.ForStyle("same") };
        var source = new FlowDocument([paragraph]) { Styles = new()
        {
            Characters = ImmutableDictionary<string, CharacterStyleDefinition>.Empty.Add("same", new() { Id = "same", LinkedStyle = "same", Formatting = new() { Bold = true } }),
            Paragraphs = ImmutableDictionary<string, ParagraphStyleDefinition>.Empty.Add("same", new() { Id = "same", LinkedStyle = "same", Formatting = new() { SpaceAfter = 22 } })
        } };
        using var stream = new MemoryStream();
        var result = await DocumentFormats.Docx.SaveWithReportAsync(source, stream);
        Assert.Contains(result.Report.Diagnostics, diagnostic => diagnostic.Code == "docx.style-id-collision");
        stream.Position = 0; var loaded = await DocumentFormats.Docx.LoadAsync(stream);
        var actual = Assert.IsType<Paragraph>(loaded.Blocks[0]);
        Assert.NotEqual(actual.Style.StyleId, actual.Runs[0].Style.StyleId);
        Assert.Equal(actual.Runs[0].Style.StyleId, loaded.Styles.Paragraphs[actual.Style.StyleId!].LinkedStyle);
        var resolved = new DocumentStyleResolver(loaded).ResolveParagraph(actual);
        Assert.True(resolved.Runs[0].Style.Bold); Assert.Equal(22, resolved.Style.SpaceAfter);
        using var rejected = new MemoryStream();
        await Assert.ThrowsAsync<DocumentConversionException>(() => DocumentFormats.Docx.SaveWithReportAsync(source, rejected, new() { Mode = ConversionMode.Strict }));
        Assert.Equal(0, rejected.Length);
    }
}
