using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class NamedStyleModelTests
{
    private static Paragraph Named(string text, string paragraph = "body", string? character = null) =>
        new(text, TextStyle.ForStyle(character)) { Style = ParagraphStyle.ForStyle(paragraph) };

    private static FlowDocument Styled(params Paragraph[] paragraphs) => new(paragraphs)
    {
        Styles = new()
        {
            Paragraphs = ImmutableDictionary<string, ParagraphStyleDefinition>.Empty
                .Add("base", new() { Id = "base", Formatting = new() { SpaceBefore = 12 }, TextFormatting = new() { Bold = true, Foreground = "#123456" } })
                .Add("body", new() { Id = "body", BasedOn = "base", Formatting = new() { SpaceAfter = 20 } }),
            Characters = ImmutableDictionary<string, CharacterStyleDefinition>.Empty
                .Add("emphasis", new() { Id = "emphasis", Formatting = new() { Italic = true } })
        }
    };

    [Fact]
    public void Sparse_false_zero_and_null_clear_inheritance_while_unset_values_remain_live()
    {
        var paragraph = Named("text", character: "emphasis");
        paragraph = paragraph with
        {
            Style = paragraph.Style with { Overrides = new() { SpaceBefore = 0 } },
            Runs = [new RichRun("text", paragraph.Runs[0].Style with { Overrides = new() { Bold = false, Foreground = new((string?)null) } })]
        };
        var document = Styled(paragraph);
        document.Validate();
        var effective = new DocumentStyleResolver(document).ResolveParagraph(paragraph);
        Assert.False(effective.Runs[0].Style.Bold);
        Assert.True(effective.Runs[0].Style.Italic);
        Assert.Null(effective.Runs[0].Style.Foreground);
        Assert.Equal(0, effective.Style.SpaceBefore);
        Assert.Equal(20, effective.Style.SpaceAfter);
        var changed = document with { Styles = document.Styles with
        { Paragraphs = document.Styles.Paragraphs.SetItem("body", document.Styles.Paragraphs["body"] with { Formatting = new() { SpaceAfter = 34 } }) } };
        Assert.Equal(34, new DocumentStyleResolver(changed).ResolveParagraph(paragraph).Style.SpaceAfter);
    }

    [Fact]
    public void Clearing_a_direct_value_reveals_parent_and_legacy_formatting_stays_explicit()
    {
        var paragraph = Named("text");
        var document = Styled(paragraph);
        var resolver = new DocumentStyleResolver(document);
        var explicitFalse = TextStyle.ForStyle(null) with { Overrides = new() { Bold = false } };
        Assert.False(resolver.ResolveText(paragraph, explicitFalse).Bold);
        Assert.True(resolver.ResolveText(paragraph, explicitFalse with { Overrides = explicitFalse.Overrides!.Clear(nameof(TextStyle.Bold)) }).Bold);
        Assert.False(resolver.ResolveText(paragraph, new TextStyle()).Bold);
        var before = resolver.ResolveText(paragraph, paragraph.Runs[0].Style);
        var difference = TextStyleOverrides.Difference(before, before with { Italic = true });
        Assert.True(difference.Italic.IsSet);
        Assert.False(difference.Bold.IsSet);
    }

    [Fact]
    public void Character_style_keeps_each_paragraph_base_and_theme_clear_is_explicit()
    {
        var first = Named("a", character: "emphasis");
        var second = Named("b", "other", "emphasis");
        var document = Styled(first, second);
        document = document with
        {
            Theme = new() { Colors = ImmutableDictionary<string, string>.Empty.Add("accent1", "#204060") },
            Styles = document.Styles with { Paragraphs = document.Styles.Paragraphs
                .Add("other", new() { Id = "other", TextFormatting = new() { FontSize = 30, ThemeForeground = new ThemeColorReference("accent1", .5) } }) }
        };
        var resolver = new DocumentStyleResolver(document);
        Assert.Equal(16, resolver.ResolveText(first, first.Runs[0].Style).FontSize);
        Assert.Equal(30, resolver.ResolveText(second, second.Runs[0].Style).FontSize);
        Assert.Equal("#90A0B0", resolver.ResolveText(second, second.Runs[0].Style).Foreground);
        Assert.Null(resolver.ResolveText(second, second.Runs[0].Style with { Overrides = new() { Foreground = new((string?)null) } }).Foreground);
    }

    [Fact]
    public void Catalog_cycles_unknown_links_and_invalid_sparse_values_are_rejected()
    {
        var document = Styled(Named("text"));
        Assert.Throws<FormatException>(() => (document with { Styles = document.Styles with
        { Paragraphs = document.Styles.Paragraphs.SetItem("base", document.Styles.Paragraphs["base"] with { BasedOn = "body" }) } }).Validate());
        Assert.Throws<FormatException>(() => (document with { Styles = document.Styles with
        { Characters = document.Styles.Characters.SetItem("emphasis", document.Styles.Characters["emphasis"] with { LinkedStyle = "missing" }) } }).Validate());
        Assert.Throws<FormatException>(() => (document with { Blocks = [Named("text") with
        { Runs = [new RichRun("text", TextStyle.ForStyle(null) with { Overrides = new() { HorizontalScale = double.NaN } })] }] }).Validate());
    }

    [Fact]
    public void Native_round_trip_preserves_presence_and_table_style_inheritance()
    {
        var table = Table.Create(1, 1) with { StyleId = "grid", StyleOverrides = new() { Background = new((string?)null) } };
        var document = Styled(Named("text")) with { Blocks = [table] };
        document = document with { Styles = document.Styles with { Tables = ImmutableDictionary<string, TableStyleDefinition>.Empty
            .Add("base", new() { Id = "base", Formatting = new() { Background = "#AABBCC", Padding = new EdgeInsets(4, 4, 4, 4) } })
            .Add("grid", new() { Id = "grid", BasedOn = "base" }) } };
        var restored = DocumentFormats.Json.Parse(DocumentFormats.Json.Serialize(document));
        var copy = Assert.IsType<Table>(restored.Blocks[0]);
        Assert.True(copy.StyleOverrides!.Background.IsSet);
        Assert.Null(copy.StyleOverrides.Background.Value);
        Assert.Equal(new EdgeInsets(4, 4, 4, 4), new DocumentStyleResolver(restored).ResolveTableStyle(copy).Padding);
    }

    [Fact]
    public void Clipboard_remaps_colliding_styles_without_changing_existing_style_links()
    {
        var source = Styled(Named("source", character: "emphasis"));
        source = source with { Styles = source.Styles with { Paragraphs = source.Styles.Paragraphs.SetItem("body",
            source.Styles.Paragraphs["body"] with { LinkedStyle = "emphasis" }) } };
        var target = Styled(Named("target", character: "emphasis"));
        target = target with { Styles = target.Styles with
        {
            Paragraphs = source.Styles.Paragraphs,
            Characters = target.Styles.Characters.SetItem("emphasis", new() { Id = "emphasis", Formatting = new() { Underline = true } })
        } };
        var session = new EditorSession(target);
        session.Select(target.Text.Length, target.Text.Length);
        session.InsertFragment(new() { Document = source });
        session.Document.Validate();
        Assert.Equal("emphasis", session.Document.Styles.Paragraphs["body"].LinkedStyle);
        Assert.True(session.Document.Styles.Characters["emphasis"].Formatting.Underline.Value);
        Assert.Contains(session.Document.Styles.Characters, pair => pair.Key != "emphasis" && pair.Value.Formatting.Italic.Value);
        Assert.Contains(session.Document.Styles.Paragraphs, pair => pair.Key.StartsWith("body-copy-", StringComparison.Ordinal));
    }

    [Fact]
    public void Named_list_formatting_numbers_all_dependent_paragraphs()
    {
        var a = Named("a"); var b = Named("b");
        var document = Styled(a, b);
        document = document with { Styles = document.Styles with
        { Paragraphs = document.Styles.Paragraphs.SetItem("body", document.Styles.Paragraphs["body"] with
        { Formatting = new() { List = ListKind.Numbered, ListId = Guid.NewGuid() } }) } };
        document.Validate();
        var markers = ListNumbering.Compute(document);
        Assert.Equal("1.", markers[a.Id].Text);
        Assert.Equal("2.", markers[b.Id].Text);
    }

    [Fact]
    public void Direct_physical_font_and_color_override_inherited_theme_references()
    {
        var paragraph = Named("x");
        var document = Styled(paragraph) with
        {
            Theme = new()
            {
                Colors = ImmutableDictionary<string, string>.Empty.Add("accent", "#112233"),
                Fonts = ImmutableDictionary<string, string>.Empty.Add("minorLatin", "Inter")
            }
        };
        document = document with { Styles = document.Styles with
        { Paragraphs = document.Styles.Paragraphs.SetItem("body", document.Styles.Paragraphs["body"] with
        { TextFormatting = new() { ThemeForeground = new ThemeColorReference("accent"), ThemeFont = new ThemeFontReference("minorLatin") } }) } };
        var resolver = new DocumentStyleResolver(document);
        var physical = TextStyle.ForStyle(null) with { Overrides = new() { Foreground = "#AABBCC", FontFamily = "Serif" } };
        var effective = resolver.ResolveText(paragraph, physical);
        Assert.Equal("#AABBCC", effective.Foreground);
        Assert.Equal("Serif", effective.FontFamily);
        Assert.Null(effective.ThemeForeground);
        Assert.Null(effective.ThemeFont);
        var inherited = resolver.ResolveText(paragraph, TextStyle.ForStyle(null));
        Assert.Equal("#112233", inherited.Foreground);
        Assert.Equal("Inter", inherited.FontFamily);
    }

    [Fact]
    public void Legacy_boolean_commands_clear_richer_inherited_bold_and_decoration_values()
    {
        var basis = new TextStyle { FontWeight = 700, UnderlineKind = UnderlineKind.Double, StrikeKind = StrikeKind.Double };
        var effective = new TextStyleOverrides { Bold = false, Underline = false, Strikethrough = false }.Apply(basis);
        Assert.False(effective.EffectiveBold);
        Assert.Equal(UnderlineKind.None, effective.UnderlineKind);
        Assert.Equal(StrikeKind.None, effective.StrikeKind);
    }


    [Fact]
    public void Inline_clipboard_merge_preserves_source_and_destination_paragraph_derived_text()
    {
        var source = Styled(Named("X"));
        source = source with { Styles = source.Styles with { Paragraphs = source.Styles.Paragraphs
            .SetItem("base", source.Styles.Paragraphs["base"] with { TextFormatting = new() { FontSize = 32 } }) } };
        var target = Styled(Named("ab"));
        var session = new EditorSession(target);
        session.Select(1, 1);
        session.InsertDocument(source);
        Assert.Equal("aXb", session.Document.Text);
        var paragraph = Assert.IsType<Paragraph>(session.Document.Blocks[0]);
        var effective = new DocumentStyleResolver(session.Document).ResolveParagraph(paragraph);
        Assert.Equal(16, effective.StyleAt(0).FontSize);
        Assert.Equal(32, effective.StyleAt(1).FontSize);
        Assert.Equal(16, effective.StyleAt(2).FontSize);
        Assert.NotNull(paragraph.StyleAt(1).Overrides);
        Assert.Equal("body", Assert.IsType<Paragraph>(source.Blocks[0]).Style.StyleId);
        session.Document.Validate();
    }

}

