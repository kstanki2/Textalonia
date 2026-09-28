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
    public static XamlDocumentFormat Xaml { get; } = new();
    public static MarkdownDocumentFormat Markdown { get; } = new();
    public static IReadOnlyList<IDocumentFormat> BuiltIn { get; } = [Json, PlainText, Html, Rtf, Docx, Xaml, Markdown];

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

    internal static string DecodeText(byte[] bytes)
    {
        // StreamReader's BOM detection substitutes permissive encodings, even when its
        // initial UTF-8 decoder throws on invalid input. Preserve BOM support explicitly.
        ReadOnlySpan<byte> input = bytes;
        Encoding encoding = new UTF8Encoding(false, true);
        var preambleLength = 0;
        if (input.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        { encoding = new UTF32Encoding(true, false, true); preambleLength = 4; }
        else if (input.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        { encoding = new UTF32Encoding(false, false, true); preambleLength = 4; }
        else if (input.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) preambleLength = 3;
        else if (input.StartsWith(new byte[] { 0xFE, 0xFF }))
        { encoding = new UnicodeEncoding(true, false, true); preambleLength = 2; }
        else if (input.StartsWith(new byte[] { 0xFF, 0xFE }))
        { encoding = new UnicodeEncoding(false, false, true); preambleLength = 2; }
        return encoding.GetString(input[preambleLength..]);
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
            var document = Parse(DocumentFormats.DecodeText(bytes));
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
    private const int CurrentVersion = 9;
    private sealed record Envelope(int Version, FlowDocument Document);
    internal static readonly JsonSerializerOptions Options = new()
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
                // Paragraph projections are not part of the wire vocabulary; accepting
                // them alongside blocks would silently discard one collection.
                foreach (var property in type.Properties.Where(p => p.Name is "paragraphs" or "mergeOriginal").ToArray())
                    type.Properties.Remove(property);
            } }
        },
        Converters = { new JsonStringEnumConverter() }
    };
    public override string Name => "Textalonia document";
    public override IReadOnlyList<string> Extensions => [".textalonia", ".json", ".art"];
    public override FlowDocument Parse(string text)
    {
        // Version 4 concrete styles migrate as explicit direct formatting. Version 5
        // adds sparse formatting, named styles and physical document themes; version 6 adds page sections; version 7 adds secondary stories and notes; version 8 adds bookmarks and general fields; version 9 adds extended tables and list markers.
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = Options.MaxDepth });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Missing document envelope.");
        var versions = json.RootElement.EnumerateObject().Where(p => p.NameEquals("version")).ToArray();
        if (versions.Length == 0) throw new NotSupportedException($"Document version is missing. Supported version is {CurrentVersion}.");
        if (versions.Length != 1 || versions[0].Value.ValueKind != JsonValueKind.Number ||
            !versions[0].Value.TryGetInt32(out var version))
            throw new FormatException("Document version must be one integer.");
        if (version is not (4 or 5 or 6 or 7 or 8 or CurrentVersion))
            throw new NotSupportedException($"Document version {version} is not supported. Supported version is {CurrentVersion}.");
        ValidateUniqueMembers(json.RootElement);
        var document = json.RootElement.Deserialize<Envelope>(Options)?.Document
            ?? throw new FormatException("Missing document.");
        document.Validate();
        return document;
    }

    internal static void ValidateUniqueMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new FormatException($"Duplicate native document member: {property.Name}.");
                ValidateUniqueMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateUniqueMembers(item);
    }

    public override string Serialize(FlowDocument document)
    {
        document.Validate();
        return JsonSerializer.Serialize(new Envelope(CurrentVersion, document), Options);
    }
}
