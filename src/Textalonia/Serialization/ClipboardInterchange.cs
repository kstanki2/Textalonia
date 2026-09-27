using System.Globalization;
using System.Text;
using System.Text.Json;
using Textalonia.Editing;

namespace Textalonia.Serialization;

internal static class ClipboardInterchange
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Serialize(DocumentFragment fragment)
    {
        fragment.Validate();
        using var native = JsonDocument.Parse(DocumentFormats.Json.Serialize(fragment.Document), new JsonDocumentOptions { MaxDepth = 256 });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("fragmentVersion", fragment.Version);
            writer.WriteBoolean("startsInsideParagraph", fragment.StartsInsideParagraph);
            writer.WriteBoolean("endsInsideParagraph", fragment.EndsInsideParagraph);
            writer.WritePropertyName("nativeDocument"); native.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static DocumentFragment Parse(string payload)
    {
        if (Encoding.UTF8.GetByteCount(payload) > MaximumBytes) throw new FormatException("Clipboard payload exceeds the 32 MB limit.");
        using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 260 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Missing clipboard envelope.");
        if (!root.TryGetProperty("fragmentVersion", out var version))
        {
            var legacy = DocumentFormats.Json.Parse(payload);
            ConversionDiagnostics.Report("clipboard.legacy-native", "Older clipboard document envelope",
                "The document is inserted with default paragraph boundary behavior.", severity: ConversionDiagnosticSeverity.Information);
            return new() { Document = legacy, StartsInsideParagraph = true, EndsInsideParagraph = true };
        }
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            throw new FormatException("Clipboard version must be an integer.");
        if (number is not (1 or 2 or DocumentFragment.CurrentVersion)) throw new NotSupportedException($"Clipboard fragment version {number} is not supported.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not ("fragmentVersion" or "startsInsideParagraph" or "endsInsideParagraph" or "nativeDocument"))
                throw new FormatException("Unknown or duplicate clipboard member.");
        if (!root.TryGetProperty("nativeDocument", out var document)) throw new FormatException("Missing clipboard document.");
        bool Flag(string key)
        {
            if (!root.TryGetProperty(key, out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("Missing or invalid clipboard boundary flag.");
            return flag.GetBoolean();
        }
        return new() { Version = number, Document = DocumentFormats.Json.Parse(document.GetRawText()),
            StartsInsideParagraph = Flag("startsInsideParagraph"), EndsInsideParagraph = Flag("endsInsideParagraph") };
    }

    internal static byte[] EncodeWindowsHtml(string html)
    {
        var bodyStart = html.IndexOf("<body>", StringComparison.OrdinalIgnoreCase);
        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        var fragment = bodyStart >= 0 && bodyEnd >= bodyStart ? html[(bodyStart + 6)..bodyEnd] : html;
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        const string template = "Version:1.0\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\nStartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, template, 0, 0, 0, 0));
        var start = headerLength + Encoding.UTF8.GetByteCount(prefix);
        var end = start + Encoding.UTF8.GetByteCount(fragment);
        var header = string.Format(CultureInfo.InvariantCulture, template, headerLength, end + Encoding.UTF8.GetByteCount(suffix), start, end);
        return Encoding.UTF8.GetBytes(header + prefix + fragment + suffix);
    }

    internal static string DecodeWindowsHtml(byte[] payload)
    {
        if (payload.Length > MaximumBytes) throw new FormatException("Clipboard HTML exceeds the 32 MB limit.");
        var header = Encoding.ASCII.GetString(payload, 0, Math.Min(payload.Length, 4096));
        int? Offset(string name)
        {
            var line = header.Split('\n').FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
            if (line is null) return null;
            if (!int.TryParse(line[(name.Length + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new FormatException("Invalid Windows HTML offset.");
            return value;
        }
        var start = Offset("StartFragment"); var end = Offset("EndFragment");
        try
        {
            if (start is >= 0 && end is >= 0)
            {
                if (end < start || end > payload.Length) throw new FormatException("Windows HTML fragment offsets exceed the payload.");
                return StrictUtf8.GetString(payload, start.Value, end.Value - start.Value);
            }
            if (start is >= 0 || end is >= 0) throw new FormatException("Incomplete Windows HTML fragment offsets.");
            return ExtractHtmlFragment(StrictUtf8.GetString(payload).TrimEnd('\0'));
        }
        catch (DecoderFallbackException ex) { throw new FormatException("Clipboard HTML is not valid UTF-8.", ex); }
    }

    internal static string ExtractHtmlFragment(string html)
    {
        var start = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var end = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        return start >= 0 && end > start ? html[(start + 20)..end] : html;
    }
}
