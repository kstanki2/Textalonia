using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Textalonia.Model;

internal static class ContentControlBindingReader
{
    // A deliberately small XPath subset: absolute named paths, optional 1-based indexes,
    // and a final text() or @attribute. No functions, predicates, axes or external lookup.
    private static readonly Regex Step = new(@"^(?<name>[A-Za-z_][A-Za-z0-9_.-]*(?::[A-Za-z_][A-Za-z0-9_.-]*)?)(?:\[(?<index>[1-9][0-9]{0,3})\])?$", RegexOptions.CultureInvariant);

    internal static bool TryRead(ContentControlBinding binding, string xml, out string value)
    {
        value = "";
        if (xml is null || xml.Length > 1_048_576 || binding.XPath is null || binding.XPath.Length > 4096 || binding.PrefixMappings is null || binding.PrefixMappings.Length > 16384) return false;
        try
        {
            static XElement Read(string source)
            {
                using var text = new StringReader(source);
                using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = 1_048_576, MaxCharactersFromEntities = 1024 });
                return XDocument.Load(reader).Root ?? throw new FormatException("Missing XML root.");
            }
            var root = Read(xml);
            if (root.DescendantsAndSelf().Take(10001).Count() > 10000 || root.DescendantsAndSelf().Any(node => node.Ancestors().Take(33).Count() > 32)) return false;
            var declarations = Read("<bindings " + binding.PrefixMappings + "/>").Attributes().Where(attribute => attribute.IsNamespaceDeclaration)
                .ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value, StringComparer.Ordinal);
            XName? Name(string text)
            {
                var parts = text.Split(':');
                if (parts.Length == 1) return XName.Get(text);
                return parts.Length == 2 && declarations.TryGetValue(parts[0], out var ns) ? XName.Get(parts[1], ns) : null;
            }
            if (!binding.XPath.StartsWith('/')) return false;
            var steps = binding.XPath[1..].Split('/');
            if (steps.Length is 0 or > 32 || steps.Any(step => step.Length == 0)) return false;
            XElement? current = root;
            for (var i = 0; i < steps.Length; i++)
            {
                var step = steps[i];
                if (i == steps.Length - 1 && step == "text()") { value = current.Value; return true; }
                if (i == steps.Length - 1 && step.StartsWith('@'))
                {
                    if (!Step.IsMatch(step[1..]) || step.Contains('[')) return false;
                    var name = Name(step[1..]); var attribute = name is null ? null : current.Attribute(name);
                    if (attribute is null) return false;
                    value = attribute.Value; return true;
                }
                var match = Step.Match(step);
                if (!match.Success) return false;
                var target = Name(match.Groups["name"].Value);
                if (target is null) return false;
                var index = match.Groups["index"].Success ? int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture) : 1;
                if (i == 0) { if (current.Name != target || index != 1) return false; }
                else current = current.Elements(target).Skip(index - 1).FirstOrDefault();
                if (current is null) return false;
            }
            value = current.Value; return true;
        }
        catch (Exception exception) when (exception is XmlException or FormatException or ArgumentException) { return false; }
    }
}
