using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Textalonia.Export;
using Textalonia.Layout;
using Textalonia.Model;
using Textalonia.Pdf.Skia;
using Textalonia.Rendering;
using Xunit;

namespace Textalonia.Tests;

public sealed class PdfExporterTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Func<Task> test) => fixture.Session.Dispatch(async () => { await test(); return true; }, CancellationToken.None);
    private static readonly TextStyle Font = TextStyle.Default with { FontFamily = null, FontSize = 16 };
    private static readonly ParagraphStyle Lines = ParagraphStyle.Default with { SpaceBefore = 0, SpaceAfter = 4, WidowControl = false };
    private static Paragraph P(string text) => new(text, Font) { Style = Lines };

    internal static FlowDocument Sample()
    {
        using var surface = SKSurface.Create(new SKImageInfo(8, 8));
        surface.Canvas.Clear(SKColors.Transparent);
        using (var paint = new SKPaint { Color = new SKColor(0, 120, 220, 160) }) surface.Canvas.DrawCircle(4, 4, 4, paint);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var inline = new InlineDescriptor { Payload = new ImageInlinePayload("photo"), Width = 48, Height = 48, AltText = "Transparent blue circle" };
        var header = new DocumentStory { Kind = DocumentStoryKind.Header, Blocks = [P("Shared snapshot header")] };
        var footer = new DocumentStory { Kind = DocumentStoryKind.Footer, Blocks = [new Paragraph([
            new RichRun("Page ", Font), new RichRun(InlineDescriptor.PageField(PageFieldKind.Page), Font), new RichRun(" / ", Font),
            new RichRun(InlineDescriptor.PageField(PageFieldKind.NumPages), Font)]) { Style = Lines }] };
        var table = Table.Create(2, 2).SetCell(0, 0, new TableCell { Blocks = [P("Table cell")] })
            .SetCell(0, 1, new TableCell { Blocks = [P("Clipped cell text wraps inside its exact measured cell.")] })
            .SetCell(1, 0, new TableCell { Blocks = [new Paragraph([new RichRun(inline, Font)])] })
            .SetCell(1, 1, new TableCell { Blocks = [P("Vector borders")] });
        return new FlowDocument([
            P("Unicode caf\u00E9 \u0395\u03BB\u03BB\u03B7\u03BD\u03B9\u03BA\u03AC \u041F\u0440\u0438\u0432\u0435\u0442"),
            P("office e\u0301"), P("\u0627\u0644\u0639\u0631\u0628\u064A\u0629"), P("\u05E2\u05D1\u05E8\u05D9\u05EA"),
            new Paragraph([new RichRun("Working PDF link", Font with { Hyperlink = "https://example.com/report" })]) { Style = Lines }, table,
            P("Second physical page") with { Style = Lines with { PageBreakBefore = true } }])
        {
            Stories = new[] { header, footer }.ToImmutableDictionary(s => s.Id),
            Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("photo", new DocumentResource
            { Kind = DocumentResourceKind.Embedded, MediaType = "image/png", Data = png.ToArray().ToImmutableArray() }),
            Sections = [new DocumentSection { PageSettings = new PageSettings { Width = 480, Height = 640,
                Margins = new EdgeInsets(40, 70, 40, 70) }, HeaderFooter = new()
                { PrimaryHeader = new() { StoryId = header.Id, LinkToPrevious = false },
                  PrimaryFooter = new() { StoryId = footer.Id, LinkToPrevious = false }, HeaderDistance = 15, FooterDistance = 15 } }]
        };
    }

    [Fact]
    public Task Pdf_replays_positioned_text_images_vectors_links_metadata_and_page_fields() => Run(async () =>
    {
        var document = Sample();
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(document, options: new() { PagesPerRow = 2 });
        using var renderer = new DocumentRenderer(snapshot);
        using var destination = new MemoryStream();
        var result = await new PdfExporter().ExportAsync(renderer, destination, new() { Metadata = new() { Title = "DX-04 output fixture", Author = "Textalonia" } });
        Assert.Equal(2, result.PageCount);
        Assert.True(destination.CanWrite);
        Assert.Same(document, snapshot.Document);
        var pdf = Encoding.Latin1.GetString(destination.ToArray());
        Assert.StartsWith("%PDF-", pdf);
        Assert.Equal(2, Regex.Matches(pdf, @"/Type /Page\b").Count);
        Assert.Equal(2, Regex.Matches(pdf, @"/MediaBox \[0 0 360 480\]").Count);
        Assert.Contains("/Title (DX-04 output fixture)", pdf);
        Assert.Contains("/URI (https://example.com/report)", pdf);
        Assert.Contains("/FontFile2", pdf);
        Assert.Contains("/ToUnicode", pdf);
        Assert.Contains("/Subtype /Image", pdf);
        Assert.Contains("/SMask", pdf);
        var streams = DecodeStreams(destination.ToArray());
        Assert.Contains(".75 0 0 -.75", streams);
        Assert.Contains("/ActualText <FEFF00650301>", streams);
        if (OperatingSystem.IsWindows()) Assert.Contains("/ActualText <FEFF06", streams);
        Assert.Contains("<0031>", streams); // The temporary PAGE label survives recording and owns a Unicode mapping.
        Assert.DoesNotContain("/StructTreeRoot", pdf);
        var directory = Path.GetFullPath("../../../../../artifacts/dx04-pdf", AppContext.BaseDirectory);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "fixture.pdf"), destination.ToArray());
        File.WriteAllText(Path.Combine(directory, "geometry.txt"), string.Join(Environment.NewLine,
            Enumerable.Range(0, renderer.PageCount).SelectMany(page => renderer.GetTextLines(page).Select(line =>
                $"page={page + 1} bounds={line.Bounds} baseline={line.Baseline} text={line.Text}"))));
        using var preview = new RenderTargetBitmap(new PixelSize(480, 640), new Vector(96, 96));
        preview.Render(new PageVisual(renderer));
        preview.Save(Path.Combine(directory, "preview-page1.png"), PngBitmapEncoderOptions.Default);
    });

    [Fact]
    public Task Page_ranges_and_invalid_ranges_are_checked_without_reflow() => Run(async () =>
    {
        using var engine = new PaginationEngine(); using var snapshot = engine.Paginate(Sample());
        using var renderer = new DocumentRenderer(snapshot); using var destination = new MemoryStream();
        var exporter = new PdfExporter();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => exporter.ExportAsync(renderer, destination, new() { Pages = new(1, 3) }));
        Assert.Equal(0, destination.Length);
        var result = await exporter.ExportAsync(renderer, destination, new() { Pages = new(2, 2) });
        Assert.Equal(1, result.PageCount);
        Assert.Single(Regex.Matches(Encoding.Latin1.GetString(destination.ToArray()), @"/Type /Page\b"));
    });

    [Fact]
    public Task Cancellation_during_page_progress_leaves_the_destination_open_and_unwritten() => Run(async () =>
    {
        using var engine = new PaginationEngine(); using var snapshot = engine.Paginate(Sample());
        using var renderer = new DocumentRenderer(snapshot); using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PdfExporter().ExportAsync(renderer, destination,
            progress: new CancelProgress(cancellation), cancellationToken: cancellation.Token));
        Assert.Equal(0, destination.Length); Assert.True(destination.CanWrite);
        Assert.Equal(2, renderer.PageCount);
    });

    [Fact]
    public Task Destination_failure_propagates_without_closing_or_rewinding_caller_stream() => Run(async () =>
    {
        using var engine = new PaginationEngine(); using var snapshot = engine.Paginate(new FlowDocument([P("write failure")]));
        using var renderer = new DocumentRenderer(snapshot); using var stream = new FailedStream();
        await Assert.ThrowsAsync<IOException>(() => new PdfExporter().ExportAsync(renderer, stream));
        Assert.False(stream.Closed); Assert.True(stream.CanWrite);
    });

    [Fact]
    public void Only_standard_pdf_capabilities_are_advertised()
    {
        Assert.Equal(new PagedExportCapabilities(true, true, true, true), new PdfExporter().Capabilities);
    }

    private static string DecodeStreams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf); var result = new StringBuilder();
        foreach (Match match in Regex.Matches(text, @"(<<[^>]+>>)\s*stream\r?\n(.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            if (!match.Groups[1].Value.Contains("FlateDecode") || match.Groups[1].Value.Contains("Length1")) continue;
            using var compressed = new MemoryStream(Encoding.Latin1.GetBytes(match.Groups[2].Value));
            using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(inflate, Encoding.Latin1); result.Append(reader.ReadToEnd());
        }
        return result.ToString();
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<PagedOutputProgress>
    { public void Report(PagedOutputProgress value) { if (value.CompletedPages == 1) cancellation.Cancel(); } }
    private sealed class FailedStream : MemoryStream
    {
        public bool Closed { get; private set; }
        public override bool CanSeek => false;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("Write failed."));
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Write failed.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException(new IOException("Write failed."));
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    private sealed class PageVisual : Visual
    {
        private readonly DocumentRenderer _renderer;
        public PageVisual(DocumentRenderer renderer) { _renderer = renderer; Bounds = new Rect(0, 0, 480, 640); }
        public override void Render(DrawingContext context) => _renderer.DrawPage(context, 0);
    }
}
