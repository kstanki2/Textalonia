using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
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
    public override string Serialize(FlowDocument document) => document.Text;
}

/// <summary>Versioned, lossless native storage, including hidden cells retained by table merges.</summary>
public sealed class JsonDocumentFormat : TextDocumentFormat
{
    private sealed record Envelope(int Version, FlowDocument Document);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 128,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    public override string Name => "Textalonia document";
    public override IReadOnlyList<string> Extensions => [".textalonia", ".json", ".art"];
    public override FlowDocument Parse(string text)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(text, Options) ?? throw new FormatException("Empty document.");
        if (envelope.Version != 1) throw new NotSupportedException($"Document version {envelope.Version} is not supported.");
        if (envelope.Document is null) throw new FormatException("Missing document.");
        envelope.Document.Validate();
        return envelope.Document;
    }
    public override string Serialize(FlowDocument document)
    {
        document.Validate();
        return JsonSerializer.Serialize(new Envelope(1, document), Options);
    }
}
