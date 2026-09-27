using System.Buffers.Binary;

namespace Textalonia.Model;

/// <summary>OpenType OS/2 fsType embedding rights. The source font is authoritative.</summary>
[Flags]
public enum FontEmbeddingRights
{
    Installable = 0, Restricted = 2, PreviewAndPrint = 4, Editable = 8,
    NoSubsetting = 256, BitmapOnly = 512
}

/// <summary>An embedded font face backed by an existing document resource.</summary>
public sealed record DocumentFontDefinition(string FamilyName, string ResourceId)
{
    public int Weight { get; init; } = 400;
    public bool Italic { get; init; }
    public bool IsSubset { get; init; }
    /// <summary>Optional additional restrictions; these never relax the font's own fsType flags.</summary>
    public FontEmbeddingRights? EmbeddingRights { get; init; }
}

/// <summary>Reads embedding restrictions without loading native font data or installing a system font.</summary>
public static class FontEmbeddingPolicy
{
    /// <summary>Returns null for malformed data or unsupported containers (including font collections).</summary>
    public static FontEmbeddingRights? ReadRights(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return null;
        var signature = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (signature is not (0x00010000 or 0x4F54544F or 0x74727565)) return null;
        var tables = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        if (tables > (data.Length - 12) / 16) return null;
        for (var i = 0; i < tables; i++)
        {
            var entry = data.Slice(12 + i * 16, 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(entry) != 0x4F532F32) continue;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            if (length < 10 || offset > data.Length || length > data.Length - offset) return null;
            return (FontEmbeddingRights)BinaryPrimitives.ReadUInt16BigEndian(data.Slice((int)offset + 8, 2));
        }
        return null;
    }

    /// <summary>Checks full-font embedding/use. Subsetting is never performed by this service.</summary>
    public static bool CanEmbed(FontEmbeddingRights? rights, bool forEditing = true)
    {
        if (rights is not { } value || (value & FontEmbeddingRights.BitmapOnly) != 0) return false;
        // Reserved bits and conflicting permission bits are not accepted as a grant.
        if (((int)value & ~0x030E) != 0) return false;
        var permission = (int)value & 0x000E;
        return permission switch { 0 => true, 8 => true, 4 => !forEditing, _ => false };
    }
}

internal static class DocumentFontValidation
{
    internal static void Validate(FlowDocument document)
    {
        if (document.Fonts.IsDefault || document.Fonts.Length > 256) throw new FormatException("Invalid document font catalog.");
        var faces = new HashSet<(string, int, bool)>();
        foreach (var font in document.Fonts)
        {
            if (font is null || string.IsNullOrWhiteSpace(font.FamilyName) || font.FamilyName.Length > 256 ||
                font.FamilyName.Any(char.IsControl) || font.Weight is < 1 or > 1000 ||
                !InlineDescriptor.ValidKey(font.ResourceId) || !document.Resources.TryGetValue(font.ResourceId, out var resource) ||
                resource.Kind != DocumentResourceKind.Embedded ||
                !faces.Add((font.FamilyName.ToUpperInvariant(), font.Weight, font.Italic)))
                throw new FormatException("Invalid or duplicate embedded font definition.");
            if (font.EmbeddingRights is { } rights && ((int)rights & ~0x030E) != 0)
                throw new FormatException("Unknown embedded-font restrictions.");
        }
    }
}
