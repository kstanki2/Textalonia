using System.Globalization;
using Textalonia.Editing;
using Textalonia.Model;

namespace Textalonia.Proofing;

/// <summary>A completed token at a committed input boundary. Offsets are UTF-16 positions in the active story.</summary>
public sealed record AutoCorrectContext(string Text, CultureInfo Culture, Guid StoryId,
    int Start, int End, char Boundary, TextStyle Style);

/// <summary>Replacement chosen for a committed token. Hosts may return rich content or an inline image.</summary>
public abstract record AutoCorrectReplacement;
public sealed record AutoCorrectText(string Value) : AutoCorrectReplacement;
public sealed record AutoCorrectFragment(DocumentFragment Value) : AutoCorrectReplacement;
public sealed record AutoCorrectInline(InlineDescriptor Descriptor, DocumentResource? Resource = null) : AutoCorrectReplacement;
public sealed record AutoCorrectLink(string Uri) : AutoCorrectReplacement;

/// <summary>Runs synchronously on the editor UI thread after committed input, never on IME preedit.</summary>
public interface IAutoCorrectService
{
    AutoCorrectReplacement? GetReplacement(AutoCorrectContext context);
}

/// <summary>Configurable replacement table, two-initial-capital correction and safe URL detection.</summary>
public sealed class AutoCorrectService : IAutoCorrectService
{
    public IDictionary<string, string> Replacements { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool CorrectTwoInitialCapitals { get; set; } = true;
    public bool DetectUrls { get; set; } = true;
    /// <summary>Called first. Return null to use the built-in rules.</summary>
    public Func<AutoCorrectContext, AutoCorrectReplacement?>? ReplacementRequested { get; set; }

    public AutoCorrectReplacement? GetReplacement(AutoCorrectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (ReplacementRequested?.Invoke(context) is { } supplied) return supplied;
        if (Replacements.TryGetValue(context.Text, out var replacement) && replacement != context.Text)
            return new AutoCorrectText(replacement);
        if (CorrectTwoInitialCapitals && context.Text.Length >= 3 &&
            char.IsUpper(context.Text[0]) && char.IsUpper(context.Text[1]) && char.IsLower(context.Text[2]))
            return new AutoCorrectText(context.Text[..1] + char.ToLower(context.Text[1], context.Culture) + context.Text[2..]);
        if (DetectUrls && char.IsWhiteSpace(context.Boundary))
        {
            var candidate = context.Text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? "https://" + context.Text : context.Text;
            if ((candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                FlowDocument.IsSafeHyperlink(candidate)) return new AutoCorrectLink(candidate);
        }
        return null;
    }
}
