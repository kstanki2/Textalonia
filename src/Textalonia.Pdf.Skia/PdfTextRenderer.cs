using System.Text;
using System.Buffers.Binary;
using System.Collections.Immutable;
using Textalonia.Model;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using Textalonia.Rendering;

namespace Textalonia.Pdf.Skia;

// Capture only each already-shaped TextLine. Native geometry retains its rendering; glyphs get
// the original UTF-8 text and clusters that Avalonia's screen-only text blobs omit.
internal sealed class PdfTextRenderer(ImmutableArray<DocumentFontDefinition> definitions) : IPageTextRenderer, IDisposable
{
    private readonly Dictionary<IPlatformTypeface, SKTypeface> _fonts = new(ReferenceEqualityComparer.Instance);

    public void DrawLine(DrawingContext context, TextLine line, Point origin)
    {
        var drawing = new DrawingGroup();
        using (var recorder = drawing.Open()) line.Draw(recorder, origin);
        Replay(context, drawing, 1);
    }

    private void Replay(DrawingContext context, Drawing drawing, double opacity)
    {
        if (drawing is DrawingGroup group)
        {
            using var transform = context.PushTransform(group.Transform?.Value ?? Matrix.Identity);
            using var alpha = context.PushOpacity(group.Opacity);
            using var clip = group.ClipGeometry is { } geometry ? context.PushGeometryClip(geometry) : default;
            foreach (var child in group.Children) Replay(context, child, opacity * group.Opacity);
        }
        else if (drawing is GlyphRunDrawing { GlyphRun: { } run, Foreground: ISolidColorBrush brush })
        {
            if (run.GlyphInfos.Count == 0) return;
            var face = GetTypeface(run.GlyphTypeface.PlatformTypeface);
            var simulations = run.GlyphTypeface.FontSimulations;
            using var font = new SKFont(face, (float)run.FontRenderingEmSize,
                skewX: (simulations & FontSimulations.Oblique) != 0 ? -.3f : 0)
            {
                LinearMetrics = true, Embolden = (simulations & FontSimulations.Bold) != 0,
                Subpixel = true, BaselineSnap = false, Edging = SKFontEdging.Antialias
            };
            var bytes = Encoding.UTF8.GetBytes(run.Characters.ToString());
            var indices = new ushort[run.GlyphInfos.Count];
            var positions = new SKPoint[indices.Length];
            var clusters = new uint[indices.Length];
            var start = run.GlyphInfos.Min(g => g.GlyphCluster);
            var byteOffsets = new int[run.Characters.Length + 1];
            for (var character = 0; character < run.Characters.Length;)
            {
                var length = char.IsHighSurrogate(run.Characters.Span[character]) && character + 1 < run.Characters.Length &&
                    char.IsLowSurrogate(run.Characters.Span[character + 1]) ? 2 : 1;
                if (length == 2) byteOffsets[character + 1] = byteOffsets[character];
                byteOffsets[character + length] = byteOffsets[character] + Encoding.UTF8.GetByteCount(run.Characters.Span.Slice(character, length));
                character += length;
            }
            var x = 0d;
            for (var i = 0; i < indices.Length; i++)
            {
                var glyph = run.GlyphInfos[i];
                indices[i] = glyph.GlyphIndex;
                positions[i] = new((float)(x + glyph.GlyphOffset.X), (float)glyph.GlyphOffset.Y);
                var character = Math.Clamp(glyph.GlyphCluster - start, 0, run.Characters.Length);
                clusters[i] = (uint)byteOffsets[character];
                x += glyph.GlyphAdvance;
            }
            using var builder = new SKTextBlobBuilder();
            if (bytes.Length > 0)
            {
                var runBuffer = builder.AllocatePositionedTextRun(font, indices.Length, bytes.Length);
                runBuffer.SetGlyphs(indices); runBuffer.SetPositions(positions);
                runBuffer.SetText(bytes); runBuffer.SetClusters(clusters);
            }
            else
            {
                var runBuffer = builder.AllocatePositionedRun(font, indices.Length);
                runBuffer.SetGlyphs(indices); runBuffer.SetPositions(positions);
            }
            using var blob = builder.Build() ?? throw new InvalidOperationException("The PDF text run could not be created.");
            var color = brush.Color;
            using var paint = new SKPaint
            {
                Color = new SKColor(color.R, color.G, color.B, (byte)Math.Clamp(Math.Round(color.A * brush.Opacity * opacity), 0, 255)),
                IsAntialias = true
            };
            using var operation = new GlyphDrawing(blob, paint, run.BaselineOrigin, run.InkBounds);
            // The public Skia bridge invokes Custom synchronously; no retained scene holds these resources.
            context.Custom(operation);
        }
        else drawing.Draw(context);
    }

    private SKTypeface GetTypeface(IPlatformTypeface platform)
    {
        if (_fonts.TryGetValue(platform, out var cached)) return cached;
        if (!platform.TryGetStream(out var stream))
            throw new NotSupportedException($"The font '{platform.FamilyName}' cannot supply data for PDF embedding.");
        using (stream)
        {
            // A platform stream may contain a font collection. Select its matching face instead
            // of silently using collection face zero and drawing unrelated glyph indices.
            using var data = SKData.Create(stream);
            for (var index = 0; index < 64; index++)
            {
                var face = SKTypeface.FromData(data, index);
                if (face is null) break;
                if (face.FamilyName == platform.FamilyName && face.FontWeight == (int)platform.Weight &&
                    face.FontWidth == (int)platform.Stretch && (int)face.FontSlant == (int)platform.Style)
                {
                    var table = face.GetTableData(0x4F532F32);
                    var rights = table is { Length: >= 10 } ? (FontEmbeddingRights?)BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(8, 2)) : null;
                    var extra = definitions.FirstOrDefault(d => d.FamilyName.Equals(platform.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                        d.Weight == (int)platform.Weight && d.Italic == (platform.Style != FontStyle.Normal))?.EmbeddingRights;
                    if (!FontEmbeddingPolicy.CanEmbed(rights, forEditing: false) ||
                        extra is { } restriction && !FontEmbeddingPolicy.CanEmbed(restriction, forEditing: false) ||
                        ((rights.GetValueOrDefault() | extra.GetValueOrDefault()) & FontEmbeddingRights.NoSubsetting) != 0)
                    {
                        face.Dispose();
                        throw new NotSupportedException($"The font '{platform.FamilyName}' does not permit this PDF backend's embedding/subsetting policy.");
                    }
                    _fonts.Add(platform, face);
                    return face;
                }
                face.Dispose();
            }
        }
        throw new NotSupportedException($"The font '{platform.FamilyName}' could not be reproduced exactly for PDF embedding.");
    }

    public void Dispose()
    {
        foreach (var font in _fonts.Values) font.Dispose();
        _fonts.Clear();
    }

    private sealed class GlyphDrawing(SKTextBlob blob, SKPaint paint, Point origin, Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => false;
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature
                ?? throw new NotSupportedException("PDF output requires the Avalonia Skia rendering backend.");
            using var lease = feature.Lease();
            lease.SkCanvas.DrawText(blob, (float)origin.X, (float)origin.Y, paint);
        }
    }
}
