using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Textalonia.Model;

/// <summary>Immutable data for one atomic U+FFFC document position. No view or runtime type is stored.</summary>
public sealed record InlineDescriptor
{
    public const char ObjectReplacementCharacter = '\uFFFC';
    public Guid Id { get; init; } = Guid.NewGuid();
    public string AltText { get; init; } = "";
    public double Width { get; init; } = 32;
    public double Height { get; init; } = 32;
    public required InlinePayload Payload { get; init; }

    public static InlineDescriptor Note(Guid noteId, string mark = "1") => new()
    { Payload = new NoteInlinePayload(noteId), AltText = mark, Width = 12, Height = 16 };
    public static InlineDescriptor PageField(PageFieldKind field) => new()
    { Payload = new PageFieldInlinePayload(field), AltText = "1", Width = 24, Height = 16 };

    internal void Validate()
    {
        if (Id == Guid.Empty || AltText is null || AltText.Length > 16_384 ||
            !double.IsFinite(Width) || !double.IsFinite(Height) || Width is <= 0 or > 10_000 || Height is <= 0 or > 10_000)
            throw new FormatException("Invalid inline identity, alternative text, or dimensions.");
        switch (Payload)
        {
            case NoteInlinePayload note when note.NoteId != Guid.Empty: break;
            case PageFieldInlinePayload field when Enum.IsDefined(field.Field): break;
            case ImageInlinePayload image when ValidKey(image.ResourceId): break;
            case MergeFieldInlinePayload field:
                field.Validate();
                break;
            case ControlInlinePayload control when ValidKey(control.Type) && control.Properties is not null &&
                control.Properties.Count <= 128 && control.Properties.All(p => ValidKey(p.Key) && p.Value is not null && p.Value.Length <= 16_384): break;
            default: throw new FormatException("Invalid inline payload.");
        }
    }

    internal static bool ValidKey(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ImageInlinePayload), "image")]
[JsonDerivedType(typeof(ControlInlinePayload), "control")]
[JsonDerivedType(typeof(MergeFieldInlinePayload), "mergeField")]
[JsonDerivedType(typeof(NoteInlinePayload), "note")]
[JsonDerivedType(typeof(PageFieldInlinePayload), "pageField")]
public abstract record InlinePayload;

public sealed record ImageInlinePayload(string ResourceId) : InlinePayload;

/// <summary>The host maps Type to a registered factory; serialized names never activate CLR types.</summary>
public sealed record ControlInlinePayload(string Type) : InlinePayload
{
    public ImmutableDictionary<string, string> Properties { get; init; } = ImmutableDictionary<string, string>.Empty;

    public bool Equals(ControlInlinePayload? other) => other is not null && Type == other.Type &&
        (ReferenceEquals(Properties, other.Properties) || Properties is not null && other.Properties is not null &&
            Properties.Count == other.Properties.Count && Properties.OrderBy(p => p.Key, StringComparer.Ordinal)
                .SequenceEqual(other.Properties.OrderBy(p => p.Key, StringComparer.Ordinal)));
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Type, StringComparer.Ordinal);
        if (Properties is not null)
            foreach (var property in Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            { hash.Add(property.Key, StringComparer.Ordinal); hash.Add(property.Value, StringComparer.Ordinal); }
        return hash.ToHashCode();
    }
}

public enum DocumentResourceKind { Embedded, Local, Host }

/// <summary>Snapshot-owned encoded resource data, or an opaque location resolved only by a host service.</summary>
public sealed record DocumentResource
{
    public const int MaximumEmbeddedBytes = 8 * 1024 * 1024;
    public const int MaximumDocumentEmbeddedBytes = 16 * 1024 * 1024;
    public DocumentResourceKind Kind { get; init; }
    public string MediaType { get; init; } = "application/octet-stream";
    [JsonConverter(typeof(ImmutableBytesConverter))]
    public ImmutableArray<byte> Data { get; init; } = [];
    public string? Location { get; init; }

    public bool Equals(DocumentResource? other) => other is not null && Kind == other.Kind && MediaType == other.MediaType &&
        Location == other.Location && Data.IsDefault == other.Data.IsDefault &&
        (Data == other.Data || Data.AsSpan().SequenceEqual(other.Data.AsSpan()));
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Kind); hash.Add(MediaType, StringComparer.Ordinal);
        hash.Add(Location, StringComparer.Ordinal); hash.Add(Data.IsDefault);
        foreach (var value in Data.AsSpan()) hash.Add(value);
        return hash.ToHashCode();
    }

    internal void Validate()
    {
        if (!Enum.IsDefined(Kind) || !InlineDescriptor.ValidKey(MediaType) || Data.IsDefault || Data.Length > MaximumEmbeddedBytes)
            throw new FormatException("Invalid resource metadata or embedded resource size.");
        if (Kind == DocumentResourceKind.Embedded ? Location is not null :
            !Data.IsEmpty || string.IsNullOrWhiteSpace(Location) || Location.Length > 4096 || Location.Any(char.IsControl))
            throw new FormatException("Embedded resources own bytes; local and host resources require an opaque location.");
    }
}

/// <summary>Stores immutable byte data as base64 in native snapshots.</summary>
public sealed class ImmutableBytesConverter : JsonConverter<ImmutableArray<byte>>
{
    public override ImmutableArray<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Resource data must be base64 text.");
        if (reader.HasValueSequence ? reader.ValueSequence.Length > (DocumentResource.MaximumEmbeddedBytes + 2L) / 3 * 4 :
            reader.ValueSpan.Length > (DocumentResource.MaximumEmbeddedBytes + 2L) / 3 * 4)
            throw new JsonException("Embedded resource exceeds the size limit.");
        var data = reader.GetBytesFromBase64();
        if (data.Length > DocumentResource.MaximumEmbeddedBytes) throw new JsonException("Embedded resource exceeds the size limit.");
        return ImmutableArray.CreateRange(data);
    }
    public override void Write(Utf8JsonWriter writer, ImmutableArray<byte> value, JsonSerializerOptions options) =>
        writer.WriteBase64StringValue(value.AsSpan());
}
