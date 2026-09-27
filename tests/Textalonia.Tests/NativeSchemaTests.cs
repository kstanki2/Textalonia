using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Textalonia.Baselines;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class NativeSchemaTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static string Encode(FlowDocument document) => DocumentFormats.Json.Serialize(document);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(int.MaxValue)]
    public void Unsupported_versions_are_reported_before_decoding_future_members(int version)
    {
        var error = Assert.Throws<NotSupportedException>(() => DocumentFormats.Json.Parse(
            JsonSerializer.Serialize(new { version, futureEnvelope = true, document = new { futureNode = new { arbitrary = 42 } } })));
        Assert.Contains(version.ToString(), error.Message);
        Assert.Equal($"Document version {version} is not supported. Supported version is 7.", error.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"2\"")]
    [InlineData("2.5")]
    [InlineData("2,\"version\":1")]
    public void Version_must_be_a_single_integer(string version) => Assert.Throws<FormatException>(() =>
        DocumentFormats.Json.Parse("{\"version\":" + version + ",\"document\":{}}"));

    [Fact]
    public void Current_version_rejects_unknown_nested_members()
    {
        var json = Fixture("native-basic.json");
        Assert.Throws<JsonException>(() => DocumentFormats.Json.Parse(json.Replace("\"fontSize\": 16", "\"fontSize\": 16, \"unknownStyle\": 700")));
        Assert.Throws<JsonException>(() => DocumentFormats.Json.Parse(json.Replace("\"spaceAfter\": 8", "\"spaceAfter\": 8, \"unknownParagraph\": true")));
        Assert.Throws<JsonException>(() => DocumentFormats.Json.Parse(json.Replace("\"columnSpan\": 1", "\"columnSpan\": 1, \"unknownCell\": []")));
    }

    [Fact]
    public void Current_version_rejects_parallel_cell_projections_and_unknown_members()
    {
        var document = new FlowDocument([Table.Create(1, 1)]);
        var json = Encode(document);
        Assert.Throws<JsonException>(() => DocumentFormats.Json.Parse(json.Replace("\"columnSpan\": 1", "\"columnSpan\": 1, \"paragraphs\": []")));
        Assert.Throws<JsonException>(() => DocumentFormats.Json.Parse(json.Replace("\"columnSpan\": 1", "\"columnSpan\": 1, \"mergeOriginal\": []")));
        Assert.Throws<JsonException>(() => DocumentFormats.Json.Parse(json.Replace("\"version\": 7", "\"version\": 7, \"unknown\": true")));
        using var parsed = JsonDocument.Parse(json);
        var cell = parsed.RootElement.GetProperty("document").GetProperty("blocks")[0].GetProperty("rows")[0][0];
        Assert.False(cell.TryGetProperty("paragraphs", out _));
        Assert.DoesNotContain("\"mergeOriginal\"", json);
        Assert.Contains("\"mergeOriginalBlocks\"", json);
    }

    [Fact]
    public void Basic_fixture_preserves_hidden_content_and_default_styles()
    {
        var document = DocumentFormats.Json.Parse(Fixture("native-basic.json"));
        Assert.Equal(Encode(BaselineDocuments.Structured()), Encode(document));
        Assert.Equal(Encode(document), Encode(DocumentFormats.Json.Parse(Encode(document))));
        foreach (var block in AllBlocks(document.Blocks))
        {
            switch (block)
            {
                case Paragraph paragraph:
                    Assert.Null(paragraph.Style.ListId);
                    Assert.Null(paragraph.Style.ListDefinition);
                    Assert.Null(paragraph.Style.ListStart);
                    Assert.False(paragraph.Style.ListRestart);
                    Assert.Null(paragraph.Style.LineHeight);
                    Assert.Equal(0, paragraph.Style.LetterSpacing);
                    Assert.Equal(0, paragraph.Style.RightIndent);
                    Assert.Equal(0, paragraph.Style.FirstLineIndent);
                    foreach (var style in paragraph.Runs.Select(r => r.Style).Append(paragraph.DefaultStyle))
                    {
                        Assert.Null(style.FontWeight);
                        Assert.Equal(5, style.FontStretch);
                    }
                    break;
                case Section section:
                    Assert.Null(section.PaddingEdges);
                    Assert.Null(section.Borders);
                    break;
                case Table table:
                    Assert.Empty(table.ColumnWidths);
                    Assert.Empty(table.RowSizing);
                    foreach (var cell in table.Rows.SelectMany(row => row))
                    {
                        Assert.Null(cell.Padding);
                        Assert.Null(cell.Borders);
                    }
                    break;
            }
        }
    }

    [Fact]
    public void Absent_optional_properties_have_documented_defaults()
    {
        var document = DocumentFormats.Json.Parse("{\"version\":4,\"document\":{\"blocks\":[{\"kind\":\"paragraph\",\"id\":\"00000000-0000-4000-8000-000000000001\"}]}}");
        var paragraph = Assert.IsType<Paragraph>(Assert.Single(document.Blocks));
        Assert.Empty(paragraph.Runs);
        Assert.Equal(TextStyle.Default, paragraph.DefaultStyle);
        Assert.Equal(ParagraphStyle.Default, paragraph.Style);
        Assert.Empty(document.Resources);
        Assert.False(paragraph.DefaultStyle.IsCode);
    }

    [Fact]
    public void Rich_fixture_preserves_formatting_shapes_and_undo_history()
    {
        var document = DocumentFormats.Json.Parse(Fixture("native-rich.json"));
        var encoded = Encode(document);
        Assert.Equal(Encode(BaselineDocuments.DocumentSemantics()), encoded);
        Assert.Equal(encoded, Encode(DocumentFormats.Json.Parse(encoded)));
        var rich = Assert.IsType<Paragraph>(document.Blocks[0]);
        Assert.Equal(600, rich.Runs[0].Style.FontWeight);
        Assert.Equal(6, rich.Runs[0].Style.FontStretch);
        Assert.True(rich.Runs[0].Style.Bold);
        Assert.Equal(600, rich.Runs[0].Style.EffectiveFontWeight); Assert.False(rich.DefaultStyle.EffectiveBold);
        Assert.Equal(28, rich.Style.LineHeight);
        Assert.Equal(1.25, rich.Style.LetterSpacing);
        Assert.Equal(-8, rich.Style.FirstLineIndent);
        Assert.Equal(15, rich.Style.RightIndent);
        var first = Assert.IsType<Paragraph>(document.Blocks[1]);
        var levels = first.Style.ListDefinition!.Levels;
        Assert.NotNull(first.Style.ListId);
        Assert.Equal(3, levels[0].Start);
        Assert.Equal("[", levels[0].Prefix);
        Assert.Equal("]", levels[0].Suffix);
        Assert.Equal(ListMarkerStyle.LowerRoman, levels[1].Marker);
        Assert.True(levels[1].IncludeAncestors);
        Assert.Equal(ListKind.Bullet, levels[2].Kind);
        Assert.Equal("◆", levels[2].Text);
        var restart = Assert.IsType<Paragraph>(document.Blocks[^2]);
        Assert.True(restart.Style.ListRestart);
        Assert.Equal(7, restart.Style.ListStart);
        var table = Assert.IsType<Table>(document.Blocks[^1]);
        Assert.Equal(new[] { 140d, 220d }, table.ColumnWidths);
        Assert.Equal(TableRowHeightMode.AtLeast, table.RowSizing[0].Mode);
        Assert.Equal(TableRowHeightMode.Exact, table.RowSizing[1].Mode);
        Assert.Equal(80, table.RowSizing[1].Height);
        var anchor = table.Rows[0][0];
        Assert.Equal(new EdgeInsets(8, 4, 12, 6), anchor.Padding);
        Assert.All(new[] { anchor.Borders!.Left, anchor.Borders.Top, anchor.Borders.Right, anchor.Borders.Bottom }, side => Assert.NotNull(side));
        var section = Assert.IsType<Section>(anchor.Blocks[0]);
        Assert.Equal(new EdgeInsets(3, 5, 7, 9), section.PaddingEdges);
        Assert.All(new[] { section.Borders!.Left, section.Borders.Top, section.Borders.Right, section.Borders.Bottom }, side => Assert.NotNull(side));
        Assert.IsType<Table>(Assert.Single(section.Blocks));
        Assert.IsType<Table>(Assert.Single(table.Rows[1][0].Blocks));
        Assert.IsType<Table>(Assert.Single(Assert.IsType<Section>(Assert.Single(anchor.MergeOriginalBlocks)).Blocks));
        var split = table.SplitCell(0, 0);
        Assert.Equal(anchor.MergeOriginalBlocks[0].Id, split.Rows[0][0].Blocks[0].Id);
        var session = new EditorSession(document);
        session.InsertText("edit");
        session.Undo();
        Assert.Equal(encoded, Encode(session.Document));
        session.Redo();
        Assert.Equal("edit" + document.Text, session.Document.Text);
    }

    [Fact]
    public void Merge_backup_identifiers_can_match_the_original_blocks()
    {
        var json = JsonNode.Parse(Fixture("native-basic.json"))!;
        var anchor = json["document"]!["blocks"]![1]!["blocks"]!.AsArray().Last()!["rows"]![0]![0]!;
        var originalId = anchor["blocks"]![0]!["id"]!.GetValue<string>();
        anchor["mergeOriginalBlocks"]![0]!["id"] = originalId;
        var document = DocumentFormats.Json.Parse(json.ToJsonString());
        var table = Assert.IsType<Table>(Assert.IsType<Section>(document.Blocks[1]).Blocks[^1]);
        Assert.Equal(table.Rows[0][0].Blocks[0].Id, table.Rows[0][0].MergeOriginalBlocks[0].Id);
        Assert.Equal(Encode(document), Encode(DocumentFormats.Json.Parse(Encode(document))));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_reader_round_trips_maximum_model_depth_and_rejects_one_deeper(bool alternateSections)
    {
        Block block = new Paragraph("deepest");
        for (var depth = 0; depth < 32; depth++)
            block = alternateSections && depth % 2 == 0
                ? new Section { Blocks = [block] }
                : new Table { Rows = [[new TableCell { Blocks = [block] }]] };
        var document = new FlowDocument([block]);
        var encoded = Encode(document);
        Assert.Equal(encoded, Encode(DocumentFormats.Json.Parse(encoded)));
        var json = JsonNode.Parse(encoded, documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!;
        json["document"]!["blocks"] = new JsonArray(new JsonObject
        {
            ["kind"] = "section", ["id"] = Guid.NewGuid().ToString(),
            ["blocks"] = json["document"]!["blocks"]!.DeepClone()
        });
        Assert.Throws<FormatException>(() => DocumentFormats.Json.Parse(json.ToJsonString(new JsonSerializerOptions { MaxDepth = 256 })));
    }
    private static IEnumerable<Block> AllBlocks(ImmutableArray<Block> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            IEnumerable<Block> nested = block switch
            {
                Section section => AllBlocks(section.Blocks),
                Table table => table.Rows.SelectMany(row => row).SelectMany(cell =>
                    AllBlocks(cell.Blocks).Concat(AllBlocks(cell.MergeOriginalBlocks))),
                _ => []
            };
            foreach (var child in nested) yield return child;
        }
    }
}
