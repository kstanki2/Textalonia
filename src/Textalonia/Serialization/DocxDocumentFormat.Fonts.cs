using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class DocxDocumentFormat
{
    private static ImmutableArray<DocumentFontDefinition> ReadEmbeddedFonts(XElement? root, XDocument? rels,
        Func<string, int, byte[]> readPart, ImmutableDictionary<string, DocumentResource>.Builder resources)
    {
        if (root is null) return [];
        var definitions = ImmutableArray.CreateBuilder<DocumentFontDefinition>();
        var relationships = rels?.Root?.Elements(Rel + "Relationship").Where(e => e.Attribute("Id") is not null)
            .ToDictionary(e => (string)e.Attribute("Id")!, StringComparer.Ordinal) ?? [];
        var faces = new HashSet<(string Family, int Weight, bool Italic)>();
        foreach (var font in root.Elements(W + "font"))
        {
            var family = (string?)font.Attribute(W + "name");
            if (string.IsNullOrWhiteSpace(family)) throw new FormatException("Embedded DOCX font has no family name.");
            foreach (var embedded in font.Elements().Where(e => e.Name.LocalName is "embedRegular" or "embedBold" or "embedItalic" or "embedBoldItalic"))
            {
                if (definitions.Count >= 256) throw new FormatException("DOCX contains too many embedded font faces.");
                var italic = embedded.Name.LocalName.Contains("Italic", StringComparison.Ordinal);
                var weight = embedded.Name.LocalName.Contains("Bold", StringComparison.Ordinal) ? 700 : 400;
                if (!faces.Add((family.ToUpperInvariant(), weight, italic))) throw new FormatException("Duplicate DOCX embedded font face.");
                var relationId = (string?)embedded.Attribute(R + "id");
                if (relationId is null || !relationships.TryGetValue(relationId, out var relationship) ||
                    (string?)relationship.Attribute("TargetMode") == "External" ||
                    (string?)relationship.Attribute("Type") != R.NamespaceName + "/font" ||
                    ResolvePart((string?)relationship.Attribute("Target") ?? "") is not { } path)
                {
                    Loss("font-resource", "Embedded font relationship", "The unavailable embedded face was omitted; no external font was fetched.", embedded);
                    continue;
                }
                var bytes = readPart(path, DocumentResource.MaximumEmbeddedBytes);
                var keyText = (string?)embedded.Attribute(W + "fontKey");
                if (keyText is not null)
                {
                    if (!Guid.TryParse(keyText, out var key) || bytes.Length < 32) throw new FormatException("Invalid DOCX font obfuscation key or data.");
                    ObfuscateFont(bytes, key);
                }
                var rights = FontEmbeddingPolicy.ReadRights(bytes);
                var subset = OnAttribute(embedded, "subsetted");
                if (rights is null)
                    Loss("font-format", "Embedded font format", "Font bytes retained, but rendering substitutes a supported font.", embedded);
                else if (!FontEmbeddingPolicy.CanEmbed(rights, forEditing: false) || subset && (rights & FontEmbeddingRights.NoSubsetting) != 0)
                    Loss("font-embedding-restricted", "Embedded font rights", "Font bytes retained for inspection; the face is not loaded or re-embedded.", embedded);
                else if (!FontEmbeddingPolicy.CanEmbed(rights))
                    Loss("font-preview-only", "Preview-and-print-only embedded font", "Font retained for viewing; editing uses a substitute.", embedded);
                var resourceId = "font-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!resources.ContainsKey(resourceId))
                {
                    if (resources.Values.Sum(resource => (long)resource.Data.Length) + bytes.Length > DocumentResource.MaximumDocumentEmbeddedBytes)
                        throw new FormatException("DOCX embedded resources exceed the document size limit.");
                    resources.Add(resourceId, new() { MediaType = "font/otf", Data = bytes.ToImmutableArray() });
                }
                definitions.Add(new(family, resourceId) { Weight = weight, Italic = italic,
                    EmbeddingRights = rights is { } flags && ((int)flags & ~0x030E) == 0 ? rights : null, IsSubset = subset });
            }
        }
        return definitions.ToImmutable();
    }

    private static bool WriteEmbeddedFonts(FlowDocument document, ZipArchive archive, Action<string, XElement> part)
    {
        if (document.Fonts.IsDefaultOrEmpty) return false;
        var fonts = new XElement(W + "fonts", new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "r", R));
        var relationships = new XElement(Rel + "Relationships");
        var count = 0;
        foreach (var group in document.Fonts.GroupBy(font => font.FamilyName).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var element = new XElement(W + "font", new XAttribute(W + "name", group.Key));
            var variants = new HashSet<string>(StringComparer.Ordinal);
            foreach (var font in group.OrderBy(font => font.Weight).ThenBy(font => font.Italic))
            {
                var resource = document.Resources[font.ResourceId];
                var rights = FontEmbeddingPolicy.ReadRights(resource.Data.AsSpan());
                if (!FontEmbeddingPolicy.CanEmbed(rights, forEditing: false) ||
                    font.EmbeddingRights is { } restrictions && !FontEmbeddingPolicy.CanEmbed(restrictions, forEditing: false) ||
                    font.IsSubset && ((rights | font.EmbeddingRights.GetValueOrDefault()) & FontEmbeddingRights.NoSubsetting) != 0)
                {
                    Loss("font-embedding-restricted", "Embedded font rights", "The font face was omitted because its embedding rights cannot authorize export.");
                    continue;
                }
                var variant = font.Weight >= 600 ? font.Italic ? "embedBoldItalic" : "embedBold" : font.Italic ? "embedItalic" : "embedRegular";
                if (!variants.Add(variant))
                {
                    Loss("font-variant", "Additional embedded font weights", "DOCX supports four named face variants; the first matching variant was retained.");
                    continue;
                }
                if (font.Weight is not (400 or 700)) Loss("font-variant-weight", "Embedded font numeric weight", "DOCX regular/bold font-table variant used.");
                var data = resource.Data.ToArray();
                var key = Guid.ParseExact(Convert.ToHexString(SHA256.HashData(data).AsSpan(0, 16)), "N");
                ObfuscateFont(data, key);
                var relationshipId = "font" + (++count).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var target = "fonts/" + relationshipId + ".odttf";
                var entry = archive.CreateEntry("word/" + target, CompressionLevel.Optimal);
                using (var output = entry.Open()) output.Write(data);
                relationships.Add(new XElement(Rel + "Relationship", new XAttribute("Id", relationshipId),
                    new XAttribute("Type", R.NamespaceName + "/font"), new XAttribute("Target", target)));
                element.Add(new XElement(W + variant, new XAttribute(R + "id", relationshipId),
                    new XAttribute(W + "fontKey", key.ToString("B").ToUpperInvariant()), new XAttribute(W + "subsetted", font.IsSubset ? "1" : "0")));
            }
            if (element.HasElements) fonts.Add(element);
        }
        if (!fonts.HasElements) return false;
        part("word/fontTable.xml", fonts); part("word/_rels/fontTable.xml.rels", relationships);
        return true;
    }

    // ECMA-376 WordprocessingML font obfuscation uses the GUID's displayed hexadecimal
    // bytes in reverse order (not Guid.ToByteArray's mixed-endian memory representation).
    private static void ObfuscateFont(byte[] bytes, Guid key)
    {
        if (bytes.Length < 32) throw new FormatException("Embedded font data is too short.");
        var keyBytes = Convert.FromHexString(key.ToString("N"));
        for (var i = 0; i < 32; i++) bytes[i] ^= keyBytes[15 - i % 16];
    }
}
