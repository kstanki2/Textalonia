using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>
/// Word's Flat OPC XML package representation. The document mapping is the same as DOCX;
/// package conversion is bounded and never resolves external XML entities or resources.
/// </summary>
public sealed class FlatOpcDocumentFormat : IDocumentFormat
{
    private static readonly XNamespace Pkg = "http://schemas.microsoft.com/office/2006/xmlPackage";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const int MaxInputBytes = 32 * 1024 * 1024;
    private const int MaxPartBytes = 32 * 1024 * 1024;
    private const long MaxPackageBytes = 128L * 1024 * 1024;
    private const int MaxParts = 4096;
    private const int MaxXmlDepth = 256;

    public string Name => "Flat OPC XML";
    public IReadOnlyList<string> Extensions => [".flatopc", ".flatopc.xml"];

    public async Task<FlowDocument> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = await DocumentFormats.ReadLimitedAsync(stream, cancellationToken, MaxInputBytes);
        var package = await Task.Run(() => ToPackage(bytes, cancellationToken), cancellationToken);
        using var source = new MemoryStream(package, writable: false);
        return await DocumentFormats.Docx.LoadAsync(source, cancellationToken);
    }

    public async Task SaveAsync(FlowDocument document, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        document.Validate();
        using var package = new MemoryStream();
        await DocumentFormats.Docx.SaveAsync(document, package, cancellationToken);
        var flatOpc = await Task.Run(() => FromPackage(package.ToArray(), cancellationToken), cancellationToken);
        await stream.WriteAsync(flatOpc, cancellationToken);
    }

    private static byte[] ToPackage(byte[] source, CancellationToken token)
    {
        var xml = LoadXml(source, "Flat OPC package", token);
        if (xml.Root?.Name != Pkg + "package") throw new FormatException("Expected a Flat OPC package root.");
        var parts = xml.Root.Elements().ToArray();
        if (parts.Length == 0 || parts.Length > MaxParts || parts.Any(p => p.Name != Pkg + "part"))
            throw new FormatException("Flat OPC package has invalid parts or exceeds the part limit.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = new List<XElement>(parts.Length);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            long totalBytes = 0;
            foreach (var part in parts)
            {
                token.ThrowIfCancellationRequested();
                var name = ValidatePartName((string?)part.Attribute(Pkg + "name"));
                if (!names.Add(name)) throw new FormatException($"Duplicate Flat OPC part: /{name}");
                var contentType = (string?)part.Attribute(Pkg + "contentType");
                if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 512 ||
                    contentType.Any(char.IsControl))
                    throw new FormatException($"Flat OPC part /{name} has no valid content type.");
                var payloads = part.Elements().ToArray();
                if (payloads.Length != 1 || part.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                    throw new FormatException($"Flat OPC part /{name} must have one payload.");
                byte[] data;
                if (payloads[0].Name == Pkg + "xmlData") data = XmlPartBytes(payloads[0], name);
                else if (payloads[0].Name == Pkg + "binaryData")
                {
                    if (payloads[0].HasElements) throw new FormatException($"Flat OPC binary part /{name} is malformed.");
                    try { data = Convert.FromBase64String(payloads[0].Value); }
                    catch (FormatException error) { throw new FormatException($"Flat OPC binary part /{name} has invalid base64 data.", error); }
                }
                else throw new FormatException($"Flat OPC part /{name} has an unknown payload.");
                if (data.Length > MaxPartBytes || totalBytes + data.Length > MaxPackageBytes)
                    throw new FormatException("Flat OPC package content exceeds its size limit.");
                totalBytes += data.Length;
                using (var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open()) entry.Write(data);
                types.Add(new XElement(ContentTypes + "Override", new XAttribute("PartName", "/" + name),
                    new XAttribute("ContentType", contentType)));
            }
            if (!names.Contains("word/document.xml")) throw new FormatException("Flat OPC package has no Word document part.");
            var manifest = new XDocument(new XElement(ContentTypes + "Types", types));
            using var manifestEntry = zip.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal).Open();
            manifest.Save(manifestEntry);
        }
        return output.ToArray();
    }

    private static byte[] FromPackage(byte[] package, CancellationToken token)
    {
        using var zip = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxParts + 1 || zip.Entries.Sum(e => e.Length) > MaxPackageBytes)
            throw new FormatException("DOCX package exceeds Flat OPC export limits.");
        var manifestEntry = zip.GetEntry("[Content_Types].xml")
            ?? throw new FormatException("DOCX package has no content type manifest.");
        var manifest = LoadXml(ReadEntry(manifestEntry, token), "content type manifest", token);
        if (manifest.Root?.Name != ContentTypes + "Types") throw new FormatException("DOCX content type manifest is invalid.");
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Root.Elements())
        {
            if (item.Name == ContentTypes + "Default")
            {
                var extension = (string?)item.Attribute("Extension");
                var type = (string?)item.Attribute("ContentType");
                if (string.IsNullOrWhiteSpace(extension) || string.IsNullOrWhiteSpace(type) || !defaults.TryAdd(extension, type))
                    throw new FormatException("DOCX content type manifest contains an invalid default.");
            }
            else if (item.Name == ContentTypes + "Override")
            {
                var name = ValidatePartName((string?)item.Attribute("PartName"));
                var type = (string?)item.Attribute("ContentType");
                if (string.IsNullOrWhiteSpace(type) || !overrides.TryAdd(name, type))
                    throw new FormatException("DOCX content type manifest contains an invalid override.");
            }
            else throw new FormatException("DOCX content type manifest contains an unknown entry.");
        }

        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false
        }))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("pkg", "package", Pkg.NamespaceName);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries.Where(e => e.FullName != "[Content_Types].xml"))
            {
                token.ThrowIfCancellationRequested();
                var name = ValidatePartName("/" + entry.FullName);
                if (!names.Add(name)) throw new FormatException($"Duplicate DOCX package part: /{name}");
                var extension = Path.GetExtension(name).TrimStart('.');
                if (!overrides.TryGetValue(name, out var type) && !defaults.TryGetValue(extension, out type))
                    throw new FormatException($"DOCX package part /{name} has no content type.");
                var bytes = ReadEntry(entry, token);
                writer.WriteStartElement("pkg", "part", Pkg.NamespaceName);
                writer.WriteAttributeString("pkg", "name", Pkg.NamespaceName, "/" + name);
                writer.WriteAttributeString("pkg", "contentType", Pkg.NamespaceName, type);
                if (IsXmlType(type))
                {
                    var xml = LoadXml(bytes, name, token);
                    writer.WriteStartElement("pkg", "xmlData", Pkg.NamespaceName);
                    foreach (var node in xml.Nodes().Where(node => node is not XText)) node.WriteTo(writer);
                    writer.WriteEndElement();
                }
                else
                {
                    writer.WriteStartElement("pkg", "binaryData", Pkg.NamespaceName);
                    writer.WriteBase64(bytes, 0, bytes.Length);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                if (output.Length > MaxPackageBytes * 2) throw new FormatException("Flat OPC output exceeds its size limit.");
            }
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return output.ToArray();
    }

    private static string ValidatePartName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '/' || value.Length > 1024)
            throw new FormatException("Flat OPC part name is invalid.");
        var name = value[1..];
        if (name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) ||
            name.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..") ||
            name.Any(c => c is '\\' or '%' or '?' or '#' or ':' || char.IsControl(c)))
            throw new FormatException($"Flat OPC part name is unsafe: {value}");
        return name;
    }

    private static byte[] XmlPartBytes(XElement payload, string name)
    {
        var elements = payload.Elements().ToArray();
        if (elements.Length != 1 || payload.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
            throw new FormatException($"Flat OPC XML part /{name} must have one root element.");
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), CloseOutput = false
        }))
        {
            writer.WriteStartDocument();
            foreach (var node in payload.Nodes().Where(node => node is not XText)) node.WriteTo(writer);
            writer.WriteEndDocument();
        }
        return output.ToArray();
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, CancellationToken token)
    {
        if (entry.Length > MaxPartBytes) throw new FormatException("DOCX package part exceeds Flat OPC export limits.");
        using var source = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (output.Length + count > MaxPartBytes) throw new FormatException("DOCX package part exceeds Flat OPC export limits.");
            output.Write(buffer, 0, count);
        }
        if (output.Length != entry.Length) throw new FormatException("DOCX package part has an inconsistent length.");
        return output.ToArray();
    }

    private static XDocument LoadXml(byte[] bytes, string description, CancellationToken token)
    {
        if (bytes.Length > MaxPartBytes) throw new FormatException($"{description} exceeds its XML size limit.");
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            using (var reader = XmlReader.Create(source, ReaderSettings()))
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.Depth > MaxXmlDepth) throw new FormatException($"{description} exceeds the XML depth limit.");
                }
            }
            source.Position = 0;
            using var documentReader = XmlReader.Create(source, ReaderSettings());
            return XDocument.Load(documentReader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException error) { throw new FormatException($"{description} is malformed XML.", error); }
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = MaxPartBytes,
        IgnoreWhitespace = false
    };

    private static bool IsXmlType(string contentType) =>
        contentType.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("text/xml", StringComparison.OrdinalIgnoreCase) ||
        contentType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
}
