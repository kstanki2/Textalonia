using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>Bounded MHTML interchange over the HTML codec. MIME resources are never fetched from the network.</summary>
public sealed class MhtmlDocumentFormat : IDocumentFormat
{
    private const int MaximumParts = 4096;
    private const int MaximumHeaders = 128;
    private const int MaximumHeaderBytes = 64 * 1024;
    private const int MaximumDocumentBytes = 32 * 1024 * 1024;
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp" };

    public string Name => "MHTML";
    public IReadOnlyList<string> Extensions => [".mht", ".mhtml"];

    public async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken);
        return await Task.Run(() => Read(bytes, cancellationToken), cancellationToken);
    }

    public async Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        var bytes = await Task.Run(() => Write(document, cancellationToken), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private sealed record MimePart(Dictionary<string, string> Headers, byte[] Data, int Number)
    {
        public string ContentType => MediaType(Header("Content-Type"));
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    private static FlowDocument Read(byte[] bytes, CancellationToken token)
    {
        var position = 0;
        var headers = ReadHeaders(bytes, ref position, bytes.Length);
        var contentType = headers.GetValueOrDefault("Content-Type") ?? throw new FormatException("MHTML Content-Type is missing.");
        List<MimePart> parts;
        if (MediaType(contentType).Equals("multipart/related", StringComparison.OrdinalIgnoreCase))
        {
            var boundary = Parameter(contentType, "boundary") ?? throw new FormatException("MHTML boundary is missing.");
            if (boundary.Length is < 1 or > 70 || boundary.Any(c => c is < '!' or > '~' || c == '"'))
                throw new FormatException("Invalid MHTML boundary.");
            parts = ReadParts(bytes, position, boundary, token);
        }
        else if (MediaType(contentType).Equals("text/html", StringComparison.OrdinalIgnoreCase))
            parts = [new MimePart(headers, bytes[position..], 1)];
        else throw new FormatException("MHTML must contain a related HTML document.");

        var start = Parameter(contentType, "start")?.Trim('<', '>');
        var root = start is null
            ? parts.FirstOrDefault(p => p.ContentType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            : parts.FirstOrDefault(p => string.Equals(ContentId(p), start, StringComparison.OrdinalIgnoreCase));
        if (root is null) throw new FormatException("MHTML HTML root part is missing.");
        if (!root.ContentType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("MHTML root part is not HTML.");

        long decodedBytes = 0;
        var decodedParts = new Dictionary<int, byte[]>();
        byte[] Decode(MimePart part)
        {
            if (decodedParts.TryGetValue(part.Number, out var cached)) return cached;
            token.ThrowIfCancellationRequested();
            var data = DecodeTransfer(part);
            if (data.Length > (part == root ? MaximumDocumentBytes : DocumentResource.MaximumEmbeddedBytes) ||
                (decodedBytes += data.Length) > MaximumDocumentBytes)
                throw new FormatException("MHTML decoded parts exceed the import limits.");
            decodedParts.Add(part.Number, data);
            return data;
        }

        var html = DecodeText(Decode(root), Parameter(root.Header("Content-Type"), "charset"));
        var document = new HtmlParser().ParseDocument(html);
        var resources = new Dictionary<string, MimePart>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Where(p => p != root))
        {
            var id = ContentId(part);
            if (id is not null && !resources.TryAdd("cid:" + id, part)) throw new FormatException("Duplicate MHTML Content-ID.");
            if (part.Header("Content-Location") is { Length: > 0 } location)
            {
                if (!resources.TryAdd(location, part)) throw new FormatException("Duplicate MHTML Content-Location.");
                var canonical = CanonicalLocation(location, root.Header("Content-Location"));
                if (canonical != location && !resources.TryAdd(canonical, part)) throw new FormatException("Ambiguous MHTML resource location.");
            }
        }

        var used = new HashSet<int>();
        var imageNumber = 0;
        foreach (var image in document.QuerySelectorAll("img[src]"))
        {
            token.ThrowIfCancellationRequested();
            imageNumber++;
            var source = image.GetAttribute("src") ?? "";
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var key = source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
                ? "cid:" + Uri.UnescapeDataString(source[4..]).Trim('<', '>')
                : source;
            if (!resources.TryGetValue(key, out var part))
                resources.TryGetValue(CanonicalLocation(key, root.Header("Content-Location")), out part);
            if (part is null)
            {
                ConversionDiagnostics.Report("mhtml.resource-missing", "MHTML image resource", "Image alternative text was retained; no external resource was fetched.", sourceLocation: $"html:img[{imageNumber}]");
                continue;
            }
            used.Add(part.Number);
            if (!ImageTypes.Contains(part.ContentType))
            {
                ConversionDiagnostics.Report("mhtml.resource-type", "Unsupported MHTML image media type", "Image alternative text was retained.", sourceLocation: $"part:{part.Number}");
                continue;
            }
            var data = Decode(part);
            image.SetAttribute("src", "data:" + part.ContentType.ToLowerInvariant() + ";base64," + Convert.ToBase64String(data));
        }
        foreach (var part in parts.Where(p => p != root && !used.Contains(p.Number)))
            ConversionDiagnostics.Report("mhtml.unused-part", "Unreferenced or unsupported MIME resource", "Part was omitted from the document.", sourceLocation: $"part:{part.Number}");
        return DocumentFormats.Html.Parse(document.DocumentElement?.OuterHtml ?? html);
    }

    private static byte[] Write(FlowDocument source, CancellationToken token)
    {
        var html = DocumentFormats.Html.Serialize(source);
        var document = new HtmlParser().ParseDocument(html);
        var images = new Dictionary<string, (string Id, string Type, string Base64)>(StringComparer.Ordinal);
        foreach (var image in document.QuerySelectorAll("img[src]"))
        {
            token.ThrowIfCancellationRequested();
            var value = image.GetAttribute("src") ?? "";
            var match = Regex.Match(value, "^data:(image/(?:png|jpeg|gif|webp|bmp));base64,([A-Za-z0-9+/=]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            if (!match.Success) continue;
            if (!images.TryGetValue(value, out var resource))
            {
                resource = ($"textalonia-image-{images.Count + 1}@local", match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value);
                images.Add(value, resource);
            }
            image.SetAttribute("src", "cid:" + resource.Id);
        }
        html = "<!DOCTYPE html>" + (document.DocumentElement?.OuterHtml ?? html);
        var boundary = "textalonia-" + Guid.NewGuid().ToString("N");
        var builder = new StringBuilder();
        builder.Append("MIME-Version: 1.0\r\nContent-Type: multipart/related; type=\"text/html\"; boundary=\"")
            .Append(boundary).Append("\"; start=\"<textalonia-document@local>\"\r\n\r\n");
        builder.Append("--").Append(boundary).Append("\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n")
            .Append("Content-ID: <textalonia-document@local>\r\nContent-Location: document.html\r\n\r\n");
        AppendBase64(builder, Convert.ToBase64String(Encoding.UTF8.GetBytes(html)));
        foreach (var image in images.Values)
        {
            token.ThrowIfCancellationRequested();
            builder.Append("--").Append(boundary).Append("\r\nContent-Type: ").Append(image.Type)
                .Append("\r\nContent-Transfer-Encoding: base64\r\nContent-ID: <").Append(image.Id).Append(">\r\n\r\n");
            AppendBase64(builder, image.Base64);
        }
        builder.Append("--").Append(boundary).Append("--\r\n");
        if (builder.Length > MaximumDocumentBytes) throw new FormatException("MHTML output exceeds the 32 MB package limit.");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static void AppendBase64(StringBuilder builder, string value)
    {
        for (var offset = 0; offset < value.Length; offset += 76)
            builder.Append(value, offset, Math.Min(76, value.Length - offset)).Append("\r\n");
    }

    private static List<MimePart> ReadParts(byte[] bytes, int position, string boundary, CancellationToken token)
    {
        var marker = Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new List<MimePart>();
        var partStart = -1;
        var closed = false;
        while (position < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var lineStart = position;
            var line = NextLine(bytes, ref position, bytes.Length);
            var candidate = bytes.AsSpan(lineStart, line);
            if (!candidate.StartsWith(marker)) continue;
            var tail = candidate[marker.Length..];
            while (!tail.IsEmpty && tail[^1] is (byte)' ' or (byte)'\t') tail = tail[..^1];
            if (!tail.IsEmpty && !tail.SequenceEqual("--"u8)) continue;
            if (partStart >= 0)
            {
                var end = lineStart;
                if (end > partStart && bytes[end - 1] == '\n') { end--; if (end > partStart && bytes[end - 1] == '\r') end--; }
                var headerPosition = partStart;
                var headers = ReadHeaders(bytes, ref headerPosition, end);
                parts.Add(new MimePart(headers, bytes[headerPosition..end], parts.Count + 1));
                if (parts.Count > MaximumParts) throw new FormatException("MHTML contains too many parts.");
            }
            if (tail.SequenceEqual("--"u8)) { closed = true; break; }
            partStart = position;
        }
        if (!closed || parts.Count == 0) throw new FormatException("MHTML multipart boundary is incomplete.");
        return parts;
    }

    private static Dictionary<string, string> ReadHeaders(byte[] bytes, ref int position, int end)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var size = 0;
        string? current = null;
        for (var count = 0; count < MaximumHeaders && position < end; count++)
        {
            var start = position;
            var length = NextLine(bytes, ref position, end);
            size += position - start;
            if (size > MaximumHeaderBytes || length > 8192) throw new FormatException("MHTML headers exceed their limits.");
            if (length == 0) return result;
            var line = Encoding.Latin1.GetString(bytes, start, length);
            if (line[0] is ' ' or '\t')
            {
                if (current is null) throw new FormatException("Malformed folded MHTML header.");
                result[current] += " " + line.Trim();
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Any(c => !(char.IsLetterOrDigit(c) || c == '-')))
                throw new FormatException("Malformed MHTML header.");
            current = line[..colon];
            if (!result.TryAdd(current, line[(colon + 1)..].Trim())) throw new FormatException("Duplicate MHTML header.");
        }
        throw new FormatException("MHTML header separator is missing.");
    }

    private static int NextLine(byte[] bytes, ref int position, int end)
    {
        var start = position;
        while (position < end && bytes[position] != '\n') position++;
        var length = position - start;
        if (length > 0 && bytes[start + length - 1] == '\r') length--;
        if (position < end) position++;
        return length;
    }

    private static byte[] DecodeTransfer(MimePart part)
    {
        var transfer = part.Header("Content-Transfer-Encoding")?.Trim() ?? "7bit";
        if (transfer.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            try { return Convert.FromBase64String(Encoding.ASCII.GetString(part.Data)); }
            catch (FormatException ex) { throw new FormatException($"Invalid MHTML base64 in part {part.Number}.", ex); }
        }
        if (transfer.Equals("quoted-printable", StringComparison.OrdinalIgnoreCase))
        {
            using var output = new MemoryStream();
            for (var i = 0; i < part.Data.Length; i++)
            {
                if (part.Data[i] != '=') { output.WriteByte(part.Data[i]); continue; }
                if (i + 1 < part.Data.Length && part.Data[i + 1] == '\n') { i++; continue; }
                if (i + 2 < part.Data.Length && part.Data[i + 1] == '\r' && part.Data[i + 2] == '\n') { i += 2; continue; }
                if (i + 2 >= part.Data.Length || !TryHex(part.Data[i + 1], out var high) || !TryHex(part.Data[i + 2], out var low))
                    throw new FormatException($"Invalid MHTML quoted-printable content in part {part.Number}.");
                output.WriteByte((byte)((high << 4) | low));
                i += 2;
            }
            return output.ToArray();
        }
        if (transfer.Equals("7bit", StringComparison.OrdinalIgnoreCase))
        {
            if (part.Data.Any(b => b > 127)) throw new FormatException($"Non-ASCII 7bit content in MHTML part {part.Number}.");
            return part.Data;
        }
        if (transfer.Equals("8bit", StringComparison.OrdinalIgnoreCase) || transfer.Equals("binary", StringComparison.OrdinalIgnoreCase)) return part.Data;
        throw new FormatException($"Unsupported MHTML transfer encoding in part {part.Number}.");
    }

    private static bool TryHex(byte value, out int result)
    {
        result = value switch { >= (byte)'0' and <= (byte)'9' => value - '0', >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
            >= (byte)'a' and <= (byte)'f' => value - 'a' + 10, _ => -1 };
        return result >= 0;
    }

    private static string DecodeText(byte[] bytes, string? charset)
    {
        charset = charset?.Trim().ToLowerInvariant() ?? "utf-8";
        Encoding encoding = charset switch
        {
            "utf-8" or "utf8" => new UTF8Encoding(false, true),
            "us-ascii" or "ascii" => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            "iso-8859-1" or "latin1" => Encoding.Latin1,
            "utf-16" or "utf-16le" => new UnicodeEncoding(false, true, true),
            "utf-16be" => new UnicodeEncoding(true, true, true),
            "windows-1252" => Encoding.Latin1,
            _ => throw new FormatException("Unsupported MHTML HTML charset: " + charset)
        };
        var text = encoding.GetString(bytes);
        if (charset == "windows-1252")
        {
            const string high = "\u20ac\u0081\u201a\u0192\u201e\u2026\u2020\u2021\u02c6\u2030\u0160\u2039\u0152\u008d\u017d\u008f\u0090\u2018\u2019\u201c\u201d\u2022\u2013\u2014\u02dc\u2122\u0161\u203a\u0153\u009d\u017e\u0178";
            text = new string(text.Select(c => c is >= '\u0080' and <= '\u009f' ? high[c - '\u0080'] : c).ToArray());
        }
        return text.TrimStart('\uFEFF');
    }

    private static string MediaType(string? header) => (header ?? "").Split(';', 2)[0].Trim();
    private static string? Parameter(string? header, string name)
    {
        if (header is null) return null;
        var match = Regex.Match(header, @"(?:^|;)\s*" + Regex.Escape(name) + @"\s*=\s*(?:""([^""]*)""|([^;\s]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
    }
    private static string? ContentId(MimePart part) => part.Header("Content-ID")?.Trim().Trim('<', '>');
    private static string CanonicalLocation(string location, string? rootLocation)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var absolute)) return absolute.AbsoluteUri;
        if (rootLocation is not null && Uri.TryCreate(rootLocation, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, location, out absolute))
            return absolute.AbsoluteUri;
        return location;
    }
}
