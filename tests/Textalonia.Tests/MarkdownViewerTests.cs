using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public sealed class MarkdownViewerTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action test) => fixture.Session.Dispatch(test, CancellationToken.None);
    internal static void Pump(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(10))
        { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        Assert.True(task.IsCompleted, "Timed out while pumping the UI dispatcher.");
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task Markdown_uses_existing_template_selection_and_accessible_text() => Run(() =>
    {
        var viewer = new MarkdownViewer();
        var window = new Window { Width = 700, Height = 400, Content = viewer };
        try
        {
            window.Show();
            Pump(viewer.UpdateMarkdownAsync("# Title\n\nSelect **this** text.\n\n[Link](https://example.com)"));
            window.UpdateLayout();
            Assert.Null(viewer.ParseError);
            Assert.False(viewer.IsParsing);
            Assert.True(viewer.IsReadOnly);
            Assert.False(viewer.ShowToolbar);
            Assert.Single(viewer.GetVisualDescendants().OfType<DocumentSurface>());
            Assert.Equal("Title\nSelect this text.\nLink", viewer.Document.Text);
            viewer.Session.Select(6, 12);
            Assert.Equal("Select", viewer.SelectedText);
            Assert.Contains(viewer.Session.Index.Paragraphs.SelectMany(p => p.Paragraph.Runs), r => r.Style.Hyperlink == "https://example.com");
            Assert.NotNull(viewer.Accessibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Old_parse_cannot_overwrite_newer_source_even_if_parser_ignores_cancellation() => Run(() =>
    {
        var viewer = new ControlledViewer();
        viewer.Markdown = "old";
        var old = viewer.WaitForParsingAsync();
        Pump(viewer.Started("old"));
        viewer.Markdown = "latest";
        Pump(viewer.Started("latest"));
        viewer.Complete("latest", "new document");
        Pump(viewer.WaitForParsingAsync());
        viewer.Complete("old", "obsolete document");
        Pump(old);
        Assert.Equal("new document", viewer.Document.Text);
        Assert.False(viewer.IsParsing);
        Assert.Null(viewer.ParseError);
    });

    [Fact]
    public Task Append_preserves_current_selection_and_scroll_and_reuses_unchanged_blocks() => Run(() =>
    {
        var viewer = new MarkdownViewer();
        var window = new Window { Width = 700, Height = 400, Content = viewer };
        try
        {
            window.Show();
            Pump(viewer.UpdateMarkdownAsync(string.Join("\n\n", Enumerable.Range(0, 100).Select(i => $"Paragraph {i}."))));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            viewer.Scroller!.Offset = new Vector(0, 320);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var offset = viewer.Scroller.Offset.Y;
            var first = viewer.Document.Blocks[0];
            viewer.AppendMarkdown("\n\nAppended paragraph.");
            // Interaction during the parse belongs to the reader and must survive application.
            viewer.Session.Select(0, 9);
            Pump(viewer.WaitForParsingAsync());
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal("Paragraph", viewer.SelectedText);
            Assert.Same(first, viewer.Document.Blocks[0]);
            Assert.InRange(Math.Abs(viewer.Scroller.Offset.Y - offset), 0, 1);
            Assert.EndsWith("Appended paragraph.", viewer.Document.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Failed_parse_retains_last_document_and_exposes_error() => Run(() =>
    {
        var viewer = new ControlledViewer();
        viewer.Markdown = "valid"; Pump(viewer.Started("valid"));
        viewer.Complete("valid", "keep me"); Pump(viewer.WaitForParsingAsync());
        var retained = viewer.Document;
        viewer.Markdown = "invalid"; Pump(viewer.Started("invalid"));
        viewer.Fail("invalid", new FormatException("Invalid Markdown fixture."));
        Pump(viewer.WaitForParsingAsync());
        Assert.Same(retained, viewer.Document);
        Assert.IsType<FormatException>(viewer.ParseError);
        Assert.False(viewer.IsParsing);
    });

    [Fact]
    public Task Source_burst_coalesces_and_external_document_change_rejects_pending_parse() => Run(() =>
    {
        var viewer = new ControlledViewer();
        for (var i = 0; i < 100; i++) viewer.Markdown = "chunk " + i;
        Pump(viewer.Started("chunk 99"));
        Assert.Equal(1, viewer.ParseCount);
        viewer.Document = FlowDocument.FromText("host replacement");
        viewer.Complete("chunk 99", "parsed replacement");
        Pump(viewer.WaitForParsingAsync());
        Assert.Equal("host replacement", viewer.Document.Text);
        Assert.IsType<InvalidOperationException>(viewer.ParseError);
    });

    [Fact]
    public Task Selection_shifts_with_insertions_before_it() => Run(() =>
    {
        var viewer = new MarkdownViewer();
        Pump(viewer.UpdateMarkdownAsync("First\n\nSelected"));
        viewer.Session.Select(6, 14);
        Pump(viewer.UpdateMarkdownAsync("First\n\nInserted\n\nSelected"));
        Assert.Equal("Selected", viewer.SelectedText);
        Assert.Equal(15, viewer.SelectionStart);
    });

    [Fact]
    public Task Insertion_above_viewport_preserves_the_visible_text_anchor() => Run(() =>
    {
        var viewer = new MarkdownViewer();
        var window = new Window { Width = 700, Height = 400, Content = viewer };
        try
        {
            window.Show();
            var source = string.Join("\n\n", Enumerable.Range(0, 100).Select(i => $"Paragraph {i}."));
            Pump(viewer.UpdateMarkdownAsync(source));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            viewer.Scroller!.Offset = new Vector(0, 320);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var surface = viewer.GetVisualDescendants().OfType<DocumentSurface>().Single();
            var anchor = surface.Layout.Paragraphs.First(p => p.Bounds.Bottom > viewer.Scroller.Offset.Y);
            var relativeY = anchor.Origin.Y - viewer.Scroller.Offset.Y;
            var initialOffset = viewer.Scroller.Offset.Y;
            Pump(viewer.UpdateMarkdownAsync("Inserted above the reader.\n\n" + source));
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var restored = surface.Layout.Paragraphs.First(p => p.Position.Paragraph.Id == anchor.Position.Paragraph.Id);
            Assert.InRange(Math.Abs(restored.Origin.Y - viewer.Scroller.Offset.Y - relativeY), 0, 1);
            Assert.True(viewer.Scroller.Offset.Y > initialOffset);
        }
        finally { window.Close(); }
    });
    [Fact]
    public Task Appended_list_items_share_identity_and_splitting_a_list_keeps_distinct_groups() => Run(() =>
    {
        var viewer = new MarkdownViewer();
        Pump(viewer.UpdateMarkdownAsync("1. one\n2. two"));
        var originalId = viewer.Session.Index.Paragraphs[0].Paragraph.Style.ListId;
        viewer.AppendMarkdown("\n3. three");
        Pump(viewer.WaitForParsingAsync());
        Assert.All(viewer.Session.Index.Paragraphs, p => Assert.Equal(originalId, p.Paragraph.Style.ListId));
        Pump(viewer.UpdateMarkdownAsync("1. one\n\nA separating paragraph.\n\n1. two\n2. three"));
        var items = viewer.Session.Index.Paragraphs.Where(p => p.Paragraph.Style.List == ListKind.Numbered).ToArray();
        Assert.NotEqual(items[0].Paragraph.Style.ListId, items[1].Paragraph.Style.ListId);
        Assert.Equal(items[1].Paragraph.Style.ListId, items[2].Paragraph.Style.ListId);
        Assert.NotNull(items[0].Paragraph.Style.ListId);
        Assert.NotNull(items[1].Paragraph.Style.ListId);
    });

    [Fact]
    public void Snapshot_reuse_retains_new_table_cell_coordinates_after_reshaping()
    {
        var first = new TableCell { Blocks = [new Paragraph("first")] };
        var second = new TableCell { Blocks = [new Paragraph("second")] };
        var before = new FlowDocument([new Table { Rows = [[first, second]] }]);
        var after = new FlowDocument([new Table { Rows = [[first], [second]] }]);
        var reconciled = DocumentTree.ReuseSnapshot(before, after, CancellationToken.None);
        var cell = DocumentTree.For(reconciled).Locate(second.Id).Node;
        Assert.Equal(1, cell.Row);
        Assert.Equal(0, cell.Column);
        reconciled.Validate();
    }
    [Fact]
    public void Code_language_is_counted_in_visible_and_hidden_retained_metadata()
    {
        var section = new Section { Semantic = SectionSemantic.CodeBlock, CodeLanguage = "example-language", Blocks = [new Paragraph("code")] };
        var visible = new List<object>();
        DocumentTree.For(new FlowDocument([section])).Locate(section.Id).Node.VisitReferences(visible.Add);
        Assert.Contains<object>(section.CodeLanguage, visible);
        var hidden = new List<object>();
        DocumentNode.HiddenBlock(section).VisitReferences(hidden.Add);
        Assert.Contains<object>(section.CodeLanguage, hidden);
    }
    private sealed class ControlledViewer : MarkdownViewer
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<DocumentLoadResult>> _results = new();
        private int _parseCount;
        public int ParseCount => _parseCount;
        public Task Started(string source) => _started.GetOrAdd(source, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public void Complete(string source, string text) => _results[source].SetResult(new(FlowDocument.FromText(text), ConversionReport.Empty));
        public void Fail(string source, Exception error) => _results[source].SetException(error);
        protected override Task<DocumentLoadResult> ParseMarkdownAsync(string markdown, ConversionOptions? options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _parseCount);
            var result = _results.GetOrAdd(markdown, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            _started.GetOrAdd(markdown, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            return result.Task;
        }
    }
}
