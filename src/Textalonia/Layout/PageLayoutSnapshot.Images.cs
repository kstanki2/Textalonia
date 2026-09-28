using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Rendering;
using ImageDrawing = Textalonia.Rendering.ImageDrawing;

namespace Textalonia.Layout;

public sealed partial class PageLayoutSnapshot
{
    internal readonly record struct WatermarkVisual(DocumentWatermark Watermark, PageLayout Page, Rect Bounds);

    internal IEnumerable<WatermarkVisual> Watermarks()
    {
        if (IsDraft) yield break;
        foreach (var page in Pages)
            if (Document.Sections.FirstOrDefault(section => section.Id == page.SectionId)?.Watermark is { } watermark)
                yield return new(watermark, page, new Rect(page.Bounds.Center.X - watermark.Width / 2,
                    page.Bounds.Center.Y - watermark.Height / 2, watermark.Width, watermark.Height));
    }

    internal void DrawWatermarks(DrawingContext context, Rect? viewport,
        Action<DrawingContext, string, Rect>? drawImage = null, IPageTextRenderer? textRenderer = null)
    {
        foreach (var visual in Watermarks())
        {
            if (viewport is { } visible && !visual.Page.Bounds.Intersects(visible)) continue;
            var watermark = visual.Watermark;
            using var pageClip = context.PushClip(visual.Page.Bounds);
            using var opacity = context.PushOpacity(watermark.Opacity);
            using var transform = context.PushTransform(ImageDrawing.Rotation(visual.Bounds, watermark.Rotation));
            using var clip = context.PushClip(visual.Bounds);
            if (watermark.ResourceId is { } resource) drawImage?.Invoke(context, resource, visual.Bounds);
            else if (watermark.Text is { } text)
            {
                var label = new TextLayout(text, new Typeface(new FontFamily(watermark.FontFamily)), watermark.FontSize,
                    DocumentLayout.Brush(watermark.Color) ?? Brushes.Gray, textAlignment: TextAlignment.Center,
                    textWrapping: TextWrapping.Wrap, maxWidth: visual.Bounds.Width, maxHeight: visual.Bounds.Height);
                using var lifetime = InlineOutputScope.Retain(label);
                var origin = new Point(visual.Bounds.Left, visual.Bounds.Center.Y - label.Height / 2);
                foreach (var line in label.TextLines)
                {
                    if (textRenderer is null) line.Draw(context, origin);
                    else textRenderer.DrawLine(context, line, origin);
                    origin = origin.WithY(origin.Y + line.Height);
                }
            }
        }
    }
}
