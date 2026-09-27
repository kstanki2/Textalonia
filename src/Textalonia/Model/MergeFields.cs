namespace Textalonia.Model;

/// <summary>A named, atomic mail-merge field. Values are supplied by the host, never evaluated as code.</summary>
public sealed record MergeFieldInlinePayload(string Name) : InlinePayload
{
    /// <summary>Optional .NET IFormattable format; the merge options supply its culture.</summary>
    public string? Format { get; init; }
    /// <summary>Literal text used when a record has no value for this field, including a null value.</summary>
    public string? FallbackText { get; init; }

    internal void Validate()
    {
        if (!InlineDescriptor.ValidKey(Name))
            throw new FormatException("Merge-field names must contain 1 to 256 non-control characters and cannot be blank.");
        if (Format is { } format && (format.Length > 1024 || format.Any(char.IsControl)))
            throw new FormatException("Merge-field formats cannot exceed 1024 characters or contain control characters.");
        if (FallbackText is { } fallback && (fallback.Length > 16_384 || fallback.Contains('\0')))
            throw new FormatException("Merge-field fallback text cannot exceed 16384 characters or contain NUL.");
    }
}

/// <summary>Creates merge-field descriptors for document construction and editor insertion.</summary>
public static class MergeFields
{
    /// <summary>Creates a field displayed as «name». Field matching is ordinal and case-sensitive.</summary>
    public static InlineDescriptor Create(string name, string? format = null, string? fallbackText = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var field = new MergeFieldInlinePayload(name) { Format = format, FallbackText = fallbackText };
        field.Validate();
        return new InlineDescriptor
        {
            Payload = field,
            AltText = $"«{name}»",
            Width = Math.Clamp((name.Length + 2) * 8d + 8, 40, 600),
            Height = 24
        };
    }
}
