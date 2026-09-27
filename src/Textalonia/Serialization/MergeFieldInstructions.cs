using System.Text;
using Textalonia.Model;

namespace Textalonia.Serialization;

/// <summary>The deliberately small Word field-code subset shared by DOCX and RTF.</summary>
internal static class MergeFieldInstructions
{
    internal sealed record Parsed(string Name, bool UnsupportedSwitches, bool CharacterFormat);

    internal static Parsed? Parse(string instruction)
    {
        if (instruction.Length > 16_384) return null;
        var tokens = new List<string>();
        var quotedTokens = new List<bool>();
        for (var i = 0; i < instruction.Length;)
        {
            while (i < instruction.Length && char.IsWhiteSpace(instruction[i])) i++;
            if (i == instruction.Length) break;
            var token = new StringBuilder();
            quotedTokens.Add(instruction[i] == '"');
            if (instruction[i] == '"')
            {
                i++; var closed = false;
                while (i < instruction.Length)
                {
                    var ch = instruction[i++];
                    if (ch == '"') { closed = true; break; }
                    if (ch == '\\' && i < instruction.Length && instruction[i] is '\\' or '"') ch = instruction[i++];
                    token.Append(ch);
                }
                if (!closed || i < instruction.Length && !char.IsWhiteSpace(instruction[i])) return null;
            }
            else
            {
                while (i < instruction.Length && !char.IsWhiteSpace(instruction[i]))
                {
                    if (instruction[i] == '"') return null;
                    token.Append(instruction[i++]);
                }
            }
            tokens.Add(token.ToString());
        }
        if (tokens.Count < 2 || quotedTokens[0] || !tokens[0].Equals("MERGEFIELD", StringComparison.OrdinalIgnoreCase) ||
            !InlineDescriptor.ValidKey(tokens[1]) || !quotedTokens[1] && tokens[1].StartsWith('\\')) return null;
        var unsupported = false; var characterFormat = false;
        for (var i = 2; i < tokens.Count; i++)
        {
            if (!quotedTokens[i] && tokens[i] == "\\*" && i + 1 < tokens.Count && !quotedTokens[i + 1] &&
                (tokens[i + 1].Equals("MERGEFORMAT", StringComparison.OrdinalIgnoreCase) ||
                 tokens[i + 1].Equals("CHARFORMAT", StringComparison.OrdinalIgnoreCase)))
            {
                characterFormat |= tokens[++i].Equals("CHARFORMAT", StringComparison.OrdinalIgnoreCase);
            }
            else unsupported = true;
        }
        return new(tokens[1], unsupported, characterFormat);
    }

    internal static string Write(string name) => " MERGEFIELD \"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\" \\* MERGEFORMAT ";

    internal static RichRun Create(string name, string cachedText, TextStyle style) => new(new InlineDescriptor
    {
        Payload = new MergeFieldInlinePayload(name), AltText = cachedText, Width = 120, Height = 24
    }, style);

    internal static void ReportExportOptions(string format, InlineDescriptor inline, MergeFieldInlinePayload field)
    {
        if (field.Format is not null)
            ConversionDiagnostics.Report(format + ".merge-field-format", "Native merge-field value format",
                "Exported the field name and cached display; .NET value formatting is not mapped to Word field switches.", inline.Id);
        if (field.FallbackText is not null)
            ConversionDiagnostics.Report(format + ".merge-field-fallback", "Native merge-field fallback text",
                "Exported the field name and cached display without the missing-value fallback policy.", inline.Id);
    }
}
