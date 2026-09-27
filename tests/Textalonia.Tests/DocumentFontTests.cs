using System.Buffers.Binary;
using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Serialization;
using Xunit;

namespace Textalonia.Tests;

public class DocumentFontTests(UiFixture fixture) : IClassFixture<UiFixture>
{
    private static FontFamily Fallback => new("avares://Avalonia.Fonts.Inter/Assets#Inter");
    private Task Run(Action action) => fixture.Session.Dispatch(action, CancellationToken.None);

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(2, true, false)]
    [InlineData(4, true, false)]
    [InlineData(4, false, true)]
    [InlineData(8, true, true)]
    [InlineData(256, true, true)]
    [InlineData(512, false, false)]
    [InlineData(10, true, false)]
    public void Embedding_policy_honors_authoritative_fsType(int bits, bool editing, bool expected)
    {
        var data = FontHeader((ushort)bits);
        var rights = FontEmbeddingPolicy.ReadRights(data);
        Assert.Equal((FontEmbeddingRights)bits, rights);
        Assert.Equal(expected, FontEmbeddingPolicy.CanEmbed(rights, editing));
    }

    [Fact]
    public void Malformed_offsets_and_unknown_rights_are_not_permission_grants()
    {
        var data = FontHeader(0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), uint.MaxValue);
        Assert.Null(FontEmbeddingPolicy.ReadRights(data));
        Assert.Null(FontEmbeddingPolicy.ReadRights([]));
        Assert.False(FontEmbeddingPolicy.CanEmbed(null));
        Assert.False(FontEmbeddingPolicy.CanEmbed((FontEmbeddingRights)1));
    }

    [Fact]
    public Task Embedded_fonts_are_private_to_the_service_and_substitution_reports_are_stable() => Run(() =>
    {
        var asset = AssetLoader.GetAssets(new Uri("avares://Avalonia.Fonts.Inter/Assets"), null).First(uri => uri.AbsolutePath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase));
        using var stream = AssetLoader.Open(asset); using var data = new MemoryStream(); stream.CopyTo(data);
        var document = WithFont(data.ToArray()); document.Validate();
        using var first = new DocumentFontService(document, Fallback);
        using var second = new DocumentFontService(document, Fallback);
        var a = first.Resolve("Document-only font"); var b = second.Resolve("Document-only font");
        Assert.NotEqual(a, b);
        Assert.Contains("Inter", new Typeface(a).GlyphTypeface.FamilyName);
        Assert.Empty(first.Diagnostics);
        using (var layout = new DocumentLayout())
        {
            var rendered = document with { Blocks = [new Paragraph("embedded glyphs", new TextStyle { FontFamily = "Document-only font" })] };
            layout.Build(rendered, 500, Fallback, Brushes.Black, Brushes.Gray, new Thickness(0), new Rect(0, 0, 500, 500));
            using var lease = layout.At(0)!.Acquire();
            var typeface = lease.Layout.TextLines[0].TextRuns[0].Properties!.Typeface;
            Assert.StartsWith("fonts:textalonia-", typeface.FontFamily.Key!.Source.OriginalString);
            Assert.Contains("Inter", typeface.GlyphTypeface.FamilyName);
            Assert.Empty(layout.FontDiagnostics);
        }
        Assert.Equal(Fallback, first.Resolve("Missing font Z"));
        first.Resolve("Missing font A"); first.Resolve("Missing font Z");
        Assert.Equal(new[] { "Missing font A", "Missing font Z" }, first.Diagnostics.Select(d => d.FamilyName));
        Assert.All(first.Diagnostics, d => Assert.Equal("font.substituted", d.Code));
        first.Dispose(); Assert.Throws<ObjectDisposedException>(() => first.Resolve("Document-only font"));
        Assert.Contains("Inter", new Typeface(b).GlyphTypeface.FamilyName);
    });

    [Fact]
    public Task Restricted_fonts_are_preserved_but_never_loaded_even_if_metadata_claims_installable() => Run(() =>
    {
        var document = WithFont(FontHeader(2));
        document.Validate();
        Assert.Single(document.PruneUnusedResources().Resources);
        using var service = new DocumentFontService(document, Fallback);
        Assert.Equal("font.embedding.restricted", Assert.Single(service.Diagnostics).Code);
        Assert.Equal(Fallback, service.Resolve("Document-only font"));
    });

    [Fact]
    public Task Docx_embedded_fonts_round_trip_unobfuscated_bytes_and_restricted_export_is_atomic() => Run(() =>
    {
        var asset = AssetLoader.GetAssets(new Uri("avares://Avalonia.Fonts.Inter/Assets"), null).First(uri => uri.AbsolutePath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase));
        using var input = AssetLoader.Open(asset); using var bytes = new MemoryStream(); input.CopyTo(bytes);
        var document = WithFont(bytes.ToArray());
        using var output = new MemoryStream(); Task.Run(() => DocumentFormats.Docx.SaveAsync(document, output)).GetAwaiter().GetResult();
        output.Position = 0;
        var loaded = Task.Run(() => DocumentFormats.Docx.LoadAsync(output)).GetAwaiter().GetResult();
        var font = Assert.Single(loaded.Fonts);
        Assert.Equal("Document-only font", font.FamilyName); Assert.Equal(400, font.Weight); Assert.False(font.Italic);
        Assert.Equal(bytes.ToArray(), loaded.Resources[font.ResourceId].Data.ToArray());
        using var service = new DocumentFontService(loaded, Fallback);
        Assert.Contains("Inter", new Typeface(service.Resolve(font.FamilyName)).GlyphTypeface.FamilyName);
        using var denied = new MemoryStream();
        Assert.Throws<DocumentConversionException>(() => Task.Run(() => DocumentFormats.Docx.SaveWithReportAsync(WithFont(FontHeader(2)), denied,
            new() { Mode = ConversionMode.Strict })).GetAwaiter().GetResult());
        Assert.Equal(0, denied.Length);
    });

    private static FlowDocument WithFont(byte[] bytes) => new()
    {
        Fonts = [new("Document-only font", "font") { EmbeddingRights = FontEmbeddingRights.Installable }],
        Resources = ImmutableDictionary<string, DocumentResource>.Empty.Add("font", new() { MediaType = "font/ttf", Data = bytes.ToImmutableArray() })
    };

    private static byte[] FontHeader(ushort rights)
    {
        var bytes = new byte[38]; BinaryPrimitives.WriteUInt32BigEndian(bytes, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 0x4F532F32);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 28);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(24), 10);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(36), rights); return bytes;
    }
}
