using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Textalonia.Model;

/// <summary>Validation for inert Office Math ML stored by an atomic equation inline.</summary>
internal static class EquationMarkup
{
    internal static readonly XNamespace Math = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    internal const int MaximumCharacters = 256 * 1024;

    internal static XElement Parse(string? xml)
    {
        if (string.IsNullOrEmpty(xml) || xml.Length > MaximumCharacters)
            throw new FormatException("Office Math XML is empty or exceeds its size limit.");
        try
        {
            static XmlReader Reader(string text) => XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumCharacters, MaxCharactersFromEntities = 1024
            });
            using (var scan = Reader(xml))
            {
                var nodeCount = 0;
                while (scan.Read())
                {
                    if (scan.Depth > 64 || ++nodeCount > 8192)
                        throw new FormatException("Office Math XML exceeds its structure limits.");
                    if (scan.NodeType != XmlNodeType.Element) continue;
                    if (scan.Depth == 0 && (scan.NamespaceURI != Math.NamespaceName || scan.LocalName is not ("oMath" or "oMathPara")))
                        throw new FormatException("Equation payload must contain one Office Math root.");
                    if (!scan.HasAttributes) continue;
                    while (scan.MoveToNextAttribute())
                        if (scan.NamespaceURI == Relationships.NamespaceName)
                            throw new FormatException("Office Math XML cannot own package relationships.");
                    scan.MoveToElement();
                }
            }
            using var reader = Reader(xml);
            var equation = XElement.Load(reader, LoadOptions.PreserveWhitespace);
            if (equation.Name != Math + "oMath" && equation.Name != Math + "oMathPara")
                throw new FormatException("Equation payload must contain one Office Math root.");
            return equation;
        }
        catch (XmlException error)
        {
            throw new FormatException("Invalid Office Math XML.", error);
        }
    }

    internal static string AlternativeText(XElement equation)
    {
        var result = new StringBuilder();
        foreach (var text in equation.Descendants(Math + "t"))
        {
            var remaining = 16_384 - result.Length;
            if (remaining <= 0) break;
            result.Append(text.Value.AsSpan(0, System.Math.Min(remaining, text.Value.Length)));
        }
        return result.Length == 0 ? "Equation" : result.ToString();
    }
}
