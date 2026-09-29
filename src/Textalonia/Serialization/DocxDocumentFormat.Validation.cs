using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Textalonia.Serialization;

public sealed partial class DocxDocumentFormat
{
    private static readonly HashSet<string> SafeRelationshipTypes = new(StringComparer.Ordinal)
    {
        R.NamespaceName + "/officeDocument", R.NamespaceName + "/styles", R.NamespaceName + "/numbering",
        R.NamespaceName + "/settings", R.NamespaceName + "/theme", R.NamespaceName + "/fontTable",
        R.NamespaceName + "/font", R.NamespaceName + "/header", R.NamespaceName + "/footer",
        R.NamespaceName + "/footnotes", R.NamespaceName + "/endnotes", R.NamespaceName + "/image",
        R.NamespaceName + "/hyperlink", R.NamespaceName + "/oleObject", R.NamespaceName + "/package", R.NamespaceName + "/glossaryDocument",
        R.NamespaceName + "/customXml", R.NamespaceName + "/customXmlProps", R.NamespaceName + "/custom-properties",
        "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"
    };

    /// <summary>Validate the package graph before model mapping. Only recognized, safe targets are followed.</summary>
    private static void ValidateImportPackage(ZipArchive archive, Func<string, XDocument?> xml, CancellationToken token)
    {
        var names = ValidatePackageNames(archive);
        ValidateContentTypes(xml("[Content_Types].xml"), names);
        var usedParts = new HashSet<string>(StringComparer.Ordinal)
        {
            "[Content_Types].xml", "word/document.xml", "word/styles.xml", "word/numbering.xml",
            "word/settings.xml", "word/fontTable.xml"
        };
        var idsByOwner = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var oleIdsByOwner = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var sawSignature = false;

        foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            var path = entry.FullName;
            if (!TryRelationshipOwner(path, out var owner))
                throw new FormatException($"DOCX relationship part has an invalid path: {path}");
            if (owner.Length > 0 && !names.Contains(owner))
            {
                PackageLoss("relationship-owner-missing", "Relationship part without its owner", "Relationships were omitted.", path);
                usedParts.Add(path);
                continue;
            }
            var relationships = xml(path)?.Root;
            if (relationships?.Name != Rel + "Relationships")
                throw new FormatException($"DOCX relationship part has an invalid root: {path}");
            var elements = relationships.Elements().ToArray();
            if (elements.Length > 8192 || elements.Any(e => e.Name != Rel + "Relationship"))
                throw new FormatException($"DOCX relationship part is malformed or exceeds its entry limit: {path}");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            idsByOwner.Add(owner, ids);
            usedParts.Add(path);
            foreach (var relation in elements)
            {
                token.ThrowIfCancellationRequested();
                var id = (string?)relation.Attribute("Id");
                var type = (string?)relation.Attribute("Type");
                var target = (string?)relation.Attribute("Target");
                var mode = (string?)relation.Attribute("TargetMode");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || !ids.Add(id) ||
                    string.IsNullOrWhiteSpace(type) || type.Length > 512 ||
                    string.IsNullOrWhiteSpace(target) || target.Length > 2048 ||
                    mode is not (null or "Internal" or "External"))
                    throw new FormatException($"DOCX relationship is malformed or has a duplicate ID: {path}");

                if (IsSignatureRelationship(type))
                {
                    sawSignature = true;
                    continue;
                }
                if (IsExcludedRelationship(type))
                {
                    Loss("excluded-relationship", "Excluded Office content relationship", "Content was omitted from the editable document and will not be written.", relation);
                    continue;
                }
                if (!SafeRelationshipTypes.Contains(type))
                {
                    Loss("unsupported-relationship", "Unsupported Office relationship", "Unrecognized relationship and its target were not imported.", relation);
                    continue;
                }
                if (type is not null && IsOleDataRelationship(type) && !HasOleOwner(owner, id, xml, oleIdsByOwner))
                {
                    PackageLoss("ole-unowned", "Embedded OLE data without an owning object", "Relationship and package bytes were omitted.", path);
                    continue;
                }
                if (mode == "External") continue;
                var resolved = ResolveSafePackageTarget(target, owner);
                if (resolved is null)
                    Loss("unsafe-relationship-target", "Unsafe internal relationship target", "Target was not opened or retained.", relation);
                else if (!names.Contains(resolved))
                    Loss("relationship-target-missing", "Missing internal relationship target", "Referenced content was omitted.", relation);
                else usedParts.Add(resolved);
            }
        }

        // Sidecar relationship parts belong to the same supported package feature as their owner.
        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
            if (TryRelationshipOwner(entry.FullName, out var owner) && usedParts.Contains(owner))
                usedParts.Add(entry.FullName);

        foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            token.ThrowIfCancellationRequested();
            var path = entry.FullName;
            if (IsSignaturePart(path)) { sawSignature = true; continue; }
            if (IsExcludedPart(path))
                PackageLoss("excluded-part", "Excluded Office package part", "Part was omitted from the editable document and will not be written.", path);
            else if (!usedParts.Contains(path))
                PackageLoss("unsupported-part", "Unsupported Office package part", "Part was not imported or copied to output.", path);
        }
        if (sawSignature)
            PackageLoss("signature-invalidated", "Existing Office digital signature", "Signature was removed; edited output cannot retain the original signature.", "_xmlsignatures/");

        ValidateReferencedIds(usedParts, idsByOwner, xml, token, exported: false);
    }

    /// <summary>Export gate: the writer may only emit relationships and references with live targets.</summary>
    private static byte[] ValidateWrittenPackage(byte[] bytes, CancellationToken token)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var names = ValidatePackageNames(archive);
        XDocument? Xml(string path)
        {
            var entry = archive.GetEntry(path);
            if (entry is null) return null;
            if (entry.Length > 32 * 1024 * 1024) throw new FormatException("DOCX output XML part exceeds its size limit.");
            using var source = entry.Open();
            using var reader = XmlReader.Create(source, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        ValidateContentTypes(Xml("[Content_Types].xml"), names);
        var idsByOwner = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var oleIdsByOwner = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var ownedEmbeddings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            if (!TryRelationshipOwner(entry.FullName, out var owner) || owner.Length > 0 && !names.Contains(owner))
                throw new FormatException("DOCX output has an orphan relationship part.");
            var root = Xml(entry.FullName)?.Root;
            if (root?.Name != Rel + "Relationships") throw new FormatException("DOCX output has malformed relationships.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            idsByOwner.Add(owner, ids);
            foreach (var relation in root.Elements(Rel + "Relationship"))
            {
                var id = (string?)relation.Attribute("Id");
                var type = (string?)relation.Attribute("Type");
                var target = (string?)relation.Attribute("Target");
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || type is null || target is null ||
                    !SafeRelationshipTypes.Contains(type))
                    throw new FormatException("DOCX output has an unsupported or malformed relationship.");
                if (IsOleDataRelationship(type) && !HasOleOwner(owner, id, Xml, oleIdsByOwner))
                    throw new FormatException("DOCX output has an unowned embedded object relationship.");
                if ((string?)relation.Attribute("TargetMode") == "External") continue;
                var resolved = ResolveSafePackageTarget(target, owner);
                if (resolved is null || !names.Contains(resolved))
                    throw new FormatException("DOCX output has a dangling relationship target.");
                if (IsOleDataRelationship(type)) ownedEmbeddings.Add(resolved);
            }
        }
        if (names.Any(name => name.StartsWith("word/embeddings/", StringComparison.Ordinal) && !ownedEmbeddings.Contains(name)))
            throw new FormatException("DOCX output has an unowned embedded package part.");
        ValidateReferencedIds(names, idsByOwner, Xml, token, exported: true);
        return bytes;
    }

    private static bool IsOleDataRelationship(string type) => type is not null &&
        (type == R.NamespaceName + "/oleObject" || type == R.NamespaceName + "/package");

    private static bool HasOleOwner(string owner, string id, Func<string, XDocument?> xml,
        Dictionary<string, HashSet<string>> cache)
    {
        if (!owner.StartsWith("word/", StringComparison.Ordinal) || !owner.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return false;
        if (!cache.TryGetValue(owner, out var ids))
        {
            ids = xml(owner)?.Descendants(O + "OLEObject").Select(element => (string?)element.Attribute(R + "id"))
                .OfType<string>().ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            cache.Add(owner, ids);
        }
        return ids.Contains(id);
    }

    private static HashSet<string> ValidatePackageNames(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName;
            if (path.EndsWith('/'))
            {
                if (entry.Length != 0 || path.Length < 2 || path.Length > 1024 || path.StartsWith('/') ||
                    path[..^1].Split('/').Any(segment => segment is "" or "." or "..") ||
                    path.Any(c => c is '\\' or '?' or '#' or ':' || char.IsControl(c)))
                    throw new FormatException($"DOCX package has an unsafe directory entry: {path}");
                continue;
            }
            if (path.Length == 0 || path.Length > 1024 || path.StartsWith('/') ||
                path.Split('/').Any(segment => segment is "" or "." or "..") ||
                path.Any(c => c is '\\' or '?' or '#' or ':' || char.IsControl(c)) ||
                !names.Add(path) || !insensitive.Add(path))
                throw new FormatException($"DOCX package has an unsafe or duplicate part name: {path}");
        }
        return names;
    }

    private static void ValidateContentTypes(XDocument? xml, HashSet<string> names)
    {
        if (xml is null) return; // Legacy test fixtures and some minimal packages omit the manifest.
        if (xml.Root?.Name != Ct + "Types") throw new FormatException("DOCX content types manifest is malformed.");
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in xml.Root.Elements())
        {
            if (element.Name == Ct + "Default")
            {
                var extension = (string?)element.Attribute("Extension");
                if (string.IsNullOrWhiteSpace(extension) || string.IsNullOrWhiteSpace((string?)element.Attribute("ContentType")) ||
                    !defaults.Add(extension)) throw new FormatException("DOCX content types manifest has an invalid default.");
            }
            else if (element.Name == Ct + "Override")
            {
                var part = (string?)element.Attribute("PartName");
                if (string.IsNullOrWhiteSpace(part) || !part.StartsWith('/') ||
                    string.IsNullOrWhiteSpace((string?)element.Attribute("ContentType")) ||
                    !overrides.Add(part[1..])) throw new FormatException("DOCX content types manifest has an invalid override.");
                if (!names.Contains(part[1..]))
                    PackageLoss("content-type-orphan", "Content type for an absent package part", "Unused content type entry was ignored.", "[Content_Types].xml");
            }
            else throw new FormatException("DOCX content types manifest has an unknown entry.");
        }
    }

    private static void ValidateReferencedIds(HashSet<string> names, Dictionary<string, HashSet<string>> idsByOwner,
        Func<string, XDocument?> xml, CancellationToken token, bool exported)
    {
        foreach (var path in names.Where(name => name.StartsWith("word/", StringComparison.Ordinal) &&
                     name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !name.Contains("/_rels/", StringComparison.Ordinal)))
        {
            token.ThrowIfCancellationRequested();
            var document = xml(path);
            if (document?.Root is null) continue;
            idsByOwner.TryGetValue(path, out var ids);
            foreach (var attribute in document.Descendants().Attributes().Where(a => a.Name.Namespace == R &&
                         a.Name.LocalName is "id" or "embed" or "link"))
            {
                if (ids?.Contains(attribute.Value) == true) continue;
                if (exported) throw new FormatException($"DOCX output has a dangling relationship ID in {path}.");
                // Existing readers give more useful feature-specific diagnostics for these references.
                if (attribute.Parent?.Name.LocalName is "headerReference" or "footerReference" or "blip" or
                    "hyperlink" or "OLEObject" or "object" or "embedRegular" or "embedBold" or
                    "embedItalic" or "embedBoldItalic") continue;
                PackageLoss("relationship-id-missing", "Unresolved Office relationship ID", "Referenced content was omitted.", path);
            }
        }
    }

    private static bool TryRelationshipOwner(string path, out string owner)
    {
        if (path == "_rels/.rels") { owner = ""; return true; }
        const string marker = "/_rels/";
        var offset = path.LastIndexOf(marker, StringComparison.Ordinal);
        if (offset < 0 || !path.EndsWith(".rels", StringComparison.Ordinal)) { owner = ""; return false; }
        var suffix = path[(offset + marker.Length)..^5];
        if (suffix.Length == 0 || suffix.Contains('/')) { owner = ""; return false; }
        owner = path[..(offset + 1)] + suffix;
        return true;
    }

    private static string? ResolveSafePackageTarget(string target, string owner)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Any(c => c is '\\' or '?' or '#' or ':' || char.IsControl(c))) return null;
        var basePath = owner.Contains('/') ? owner[..(owner.LastIndexOf('/') + 1)] : "";
        var full = target.StartsWith('/') ? target[1..] : basePath + target;
        var result = new List<string>();
        foreach (var segment in full.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..") { if (result.Count == 0) return null; result.RemoveAt(result.Count - 1); }
            else result.Add(segment);
        }
        return result.Count == 0 ? null : string.Join('/', result);
    }

    private static bool IsSignatureRelationship(string type) =>
        type.StartsWith("http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/", StringComparison.Ordinal);

    private static bool IsSignaturePart(string path) =>
        path.StartsWith("_xmlsignatures/", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".sigs", StringComparison.OrdinalIgnoreCase);

    private static bool IsExcludedRelationship(string type)
    {
        var suffix = type[(type.LastIndexOf('/') + 1)..];
        return suffix is "vbaProject" or "vbaData" or "activeXControl" or "control" or "controlPackage" or
            "chart" or "drawing" or "comments" or "commentAuthors" or "commentsExtended" or "people" or
            "threadedComment" or "diagramData" or "diagramLayout" or "diagramColors" or "diagramQuickStyle";
    }

    private static bool IsExcludedPart(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Contains("/activex/", StringComparison.Ordinal) || lower.Contains("/charts/", StringComparison.Ordinal) ||
            lower.Contains("/drawings/", StringComparison.Ordinal) || lower.Contains("/diagrams/", StringComparison.Ordinal) ||
            lower.Contains("/threadedcomments/", StringComparison.Ordinal) || lower.EndsWith("/vbaproject.bin", StringComparison.Ordinal) ||
            lower.EndsWith("/vbadata.xml", StringComparison.Ordinal) || lower.StartsWith("word/comments", StringComparison.Ordinal) ||
            lower is "word/people.xml";
    }

    private static void PackageLoss(string code, string feature, string fallback, string path) =>
        ConversionDiagnostics.Report("docx." + code, feature, fallback, sourceLocation: path);
}
