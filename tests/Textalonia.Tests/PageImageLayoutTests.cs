using System.Collections.Immutable;
using SkiaSharp;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.Imaging;
using Textalonia.Controls;
using Textalonia.Layout;
using Textalonia.Model;
using Textalonia.Rendering;
using Xunit;
using ImageDrawing = Textalonia.Rendering.ImageDrawing;

namespace Textalonia.Tests;

public class PageImageLayoutTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);
    private static InlineDescriptor Image(ImageWrapKind wrap = ImageWrapKind.Square, ImageAnchorKind anchor = ImageAnchorKind.Paragraph) => new()
    {
        Width = 100, Height = 80, Payload = new ImageInlinePayload("image"),
        Placement = new() { Anchor = anchor, Wrap = wrap, Distance = 5 }, AltText = "picture"
    };
    private static FlowDocument Document(params Block[] blocks) => new(blocks)
    {
        Sections = [new DocumentSection { PageSettings = new() { Width = 320, Height = 300, Margins = new(20, 20, 20, 20) } }]
    };
    private static Paragraph Body(InlineDescriptor image) => new([new RichRun(image),
        new RichRun(string.Join(" ", Enumerable.Repeat("words", 100)))]) { Style = new() { SpaceAfter = 0 } };

    [Fact]
    public Task Square_wrap_keeps_atomic_offsets_and_recovers_full_width_after_image() => Run(() =>
    {
        var image = Image(); var paragraph = Body(image);
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Document(paragraph));
        var visual = Assert.Single(layout.InlineVisuals());
        Assert.Equal(image, visual.Descriptor); Assert.Equal(0, visual.Position); Assert.Equal(new Rect(20, 20, 100, 80), visual.Bounds);
        var firstPage = layout.Fragments.Where(f => f.PageIndex == 0).ToArray();
        Assert.True(firstPage[0].Bounds.Left >= 125);
        Assert.Contains(firstPage, f => f.Bounds.Top >= 105 && f.Bounds.Left == 20);
        Assert.Equal(0, layout.Fragments[0].TextStart); Assert.Equal(paragraph.Length, layout.Fragments[^1].TextEnd);
        Assert.All(layout.Fragments, f => Assert.True(f.Bounds.Height < image.Height));
        for (var i = 1; i < layout.Fragments.Length; i++) Assert.Equal(layout.Fragments[i - 1].TextEnd, layout.Fragments[i].TextStart);
    });

    [Fact]
    public Task Top_bottom_page_anchor_uses_page_coordinates_and_advances_text() => Run(() =>
    {
        var image = Image(ImageWrapKind.TopBottom, ImageAnchorKind.Page) with
        { Placement = new() { Anchor = ImageAnchorKind.Page, Wrap = ImageWrapKind.TopBottom, X = 30, Y = 20, Distance = 5 } };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Document(Body(image)));
        Assert.Equal(new Point(30, 20), Assert.Single(layout.InlineVisuals()).Bounds.Position);
        Assert.True(layout.Fragments[0].Bounds.Top >= 105); Assert.Equal(20, layout.Fragments[0].Bounds.Left);
    });

    [Fact]
    public Task Contour_wrap_uses_polygon_bands_and_rotation_expands_square_exclusions() => Run(() =>
    {
        var square = Image(); var contour = square with { Placement = square.Placement! with
            { Wrap = ImageWrapKind.Contour, Contour = [new(0, 0), new(0, 1), new(1, 1)] } };
        using var engine = new PaginationEngine(); using var rectangle = engine.Paginate(Document(Body(square)));
        using var triangle = engine.Paginate(Document(Body(contour)));
        Assert.True(triangle.Fragments[0].Bounds.Left < rectangle.Fragments[0].Bounds.Left);
        var rotated = ImageDrawing.RotatedBounds(new Rect(0, 0, 100, 80), 90);
        Assert.Equal(80, rotated.Width, 5); Assert.Equal(100, rotated.Height, 5);
        Assert.Equal(new Rect(20, 10, 140, 70), ImageDrawing.CroppedSource(new Size(200, 100), new() { Left = .1, Top = .1, Right = .2, Bottom = .2 }));
    });

    [Fact]
    public Task Secondary_story_and_table_floats_have_diagnosed_inline_fallback() => Run(() =>
    {
        var image = Image(); var table = Table.Create(1, 1).SetCell(0, 0, new() { Blocks = [Body(image)] });
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Document(table));
        Assert.Equal(100, Assert.Single(layout.InlineVisuals()).Bounds.Width);
        Assert.Contains(layout.LayoutDiagnostics, d => d.Contains("uses inline placement", StringComparison.Ordinal));
        Assert.Throws<PagedOutputException>(() => new DocumentRenderer(layout));
    });

    [Fact]
    public Task Positioned_images_follow_page_arrangement_and_do_not_create_overflow_pages() => Run(() =>
    {
        var image = Image(ImageWrapKind.InFrontOfText) with { Width = 600, Height = 500 };
        var paragraph = new Paragraph([new RichRun(image), new RichRun("second")]) { Style = new() { PageBreakBefore = true } };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(Document(new Paragraph("first"), paragraph),
            options: new() { PagesPerRow = 2, PageGap = 37 });
        Assert.Equal(2, layout.Pages.Length); var visual = Assert.Single(layout.InlineVisuals());
        Assert.Equal(1, visual.PageIndex); Assert.Equal(layout.Pages[1].Bounds.Left + 20, visual.Bounds.Left);
        Assert.Equal(layout.Pages[1].Bounds, visual.Clip); Assert.Contains(layout.LayoutDiagnostics, d => d.Contains("clipped", StringComparison.Ordinal));
    });

    [Theory]
    [InlineData(ImageWrapKind.BehindText, true)]
    [InlineData(ImageWrapKind.InFrontOfText, false)]
    public Task Output_uses_shared_z_order_and_watermark_text_hook(ImageWrapKind wrap, bool imageFirst) => Run(() =>
    {
        var paragraph = new Paragraph([new RichRun(Image(wrap)), new RichRun("body")]);
        var document = Document(paragraph);
        document = document with { Sections = [document.Sections[0] with { Watermark = new() { Text = "DRAFT", FontSize = 24, Width = 200, Height = 70 } }] };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(document);
        var recorder = new Recorder(); using var renderer = new DocumentRenderer(layout, new() { InlineProvider = recorder });
        using (var drawing = new DrawingGroup().Open()) renderer.DrawPage(drawing, 0, recorder);
        Assert.Equal("DRAFT", recorder.Events[0]);
        Assert.Equal(imageFirst ? "image" : "body", recorder.Events[1]);
        Assert.Equal(imageFirst ? "body" : "image", recorder.Events[2]);
    });

    [Fact]
    public Task Ole_previews_and_image_watermarks_share_bounded_decoding_without_activating_data() => Run(() =>
    {
        var resource = new DocumentResource { Kind = DocumentResourceKind.Embedded, MediaType = "image/svg+xml",
            Data = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1' fill='red'/></svg>").ToImmutableArray() };
        var ole = new InlineDescriptor { Payload = new OleInlinePayload("ole", "preview"), Width = 40, Height = 30 };
        var document = Document(new Paragraph([new RichRun(ole)]));
        document = document with { Resources = document.Resources.Add("preview", resource).Add("ole", new() { Data = [1, 2, 3] }),
            Sections = [document.Sections[0] with { Watermark = new() { ResourceId = "preview" } }] };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(document);
        using var renderer = new DocumentRenderer(layout, new() { ImageLimits = new() { MaximumDecodedPixels = 1 } });
        Assert.Empty(renderer.Diagnostics);
        using var target = new RenderTargetBitmap(new PixelSize(320, 300), new Vector(96, 96));
        using (var drawing = target.CreateDrawingContext()) renderer.DrawPage(drawing, 0);
    });

    [Theory]
    [InlineData("<svg")]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'><path d='invalid'/></svg>")]
    public Task Invalid_svg_output_reports_tolerant_fallback_and_strict_rejection(string svg) => Run(() =>
    {
        var document = Document(new Paragraph([new RichRun(Image(ImageWrapKind.InFrontOfText))]));
        document = document with { Resources = document.Resources.Add("image", new() { MediaType = "image/svg+xml",
            Data = System.Text.Encoding.UTF8.GetBytes(svg).ToImmutableArray() }) };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(document);
        Assert.Throws<PagedOutputException>(() => new DocumentRenderer(layout));
        using var renderer = new DocumentRenderer(layout, new() { UnsupportedContent = UnsupportedContentPolicy.Tolerant });
        Assert.Contains(renderer.Diagnostics, diagnostic => diagnostic.Code == "output.image.unavailable");
        using var target = new RenderTargetBitmap(new PixelSize(320, 300), new Vector(96, 96));
        using (var drawing = target.CreateDrawingContext()) renderer.DrawPage(drawing, 0);
    });

    [Fact]
    public Task Cropped_rotated_image_and_translucent_watermark_render_their_expected_pixels() => Run(() =>
    {
        using var bitmap = new SKBitmap(20, 10);
        bitmap.Erase(SKColors.Red);
        for (var y = 0; y < 10; y++) for (var x = 10; x < 20; x++) bitmap.SetPixel(x, y, SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var descriptor = Image(ImageWrapKind.InFrontOfText, ImageAnchorKind.Page) with { Width = 80, Height = 40,
            Placement = new() { Anchor = ImageAnchorKind.Page, Wrap = ImageWrapKind.InFrontOfText,
                X = 50, Y = 50, Rotation = 90, Crop = new() { Left = .5 } } };
        var document = Document(new Paragraph([new RichRun(descriptor)]));
        document = document with { Resources = document.Resources.Add("image", new() { MediaType = "image/png", Data = encoded.ToArray().ToImmutableArray() }),
            Sections = [document.Sections[0] with { Watermark = new() { ResourceId = "image", Width = 40, Height = 40, Rotation = 0, Opacity = .5 } }] };
        using var engine = new PaginationEngine(); using var layout = engine.Paginate(document);
        using var renderer = new DocumentRenderer(layout);
        using var target = new RenderTargetBitmap(new PixelSize(320, 300), new Vector(96, 96));
        using (var drawing = target.CreateDrawingContext()) renderer.DrawPage(drawing, 0);
        using var stream = new MemoryStream(); target.Save(stream, PngBitmapEncoderOptions.Default);
        using var pixels = SKBitmap.Decode(stream.ToArray());
        Assert.Equal(SKColors.Blue, pixels.GetPixel(90, 35));
        Assert.Equal(SKColors.White, pixels.GetPixel(55, 70));
        var watermark = pixels.GetPixel(150, 150);
        Assert.Equal(255, watermark.Red); Assert.InRange(watermark.Green, 126, 129); Assert.InRange(watermark.Blue, 126, 129);
    });

    [Fact]
    public Task Removing_float_invalidates_following_paragraph_wrap_checkpoints() => Run(() =>
    {
        var anchor = new Paragraph([new RichRun(Image())]);
        var body = new Paragraph(string.Join(" ", Enumerable.Repeat("words", 100)));
        var document = Document(anchor, body);
        using var engine = new PaginationEngine(); using var before = engine.Paginate(document);
        Assert.True(before.Fragments.First(f => f.ParagraphId == body.Id).Bounds.Left > 20);
        using var after = engine.Paginate(document with { Blocks = [anchor with { Runs = [] }, body] });
        Assert.Equal(20, after.Fragments.First(f => f.ParagraphId == body.Id).Bounds.Left);
        Assert.Empty(after.InlineVisuals());
    });

    private sealed class Recorder : IInlinePrintProvider, IInlinePrintRepresentation, IPageTextRenderer
    {
        public List<string> Events { get; } = [];
        public IInlinePrintRepresentation TryCreateRepresentation(FlowDocument document, InlineDescriptor descriptor) => this;
        public void Draw(DrawingContext context, Rect bounds) => Events.Add("image");
        public void Dispose() { }
        public void DrawLine(DrawingContext context, TextLine line, Point origin)
        {
            var text = string.Concat(line.TextRuns.Where(r => r is not InlineObjectRun).Select(r => r.Text.ToString()));
            if (text.Length > 0) Events.Add(text);
        }
    }
}
