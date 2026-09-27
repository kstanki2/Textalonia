using Avalonia;
using Avalonia.Media;
using Textalonia.Rendering;

namespace Textalonia.Controls;

/// <summary>
/// Displays one physical page from a captured output renderer. The host owns the renderer and must
/// keep it alive while this control is displayed. Zoom changes only the preview, never pagination.
/// </summary>
public sealed class DocumentPrintPreview : Avalonia.Controls.Control
{
    public static readonly StyledProperty<DocumentRenderer?> RendererProperty =
        AvaloniaProperty.Register<DocumentPrintPreview, DocumentRenderer?>(nameof(Renderer));
    public static readonly StyledProperty<int> PageIndexProperty =
        AvaloniaProperty.Register<DocumentPrintPreview, int>(nameof(PageIndex), validate: value => value >= 0);
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<DocumentPrintPreview, double>(nameof(Zoom), 1,
            validate: value => double.IsFinite(value) && value >= .1 && value <= 5);

    public DocumentRenderer? Renderer { get => GetValue(RendererProperty); set => SetValue(RendererProperty, value); }
    /// <summary>Zero-based physical page. Pages outside the snapshot display an empty surface.</summary>
    public int PageIndex { get => GetValue(PageIndexProperty); set => SetValue(PageIndexProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    static DocumentPrintPreview()
    {
        AffectsMeasure<DocumentPrintPreview>(RendererProperty, PageIndexProperty, ZoomProperty);
        AffectsRender<DocumentPrintPreview>(RendererProperty, PageIndexProperty, ZoomProperty);
    }

    protected override Size MeasureOverride(Size availableSize) => Renderer is { } renderer && PageIndex < renderer.PageCount
        ? renderer.Snapshot.Pages[PageIndex].Bounds.Size * Zoom : default;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Renderer is not { } renderer || PageIndex >= renderer.PageCount) return;
        using (context.PushTransform(Matrix.CreateScale(Zoom, Zoom)))
            renderer.DrawPage(context, PageIndex);
    }
}
