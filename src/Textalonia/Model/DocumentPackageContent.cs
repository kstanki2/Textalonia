using System.Collections.Immutable;
using System.Globalization;
using System.Xml;

namespace Textalonia.Model;

/// <summary>Built-in Office document properties. Dates are stored as UTC instants.</summary>
public sealed record DocumentCoreProperties
{
    public string? Title { get; init; }
    public string? Subject { get; init; }
    public string? Creator { get; init; }
    public string? Keywords { get; init; }
    public string? Description { get; init; }
    public string? LastModifiedBy { get; init; }
    public string? Revision { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string? Category { get; init; }
    public string? ContentStatus { get; init; }
    public string? Identifier { get; init; }
    public string? Language { get; init; }
    public string? Version { get; init; }

    internal void Validate()
    {
        foreach (var value in new[] { Title, Subject, Creator, Keywords, Description, LastModifiedBy,
            Revision, Category, ContentStatus, Identifier, Language, Version })
            if (value is { Length: > 16384 }) throw new FormatException("Document core property exceeds the size limit.");
    }
}

public enum DocumentPropertyType { Text, Integer, Decimal, Boolean, DateTime }

/// <summary>A custom Office property with its original value type and invariant lexical value.</summary>
public sealed record DocumentCustomProperty
{
    public string Name { get; init; } = "";
    public DocumentPropertyType Type { get; init; }
    public string Value { get; init; } = "";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 255 || Value is null || Value.Length > 16384 || !Enum.IsDefined(Type))
            throw new FormatException("Invalid custom document property.");
        var valid = Type switch
        {
            DocumentPropertyType.Integer => long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
            DocumentPropertyType.Decimal => double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
            DocumentPropertyType.Boolean => Value is "true" or "false" or "1" or "0",
            DocumentPropertyType.DateTime => DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            _ => true
        };
        if (!valid) throw new FormatException("Invalid typed custom document property value.");
    }
}

/// <summary>A document-owned, bounded custom XML item and its optional item-properties part.</summary>
public sealed record DocumentCustomXmlPart
{
    public string PartName { get; init; } = "";
    public string Xml { get; init; } = "";
    public string? PropertiesPartName { get; init; }
    public string? PropertiesXml { get; init; }

    internal void Validate()
    {
        if (PartName is null || Xml is null || !ValidPartName(PartName) || Xml.Length > 1_048_576 ||
            (PropertiesPartName is null) != (PropertiesXml is null) ||
            PropertiesPartName is not null && (!ValidPartName(PropertiesPartName) || PropertiesXml!.Length > 65536 || PropertiesPartName == PartName))
            throw new FormatException("Invalid custom XML package part.");
        ValidateXml(Xml);
        if (PropertiesXml is not null) ValidateXml(PropertiesXml);
    }

    private static bool ValidPartName(string name) => name.StartsWith("customXml/", StringComparison.Ordinal) &&
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && name.Length <= 255 &&
        name["customXml/".Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static void ValidateXml(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 1_048_576, MaxCharactersFromEntities = 1024,
                ConformanceLevel = ConformanceLevel.Document
            });
            var roots = 0;
            while (reader.Read())
            {
                if (reader.Depth > 128) throw new FormatException("Custom XML nesting exceeds the limit.");
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0) roots++;
            }
            if (roots != 1) throw new FormatException("Custom XML must contain one root element.");
        }
        catch (XmlException exception) { throw new FormatException("Invalid custom XML package part.", exception); }
    }
}

/// <summary>Stored Word compatibility XML. Preserved for interchange; layout support is mode-specific.</summary>
public sealed record DocumentCompatibilitySettings
{
    public string? Xml { get; init; }
    internal void Validate()
    {
        if (Xml is null) return;
        if (Xml.Length > 65536) throw new FormatException("Compatibility settings exceed the size limit.");
        try
        {
            using var reader = XmlReader.Create(new StringReader(Xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 65536, MaxCharactersFromEntities = 1024
            });
            reader.MoveToContent();
            if (reader.LocalName != "compat" || reader.NamespaceURI != "http://schemas.openxmlformats.org/wordprocessingml/2006/main")
                throw new FormatException("Invalid Word compatibility settings root.");
            while (reader.Read()) if (reader.Depth > 32) throw new FormatException("Compatibility settings nesting exceeds the limit.");
        }
        catch (XmlException exception) { throw new FormatException("Invalid Word compatibility settings.", exception); }
    }
}
