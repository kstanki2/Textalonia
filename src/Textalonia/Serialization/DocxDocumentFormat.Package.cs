using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using Textalonia.Model;

namespace Textalonia.Serialization;

public sealed partial class DocxDocumentFormat
{
    private static readonly XNamespace Core = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Dcterms = "http://purl.org/dc/terms/";
    private static readonly XNamespace Custom = "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";
    private static readonly XNamespace Variant = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";

    private static DocumentCoreProperties ReadCoreProperties(XDocument? xml)
    {
        var root = xml?.Root;
        if (root is null) return new();
        if (root.Name != Core + "coreProperties") throw new FormatException("Invalid DOCX core properties root.");
        DateTimeOffset? Date(XName name)
        {
            var source = root.Element(name);
            if (source is null) return null;
            if (DateTimeOffset.TryParse(source.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)) return value;
            Loss("core-date", "Invalid core property date", "Date omitted; other core properties retained.", source);
            return null;
        }
        return new DocumentCoreProperties
        {
            Title = (string?)root.Element(Dc + "title"), Subject = (string?)root.Element(Dc + "subject"),
            Creator = (string?)root.Element(Dc + "creator"), Keywords = (string?)root.Element(Core + "keywords"),
            Description = (string?)root.Element(Dc + "description"), LastModifiedBy = (string?)root.Element(Core + "lastModifiedBy"),
            Revision = (string?)root.Element(Core + "revision"), Created = Date(Dcterms + "created"), Modified = Date(Dcterms + "modified"),
            Category = (string?)root.Element(Core + "category"), ContentStatus = (string?)root.Element(Core + "contentStatus"),
            Identifier = (string?)root.Element(Dc + "identifier"), Language = (string?)root.Element(Dc + "language"),
            Version = (string?)root.Element(Core + "version")
        };
    }

    private static XElement WriteCoreProperties(DocumentCoreProperties core)
    {
        XElement? Text(XName name, string? value) => value is null ? null : new XElement(name, value);
        XElement? Date(XName name, DateTimeOffset? value) => value is null ? null :
            new XElement(name, new XAttribute(XNamespace.Xmlns + "dcterms", Dcterms.NamespaceName),
                new XAttribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type", "dcterms:W3CDTF"),
                value.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        return new XElement(Core + "coreProperties",
            new XAttribute(XNamespace.Xmlns + "dc", Dc.NamespaceName),
            Text(Dc + "title", core.Title), Text(Dc + "subject", core.Subject), Text(Dc + "creator", core.Creator),
            Text(Core + "keywords", core.Keywords), Text(Dc + "description", core.Description),
            Text(Core + "lastModifiedBy", core.LastModifiedBy), Text(Core + "revision", core.Revision),
            Date(Dcterms + "created", core.Created), Date(Dcterms + "modified", core.Modified),
            Text(Core + "category", core.Category), Text(Core + "contentStatus", core.ContentStatus),
            Text(Dc + "identifier", core.Identifier), Text(Dc + "language", core.Language), Text(Core + "version", core.Version));
    }

    private static ImmutableArray<DocumentCustomProperty> ReadTypedProperties(XDocument? xml)
    {
        if (xml?.Root is null) return [];
        if (xml.Root.Name != Custom + "Properties") throw new FormatException("Invalid DOCX custom properties root.");
        var result = ImmutableArray.CreateBuilder<DocumentCustomProperty>();
        foreach (var element in xml.Root.Elements(Custom + "property"))
        {
            var name = (string?)element.Attribute("name");
            var value = element.Elements().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(name) || value is null) continue;
            var type = value.Name.LocalName switch
            {
                "lpwstr" or "lpstr" or "bstr" => DocumentPropertyType.Text,
                "i4" or "int" or "i8" => DocumentPropertyType.Integer,
                "r8" or "r4" or "decimal" => DocumentPropertyType.Decimal,
                "bool" => DocumentPropertyType.Boolean,
                "filetime" or "date" => DocumentPropertyType.DateTime,
                _ => (DocumentPropertyType?)null
            };
            if (type is null)
            {
                Loss("property-type", "Unsupported custom property type", "Text value retained in the legacy property catalog.", element);
                continue;
            }
            var property = new DocumentCustomProperty { Name = name, Type = type.Value, Value = value.Value };
            try { property.Validate(); }
            catch (FormatException)
            {
                Loss("property-value", "Invalid typed custom property", "Text value retained in the legacy property catalog.", element);
                continue;
            }
            result.Add(property);
        }
        return result.ToImmutable();
    }

    private static XElement WriteTypedProperties(FlowDocument document)
    {
        var typed = document.CustomProperties.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in document.Properties)
        {
            if (!typed.TryGetValue(pair.Key, out var existing))
            {
                typed.Add(pair.Key, new DocumentCustomProperty { Name = pair.Key, Value = pair.Value });
                continue;
            }
            if (existing.Value == pair.Value) continue;
            var updated = existing with { Value = pair.Value };
            try { updated.Validate(); typed[pair.Key] = updated; }
            catch (FormatException)
            {
                Loss("property-type", "Changed custom property no longer matches its Office type", "Changed value exported as text.");
                typed[pair.Key] = updated with { Type = DocumentPropertyType.Text };
            }
        }
        return new XElement(Custom + "Properties", new XAttribute(XNamespace.Xmlns + "vt", Variant),
            typed.Values.OrderBy(p => p.Name, StringComparer.Ordinal).Select((property, index) =>
                new XElement(Custom + "property", new XAttribute("fmtid", "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}"),
                    new XAttribute("pid", index + 2), new XAttribute("name", property.Name),
                    new XElement(Variant + (property.Type switch
                    {
                        DocumentPropertyType.Integer => "i8", DocumentPropertyType.Decimal => "r8",
                        DocumentPropertyType.Boolean => "bool", DocumentPropertyType.DateTime => "filetime", _ => "lpwstr"
                    }), property.Value))));
    }

    private static ImmutableArray<DocumentCustomXmlPart> ReadCustomXmlParts(
        IEnumerable<XElement> relationships, Func<string, XDocument?> xml)
    {
        var result = ImmutableArray.CreateBuilder<DocumentCustomXmlPart>();
        foreach (var relationship in relationships.Where(e => (string?)e.Attribute("Type") == R.NamespaceName + "/customXml"))
        {
            if ((string?)relationship.Attribute("TargetMode") == "External")
            {
                Loss("custom-xml-external", "External custom XML relationship", "External target was not accessed.", relationship);
                continue;
            }
            var path = ResolvePart((string?)relationship.Attribute("Target") ?? "");
            if (path is null || !path.StartsWith("customXml/", StringComparison.Ordinal))
            {
                Loss("custom-xml-target", "Unsafe custom XML relationship", "Custom XML item omitted.", relationship);
                continue;
            }
            var item = xml(path);
            if (item is null)
            {
                Loss("custom-xml-missing", "Missing custom XML item", "Custom XML item omitted.", relationship);
                continue;
            }
            var relsPath = "customXml/_rels/" + path["customXml/".Length..] + ".rels";
            var propertiesRelationship = xml(relsPath)?.Root?.Elements(Rel + "Relationship")
                .FirstOrDefault(e => (string?)e.Attribute("Type") == R.NamespaceName + "/customXmlProps");
            var propertiesPath = propertiesRelationship is null ? null :
                ResolvePart((string?)propertiesRelationship.Attribute("Target") ?? "", path);
            var properties = propertiesPath is null ? null : xml(propertiesPath);
            if (propertiesRelationship is not null && properties is null)
                Loss("custom-xml-properties", "Missing or unsafe custom XML item properties", "Custom XML data retained without item properties.", propertiesRelationship);
            var part = new DocumentCustomXmlPart
            {
                PartName = path, Xml = item.ToString(SaveOptions.DisableFormatting),
                PropertiesPartName = properties is null ? null : propertiesPath,
                PropertiesXml = properties?.ToString(SaveOptions.DisableFormatting)
            };
            try { part.Validate(); result.Add(part); }
            catch (FormatException) { Loss("custom-xml-limit", "Unsupported custom XML item path or size", "Custom XML item omitted.", relationship); }
            if (result.Count > 256) throw new FormatException("DOCX custom XML item count exceeds the limit.");
        }
        return result.ToImmutable();
    }
}
