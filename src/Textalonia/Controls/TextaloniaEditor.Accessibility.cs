namespace Textalonia.Controls;

public partial class TextaloniaEditor
{
    private DocumentTextProvider? _accessibility;
    /// <summary>Committed text ranges for host bridges. The pinned Avalonia backend exposes only the native value pattern.</summary>
    public DocumentTextProvider Accessibility => _accessibility ??= new(this);
    internal DocumentSurface? AccessibilitySurface => _surface;
}
