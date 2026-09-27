using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Textalonia.Controls;
using Textalonia.Export;
using Textalonia.Model;
using Textalonia.Rendering;
using Xunit;

namespace Textalonia.Tests;

public sealed class OutputUiTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);

    [Fact]
    public Task Output_snapshot_is_physical_immutable_and_survives_the_paginator() => Run(() =>
    {
        var editor = new TextaloniaEditor
        {
            Document = FlowDocument.FromText(string.Join("\n", Enumerable.Repeat("Snapshot body", 100))),
            ViewMode = DocumentViewMode.Draft, Zoom = 2, IsReadOnly = true
        };
        editor.Session.Select(2, 7);
        var document = editor.Document; var selection = editor.Session.Selection; var revision = editor.Session.Revision;
        using var renderer = editor.CreateOutputRenderer();
        Assert.True(renderer.PageCount > 1);
        Assert.Equal(selection, editor.Session.Selection); Assert.Equal(revision, editor.Session.Revision);
        Assert.Equal(DocumentViewMode.Draft, editor.ViewMode); Assert.Equal(2, editor.Zoom);
        var preview = new DocumentPrintPreview { Renderer = renderer, Zoom = .5 };
        preview.Measure(Size.Infinity);
        Assert.Equal(renderer.Snapshot.Pages[0].Bounds.Size * .5, preview.DesiredSize);
        editor.Document = FlowDocument.FromText("Replacement");
        Assert.Same(document, renderer.Snapshot.Document);
        using var bitmap = new RenderTargetBitmap(new PixelSize(500, 700));
        using (var context = bitmap.CreateDrawingContext()) renderer.DrawPage(context, 0);
        preview.PageIndex = renderer.PageCount - 1;
        preview.Measure(Size.Infinity);
        Assert.True(preview.DesiredSize.Height > 0);
    });

    [Fact]
    public Task Export_uses_a_captured_snapshot_and_does_not_own_destination_or_edit_state() => Run(() =>
    {
        var editor = new TextaloniaEditor { Document = FlowDocument.FromText("Export me"), IsReadOnly = true };
        editor.Session.Select(1, 4);
        var revision = editor.Session.Revision; var selection = editor.Session.Selection;
        var exporter = new RecordingExporter(); editor.PdfExporter = exporter;
        using var destination = new MemoryStream();
        var result = editor.ExportPdfAsync(destination).GetAwaiter().GetResult();
        Assert.Equal(1, result.PageCount); Assert.Equal("Export me", exporter.Text);
        Assert.True(destination.CanWrite); Assert.Equal(3, destination.Length);
        Assert.Equal(revision, editor.Session.Revision); Assert.Equal(selection, editor.Session.Selection);
        Assert.False(editor.Session.CanUndo);
        Assert.Throws<ObjectDisposedException>(() => exporter.Renderer!.Validate());
    });

    [Fact]
    public Task Cancelled_or_failed_exports_leave_editor_and_caller_stream_available() => Run(() =>
    {
        var editor = new TextaloniaEditor { Text = "Original", PdfExporter = new RecordingExporter { Fail = true } };
        var document = editor.Document;
        using var destination = new MemoryStream();
        Assert.Throws<IOException>(() => editor.ExportPdfAsync(destination).GetAwaiter().GetResult());
        Assert.Same(document, editor.Document); Assert.True(destination.CanWrite);
        var exporter = new RecordingExporter(); editor.PdfExporter = exporter;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => editor.ExportPdfAsync(destination, cancellationToken: cancelled.Token).GetAwaiter().GetResult());
        Assert.Null(exporter.Renderer); Assert.Same(document, editor.Document); Assert.True(destination.CanWrite);
    });

    [Fact]
    public Task Output_toolbar_remains_available_for_read_only_documents() => Run(() =>
    {
        var toolbar = new TextaloniaToolbar { Editor = new TextaloniaEditor { IsReadOnly = true } };
        var output = Assert.Single(toolbar.Children.OfType<Button>(), button => Equals(button.Content, "Output"));
        Assert.True(output.IsEnabled); Assert.NotNull(output.Flyout);
    });

    private sealed class RecordingExporter : IPagedDocumentExporter
    {
        public bool Fail { get; init; }
        public string? Text { get; private set; }
        public DocumentRenderer? Renderer { get; private set; }
        public PagedExportCapabilities Capabilities { get; } = new(true, true, true, true);
        public Task<PagedOutputResult> ExportAsync(DocumentRenderer renderer, Stream destination, PagedExportOptions? options = null,
            IProgress<PagedOutputProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Renderer = renderer; Text = renderer.Snapshot.Document.Text;
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("Test write failure");
            destination.Write([1, 2, 3]);
            return Task.FromResult(new PagedOutputResult(renderer.PageCount, []));
        }
    }
}
