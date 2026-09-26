using System.Collections.Immutable;
using Textalonia.Editing;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class EditingTests
{
    [Fact]
    public void Replacing_across_paragraphs_preserves_unselected_formatting_and_undo_selection()
    {
        var bold = TextStyle.Default with { Bold = true };
        var italic = TextStyle.Default with { Italic = true };
        var original = new FlowDocument([new Paragraph("hello", bold), new Paragraph("world", italic)]);
        var editor = new EditorSession(original);
        editor.Select(3, 8);
        editor.InsertText("X\nY");
        Assert.Equal("helX\nYrld", editor.Index.Text);
        Assert.True(editor.Index.Paragraphs[0].Paragraph.Runs[0].Style.Bold);
        Assert.True(editor.Index.Paragraphs[1].Paragraph.Runs[^1].Style.Italic);
        editor.Undo();
        Assert.Same(original, editor.Document);
        Assert.Equal(new TextSelection(3, 8), editor.Selection);
        editor.Redo();
        Assert.Equal("helX\nYrld", editor.Index.Text);
        Assert.Equal(new TextSelection(6, 6), editor.Selection);
    }

    [Fact]
    public void Partial_formatting_does_not_rewrite_surrounding_runs()
    {
        var editor = new EditorSession(FlowDocument.FromText("hello world"));
        editor.Select(2, 8);
        editor.ToggleBold();
        var paragraph = editor.Index.Paragraphs[0].Paragraph;
        Assert.Equal(new[] { "he", "llo wo", "rld" }, paragraph.Runs.Select(r => r.Text));
        Assert.False(paragraph.Runs[0].Style.Bold);
        Assert.True(paragraph.Runs[1].Style.Bold);
        Assert.False(paragraph.Runs[2].Style.Bold);
        editor.Undo();
        Assert.Single(editor.Index.Paragraphs[0].Paragraph.Runs);
    }

    [Fact]
    public void Grapheme_navigation_and_deletion_keep_emoji_and_combining_marks_intact()
    {
        var editor = new EditorSession(FlowDocument.FromText("A\U0001F469\u200D\U0001F4BBe\u0301Z"));
        editor.Select(editor.Index.Length - 1, editor.Index.Length - 1);
        editor.DeleteBackward();
        Assert.Equal("A\U0001F469\u200D\U0001F4BBZ", editor.Index.Text);
        editor.DeleteBackward();
        Assert.Equal("AZ", editor.Index.Text);
        editor.Undo();
        Assert.Equal("A\U0001F469\u200D\U0001F4BBZ", editor.Index.Text);
    }

    [Fact]
    public void Undo_of_backspace_restores_a_collapsed_caret()
    {
        var editor = new EditorSession(FlowDocument.FromText("abc"));
        editor.Select(3, 3);
        editor.DeleteBackward();
        editor.Undo();
        Assert.Equal("abc", editor.Index.Text);
        Assert.Equal(new TextSelection(3, 3), editor.Selection);
    }

    [Fact]
    public void Undo_coalesces_typing_but_breaks_at_navigation_and_clears_redo_after_edit()
    {
        var editor = new EditorSession();
        editor.InsertText("a", true); editor.InsertText("b", true); editor.InsertText("c", true);
        editor.Undo(); Assert.Equal("", editor.Index.Text);
        editor.Redo(); Assert.Equal("abc", editor.Index.Text);
        editor.Select(1, 1); editor.InsertText("X", true);
        editor.Undo(); Assert.Equal("abc", editor.Index.Text);
        editor.InsertText("Q");
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void Read_only_blocks_mutation_and_history_but_allows_selection()
    {
        var editor = new EditorSession(FlowDocument.FromText("abc"));
        editor.InsertText("z");
        var document = editor.Document;
        editor.IsReadOnly = true;
        editor.SelectAll();
        editor.InsertText("oops"); editor.ToggleBold(); editor.DeleteBackward(); editor.InsertTable();
        editor.Execute(_ => new()); editor.Undo();
        Assert.Same(document, editor.Document);
        Assert.Equal("zabc", editor.SelectedText);
        Assert.False(editor.CanUndo);
        Assert.Equal(0, editor.ReplaceAll("a", "b"));
    }

    [Fact]
    public void Find_replace_is_case_configurable_and_one_undo_step()
    {
        var editor = new EditorSession(FlowDocument.FromText("One one ONE"));
        Assert.Single(editor.FindAll("One", true));
        Assert.Equal(3, editor.ReplaceAll("one", "two"));
        Assert.Equal("two two two", editor.Index.Text);
        editor.Undo();
        Assert.Equal("One one ONE", editor.Index.Text);
        Assert.Empty(editor.FindAll(""));
    }

    [Fact]
    public void Empty_list_enter_exits_list_and_multiline_insertion_preserves_list_style()
    {
        var editor = new EditorSession();
        editor.ToggleList(ListKind.Bullet);
        editor.InsertText("first\nsecond");
        Assert.All(editor.Index.Paragraphs, p => Assert.Equal(ListKind.Bullet, p.Paragraph.Style.List));
        editor.InsertParagraph();
        editor.InsertParagraph();
        Assert.Equal(ListKind.None, editor.Index.Paragraphs[^1].Paragraph.Style.List);
    }

    [Fact]
    public void Randomized_replacements_match_plain_text_reference()
    {
        var random = new Random(7401);
        var editor = new EditorSession { UndoLimit = 1000 };
        var reference = "";
        string[] insertions = ["", "a", "bc", "\n", "x\ny", "\n\n", "q\r\nz"];
        for (var i = 0; i < 600; i++)
        {
            var a = random.Next(reference.Length + 1); var b = random.Next(reference.Length + 1);
            var insertion = insertions[random.Next(insertions.Length)];
            editor.Select(a, b); editor.InsertText(insertion);
            reference = reference[..Math.Min(a, b)] + FlowDocument.NormalizeNewlines(insertion) + reference[Math.Max(a, b)..];
            Assert.Equal(reference, editor.Index.Text);
            editor.Document.Validate();
            Assert.All(editor.Index.Paragraphs, p => Assert.Equal(p.Paragraph.Length, p.Paragraph.Runs.Sum(r => r.Text.Length)));
        }
    }

    [Fact]
    public void Native_rich_clipboard_fragment_preserves_runs_and_trailing_newline()
    {
        var editor = new EditorSession(new FlowDocument([
            new Paragraph("one", TextStyle.Default with { Bold = true }), new Paragraph("two")
        ]));
        editor.Select(0, 4);
        var fragment = editor.CopySelection();
        Assert.Equal("one\n", fragment.Text);
        var destination = new EditorSession();
        destination.InsertDocument(fragment);
        Assert.Equal("one\n", destination.Index.Text);
        Assert.True(destination.Index.Paragraphs[0].Paragraph.Runs[0].Style.Bold);
    }

    [Fact]
    public void Merging_and_splitting_cells_is_lossless_and_keeps_edited_merged_text()
    {
        var table = Table.Create(2, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Paragraphs = [new Paragraph("alpha")] });
        table = table.SetCell(0, 1, table.Rows[0][1] with { Paragraphs = [new Paragraph("beta")] });
        var merged = table.MergeCells(0, 0, 1, 2);
        Assert.True(merged.IsCovered(0, 1));
        Assert.Equal("alpha\nbeta", string.Join("\n", merged.Rows[0][0].Paragraphs.Select(p => p.Text)));
        var split = merged.SplitCell(0, 0);
        Assert.Equal("alpha", split.Rows[0][0].Paragraphs[0].Text);
        Assert.Equal("beta", split.Rows[0][1].Paragraphs[0].Text);
        merged = merged.SetCell(0, 0, merged.Rows[0][0] with { Paragraphs = [new Paragraph("edited")] });
        Assert.Equal("edited", merged.SplitCell(0, 0).Rows[0][0].Paragraphs[0].Text);
        new FlowDocument([split]).Validate();
    }

    [Fact]
    public void Editing_across_table_cells_preserves_table_structure()
    {
        var table = Table.Create(1, 2);
        table = table.SetCell(0, 0, table.Rows[0][0] with { Paragraphs = [new Paragraph("alpha")] });
        table = table.SetCell(0, 1, table.Rows[0][1] with { Paragraphs = [new Paragraph("beta")] });
        var editor = new EditorSession(new FlowDocument([table, new Paragraph("end")]));
        editor.Select(2, 8);
        editor.InsertText("X");
        var edited = Assert.IsType<Table>(editor.Document.Blocks[0]);
        Assert.Equal(2, edited.ColumnCount);
        Assert.Equal("alX", edited.Rows[0][0].Paragraphs[0].Text);
        Assert.Equal("ta", edited.Rows[0][1].Paragraphs[0].Text);
        editor.Document.Validate();
        editor.Undo(); Assert.Same(table, editor.Document.Blocks[0]);
    }

    [Fact]
    public void Replacing_the_entire_document_removes_table_and_section_structure()
    {
        var document = new FlowDocument([new Section { Blocks = [new Paragraph("before"), Table.Create(2, 2)] }, new Paragraph("after")]);
        var editor = new EditorSession(document);
        editor.SelectAll(); editor.InsertText("replacement");
        Assert.Single(editor.Document.Blocks);
        Assert.IsType<Paragraph>(editor.Document.Blocks[0]);
        Assert.Equal("replacement", editor.Index.Text);
        editor.Undo(); Assert.Same(document, editor.Document);
    }

    [Fact]
    public void History_limit_drops_oldest_states()
    {
        var editor = new EditorSession { UndoLimit = 2 };
        editor.InsertText("a"); editor.InsertText("b"); editor.InsertText("c");
        editor.Undo(); editor.Undo(); editor.Undo();
        Assert.Equal("a", editor.Index.Text);
        Assert.False(editor.CanUndo);
    }
}
