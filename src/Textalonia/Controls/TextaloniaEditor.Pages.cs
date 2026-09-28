using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Textalonia.Model;

namespace Textalonia.Controls;

/// <summary>Simple follows the viewport; Draft follows paper width; PrintLayout displays physical pages.</summary>
public enum DocumentViewMode { Simple, Draft, PrintLayout }

public partial class TextaloniaEditor
{
    public static readonly StyledProperty<DocumentViewMode> ViewModeProperty =
        AvaloniaProperty.Register<TextaloniaEditor, DocumentViewMode>(nameof(ViewMode), validate: Enum.IsDefined);
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<TextaloniaEditor, double>(nameof(Zoom), 1,
            validate: value => double.IsFinite(value) && value >= .1 && value <= 5);
    public static readonly StyledProperty<double> PageGapProperty =
        AvaloniaProperty.Register<TextaloniaEditor, double>(nameof(PageGap), 24,
            validate: value => double.IsFinite(value) && value >= 0 && value <= 1000);
    public static readonly StyledProperty<int> PagesPerRowProperty =
        AvaloniaProperty.Register<TextaloniaEditor, int>(nameof(PagesPerRow), 1,
            validate: value => value >= 1 && value <= 8);
    public static readonly DirectProperty<TextaloniaEditor, int> PageCountProperty =
        AvaloniaProperty.RegisterDirect<TextaloniaEditor, int>(nameof(PageCount), editor => editor.PageCount);
    public static readonly DirectProperty<TextaloniaEditor, int> CurrentPageNumberProperty =
        AvaloniaProperty.RegisterDirect<TextaloniaEditor, int>(nameof(CurrentPageNumber), editor => editor.CurrentPageNumber);

    private int _pageCount = 1, _currentPageNumber = 1;
    private int? _navigationPage;
    private Vector _navigationOffset;
    public DocumentViewMode ViewMode { get => GetValue(ViewModeProperty); set => SetValue(ViewModeProperty, value); }
    /// <summary>View scale from 0.1 to 5. Does not change document units or undo history.</summary>
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    /// <summary>Space between sheets in unscaled document units.</summary>
    public double PageGap { get => GetValue(PageGapProperty); set => SetValue(PageGapProperty, value); }
    public int PagesPerRow { get => GetValue(PagesPerRowProperty); set => SetValue(PagesPerRowProperty, value); }
    /// <summary>Exact physical page count in PrintLayout; one continuous surface in Simple and Draft.</summary>
    public int PageCount => _pageCount;
    /// <summary>One-based navigation target, or the physical page nearest the viewport's top edge after scrolling.</summary>
    public int CurrentPageNumber => _currentPageNumber;
    public PageSettings CurrentPageSettings => Session.CurrentPageSettings;

    public void ApplyPageSettings(PageSettings settings) => Session.SetPageSettings(settings);

    /// <summary>Fits the configured page arrangement to the current viewport.</summary>
    public void FitWidth()
    {
        var width = Scroller?.Viewport.Width ?? Bounds.Width;
        if (width <= 0) return;
        if (ViewMode == DocumentViewMode.Simple) { Zoom = 1; return; }
        _surface?.EnsureLayout(_surface.Bounds.Width);
        var documentWidth = _surface?.PagedLayout?.Width ?? CurrentPageSettings.EffectiveWidth;
        Zoom = Math.Clamp((width - 2) / Math.Max(1, documentWidth), .1, 5);
    }

    /// <summary>Fits one physical sheet into the viewport and selects PrintLayout.</summary>
    public void FitPage()
    {
        ViewMode = DocumentViewMode.PrintLayout;
        _surface?.EnsureLayout(_surface.Bounds.Width);
        var page = _surface?.PagedLayout?.Pages.ElementAtOrDefault(CurrentPageNumber - 1);
        var width = page?.Bounds.Width ?? CurrentPageSettings.EffectiveWidth;
        var height = page?.Bounds.Height ?? CurrentPageSettings.EffectiveHeight;
        var viewport = Scroller?.Viewport ?? Bounds.Size;
        if (viewport.Width > 0 && viewport.Height > 0)
            Zoom = Math.Clamp(Math.Min(viewport.Width / (width + PageGap * 2), viewport.Height / (height + PageGap * 2)), .1, 5);
    }

    /// <summary>Scrolls to a zero-based physical page without changing selection or undo history.</summary>
    public void GoToPage(int pageIndex)
    {
        _surface?.EnsureLayout(_surface.Bounds.Width);
        if (pageIndex < 0 || pageIndex >= PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (_surface is { } surface)
        {
            // Publish the new extent before the scroll viewer clamps a request after a view/zoom change.
            surface.InvalidateMeasure(); surface.UpdateLayout(); surface.EnsureLayout(surface.Bounds.Width);
        }
        if (Scroller is { } scroller && _surface?.PagedLayout is { } snapshot)
        {
            var bounds = snapshot.Pages[pageIndex].Bounds;
            scroller.Offset = new Vector(Math.Max(0, bounds.X * Zoom - PageGap * Zoom), Math.Max(0, bounds.Y * Zoom - PageGap * Zoom));
            _navigationPage = pageIndex;
            _navigationOffset = scroller.Offset;
        }
        SetAndRaise(CurrentPageNumberProperty, ref _currentPageNumber, pageIndex + 1);
    }

    internal void UpdatePageStatus(int count, int current)
    {
        SetAndRaise(PageCountProperty, ref _pageCount, Math.Max(1, count));
        if (_navigationPage is { } target && target < _pageCount && Scroller is { } scroller &&
            Math.Abs(scroller.Offset.X - _navigationOffset.X) < .1 && Math.Abs(scroller.Offset.Y - _navigationOffset.Y) < .1)
            current = target + 1;
        else _navigationPage = null;
        SetAndRaise(CurrentPageNumberProperty, ref _currentPageNumber, Math.Clamp(current, 1, _pageCount));
    }

    private void UpdatePageView(AvaloniaPropertyChangedEventArgs? change = null)
    {
        var offset = Scroller?.Offset ?? default;
        var target = _navigationPage;
        _navigationPage = null;
        _surface?.ResetVerticalNavigation();
        if (Scroller is { } scroller)
            scroller.HorizontalScrollBarVisibility = ViewMode == DocumentViewMode.Simple
                ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _surface?.Refresh();
        if (change?.Property == ZoomProperty && change.OldValue is double oldZoom && _surface is { } surface && Scroller is { } viewport)
        {
            surface.UpdateLayout();
            viewport.Offset = offset * (Zoom / oldZoom);
            _navigationPage = target;
            _navigationOffset = viewport.Offset;
        }
    }
}
