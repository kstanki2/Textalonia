using Textalonia.Model;

namespace Textalonia.Serialization;

internal static class PageFieldInstructions
{
    internal static bool TryParse(string instruction, out PageFieldKind kind)
    {
        var parts = instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        kind = default;
        if (parts.Length is not (1 or 3) || parts.Length == 3 && (parts[1] != @"\*" || !parts[2].Equals("MERGEFORMAT", StringComparison.OrdinalIgnoreCase))) return false;
        return parts[0].Length > 0 && char.IsAsciiLetter(parts[0][0]) && Enum.TryParse(parts[0], true, out kind) && Enum.IsDefined(kind);
    }
}
