using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>Format extensions leave caller-owned streams open.</summary>
public interface IDocumentFormat
{
    string Name { get; }
    IReadOnlyList<string> Extensions { get; }
    Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default);
    Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default);
}

public static class DocumentFormats
{
    public static JsonDocumentFormat Json { get; } = new();
    public static PlainTextDocumentFormat PlainText { get; } = new();
    public static HtmlDocumentFormat Html { get; } = new();
    public static RtfDocumentFormat Rtf { get; } = new();
    public static DocxDocumentFormat Docx { get; } = new();
    public static IReadOnlyList<IDocumentFormat> BuiltIn { get; } = [Json, PlainText, Html, Rtf, Docx];

    public static IDocumentFormat ForPath(string path) =>
        BuiltIn.FirstOrDefault(f => f.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        ?? throw new NotSupportedException($"Unsupported document extension: {Path.GetExtension(path)}");

    internal static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken token, int limit = 32 * 1024 * 1024)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(bytes, token)) > 0)
        {
            if (buffer.Length + read > limit) throw new FormatException("Document exceeds the 32 MB import limit.");
            await buffer.WriteAsync(bytes.AsMemory(0, read), token);
        }
        return buffer.ToArray();
    }
}

/// <summary>Text codecs do parsing and encoding off the calling thread.</summary>
public abstract class TextDocumentFormat : IDocumentFormat
{
    public abstract string Name { get; }
    public abstract IReadOnlyList<string> Extensions { get; }
    public abstract FlowDocument Parse(string text);
    public abstract string Serialize(FlowDocument document);
    public virtual async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        return await Task.Run(() =>
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
            var document = Parse(reader.ReadToEnd());
            document.Validate();
            return document;
        }, cancellationToken);
    }
    public async Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default)
    {
        document.Validate();
        var bytes = await Task.Run(() => Encoding.UTF8.GetBytes(Serialize(document)), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}

public sealed class PlainTextDocumentFormat : TextDocumentFormat
{
    public override string Name => "Plain text";
    public override IReadOnlyList<string> Extensions => [".txt"];
    public override FlowDocument Parse(string text) => FlowDocument.FromText(text);
    public override string Serialize(FlowDocument document) => document.PlainText;
}

/// <summary>Versioned, lossless native storage, including hidden cells retained by table merges.</summary>
public sealed class JsonDocumentFormat : TextDocumentFormat
{
    private const int CurrentVersion = 3;
    private sealed record Envelope(int Version, FlowDocument Document);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 256,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { type =>
            {
                if (type.Type != typeof(TableCell)) return;
                // Ignored compatibility setters are not valid members of the v2 wire
                // vocabulary; accepting both would silently discard one collection.
                foreach (var property in type.Properties.Where(p => p.Name is "paragraphs" or "mergeOriginal").ToArray())
                    type.Properties.Remove(property);
            } }
        },
        Converters = { new JsonStringEnumConverter() }
    };
    // Freeze the v2 vocabulary even as new properties are added to the current model.
    private static readonly JsonSerializerOptions VersionTwoOptions = new(Options)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { type =>
            {
                foreach (var property in type.Properties.Where(p =>
                    type.Type == typeof(TableCell) && p.Name is "paragraphs" or "mergeOriginal" ||
                    type.Type == typeof(RichRun) && p.Name == "inline" ||
                    type.Type == typeof(FlowDocument) && p.Name == "resources").ToArray())
                    type.Properties.Remove(property);
            } }
        }
    };
    public override string Name => "Textalonia document";
    public override IReadOnlyList<string> Extensions => [".textalonia", ".json", ".art"];
    public override FlowDocument Parse(string text)
    {
        // Inspect only the envelope before choosing a reader. Future document shapes must
        // report an unsupported version rather than a misleading unknown-member error.
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = Options.MaxDepth });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Missing document envelope.");
        var versions = json.RootElement.EnumerateObject().Where(p => p.NameEquals("version")).ToArray();
        if (versions.Length == 0) throw new NotSupportedException("Document version is missing. Supported versions are 1, 2, and 3.");
        if (versions.Length != 1 || versions[0].Value.ValueKind != JsonValueKind.Number ||
            !versions[0].Value.TryGetInt32(out var version))
            throw new FormatException("Document version must be one integer.");
        var document = version switch
        {
            1 => NativeDocumentV1.Read(json.RootElement, Options),
            2 => json.RootElement.Deserialize<Envelope>(VersionTwoOptions)?.Document
                ?? throw new FormatException("Missing document."),
            CurrentVersion => json.RootElement.Deserialize<Envelope>(Options)?.Document
                ?? throw new FormatException("Missing document."),
            _ => throw new NotSupportedException($"Document version {version} is not supported. Supported versions are 1, 2, and 3.")
        };
        document.Validate();
        return document;
    }
    public override string Serialize(FlowDocument document)
    {
        document.Validate();
        return JsonSerializer.Serialize(new Envelope(CurrentVersion, document), Options);
    }
}
