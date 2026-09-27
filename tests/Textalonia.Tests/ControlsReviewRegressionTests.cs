using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public sealed class ControlsReviewRegressionTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Fact]
    public async Task Code_highlighting_visits_nested_table_cells_and_preserves_hidden_merge_history()
    {
        static Section Code(string text) => new() { Semantic = SectionSemantic.CodeBlock, CodeLanguage = "cs", Blocks = [new Paragraph(text)] };
        var merged = Table.Create(1, 2)
            .SetCell(0, 0, new TableCell { Blocks = [Code("first")] })
            .SetCell(0, 1, new TableCell { Blocks = [Code("second")] })
            .MergeCells(0, 0, 1, 2);
        var outer = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Section { Blocks = [merged] }] });
        var original = new FlowDocument([outer]);
        var highlighter = new TableCodeHighlighter();
        var presentation = await new CodeHighlightCache().HighlightAsync(original, highlighter, CancellationToken.None);

        Assert.Equal(2, highlighter.Calls);
        Assert.Equal(original.Text, presentation.Text);
        Assert.All(new DocumentIndex(presentation).Paragraphs, p => Assert.Equal("#FF1234", p.Paragraph.Runs[0].Style.Foreground));
        Assert.All(new DocumentIndex(original).Paragraphs, p => Assert.Null(p.Paragraph.Runs[0].Style.Foreground));
        var outerPresentation = Assert.IsType<Table>(presentation.Blocks[0]);
        var section = Assert.IsType<Section>(outerPresentation.Rows[0][0].Blocks[0]);
        var mergedPresentation = Assert.IsType<Table>(section.Blocks[0]);
        Assert.Same(merged.Rows[0][1], mergedPresentation.Rows[0][1]);
        Assert.Equal(merged.Rows[0][0].MergeOriginalBlocks, mergedPresentation.Rows[0][0].MergeOriginalBlocks);
        presentation.Validate();
    }

    [Fact]
    public Task Tab_navigates_cells_with_nested_sections_without_inserting_a_row() => fixture.Session.Dispatch(() =>
    {
        var first = new Paragraph("first");
        var second = new Paragraph("second");
        var table = Table.Create(1, 2)
            .SetCell(0, 0, new TableCell { Blocks = [new Section { Blocks = [first] }] })
            .SetCell(0, 1, new TableCell { Blocks = [new Section { Blocks = [second] }] });
        var original = new FlowDocument([table]);
        var editor = new TextaloniaEditor { Document = original, ShowToolbar = false };
        var window = Open(editor);
        try
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None);
            Assert.Equal(second.Id, editor.Session.Index.At(editor.SelectionEnd).Paragraph.Id);
            Assert.True(editor.Session.Selection.IsEmpty);
            Assert.Same(original, editor.Document);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Shift_tab_collapses_selection_at_the_previous_visible_cell() => fixture.Session.Dispatch(() =>
    {
        var table = Table.Create(1, 3)
            .SetCell(0, 0, new TableCell { Blocks = [new Paragraph("first")] })
            .SetCell(0, 1, new TableCell { Blocks = [new Paragraph("covered")] })
            .SetCell(0, 2, new TableCell { Blocks = [new Paragraph("last")] })
            .MergeCells(0, 0, 1, 2);
        var original = new FlowDocument([table]);
        var editor = new TextaloniaEditor { Document = original, ShowToolbar = false };
        var window = Open(editor);
        try
        {
            var end = editor.Session.Index.Tree.Locate(table.Rows[0][2].Id).Start;
            editor.Session.Select(end, end);
            window.KeyPress(Key.Tab, RawInputModifiers.Shift);
            Assert.Equal(0, editor.SelectionEnd);
            Assert.True(editor.Session.Selection.IsEmpty);
            Assert.Same(original, editor.Document);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    [Fact]
    public Task Tab_at_the_table_row_limit_leaves_the_document_unchanged() => fixture.Session.Dispatch(() =>
    {
        var original = new FlowDocument([Table.Create(1000, 1)]);
        var editor = new TextaloniaEditor { Document = original };
        var surface = new DocumentSurface { Editor = editor };
        var keyboard = new DefaultKeyboardComponent();
        keyboard.Attach(new DocumentInputContext(surface, editor));
        try
        {
            editor.Session.Select(editor.Session.Index.Length, editor.Session.Index.Length);
            var key = new KeyEventArgs { Key = Key.Tab };
            keyboard.KeyDown(key);
            Assert.False(key.Handled);
            Assert.Same(original, editor.Document);
            Assert.False(editor.Session.CanUndo);
        }
        finally { keyboard.Detach(); surface.Layout.Dispose(); }
    }, CancellationToken.None);

    [Fact]
    public Task Tab_enters_a_neighboring_cell_containing_only_a_nested_table() => fixture.Session.Dispatch(() =>
    {
        var nested = Table.Create(1, 1).SetCell(0, 0, new TableCell { Blocks = [new Paragraph("nested")] });
        var table = Table.Create(1, 2)
            .SetCell(0, 0, new TableCell { Blocks = [new Paragraph("first")] })
            .SetCell(0, 1, new TableCell { Blocks = [nested] });
        var original = new FlowDocument([table]);
        var editor = new TextaloniaEditor { Document = original, ShowToolbar = false };
        var window = Open(editor);
        try
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None);
            Assert.Equal(nested.Id, editor.Session.CurrentCell()!.Value.Table.Id);
            Assert.True(editor.Session.Selection.IsEmpty);
            Assert.Same(original, editor.Document);
        }
        finally { window.Close(); }
    }, CancellationToken.None);

    private static Window Open(TextaloniaEditor editor)
    {
        var window = new Window { Width = 700, Height = 400, Content = editor };
        window.Show(); window.UpdateLayout(); editor.FocusDocument(); Dispatcher.UIThread.RunJobs();
        return window;
    }

    private sealed class TableCodeHighlighter : ICodeHighlighter
    {
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyList<CodeHighlightToken>> TokenizeAsync(string? language, string code, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult<IReadOnlyList<CodeHighlightToken>>([new(0, code.Length, "code")]);
        }
        public CodeHighlightStyle? GetStyle(string? language, string tokenKind) => new(Foreground: "#FF1234");
    }
}
