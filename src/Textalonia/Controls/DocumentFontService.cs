using Avalonia.Media;
using Avalonia.Media.Fonts;
using Textalonia.Model;

namespace Textalonia.Controls;

public sealed record DocumentFontDiagnostic(string Code, string FamilyName, string SubstituteFamily, string Message);

/// <summary>
/// Owns an isolated embedded-font collection. Dispose after all layouts using its resolved families.
/// Missing fonts use the supplied fallback; diagnostics are deduplicated and sorted independently of lookup order.
/// </summary>
public sealed class DocumentFontService : IDisposable
{
    private readonly FontFamily _fallback;
    private readonly Dictionary<string, FontFamily> _embedded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FontFamily> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Code, string Family), DocumentFontDiagnostic> _diagnostics = [];
    private readonly ScopedFontCollection? _collection;
    private bool _disposed;

    public DocumentFontService(FlowDocument document, FontFamily fallback, bool forEditing = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        if (document.Fonts.IsDefaultOrEmpty) return;
        _collection = new();
        foreach (var definition in document.Fonts.OrderBy(f => f.FamilyName, StringComparer.Ordinal).ThenBy(f => f.Weight).ThenBy(f => f.Italic))
        {
            if (!document.Resources.TryGetValue(definition.ResourceId, out var resource) || resource.Kind != DocumentResourceKind.Embedded)
            {
                Diagnose("font.resource.missing", definition.FamilyName, "The embedded font resource is missing.");
                continue;
            }
            var rights = FontEmbeddingPolicy.ReadRights(resource.Data.AsSpan());
            if (!FontEmbeddingPolicy.CanEmbed(rights, forEditing) || definition.IsSubset && ((rights | definition.EmbeddingRights.GetValueOrDefault()) & FontEmbeddingRights.NoSubsetting) != 0 ||
                definition.EmbeddingRights is { } restrictions && !FontEmbeddingPolicy.CanEmbed(restrictions, forEditing))
            {
                Diagnose(rights is null ? "font.format.unsupported" : "font.embedding.restricted", definition.FamilyName,
                    rights is null ? "Embedding rights could not be read from a supported OpenType font." :
                    "The font's embedding rights do not permit the requested document use.");
                continue;
            }
            try
            {
                using var stream = new MemoryStream(resource.Data.ToArray(), writable: false);
                if (!_collection.TryAddGlyphTypeface(stream, out var glyph))
                {
                    Diagnose("font.load.failed", definition.FamilyName, "The font backend could not load this embedded face.");
                    continue;
                }
                // The document's family is an alias; use the font's actual family inside this private collection.
                _embedded[definition.FamilyName] = new FontFamily(_collection.Key.AbsoluteUri + "#" + glyph.FamilyName);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                Diagnose("font.load.failed", definition.FamilyName, "The font backend rejected this embedded face.");
            }
        }
        FontManager.Current.AddFontCollection(_collection);
    }

    public IReadOnlyList<DocumentFontDiagnostic> Diagnostics => _diagnostics.Values.OrderBy(d => d.FamilyName, StringComparer.Ordinal)
        .ThenBy(d => d.Code, StringComparer.Ordinal).ToArray();

    public bool HasEmbeddedFonts => _embedded.Count > 0;

    public FontFamily Resolve(TextStyle style) => Resolve(style.FontFamily);

    public FontFamily Resolve(string? familyName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(familyName)) return _fallback;
        if (_embedded.TryGetValue(familyName, out var embedded)) return embedded;
        if (_resolved.TryGetValue(familyName, out var cached)) return cached;
        if (familyName.ToLowerInvariant() is "monospace" or "serif" or "sans-serif" or "cursive" or "fantasy")
            return _resolved[familyName] = new FontFamily(familyName);
        FontFamily resolved;
        try
        {
            var requested = new FontFamily(familyName);
            // Avalonia may silently substitute during glyph lookup. System-family enumeration proves availability first.
            if (requested.Key is not null || FontManager.Current.SystemFonts.Any(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase)))
                resolved = requested;
            else
            {
                resolved = _fallback;
                Diagnose("font.substituted", familyName, "The requested font is unavailable; the configured document fallback is used.");
            }
        }
        catch (ArgumentException)
        {
            resolved = _fallback;
            Diagnose("font.substituted", familyName, "The requested font family is invalid; the configured document fallback is used.");
        }
        _resolved.Add(familyName, resolved);
        return resolved;
    }

    private void Diagnose(string code, string family, string message) =>
        _diagnostics.TryAdd((code, family), new(code, family, _fallback.Name, message));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_collection is not null) FontManager.Current.RemoveFontCollection(_collection.Key);
        _embedded.Clear(); _resolved.Clear();
    }

    private sealed class ScopedFontCollection : FontCollectionBase
    {
        public override Uri Key { get; } = new("fonts:textalonia-" + Guid.NewGuid().ToString("N"));
    }
}
