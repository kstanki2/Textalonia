using System.Collections.Immutable;

namespace Textalonia.Model;

public enum ImageAnchorKind { Inline, Paragraph, Page }
public enum ImageWrapKind { Square, TopBottom, BehindText, InFrontOfText, Contour }

/// <summary>Fractions removed from each edge of the source image, before rotation.</summary>
public sealed record ImageCrop
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }

    internal void Validate()
    {
        if (new[] { Left, Top, Right, Bottom }.Any(value => !double.IsFinite(value) || value is < 0 or >= 1) ||
            Left + Right >= 1 || Top + Bottom >= 1)
            throw new FormatException("Image cropping must leave a nonempty source rectangle.");
    }
}

/// <summary>A normalized point in the displayed image rectangle.</summary>
public readonly record struct ImageContourPoint(double X, double Y);

/// <summary>Image-only geometry in DIP. A null placement retains the legacy inline behavior.</summary>
public sealed record ImagePlacement
{
    public ImageAnchorKind Anchor { get; init; }
    public ImageWrapKind Wrap { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Distance { get; init; } = 4;
    public double Rotation { get; init; }
    public ImageCrop Crop { get; init; } = new();
    public bool LockAspectRatio { get; init; } = true;
    public ImmutableArray<ImageContourPoint> Contour { get; init; } = [];

    public bool Equals(ImagePlacement? other) => other is not null && Anchor == other.Anchor && Wrap == other.Wrap &&
        X == other.X && Y == other.Y && Distance == other.Distance && Rotation == other.Rotation &&
        Crop == other.Crop && LockAspectRatio == other.LockAspectRatio && Contour.IsDefault == other.Contour.IsDefault &&
        Contour.AsSpan().SequenceEqual(other.Contour.AsSpan());
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Anchor); hash.Add(Wrap); hash.Add(X); hash.Add(Y); hash.Add(Distance); hash.Add(Rotation);
        hash.Add(Crop); hash.Add(LockAspectRatio);
        foreach (var point in Contour.AsSpan()) hash.Add(point);
        return hash.ToHashCode();
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(Anchor) || !Enum.IsDefined(Wrap) || !double.IsFinite(X) || !double.IsFinite(Y) ||
            X is < -100000 or > 100000 || Y is < -100000 or > 100000 || !double.IsFinite(Distance) ||
            Distance is < 0 or > 1000 || !double.IsFinite(Rotation) || Rotation is < -360 or > 360 || Crop is null ||
            Contour.IsDefault || Contour.Length > 256 || Contour.Length is 1 or 2 ||
            Contour.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X is < 0 or > 1 || point.Y is < 0 or > 1))
            throw new FormatException("Invalid image placement, rotation, or contour.");
        Crop.Validate();
    }
}

/// <summary>Dedicated section header background, independent of linked header text and variants.
/// Exactly one of Text and ResourceId must be provided. Removal affects only its owning section.</summary>
public sealed record DocumentWatermark
{
    public string? Text { get; init; }
    public string? ResourceId { get; init; }
    public double Width { get; init; } = 400;
    public double Height { get; init; } = 120;
    public double Rotation { get; init; } = -45;
    public double Opacity { get; init; } = .2;
    public string FontFamily { get; init; } = "Arial";
    public double FontSize { get; init; } = 72;
    public string Color { get; init; } = "#808080";

    internal void Validate()
    {
        if ((Text is null) == (ResourceId is null) || Text is { Length: 0 or > 16384 } ||
            ResourceId is not null && !InlineDescriptor.ValidKey(ResourceId) || !InlineDescriptor.ValidKey(FontFamily) ||
            !double.IsFinite(Width) || !double.IsFinite(Height) || Width is <= 0 or > 10000 || Height is <= 0 or > 10000 ||
            !double.IsFinite(Rotation) || Rotation is < -360 or > 360 || !double.IsFinite(Opacity) || Opacity is < 0 or > 1 ||
            !double.IsFinite(FontSize) || FontSize is < 1 or > 512 || Color is null)
            throw new FormatException("Invalid section watermark.");
        FlowDocument.ValidateColor(Color);
    }
}
