using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Textalonia.Model;

namespace Textalonia.Rendering;

/// <summary>Shared image geometry for the editor, preview and output.</summary>
internal static class ImageDrawing
{
    internal static string? ResourceId(InlineDescriptor descriptor) => descriptor.Payload switch
    {
        ImageInlinePayload image => image.PreviewResourceId ?? image.ResourceId,
        OleInlinePayload ole => ole.PreviewResourceId,
        _ => null
    };

    internal static Rect CroppedSource(Size size, ImageCrop? crop) => crop is null ? new Rect(size) :
        new Rect(size.Width * crop.Left, size.Height * crop.Top,
            size.Width * (1 - crop.Left - crop.Right), size.Height * (1 - crop.Top - crop.Bottom));

    internal static Matrix Rotation(Rect bounds, double degrees) =>
        Matrix.CreateTranslation(-bounds.Center.X, -bounds.Center.Y) * Matrix.CreateRotation(degrees * Math.PI / 180) *
        Matrix.CreateTranslation(bounds.Center.X, bounds.Center.Y);

    internal static Rect RotatedBounds(Rect bounds, double degrees) => bounds.TransformToAABB(Rotation(bounds, degrees));

    internal static void Draw(DrawingContext context, IImage image, Rect bounds, ImagePlacement? placement)
    {
        using var transform = context.PushTransform(Rotation(bounds, placement?.Rotation ?? 0));
        using var clip = context.PushClip(bounds);
        context.DrawImage(image, CroppedSource(image.Size, placement?.Crop), bounds);
    }

    internal static void DrawRepresentation(DrawingContext context, IInlinePrintRepresentation representation, Rect bounds, ImagePlacement? placement)
    {
        using var transform = context.PushTransform(Rotation(bounds, placement?.Rotation ?? 0));
        using var clip = context.PushClip(bounds);
        var crop = placement?.Crop;
        var fullWidth = bounds.Width / (1 - (crop?.Left ?? 0) - (crop?.Right ?? 0));
        var fullHeight = bounds.Height / (1 - (crop?.Top ?? 0) - (crop?.Bottom ?? 0));
        representation.Draw(context, new Rect(bounds.Left - fullWidth * (crop?.Left ?? 0),
            bounds.Top - fullHeight * (crop?.Top ?? 0), fullWidth, fullHeight));
    }

    internal static void DrawPlaceholder(DrawingContext context, InlineDescriptor descriptor, Rect bounds)
    {
        using var transform = context.PushTransform(Rotation(bounds, descriptor.Placement?.Rotation ?? 0));
        using var clip = context.PushClip(bounds);
        context.DrawRectangle(Brushes.WhiteSmoke, new Pen(Brushes.Gray, 1), bounds.Deflate(.5));
        var label = new TextLayout(string.IsNullOrEmpty(descriptor.AltText) ? "Image" : descriptor.AltText,
            new Typeface(FontFamily.Default), 12, Brushes.Gray, maxWidth: Math.Max(1, bounds.Width - 6),
            maxLines: 1, textTrimming: TextTrimming.CharacterEllipsis);
        using var lifetime = InlineOutputScope.Retain(label);
        label.Draw(context, new Point(bounds.Left + 3, bounds.Center.Y - label.Height / 2));
    }

    internal static bool BehindText(InlineDescriptor descriptor) =>
        descriptor.Placement is { Anchor: not ImageAnchorKind.Inline, Wrap: ImageWrapKind.BehindText };
}
