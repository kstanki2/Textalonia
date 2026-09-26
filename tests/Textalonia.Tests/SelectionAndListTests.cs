using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class SelectionAndListTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_selection_enables_one_property_uniformly_and_undo_restores_direction(bool reverse)
    {
        var original = new FlowDocument([
            new Paragraph([new RichRun("ab", new() { Bold = true, Foreground = "#123456" }), new RichRun("cd", new() { Italic = true })]),
            new Paragraph("ef", new() { FontWeight = 700, Underline = true }) { Style = new() { Alignment = ParagraphAlignment.Right } }
        ]);
        var session = new EditorSession(original);
        session.Select(reverse ? 7 : 0, reverse ? 0 : 7);
        var selection = session.Selection;
        Assert.True(session.FormattingState.Bold.IsMixed);
        Assert.True(session.FormattingState.Alignment.IsMixed);
        Assert.False(session.FormattingState.FontFamily.IsMixed);
        Assert.Null(session.FormattingState.FontFamily.Value);
        session.ToggleBold();
        Assert.Equal(new FormattingValue<bool>(true), session.FormattingState.Bold);
        Assert.Equal("#123456", session.Index.Paragraphs[0].Paragraph.Runs[0].Style.Foreground);
        Assert.True(session.Index.Paragraphs[0].Paragraph.Runs[1].Style.Italic);
        Assert.True(session.Index.Paragraphs[1].Paragraph.Runs[0].Style.Underline);
        session.Undo();
        Assert.Same(original, session.Document);
        Assert.Equal(selection, session.Selection);
        Assert.False(session.CanUndo);
        session.Redo();
        Assert.True(session.FormattingState.Bold.Value);
    }

    [Fact]
    public void Caret_formatting_has_undo_and_uses_typing_style_instead_of_adjacent_text()
    {
        var session = new EditorSession(FlowDocument.FromText("a"));
        session.Select(1, 1);
        session.ApplyStyle(s => s with { FontWeight = 650 });
        Assert.True(session.FormattingState.IsCollapsed);
        Assert.True(session.FormattingState.Bold.Value);
        Assert.False(session.Index.At(0).Paragraph.Runs[0].Style.EffectiveBold);
        session.Undo(); Assert.False(session.FormattingState.Bold.Value);
        session.Redo(); Assert.True(session.FormattingState.Bold.Value);
        session.InsertText("b");
        Assert.Equal(650, session.Index.At(0).Paragraph.Runs[^1].Style.FontWeight);
    }

    [Fact]
    public void Empty_selected_paragraphs_and_cells_participate_and_end_boundary_is_excluded()
    {
        var table = Table.Create(1, 3);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [new Paragraph("", new() { Underline = true })] });
        table = table.SetCell(0, 1, table.Rows[0][1] with { Blocks = [new Paragraph("a")] });
        table = table.SetCell(0, 2, table.Rows[0][2] with { Blocks = [new Paragraph("b", new() { FontSize = 32 })] });
        var original = new FlowDocument([table]);
        var session = new EditorSession(original);
        session.Select(3, 0);
        Assert.True(session.FormattingState.Underline.IsMixed);
        Assert.Equal(new FormattingValue<double>(16), session.FormattingState.FontSize);
        session.ToggleUnderline();
        Assert.Equal(new FormattingValue<bool>(true), session.FormattingState.Underline);
        Assert.True(session.Index.Paragraphs[0].Paragraph.DefaultStyle.Underline);
        Assert.False(session.Index.Paragraphs[2].Paragraph.Runs[0].Style.Underline);
        session.Undo(); Assert.Same(original, session.Document); Assert.False(session.CanUndo);
    }

    [Fact]
    public void Rich_properties_aggregate_effective_weight_and_semantically_equal_definitions()
    {
        var definition = new ListDefinition { Levels = [new() { Start = 3 }] };
        var session = new EditorSession(new FlowDocument([
            new Paragraph("a", new() { Bold = true }) { Style = new() { LetterSpacing = 1, LineHeight = 20, ListDefinition = definition } },
            new Paragraph("b", new() { FontWeight = 700, FontStretch = 7 }) { Style = new() { LetterSpacing = 2, LineHeight = 20,
                ListDefinition = new() { Levels = [new() { Start = 3 }] } } }
        ]));
        session.SelectAll();
        Assert.Equal(new FormattingValue<int>(700), session.FormattingState.FontWeight);
        Assert.True(session.FormattingState.FontStretch.IsMixed);
        Assert.True(session.FormattingState.LetterSpacing.IsMixed);
        Assert.False(session.FormattingState.Paragraph(s => s.LineHeight).IsMixed);
        Assert.False(session.FormattingState.Paragraph(s => s.ListDefinition).IsMixed);
    }

    [Fact]
    public void Invalid_selection_dependent_changes_are_atomic()
    {
        var original = new FlowDocument([new Paragraph("a", new() { Italic = true }), new Paragraph("b")]);
        var session = new EditorSession(original); session.SelectAll();
        Assert.Throws<FormatException>(() => session.ApplyStyle(s => s with { FontSize = s.Italic ? double.NaN : 20 }));
        Assert.Throws<FormatException>(() => session.ApplyParagraphStyle(s => s with { ListId = Guid.Empty }));
        Assert.Same(original, session.Document); Assert.False(session.CanUndo);
    }

    [Fact]
    public void Identity_continues_across_sections_and_cells_and_restarts_multilevel_numbers()
    {
        var id = Guid.NewGuid();
        var definition = new ListDefinition { Levels = [new() { Start = 3 }, new() { Marker = ListMarkerStyle.LowerLetter, IncludeAncestors = true }] };
        Paragraph Item(string text, int level = 0, int? restart = null) => new(text)
        { Style = new() { List = ListKind.Numbered, ListId = id, ListDefinition = definition, ListLevel = level, ListStart = restart } };
        var first = Item("first"); var child1 = Item("child 1", 1); var child2 = Item("child 2", 1);
        var next = Item("next"); var restart = Item("restart", restart: 9); var after = Item("after");
        var table = Table.Create(1, 1);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [child2] });
        var document = new FlowDocument([first, new Paragraph("gap"), new Section { Blocks = [child1, table, next] }, restart, after]);
        document.Validate();
        var markers = ListNumbering.Compute(document);
        Assert.Equal(new[] { "3.", "3.a.", "3.b.", "4.", "9.", "10." }, new[] { first, child1, child2, next, restart, after }.Select(p => markers[p.Id].Text));
        Assert.Equal(markers[after.Id], ListNumbering.GetMarker(document, after.Id));
    }

    [Theory]
    [InlineData(ListMarkerStyle.UpperLetter, 27, "AA.")]
    [InlineData(ListMarkerStyle.LowerRoman, 49, "xlix.")]
    [InlineData(ListMarkerStyle.UpperRoman, 4000, "4000.")]
    public void Level_markers_format_starts(ListMarkerStyle marker, int start, string expected)
    {
        var paragraph = new Paragraph("item") { Style = new() { List = ListKind.Numbered,
            ListDefinition = new() { Levels = [new() { Start = start, Marker = marker }] } } };
        Assert.Equal(expected, ListNumbering.GetMarker(new FlowDocument([paragraph]), paragraph.Id)!.Text);
    }

    [Fact]
    public void Enter_preserves_identity_without_repeating_restart_and_empty_item_exits()
    {
        var session = new EditorSession(FlowDocument.FromText("item"));
        session.ToggleList(ListKind.Numbered); session.RestartList(5);
        var identity = session.Index.At(0).Paragraph.Style.ListId;
        session.Select(4, 4); session.InsertParagraph(); session.InsertText("next");
        Assert.Equal(new[] { "5.", "6." }, session.Index.Paragraphs.Select(p => ListNumbering.GetMarker(session.Document, p.Paragraph.Id)!.Text));
        Assert.All(session.Index.Paragraphs, p => Assert.Equal(identity, p.Paragraph.Style.ListId));
        Assert.False(session.Index.Paragraphs[1].Paragraph.Style.ListRestart);
        session.InsertParagraph(); session.InsertParagraph();
        var empty = session.Index.Paragraphs[^1].Paragraph.Style;
        Assert.Equal(ListKind.None, empty.List); Assert.Null(empty.ListId); Assert.Null(empty.ListStart);
        session.Undo(); Assert.Equal(ListKind.Numbered, session.Index.Paragraphs[^1].Paragraph.Style.List);
    }

    [Fact]
    public void Indent_continue_and_restart_commands_are_undoable()
    {
        var session = new EditorSession(FlowDocument.FromText("a\nb\ngap\nc"));
        session.Select(0, 3); session.SetList(ListKind.Numbered, new() { Levels = [new() { Start = 2 }, new() { Start = 4, IncludeAncestors = true }] });
        var identity = session.Index.At(0).Paragraph.Style.ListId!.Value;
        session.Select(2, 2); session.IndentList();
        Assert.Equal("2.4.", ListNumbering.GetMarker(session.Document, session.Index.At(2).Paragraph.Id)!.Text);
        session.Select(session.Index.Length, session.Index.Length); session.ContinueList(identity);
        Assert.Equal("3.", ListNumbering.GetMarker(session.Document, session.Index.At(session.Index.Length).Paragraph.Id)!.Text);
        var original = session.Document;
        session.RestartList(8); Assert.Equal("8.", ListNumbering.GetMarker(session.Document, session.Index.At(session.Index.Length).Paragraph.Id)!.Text);
        session.Undo(); Assert.Same(original, session.Document);
    }

    [Fact]
    public void Paste_remaps_list_identities_consistently_and_removing_restart_item_renumbers()
    {
        var source = new EditorSession(FlowDocument.FromText("a\nb"));
        source.SelectAll(); source.ToggleList(ListKind.Numbered);
        var sourceId = source.Index.At(0).Paragraph.Style.ListId;
        var destination = new EditorSession(); destination.InsertDocument(source.CopySelection());
        var copied = destination.Index.Paragraphs;
        Assert.NotEqual(sourceId, copied[0].Paragraph.Style.ListId);
        Assert.Equal(copied[0].Paragraph.Style.ListId, copied[1].Paragraph.Style.ListId);
        destination.Select(0, 0); destination.RestartList(8);
        var original = destination.Document;
        var surviving = destination.Index.Paragraphs[1].Paragraph.Id;
        destination.Select(0, 2); destination.InsertText("");
        Assert.Equal(surviving, destination.Index.At(0).Paragraph.Id);
        Assert.Equal("1.", ListNumbering.GetMarker(destination.Document, surviving)!.Text);
        destination.Undo(); Assert.Same(original, destination.Document);
    }

    [Fact]
    public async Task Native_list_definitions_roundtrip_and_merged_split_restores_original_ids()
    {
        var style = new ParagraphStyle { List = ListKind.Numbered, ListId = Guid.NewGuid(), ListLevel = 1, ListRestart = true, ListStart = 7,
            ListDefinition = new() { Levels = [new(), new() { Marker = ListMarkerStyle.LowerLetter, IncludeAncestors = true, Prefix = "(", Suffix = ")" }] } };
        var original = new Paragraph("first") { Style = style };
        var table = Table.Create(1, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Blocks = [original] });
        var document = new FlowDocument([table.MergeCells(0, 0, 1, 2)]);
        using var stream = new MemoryStream(); await DocumentFormats.Json.SaveAsync(document, stream); stream.Position = 0;
        var loaded = await DocumentFormats.Json.LoadAsync(stream);
        var restored = Assert.IsType<Table>(loaded.Blocks[0]).SplitCell(0, 0);
        var paragraph = Assert.IsType<Paragraph>(restored.Rows[0][0].Blocks[0]);
        Assert.Equal(original.Id, paragraph.Id); Assert.Equal(style, paragraph.Style);
        Assert.Equal("(1.g)", ListNumbering.GetMarker(new FlowDocument([restored]), paragraph.Id)!.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Numbering_reuses_unchanged_subtrees_after_a_local_text_edit(bool identified)
    {
        var identity = identified ? Guid.NewGuid() : (Guid?)null;
        var style = new ParagraphStyle { List = ListKind.Numbered, ListId = identity };
        var session = new EditorSession(new FlowDocument(Enumerable.Range(0, 4096).Select(i => new Paragraph("x") { Style = style })));
        var lastId = session.Index.Paragraphs[^1].Paragraph.Id;
        Assert.Equal("4096.", ListNumbering.GetMarker(session.Document, lastId)!.Text);
        session.Select(4096, 4096); session.InsertText("z");
        var before = ListNumbering.EvaluationMisses;
        Assert.Equal("4096.", ListNumbering.GetMarker(session.Document, lastId)!.Text);
        Assert.InRange(ListNumbering.EvaluationMisses - before, 1, 60);
        session.RestartList(20);
        Assert.Equal("2067.", ListNumbering.GetMarker(session.Document, lastId)!.Text);
        session.Undo(); Assert.Equal("4096.", ListNumbering.GetMarker(session.Document, lastId)!.Text);
    }

    [Fact]
    public void Every_list_definition_field_is_validated()
    {
        ParagraphStyle[] invalid = [
            new() { ListId = Guid.Empty }, new() { ListStart = 0 }, new() { ListStart = 1_000_001 },
            new() { ListDefinition = new() { Levels = default } },
            new() { ListDefinition = new() { Levels = Enumerable.Repeat(new ListLevelDefinition(), 10).ToImmutableArray() } },
            new() { ListDefinition = new() { Levels = [new() { Start = 0 }] } },
            new() { ListDefinition = new() { Levels = [new() { Kind = ListKind.None }] } },
            new() { ListDefinition = new() { Levels = [new() { Marker = (ListMarkerStyle)99 }] } },
            new() { ListDefinition = new() { Levels = [new() { Prefix = null! }] } },
            new() { ListDefinition = new() { Levels = [new() { Suffix = new string('x', 101) }] } },
            new() { ListDefinition = new() { Levels = [new() { Text = new string('x', 101) }] } }
        ];
        foreach (var style in invalid) Assert.Throws<FormatException>(() => new FlowDocument([new Paragraph("x") { Style = style }]).Validate());
    }
    [Fact]
    public void Nested_session_insertion_cell_lookup_edit_delete_and_undo_target_innermost_cell()
    {
        var outer = Table.Create(1, 1);
        outer = outer.SetCell(0, 0, outer.Rows[0][0] with { Blocks = [new Section { Blocks = [new Paragraph("outer")] }] });
        var original = new FlowDocument([outer]);
        var session = new EditorSession(original);
        Assert.Equal(outer.Id, session.CurrentCell()!.Value.Table.Id);
        session.InsertTable(1, 1);
        var innerId = session.CurrentCell()!.Value.Table.Id;
        Assert.NotEqual(outer.Id, innerId);
        session.InsertText("inner"); session.Document.Validate();
        session.DeleteCurrentTable();
        Assert.DoesNotContain("inner", session.Document.Text);
        Assert.Equal(outer.Id, session.CurrentCell()!.Value.Table.Id);
        session.Undo(); Assert.Contains("inner", session.Document.Text);
        session.Undo(); session.Undo(); Assert.Same(original, session.Document);
    }
}
