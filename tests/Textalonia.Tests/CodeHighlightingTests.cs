using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public sealed class CodeHighlightingTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);

    [Fact]
    public Task Highlighting_changes_presentation_without_changing_selection_document_or_export() => Run(() =>
    {
        var adapter = new TestHighlighter();
        var viewer = new MarkdownViewer();
        MarkdownViewerTests.Pump(viewer.UpdateMarkdownAsync("```csharp\nlet value = 42;\n```"));
        var document = viewer.Document;
        var serialized = new MarkdownDocumentFormat().Serialize(document);
        viewer.SelectAll();
        var selected = viewer.SelectedText;
        viewer.CodeHighlighter = adapter;
        MarkdownViewerTests.Pump(viewer.WaitForHighlightingAsync());
        Assert.Null(viewer.HighlightError);
        Assert.False(viewer.IsHighlighting);
        Assert.Same(document, viewer.Document);
        Assert.Equal(selected, viewer.SelectedText);
        Assert.Equal(serialized, new MarkdownDocumentFormat().Serialize(viewer.Document));
        var styled = new DocumentIndex(viewer.PresentationDocument).Paragraphs[0].Paragraph;
        Assert.Equal("#FF1234", styled.Runs[0].Style.Foreground);
        Assert.Equal(document.Text, viewer.PresentationDocument.Text);
        Assert.NotEqual("#FF1234", new DocumentIndex(document).Paragraphs[0].Paragraph.Runs[0].Style.Foreground);
        viewer.CodeHighlighter = null;
        Assert.Equal(document.Text, viewer.PresentationDocument.Text);
        Assert.NotEqual("#FF1234", new DocumentIndex(viewer.PresentationDocument).Paragraphs[0].Paragraph.Runs[0].Style.Foreground);
    });

    [Fact]
    public Task Unchanged_code_is_cached_and_code_changes_are_recomputed() => Run(() =>
    {
        var adapter = new TestHighlighter();
        var viewer = new MarkdownViewer { CodeHighlighter = adapter };
        MarkdownViewerTests.Pump(viewer.UpdateMarkdownAsync("Intro\n\n```cs\nlet value\n```"));
        MarkdownViewerTests.Pump(viewer.WaitForHighlightingAsync());
        Assert.Equal(1, adapter.Calls);
        MarkdownViewerTests.Pump(viewer.UpdateMarkdownAsync("Changed intro\n\n```cs\nlet value\n```"));
        MarkdownViewerTests.Pump(viewer.WaitForHighlightingAsync());
        Assert.Equal(1, adapter.Calls);
        MarkdownViewerTests.Pump(viewer.UpdateMarkdownAsync("Changed intro\n\n```cs\nlet other\n```"));
        MarkdownViewerTests.Pump(viewer.WaitForHighlightingAsync());
        Assert.Equal(2, adapter.Calls);
    });

    [Theory]
    [InlineData(-1, 2, "#FF1234")]
    [InlineData(0, 500, "#FF1234")]
    [InlineData(0, 2, "not-a-color")]
    public Task Bad_adapter_ranges_and_styles_fail_without_losing_the_document(int start, int length, string color) => Run(() =>
    {
        var viewer = new MarkdownViewer { CodeHighlighter = new BadHighlighter(start, length, color) };
        MarkdownViewerTests.Pump(viewer.UpdateMarkdownAsync("```unknown\noriginal code\n```"));
        MarkdownViewerTests.Pump(viewer.WaitForHighlightingAsync());
        Assert.IsType<InvalidOperationException>(viewer.HighlightError);
        Assert.False(viewer.IsHighlighting);
        Assert.Null(viewer.ParseError);
        Assert.Equal("original code", viewer.Document.Text);
        Assert.Same(viewer.Document, viewer.PresentationDocument);
    });

    [Fact]
    public Task Old_highlight_cannot_replace_a_new_document_after_cancellation() => Run(() =>
    {
        var adapter = new DelayedHighlighter();
        var viewer = new MarkdownViewer { CodeHighlighter = adapter };
        MarkdownViewerTests.Pump(viewer.UpdateMarkdownAsync("```cs\nold code\n```"));
        var oldHighlight = viewer.WaitForHighlightingAsync();
        MarkdownViewerTests.Pump(adapter.Started.Task);
        MarkdownViewerTests.Pump(viewer.UpdateMarkdownAsync("New plain paragraph"));
        MarkdownViewerTests.Pump(viewer.WaitForHighlightingAsync());
        adapter.Result.SetResult([new(0, 3, "keyword")]);
        MarkdownViewerTests.Pump(oldHighlight);
        Assert.Equal("New plain paragraph", viewer.Document.Text);
        Assert.Equal(viewer.Document.Text, viewer.PresentationDocument.Text);
        Assert.Null(viewer.HighlightError);
        Assert.False(viewer.IsHighlighting);
    });

    [Fact]
    public async Task Cache_has_entry_and_byte_bounds_and_oversized_code_is_left_plain()
    {
        var cache = new CodeHighlightCache();
        var adapter = new TestHighlighter();
        for (var i = 0; i < 80; i++)
            await cache.HighlightAsync(Code("code " + i), adapter, CancellationToken.None);
        Assert.InRange(cache.Count, 1, 64);
        Assert.InRange(cache.Bytes, 1, 1024 * 1024);
        var calls = adapter.Calls;
        var large = Code(new string('x', CodeHighlightCache.MaximumCodeLength + 1));
        var rendered = await cache.HighlightAsync(large, adapter, CancellationToken.None);
        Assert.Equal(calls, adapter.Calls);
        Assert.Same(large.Blocks[0], rendered.Blocks[0]);
    }

    [Fact]
    public async Task Unknown_languages_can_return_no_tokens_and_multiline_tokens_keep_exact_text()
    {
        var cache = new CodeHighlightCache();
        var unknown = Code("original", "unknown");
        var plain = await cache.HighlightAsync(unknown, new UnknownHighlighter(), CancellationToken.None);
        Assert.Equal(unknown.Text, plain.Text);
        var source = new FlowDocument([new Section { Semantic = SectionSemantic.CodeBlock, CodeLanguage = "cs", Blocks = [new Paragraph("ab"), new Paragraph("cd")] }]);
        var result = await cache.HighlightAsync(source, new BadHighlighter(1, 3, "#FF1234"), CancellationToken.None);
        Assert.Equal("ab\ncd", result.Text);
        var paragraphs = new DocumentIndex(result).Paragraphs;
        Assert.Equal("#FF1234", paragraphs[0].Paragraph.Runs[^1].Style.Foreground);
        Assert.Equal("#FF1234", paragraphs[1].Paragraph.Runs[0].Style.Foreground);
    }

    [Fact]
    public Task Host_document_replacement_cancels_old_highlighting_and_resets_busy_state() => Run(() =>
    {
        var adapter = new DelayedHighlighter();
        var viewer = new MarkdownViewer { CodeHighlighter = adapter };
        MarkdownViewerTests.Pump(viewer.UpdateMarkdownAsync("```cs\nold code\n```"));
        var old = viewer.WaitForHighlightingAsync();
        MarkdownViewerTests.Pump(adapter.Started.Task);
        viewer.Document = FlowDocument.FromText("host supplied document");
        MarkdownViewerTests.Pump(viewer.WaitForHighlightingAsync());
        adapter.Result.SetResult([new(0, 3, "keyword")]);
        MarkdownViewerTests.Pump(old);
        Assert.False(viewer.IsHighlighting);
        Assert.Null(viewer.HighlightError);
        Assert.Equal("host supplied document", viewer.PresentationDocument.Text);
    });
    [Fact]
    public Task Enabling_and_disabling_highlighting_keeps_the_readers_viewport() => Run(() =>
    {
        var viewer = new MarkdownViewer();
        var window = new Window { Width = 700, Height = 400, Content = viewer };
        try
        {
            window.Show();
            var source = string.Join("\n\n", Enumerable.Range(0, 100).Select(i => $"Paragraph {i}.")) + "\n\n```cs\nlet code\n```";
            MarkdownViewerTests.Pump(viewer.UpdateMarkdownAsync(source));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            viewer.Scroller!.Offset = new Vector(0, 320);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var offset = viewer.Scroller.Offset.Y;
            viewer.CodeHighlighter = new TestHighlighter();
            MarkdownViewerTests.Pump(viewer.WaitForHighlightingAsync());
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.InRange(Math.Abs(viewer.Scroller.Offset.Y - offset), 0, 1);
            viewer.CodeHighlighter = null;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.InRange(Math.Abs(viewer.Scroller.Offset.Y - offset), 0, 1);
        }
        finally { window.Close(); }
    });
    private static FlowDocument Code(string text, string language = "cs") => new([new Section
    { Semantic = SectionSemantic.CodeBlock, CodeLanguage = language, Blocks = [new Paragraph(text)] }]);

    private sealed class TestHighlighter : ICodeHighlighter
    {
        private int _calls;
        public int Calls => _calls;
        public ValueTask<IReadOnlyList<CodeHighlightToken>> TokenizeAsync(string? language, string code, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _calls); return ValueTask.FromResult<IReadOnlyList<CodeHighlightToken>>([new(0, Math.Min(3, code.Length), "keyword")]); }
        public CodeHighlightStyle? GetStyle(string? language, string tokenKind) => new(Foreground: "#FF1234", Bold: true);
    }
    private sealed class BadHighlighter(int start, int length, string color) : ICodeHighlighter
    {
        public ValueTask<IReadOnlyList<CodeHighlightToken>> TokenizeAsync(string? language, string code, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CodeHighlightToken>>([new(start, length, "keyword")]);
        public CodeHighlightStyle? GetStyle(string? language, string tokenKind) => new(Foreground: color);
    }
    private sealed class UnknownHighlighter : ICodeHighlighter
    {
        public ValueTask<IReadOnlyList<CodeHighlightToken>> TokenizeAsync(string? language, string code, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<CodeHighlightToken>>([]);
        public CodeHighlightStyle? GetStyle(string? language, string tokenKind) => null;
    }
    private sealed class DelayedHighlighter : ICodeHighlighter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<CodeHighlightToken>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyList<CodeHighlightToken>> TokenizeAsync(string? language, string code, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); return new(Result.Task); }
        public CodeHighlightStyle? GetStyle(string? language, string tokenKind) => new(Foreground: "#FF1234");
    }
}
