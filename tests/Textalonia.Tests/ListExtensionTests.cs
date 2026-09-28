using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Textalonia.Controls;
using Textalonia.Editing;
using Textalonia.Layout;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class ListExtensionTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static readonly FontFamily Font = new("avares://Avalonia.Fonts.Inter/Assets#Inter");
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static ListDefinition Definition => new() { Levels = [new()
    {
        TextIndent = 70, MarkerIndent = 12, TabPosition = 90,
        MarkerFormatting = new() { Bold = true, Foreground = "#FF0000", FontSize = 24 }
    }] };
    private static FlowDocument Document(ListDefinition definition)
    {
        var id = Guid.NewGuid();
        return new([new Paragraph("first") { Style = new() { List = ListKind.Numbered, ListId = id, ListDefinition = definition } },
            new Paragraph("second") { Style = new() { List = ListKind.Numbered, ListId = id } }]);
    }

    [Fact]
    public void Links_resolve_marker_independently_and_copy_remaps_conflicting_style_identifiers()
    {
        var definition = Definition with { Levels = [Definition.Levels[0] with { CharacterStyleId = "marker", ParagraphStyleId = "body", MarkerFormatting = new() { Bold = false } }] };
        var paragraph = new Paragraph("linked", TextStyle.ForStyle(null)) { Style = definition.CreateParagraphStyle(listId: Guid.NewGuid()) };
        var styles = new DocumentStyleCatalog
        {
            Characters = ImmutableDictionary<string, CharacterStyleDefinition>.Empty.Add("marker", new() { Id = "marker", Formatting = new() { FontSize = 28, Bold = true, Foreground = "#FF0000" } }),
            Paragraphs = ImmutableDictionary<string, ParagraphStyleDefinition>.Empty.Add("body", new() { Id = "body", Formatting = new() { SpaceAfter = 17 }, TextFormatting = new() { FontSize = 18 } })
        };
        var document = new FlowDocument([paragraph]) { Styles = styles }; document.Validate();
        var resolver = new DocumentStyleResolver(document);
        var marker = resolver.ResolveListMarkerStyle(paragraph, definition.Levels[0]);
        Assert.False(marker.Bold); Assert.Equal(28, marker.FontSize); Assert.Equal("#FF0000", marker.Foreground);
        Assert.Equal(18, resolver.ResolveParagraph(paragraph).DefaultStyle.FontSize);
        Assert.Equal(17, resolver.ResolveParagraph(paragraph).Style.SpaceAfter);
        var destination = new FlowDocument([new Paragraph("existing")]) { Styles = styles with
        { Characters = styles.Characters.SetItem("marker", styles.Characters["marker"] with { Formatting = new() { FontSize = 12 } }) } };
        var imported = DocumentStyleImport.Prepare(document, destination); imported.Validate();
        var copied = Assert.IsType<Paragraph>(imported.Blocks[0]);
        var copiedLevel = new DocumentStyleResolver(imported).ResolveParagraphStyle(copied.Style).ListDefinition!.Levels[0];
        Assert.NotEqual("marker", copiedLevel.CharacterStyleId);
        Assert.Equal(28, new DocumentStyleResolver(imported).ResolveListMarkerStyle(copied, copiedLevel).FontSize);
    }

    [Fact]
    public void Invalid_marker_dimensions_formatting_and_links_are_rejected()
    {
        ListLevelDefinition[] levels = [new() { TextIndent = double.NaN }, new() { MarkerIndent = -1 },
            new() { TabPosition = double.PositiveInfinity }, new() { FollowCharacter = (ListFollowCharacter)30 },
            new() { MarkerFormatting = new() { FontSize = 0 } }, new() { CharacterStyleId = "missing" }, new() { ParagraphStyleId = "missing" }];
        foreach (var level in levels) Assert.Throws<FormatException>(() => Document(new() { Levels = [level] }).Validate());
    }

    [Fact]
    public async Task Native_and_xaml_preserve_extended_levels_exactly()
    {
        var document = Document(Definition);
        foreach (var format in new IDocumentFormat[] { DocumentFormats.Json, DocumentFormats.Xaml })
        {
            using var stream = new MemoryStream(); await format.SaveAsync(document, stream); stream.Position = 0;
            var loaded = await format.LoadAsync(stream);
            Assert.Equal(Definition, Assert.IsType<Paragraph>(loaded.Blocks[0]).Style.ListDefinition);
            Assert.Equal(Definition, ListNumbering.GetMarker(loaded, loaded.Blocks[1].Id)!.Definition);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Office_formats_preserve_marker_font_color_and_positions(bool rtf)
    {
        var document = Document(Definition);
        IDocumentFormat format = rtf ? DocumentFormats.Rtf : DocumentFormats.Docx;
        using var stream = new MemoryStream(); await format.SaveAsync(document, stream); stream.Position = 0;
        var loaded = await format.LoadAsync(stream); loaded.Validate();
        var first = new DocumentIndex(loaded).Paragraphs[0].Paragraph;
        var level = ListNumbering.GetMarker(loaded, first.Id)!.LevelDefinition;
        var marker = new DocumentStyleResolver(loaded).ResolveListMarkerStyle(first, level);
        Assert.True(marker.Bold); Assert.Equal(24, marker.FontSize); Assert.Equal("#FF0000", marker.Foreground);
        Assert.Equal(70, level.TextIndent); Assert.Equal(12, level.MarkerIndent); Assert.Equal(90, level.TabPosition);
        Assert.Equal("2.", ListNumbering.GetMarker(loaded, new DocumentIndex(loaded).Paragraphs[1].Paragraph.Id)!.Text);
    }

    [Fact]
    public Task Inherited_positions_and_marker_styles_match_in_simple_and_page_layout() => Run(() =>
    {
        var document = Document(Definition);
        using var simple = new DocumentLayout();
        simple.Build(document, 500, Font, Brushes.Black, Brushes.Gray, new Thickness(0), new Rect(0, 0, 500, 500));
        Assert.All(simple.Paragraphs, visual =>
        {
            Assert.Equal(90, visual.Origin.X, 5); Assert.Equal(24, visual.MarkerStyle!.FontSize);
            Assert.Equal(12, visual.MarkerDefinition!.MarkerIndent);
        });
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document, Font);
        Assert.All(pages.Fragments, line =>
        {
            Assert.Equal(pages.Pages[line.PageIndex].ContentBounds.Left + 90, line.Origin.X, 5);
            Assert.Equal(24, line.MarkerStyle!.FontSize); Assert.Equal(12, line.MarkerDefinition!.MarkerIndent);
        });
        Assert.Equal(new[] { "1.", "2." }, pages.Fragments.Select(line => line.Marker));
    });

    [Fact]
    public Task Space_and_nothing_follow_marker_width_and_restart_reflows_later_items() => Run(() =>
    {
        var definition = Definition with { Levels = [Definition.Levels[0] with { FollowCharacter = ListFollowCharacter.Nothing }] };
        var document = Document(definition);
        using var engine = new PaginationEngine(); using var before = engine.Paginate(document, Font);
        var secondX = before.Fragments[1].Origin.X;
        var first = Assert.IsType<Paragraph>(document.Blocks[0]);
        var changed = document.ReplaceBlock(first.Id, first with { Style = first.Style with { ListStart = 1000 } });
        using var after = engine.Paginate(changed, Font);
        Assert.Equal("1001.", after.Fragments[1].Marker); Assert.True(after.Fragments[1].Origin.X > secondX);
        using var label = ListMarkerDrawing.Shape("1.", new() { FontSize = 24, Bold = true }, Font, Brushes.Black);
        Assert.Equal(before.Pages[0].ContentBounds.Left + 12 + label.Width, before.Fragments[0].Origin.X, 4);
    });
    [Fact]
    public Task Story_lists_use_shared_styles_and_marker_fonts_use_document_substitution() => Run(() =>
    {
        var definition = Definition with { Levels = [Definition.Levels[0] with
        { MarkerFormatting = new() { FontFamily = "DefinitelyMissingDxMarkerFont", Foreground = "#FF0000" } }] };
        var story = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = Document(definition).Blocks };
        var document = new FlowDocument([new Paragraph("body")])
        {
            Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story),
            Sections = [new DocumentSection { HeaderFooter = new()
                { PrimaryHeader = new() { StoryId = story.Id, LinkToPrevious = false }, HeaderDistance = 10 } }]
        };
        using var engine = new PaginationEngine(); using var pages = engine.Paginate(document, Font);
        Assert.Equal(new[] { "1.", "2." }, pages.StoryFragments.Select(line => line.Marker));
        Assert.All(pages.StoryFragments, line => Assert.Equal("#FF0000", line.MarkerStyle!.Foreground));
        Assert.Contains(pages.FontDiagnostics, diagnostic => diagnostic.Code == "font.substituted" && diagnostic.FamilyName == "DefinitelyMissingDxMarkerFont");
    });

    [Fact]
    public void Hidden_and_secondary_list_style_references_are_validated()
    {
        var bad = new Paragraph("hidden") { Style = new() { ListDefinition = new()
            { Levels = [new() { CharacterStyleId = "unknown-marker-style" }] } } };
        var story = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [bad] };
        Assert.Throws<FormatException>(() => (new FlowDocument([new Paragraph("body")])
            { Stories = ImmutableDictionary<Guid, DocumentStory>.Empty.Add(story.Id, story) }).Validate());
        var table = Table.Create(1, 1); table = table.SetCell(0, 0, table.Rows[0][0] with { MergeOriginalBlocks = [bad] });
        Assert.Throws<FormatException>(() => new FlowDocument([table]).Validate());
    }

}
