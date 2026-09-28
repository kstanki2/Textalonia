using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Xml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using Textalonia.Controls;
using Textalonia.Model;
using Xunit;

namespace Textalonia.Tests;

public class BoundedImageDecoderTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);

    [Fact]
    public Task Svg_renders_viewbox_shapes_paths_and_inherited_transforms() => Run(() =>
    {
        using var bitmap = BoundedImageDecoder.Decode(Utf8("""
            <svg xmlns="http://www.w3.org/2000/svg" width="40" height="20" viewBox="0 0 20 10">
              <rect width="20" height="10" fill="white"/>
              <g transform="translate(2 1)" fill="red"><path d="M0 0 H5 V5 H0 Z"/></g>
              <circle cx="15" cy="5" r="3" fill="#0000ff"/>
            </svg>
            """));
        Assert.Equal(40, bitmap.PixelSize.Width);
        Assert.Equal(20, bitmap.PixelSize.Height);
        using var pixels = Pixels(bitmap);
        Assert.Equal(SKColors.Red, pixels.GetPixel(8, 6));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(30, 10));
        Assert.Equal(SKColors.White, pixels.GetPixel(1, 1));
    });

    [Fact]
    public Task Svg_cache_decodes_on_background_worker_and_retains_pixel_budget() => Run(() =>
    {
        var data = Utf8("<svg width='4' height='3'><rect width='4' height='3' fill='red'/></svg>");
        var resource = new DocumentResource { Kind = DocumentResourceKind.Embedded, MediaType = "image/svg+xml", Data = data.ToImmutableArray() };
        using var cache = new InlineImageCache(options: new() { MaximumDecodedPixels = 12 });
        cache.Request("svg", resource);
        var timer = Stopwatch.StartNew();
        while (cache.PendingLoadCount > 0 && timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.Equal(0, cache.PendingLoadCount);
        Assert.NotNull(cache.Request("svg", resource));
        Assert.Equal(12, cache.CachedPixelCount);
    });

    [Theory]
    [InlineData("", false)]
    [InlineData("fill-rule='evenodd'", true)]
    public Task Svg_uses_nonzero_fill_by_default_and_inherits_explicit_evenodd(string fillRule, bool hole) => Run(() =>
    {
        using var bitmap = BoundedImageDecoder.Decode(Utf8($"""
            <svg width="10" height="10">
              <rect width="10" height="10" fill="white"/>
              <g {fillRule}><path fill="red" d="M0 0H10V10H0Z M2 2H8V8H2Z"/></g>
            </svg>
            """));
        using var pixels = Pixels(bitmap);
        Assert.Equal(hole ? SKColors.White : SKColors.Red, pixels.GetPixel(5, 5));
    });

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<image href='https://example.invalid/a.png'/>")]
    [InlineData("<use href='#shape'/>")]
    [InlineData("<foreignObject/>")]
    [InlineData("<rect width='1' height='1' onload='alert(1)'/>")]
    [InlineData("<rect width='1' height='1' style='fill:red'/>")]
    [InlineData("<rect width='1' height='1' fill='url(https://example.invalid/paint)'/>")]
    [InlineData("<path d='M0 0L1e200 1'/>")]
    [InlineData("<g transform='scale(1000000) scale(1000000)'/>")]
    [InlineData("<g transform='scale(1000000)'><g transform='scale(1000000)'/></g>")]
    [InlineData("<rect width='NaN' height='1'/>")]
    [InlineData("<rect width='-1' height='1'/>")]
    public Task Svg_rejects_unsupported_active_referenced_or_unbounded_content(string content) => Run(() =>
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(Utf8($"<svg width='8' height='8'>{content}</svg>"))));

    [Fact]
    public void Svg_rejects_dtd_and_processing_instructions_without_a_resolver()
    {
        Assert.Throws<XmlException>(() => BoundedImageDecoder.Decode(Utf8("<!DOCTYPE svg [<!ENTITY remote SYSTEM 'file:///secret'>]><svg>&remote;</svg>")));
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(Utf8("<?xml-stylesheet href='https://example.invalid/style'?><svg/>")));
    }

    [Fact]
    public void Svg_limits_dimensions_xml_depth_elements_and_aggregate_path_complexity()
    {
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(Utf8("<svg width='500' height='500'/>"), options: new() { MaximumDecodedPixels = 100 }));
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(Utf8("<svg width='500' height='1'/>"), options: new() { MaximumDimension = 100 }));
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(Utf8("<svg><g><g><g/></g></g></svg>"), options: new() { MaximumVectorDepth = 3 }));
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(Utf8("<svg><rect/><rect/></svg>"), options: new() { MaximumVectorElements = 2 }));
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(Utf8("<svg><path d='M0 0L1 1'/><path d='M0 0L1 1'/></svg>"), options: new() { MaximumVectorPathCharacters = 10 }));
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(Utf8("<svg/>"), options: new() { MaximumEncodedBytes = 3 }));
    }

    [Fact]
    public Task Ico_decodes_with_the_native_backend_and_checks_payload_dimensions() => Run(() =>
    {
        var bytes = Ico();
        Assert.Equal((2, 1), ImageDimensions.Read(bytes));
        using var bitmap = BoundedImageDecoder.Decode(bytes, "image/x-icon");
        Assert.Equal(2, bitmap.PixelSize.Width);
        Assert.Equal(1, bitmap.PixelSize.Height);
        using var pixels = Pixels(bitmap);
        Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
        var mismatch = bytes.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(mismatch.AsSpan(26), 10000);
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(mismatch));
        var outOfBounds = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(outOfBounds.AsSpan(18), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(outOfBounds));
        Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(bytes, options: new() { MaximumDimension = 1 }));
    });

    [Theory]
    [InlineData("image/x-emf")]
    [InlineData("image/x-wmf")]
    [InlineData("image/tiff")]
    public void Preserving_an_original_does_not_claim_native_decoding(string mediaType)
    {
        var error = Assert.Throws<InvalidDataException>(() => BoundedImageDecoder.Decode(new byte[] { 1, 2, 3, 4 }, mediaType));
        Assert.Contains("supplied preview", error.Message);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static SKBitmap Pixels(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return SKBitmap.Decode(stream.ToArray());
    }

    private static byte[] Ico()
    {
        var bytes = new byte[74];
        bytes[2] = 1; bytes[4] = 1;
        bytes[6] = 2; bytes[7] = 1;
        bytes[10] = 1; bytes[12] = 32;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 52);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 22);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(26), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(30), 2);
        bytes[34] = 1; bytes[36] = 32;
        bytes[64] = 255; bytes[65] = 255;
        bytes[68] = 255; bytes[69] = 255;
        return bytes;
    }
}
