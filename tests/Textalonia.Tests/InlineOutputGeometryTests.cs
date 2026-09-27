using System.Collections.Immutable;
using SkiaSharp;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Textalonia.Controls;
using Textalonia.Layout;
using Textalonia.Model;
using Textalonia.Rendering;
using Xunit;

namespace Textalonia.Tests;

public class InlineOutputGeometryTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    [Theory]
    [InlineData(Baseline.Normal, false)]
    [InlineData(Baseline.Subscript, false)]
    [InlineData(Baseline.Superscript, false)]
    [InlineData(Baseline.Normal, true)]
    [InlineData(Baseline.Subscript, true)]
    [InlineData(Baseline.Superscript, true)]
    public Task Printed_inline_bounds_match_actual_shaped_drawing_with_baseline_alignment(Baseline baseline, bool typography) =>
        fixture.Session.Dispatch(() =>
        {
            var descriptor = new InlineDescriptor { Payload = new ControlInlinePayload("printable"), Width = 24, Height = 10 };
            var textStyle = TextStyle.Default with { FontSize = 32, BaselineOffset = typography ? 8 : 0 };
            var inlineStyle = TextStyle.Default with { Baseline = baseline };
            var paragraph = new Paragraph([new RichRun("before ", textStyle), new RichRun(descriptor, inlineStyle), new RichRun(" after", textStyle)])
            { Style = new() { PageBreakBefore = true } };
            var document = new FlowDocument([new Paragraph("First page"), paragraph])
            {
                Sections = [new DocumentSection { PageSettings = new()
                { Width = 420, Height = 200, Margins = new EdgeInsets(20, 20, 20, 20) } }]
            };
            using var engine = new PaginationEngine();
            using var snapshot = engine.Paginate(document, options: new() { PagesPerRow = 2, PageGap = 53 });
            var fragment = Assert.Single(snapshot.Fragments.Where(fragment => fragment.ParagraphId == paragraph.Id));
            using var lease = fragment.Acquire();
            Assert.Equal(typography, lease.Layout.TextLines[fragment.Line.Index] is TypographyLine);

            // Record the inline rectangle at the actual origin chosen by TextLine.Draw. This is
            // independent of the snapshot overlay calculation used by print representations.
            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
            using (var scope = new InlineOutputScope((_, target, bounds) =>
            { target.DrawRectangle(Brushes.Red, null, bounds); return true; }))
                snapshot.DrawContent(context);
            var actualDrawnBounds = Assert.Single(RedRectangles(drawing, Matrix.Identity));
            var visual = Assert.Single(snapshot.InlineVisuals());
            Assert.Equal(actualDrawnBounds, visual.Bounds);

            var provider = new Provider();
            using var renderer = new DocumentRenderer(snapshot, new() { InlineProvider = provider });
            using (var context = new DrawingGroup().Open()) renderer.DrawPage(context, fragment.PageIndex);
            Assert.Equal(actualDrawnBounds, provider.Bounds);
        }, CancellationToken.None);

    [Fact]
    public Task Repeated_image_resource_is_decoded_once_and_reuses_the_pixel_budget() => fixture.Session.Dispatch(() =>
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var resource = new DocumentResource { Kind = DocumentResourceKind.Embedded, MediaType = "image/png", Data = data.ToArray().ToImmutableArray() };
        var first = new InlineDescriptor { Payload = new ImageInlinePayload("shared") };
        var second = first with { Id = Guid.NewGuid() };
        var document = new FlowDocument([new Paragraph([new RichRun(first), new RichRun(second)])])
        { Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("shared", resource) };
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(document);
        using var renderer = new DocumentRenderer(snapshot, new() { ImageLimits = new() { MaximumDecodedPixels = 64 } });
        Assert.Empty(renderer.Diagnostics);
        var size = snapshot.Pages[0].Bounds.Size;
        using var target = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
        using (var context = target.CreateDrawingContext()) renderer.DrawPage(context, 0);
        using var encoded = new MemoryStream();
        target.Save(encoded, PngBitmapEncoderOptions.Default);
        using var decoded = SKBitmap.Decode(encoded.ToArray());
        var visuals = snapshot.InlineVisuals().ToArray();
        Assert.Equal(2, visuals.Length);
        Assert.All(visuals, visual => Assert.Equal(SKColors.Red, decoded.GetPixel((int)visual.Bounds.Center.X, (int)visual.Bounds.Center.Y)));
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Distinct_equal_print_representations_are_each_disposed_once(bool failPreflight) => fixture.Session.Dispatch(() =>
    {
        var first = new InlineDescriptor { Payload = new ControlInlinePayload("provided") };
        var second = first with { Id = Guid.NewGuid() };
        var missing = new InlineDescriptor { Payload = new ControlInlinePayload("missing") };
        var runs = new List<RichRun> { new(first), new(second) };
        if (failPreflight) runs.Add(new(missing));
        using var engine = new PaginationEngine();
        using var snapshot = engine.Paginate(new FlowDocument([new Paragraph(runs)]));
        var provider = new EqualProvider();
        if (failPreflight)
            Assert.Throws<PagedOutputException>(() => new DocumentRenderer(snapshot, new() { InlineProvider = provider }, ownsSnapshot: true));
        else
        {
            var renderer = new DocumentRenderer(snapshot, new() { InlineProvider = provider });
            renderer.Dispose();
            renderer.Dispose();
        }
        Assert.Equal(2, provider.Representations.Count);
        Assert.NotSame(provider.Representations[0], provider.Representations[1]);
        Assert.Equal(provider.Representations[0], provider.Representations[1]);
        Assert.All(provider.Representations, representation => Assert.Equal(1, representation.Disposals));
        _ = snapshot.Caret(0);
    }, CancellationToken.None);

    private sealed class EqualProvider : IInlinePrintProvider
    {
        public List<EqualRepresentation> Representations { get; } = [];
        public IInlinePrintRepresentation? TryCreateRepresentation(FlowDocument document, InlineDescriptor descriptor)
        {
            if (descriptor.Payload is not ControlInlinePayload { Type: "provided" }) return null;
            var representation = new EqualRepresentation();
            Representations.Add(representation);
            return representation;
        }
    }

    private sealed class EqualRepresentation : IInlinePrintRepresentation
    {
        public int Disposals { get; private set; }
        public void Draw(DrawingContext context, Rect bounds) { }
        public void Dispose() => Disposals++;
        public override bool Equals(object? obj) => obj is EqualRepresentation;
        public override int GetHashCode() => 0;
    }

    private static IEnumerable<Rect> RedRectangles(Drawing drawing, Matrix transform)
    {
        if (drawing is DrawingGroup group)
        {
            var combined = (group.Transform?.Value ?? Matrix.Identity) * transform;
            foreach (var child in group.Children)
                foreach (var rect in RedRectangles(child, combined)) yield return rect;
        }
        else if (drawing is GeometryDrawing { Brush: ISolidColorBrush { Color: var color }, Geometry: { } geometry } && color == Colors.Red)
            yield return geometry.Bounds.TransformToAABB(transform);
    }

    private sealed class Provider : IInlinePrintProvider, IInlinePrintRepresentation
    {
        public Rect? Bounds { get; private set; }
        public IInlinePrintRepresentation? TryCreateRepresentation(FlowDocument document, InlineDescriptor descriptor) => this;
        public void Draw(DrawingContext context, Rect bounds) => Bounds = bounds;
        public void Dispose() { }
    }
}
