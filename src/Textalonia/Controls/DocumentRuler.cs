using System.Globalization;
using System.Collections.Immutable;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Textalonia.Editing;
using Textalonia.Layout;
using Textalonia.Model;

namespace Textalonia.Controls;

public enum RulerOrientation { Horizontal, Vertical }

internal enum RulerMarkerKind
{
    LeftMargin, RightMargin, TopMargin, BottomMargin,
    LeftIndent, FirstLineIndent, RightIndent, TabStop,
    ColumnBoundary, TableColumnBoundary, TableRowBoundary
}

internal readonly record struct RulerMarker(RulerMarkerKind Kind, double Position, int Index = -1, string? Label = null);

/// <summary>Physical ruler coordinates share the paged surface's zoom and scroll transform.</summary>
internal sealed record RulerMetrics(PageLayout Page, ParagraphStyle Paragraph, Rect Column,
    double ParagraphOrigin, double ParagraphRight, double TabOrigin, double Zoom, Vector Scroll, int Revision, TableCellVisual? Cell)
{
    public double Horizontal(double documentX) => documentX * Zoom - Scroll.X;
    public double Vertical(double documentY) => documentY * Zoom - Scroll.Y;
    public double ToDocumentDelta(double viewDelta) => viewDelta / Zoom;
    public double ParagraphLeft => ParagraphOrigin + Paragraph.Indent;

    public IReadOnlyList<RulerMarker> Markers(RulerOrientation orientation)
    {
        var result = new List<RulerMarker>();
        if (orientation == RulerOrientation.Vertical)
        {
            result.Add(new(RulerMarkerKind.TopMargin, Vertical(Page.ContentBounds.Top), Label: "Top margin"));
            result.Add(new(RulerMarkerKind.BottomMargin, Vertical(Page.ContentBounds.Bottom), Label: "Bottom margin"));
            if (Cell is { } cell)
                result.Add(new(RulerMarkerKind.TableRowBoundary, Vertical(cell.Bounds.Bottom), Label: "Table row boundary"));
            return result;
        }

        result.Add(new(RulerMarkerKind.LeftMargin, Horizontal(Page.ContentBounds.Left), Label: "Left margin"));
        result.Add(new(RulerMarkerKind.RightMargin, Horizontal(Page.ContentBounds.Right), Label: "Right margin"));
        for (var i = 0; i < Page.Columns.Length - 1; i++)
            result.Add(new(RulerMarkerKind.ColumnBoundary, Horizontal(Page.Columns[i].Right), i, "Column boundary"));
        result.Add(new(RulerMarkerKind.LeftIndent, Horizontal(ParagraphLeft), Label: "Left indent"));
        result.Add(new(RulerMarkerKind.FirstLineIndent, Horizontal(ParagraphLeft + Paragraph.FirstLineIndent), Label: "First line indent"));
        result.Add(new(RulerMarkerKind.RightIndent, Horizontal(ParagraphRight), Label: "Right indent"));
        for (var i = 0; i < Paragraph.TabStops.Length; i++)
            result.Add(new(RulerMarkerKind.TabStop, Horizontal(TabOrigin + Paragraph.TabStops[i].Position), i, "Tab stop"));
        if (Cell is { } tableCell && tableCell.Column + tableCell.Cell.ColumnSpan < tableCell.Table.ColumnCount)
            result.Add(new(RulerMarkerKind.TableColumnBoundary, Horizontal(tableCell.Bounds.Right), Label: "Table column boundary"));
        return result;
    }

    public static RulerMetrics? Read(TextaloniaEditor? editor)
    {
        if (editor?.ViewMode == DocumentViewMode.Simple || editor?.Scroller?.Content is not DocumentSurface surface)
            return null;
        var pages = surface.PagedLayout;
        if (pages is null || pages.Pages.IsEmpty) return null;
        var position = editor.Session.Selection.Active;
        var storyId = editor.ActiveStoryId;
        // Story switches can precede the surface's next layout pass. The cached snapshot
        // cannot resolve a note/header/footer that was just created in the session.
        if (storyId != Guid.Empty && !pages.Document.Stories.ContainsKey(storyId)) return null;
        var pageIndex = storyId != Guid.Empty && editor.ActiveStoryPageIndex >= 0
            ? editor.ActiveStoryPageIndex : pages.GetPageIndex(storyId, position);
        pageIndex = Math.Clamp(pageIndex, 0, pages.Pages.Length - 1);
        var page = pages.Pages[pageIndex];
        var entry = editor.Session.Index.At(position);
        var paragraph = new DocumentStyleResolver(editor.Session.ActiveDocument).ResolveParagraphStyle(entry.Paragraph.Style);
        var fragments = storyId == Guid.Empty ? pages.Fragments : pages.StoryFragments;
        var fragment = fragments.LastOrDefault(f => f.PageIndex == pageIndex && f.StoryKey == storyId &&
            f.ParagraphId == entry.Paragraph.Id && !f.IsRepeatedTableHeader &&
            position >= f.TextStart && position <= f.TextEnd && (position < f.TextEnd || f.TextEnd == entry.End)) ??
            fragments.FirstOrDefault(f => f.PageIndex == pageIndex && f.StoryKey == storyId &&
                f.ParagraphId == entry.Paragraph.Id && !f.IsRepeatedTableHeader);
        var column = fragment?.ColumnBounds ?? (page.Columns.IsEmpty ? page.ContentBounds : page.Columns[0]);
        var origin = fragment is null ? column.Left : fragment.Origin.X - paragraph.Indent -
            (fragment.TextStart == entry.Start ? paragraph.FirstLineIndent : 0);
        var right = fragment is null ? column.Right - paragraph.RightIndent :
            Math.Min(fragment.Bounds.Right, fragment.Clip.Right);
        // PreparedSource resets tab advances at each line. The marker follows that line's painted origin.
        var tabOrigin = fragment?.Origin.X ?? origin + paragraph.Indent + paragraph.FirstLineIndent;
        var target = editor.Session.CurrentCell();
        var cell = target is null ? null : pages.TableCells(storyId, pageIndex).FirstOrDefault(c => c.Table.Id == target.Value.Table.Id &&
            c.Row == target.Value.Row && c.Column == target.Value.Column && c.Bounds.Intersects(page.Bounds));
        return new(page, paragraph, column, origin, right, tabOrigin, editor.Zoom, editor.Scroller.Offset, editor.Session.Revision, cell);
    }
}

/// <summary>A ruler for paged views. Pointer previews do not change the document; release makes one undo step.</summary>
public sealed class DocumentRuler : Control
{
    public static readonly StyledProperty<RulerOrientation> OrientationProperty =
        AvaloniaProperty.Register<DocumentRuler, RulerOrientation>(nameof(Orientation), validate: Enum.IsDefined);
    public static readonly StyledProperty<IBrush?> RulerBackgroundProperty =
        AvaloniaProperty.Register<DocumentRuler, IBrush?>(nameof(RulerBackground));
    public static readonly StyledProperty<IBrush?> PaperBackgroundProperty =
        AvaloniaProperty.Register<DocumentRuler, IBrush?>(nameof(PaperBackground));
    public static readonly StyledProperty<IBrush?> RulerForegroundProperty =
        AvaloniaProperty.Register<DocumentRuler, IBrush?>(nameof(RulerForeground));
    public static readonly StyledProperty<IBrush?> RulerBorderProperty =
        AvaloniaProperty.Register<DocumentRuler, IBrush?>(nameof(RulerBorder));
    private TextaloniaEditor? _editor;
    private Drag? _drag;
    private IPointer? _pointer;
    private bool _releasing;
    private int _keyboardMarker;

    private sealed record Drag(RulerMetrics Metrics, RulerMarker Marker, double Start, double Current);

    public RulerOrientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public IBrush? RulerBackground { get => GetValue(RulerBackgroundProperty); set => SetValue(RulerBackgroundProperty, value); }
    public IBrush? PaperBackground { get => GetValue(PaperBackgroundProperty); set => SetValue(PaperBackgroundProperty, value); }
    public IBrush? RulerForeground { get => GetValue(RulerForegroundProperty); set => SetValue(RulerForegroundProperty, value); }
    public IBrush? RulerBorder { get => GetValue(RulerBorderProperty); set => SetValue(RulerBorderProperty, value); }

    public TextaloniaEditor? Editor
    {
        get => _editor;
        set
        {
            if (ReferenceEquals(_editor, value)) return;
            CancelDrag();
            if (_editor is not null)
            {
                _editor.PropertyChanged -= EditorPropertyChanged;
                _editor.DocumentChanged -= EditorChanged;
                _editor.SelectionChanged -= EditorChanged;
                _editor.LayoutCompleted -= OnLayoutCompleted;
                _editor.Commands.LocalizationChanged -= OnLocalizationChanged;
                if (_editor.Scroller is { } oldScroller) oldScroller.ScrollChanged -= ScrollerChanged;
            }
            _editor = value;
            if (_editor is not null)
            {
                _editor.PropertyChanged += EditorPropertyChanged;
                _editor.DocumentChanged += EditorChanged;
                _editor.SelectionChanged += EditorChanged;
                _editor.LayoutCompleted += OnLayoutCompleted;
                _editor.Commands.LocalizationChanged += OnLocalizationChanged;
                if (_editor.Scroller is { } scroller) scroller.ScrollChanged += ScrollerChanged;
            }
            UpdateAutomationName();
            InvalidateVisual();
        }
    }

    public DocumentRuler()
    {
        Focusable = true;
        ClipToBounds = true;
        UpdateAutomationName();
    }

    private void UpdateAutomationName()
    {
        var horizontal = Orientation == RulerOrientation.Horizontal;
        var name = Label(horizontal ? "Horizontal.Name" : "Vertical.Name",
            horizontal ? "Horizontal ruler" : "Vertical ruler");
        var instructions = Label(horizontal ? "Horizontal.Instructions" : "Vertical.Instructions",
            horizontal ? "Up and Down select a marker; Left and Right adjust it."
                : "Left and Right select a marker; Up and Down adjust it.");
        string? selected = null;
        if (_editor is not null && RulerMetrics.Read(_editor) is { } metrics)
        {
            var markers = metrics.Markers(Orientation);
            if (_keyboardMarker >= 0 && _keyboardMarker < markers.Count)
            {
                var marker = markers[_keyboardMarker];
                selected = Label("Marker." + marker.Kind, marker.Label ?? marker.Kind.ToString());
            }
        }
        var template = selected is null ? Label("Automation", "{0}. {1}")
            : Label("Automation.Selected", "{0}, {1}. {2}");
        try { AutomationProperties.SetName(this, selected is null
            ? string.Format(CultureInfo.CurrentUICulture, template, name, instructions)
            : string.Format(CultureInfo.CurrentUICulture, template, name, selected, instructions)); }
        catch (FormatException) { AutomationProperties.SetName(this, selected is null
            ? $"{name}. {instructions}" : $"{name}, {selected}. {instructions}"); }
    }

    private string Label(string suffix, string fallback) =>
        _editor?.Commands.Localize?.Invoke("Textalonia.UI.Ruler." + suffix) is { Length: > 0 } localized
            ? localized : fallback;

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        UpdateAutomationName();
    }

    private void EditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextaloniaEditor.ZoomProperty || e.Property == TextaloniaEditor.ViewModeProperty ||
            e.Property == TextaloniaEditor.CurrentPageNumberProperty || e.Property == TextaloniaEditor.IsReadOnlyProperty ||
            e.Property == TextaloniaEditor.PageGapProperty || e.Property == TextaloniaEditor.PagesPerRowProperty)
            InvalidateVisual();
    }
    private void EditorChanged(object? sender, EventArgs e) { if (_drag is not null && _editor?.Session.Revision != _drag.Metrics.Revision) CancelDrag(); UpdateAutomationName(); InvalidateVisual(); }
    private void OnLocalizationChanged(object? sender, EventArgs e) => UpdateAutomationName();
    private void OnLayoutCompleted(object? sender, EditorLayoutCompletedEventArgs e)
    {
        var attached = _editor;
        Dispatcher.UIThread.Post(() =>
        {
            if (attached is null || !ReferenceEquals(_editor, attached)) return;
            UpdateAutomationName();
            InvalidateVisual();
        }, DispatcherPriority.Loaded);
    }
    private void ScrollerChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => Orientation == RulerOrientation.Horizontal
        ? new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, 24)
        : new Size(24, double.IsFinite(availableSize.Height) ? availableSize.Height : 0);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(RulerBackground ?? Editor?.Background ?? Brushes.LightGray, new Rect(Bounds.Size));
        if (RulerMetrics.Read(Editor) is not { } metrics) return;
        DrawTicks(context, metrics);
        DrawMarkers(context, metrics);
    }

    private void DrawTicks(DrawingContext context, RulerMetrics metrics)
    {
        var horizontal = Orientation == RulerOrientation.Horizontal;
        var pageStart = horizontal ? metrics.Page.Bounds.Left : metrics.Page.Bounds.Top;
        var pageEnd = horizontal ? metrics.Page.Bounds.Right : metrics.Page.Bounds.Bottom;
        var viewStart = horizontal ? metrics.Horizontal(pageStart) : metrics.Vertical(pageStart);
        var viewEnd = horizontal ? metrics.Horizontal(pageEnd) : metrics.Vertical(pageEnd);
        var bandStart = horizontal ? metrics.Horizontal(metrics.Page.ContentBounds.Left) : metrics.Vertical(metrics.Page.ContentBounds.Top);
        var bandEnd = horizontal ? metrics.Horizontal(metrics.Page.ContentBounds.Right) : metrics.Vertical(metrics.Page.ContentBounds.Bottom);
        var foreground = RulerForeground ?? Editor?.Foreground ?? Brushes.Black;
        var border = RulerBorder ?? Editor?.BorderBrush ?? Brushes.Gray;
        if (horizontal)
            context.FillRectangle(PaperBackground ?? Editor?.Background ?? Brushes.White, new Rect(Math.Max(0, bandStart), 1, Math.Max(0, bandEnd - Math.Max(0, bandStart)), 22));
        else context.FillRectangle(PaperBackground ?? Editor?.Background ?? Brushes.White, new Rect(1, Math.Max(0, bandStart), 22, Math.Max(0, bandEnd - Math.Max(0, bandStart))));
        var line = new Pen(border, 1);
        var axisLength = horizontal ? Bounds.Width : Bounds.Height;
        var firstTick = Math.Max(0, Math.Floor(-viewStart / metrics.Zoom / 24) * 24);
        var lastTick = Math.Min(pageEnd - pageStart, (axisLength - viewStart) / metrics.Zoom + 24);
        for (var tick = firstTick; tick <= lastTick; tick += 24)
        {
            var p = viewStart + tick * metrics.Zoom;
            if (p < -10 || p > axisLength + 10) continue;
            var major = ((int)Math.Round(tick / 24)) % 4 == 0;
            var length = major ? 10 : 5;
            if (horizontal) context.DrawLine(line, new Point(p, 23), new Point(p, 23 - length));
            else context.DrawLine(line, new Point(23, p), new Point(23 - length, p));
            if (!major) continue;
            var inches = (int)Math.Round(tick / 96);
            using var label = new TextLayout(inches.ToString(), new Typeface(FontFamily.Default), 9, foreground);
            if (horizontal) label.Draw(context, new Point(p + 2, 1));
            else label.Draw(context, new Point(1, p + 1));
        }
        if (horizontal)
        {
            context.DrawLine(line, new Point(viewStart, 23), new Point(viewEnd, 23));
            foreach (var column in metrics.Page.Columns)
            {
                var x = metrics.Horizontal(column.Left);
                context.DrawLine(line, new Point(x, 1), new Point(x, 23));
                x = metrics.Horizontal(column.Right);
                context.DrawLine(line, new Point(x, 1), new Point(x, 23));
            }
        }
        else context.DrawLine(line, new Point(23, viewStart), new Point(23, viewEnd));
    }

    private void DrawMarkers(DrawingContext context, RulerMetrics metrics)
    {
        var markers = metrics.Markers(Orientation);
        for (var i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            var p = _drag is { } drag && drag.Marker.Kind == marker.Kind && drag.Marker.Index == marker.Index
                ? drag.Marker.Position + drag.Current - drag.Start : marker.Position;
            var selected = IsFocused && _keyboardMarker == i;
            var brush = selected ? Brushes.DodgerBlue : RulerForeground ?? Editor?.Foreground ?? Brushes.Black;
            if (Orientation == RulerOrientation.Horizontal)
            {
                var top = MarkerCrossPosition(marker.Kind);
                context.FillRectangle(brush, new Rect(p - 3, top, 6, 8));
            }
            else context.FillRectangle(brush, new Rect(MarkerCrossPosition(marker.Kind), p - 3, 8, 6));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is not { IsReadOnly: false } editor || e.Pointer.Type != PointerType.Mouse ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || RulerMetrics.Read(editor) is not { } metrics) return;
        var pointer = e.GetPosition(this);
        var coordinate = Coordinate(pointer);
        var cross = Orientation == RulerOrientation.Horizontal ? pointer.Y : pointer.X;
        var markers = metrics.Markers(Orientation);
        var closest = markers.Select((marker, index) => (marker, index,
                distance: Math.Abs(marker.Position - coordinate) + Math.Abs(MarkerCrossPosition(marker.Kind) + 4 - cross)))
            .Where(item => Math.Abs(item.marker.Position - coordinate) <= 5 &&
                Math.Abs(MarkerCrossPosition(item.marker.Kind) + 4 - cross) <= 5)
            .OrderBy(item => item.distance).FirstOrDefault();
        RulerMarker marker;
        if (closest.marker.Label is not null) { marker = closest.marker; _keyboardMarker = closest.index; }
        else if (Orientation == RulerOrientation.Horizontal && coordinate >= metrics.Horizontal(metrics.TabOrigin) &&
            coordinate <= metrics.Horizontal(metrics.ParagraphRight))
            marker = new(RulerMarkerKind.TabStop, coordinate, -1, "New tab stop");
        else return;
        if (editor.Session.GetCapability(RulerEdit.OperationFor(marker.Kind)) != CommandCapability.Enabled) return;
        if (marker.Kind is RulerMarkerKind.TableColumnBoundary or RulerMarkerKind.TableRowBoundary &&
            !BeginTableResize(metrics, marker)) return;
        _drag = new(metrics, marker, coordinate, coordinate);
        _pointer = e.Pointer;
        Focus(); UpdateAutomationName(); e.Pointer.Capture(this); e.Handled = true; InvalidateVisual();
    }

    private bool BeginTableResize(RulerMetrics metrics, RulerMarker marker)
    {
        if (metrics.Cell is not { } cell || Editor is null) return false;
        var axis = marker.Kind == RulerMarkerKind.TableColumnBoundary ? TableResizeAxis.Column : TableResizeAxis.Row;
        var index = axis == TableResizeAxis.Column ? cell.Column + cell.Cell.ColumnSpan - 1 : cell.Row + cell.Cell.RowSpan - 1;
        var size = axis == TableResizeAxis.Column ? cell.ColumnWidth : cell.RowHeight;
        return Editor.BeginTableResize(cell.Table.Id, axis, index, size);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } drag) return;
        if (Editor?.Session.Revision != drag.Metrics.Revision || Editor.IsReadOnly ||
            Editor.Session.GetCapability(RulerEdit.OperationFor(drag.Marker.Kind)) != CommandCapability.Enabled)
        { CancelDrag(); return; }
        var current = Coordinate(e.GetPosition(this));
        _drag = drag with { Current = current };
        PreviewTableDrag(_drag);
        InvalidateVisual(); e.Handled = true;
    }

    private void PreviewTableDrag(Drag drag)
    {
        if (drag.Metrics.Cell is not { } cell || Editor is null) return;
        var initial = drag.Marker.Kind switch
        {
            RulerMarkerKind.TableColumnBoundary => cell.ColumnWidth,
            RulerMarkerKind.TableRowBoundary => cell.RowHeight,
            _ => 0
        };
        if (initial > 0) Editor.PreviewTableResize(initial + drag.Metrics.ToDocumentDelta(drag.Current - drag.Start));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not { } drag) return;
        _releasing = true;
        _drag = null;
        _pointer = null;
        try
        {
            if (e.Pointer.Captured == this) e.Pointer.Capture(null);
            if (Editor is { IsReadOnly: false } editor && editor.Session.Revision == drag.Metrics.Revision)
            {
                var delta = drag.Metrics.ToDocumentDelta(Coordinate(e.GetPosition(this)) - drag.Start);
                if (drag.Marker.Kind is RulerMarkerKind.TableColumnBoundary or RulerMarkerKind.TableRowBoundary)
                {
                    PreviewTableDrag(drag with { Current = Coordinate(e.GetPosition(this)) });
                    editor.CommitTableResize();
                }
                else RulerEdit.Apply(editor, drag.Metrics, drag.Marker, delta);
            }
            else Editor?.CancelTableResize();
        }
        finally { _releasing = false; InvalidateVisual(); }
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_releasing) CancelDrag();
    }

    private void CancelDrag()
    {
        if (_drag is null) return;
        _drag = null;
        var pointer = _pointer;
        _pointer = null;
        if (pointer?.Captured == this) pointer.Capture(null);
        Editor?.CancelTableResize();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _drag is not null) { CancelDrag(); e.Handled = true; return; }
        if (Editor is not { IsReadOnly: false } editor || RulerMetrics.Read(editor) is not { } metrics) return;
        var markers = metrics.Markers(Orientation);
        if (markers.Count == 0) return;
        _keyboardMarker = Math.Clamp(_keyboardMarker, 0, markers.Count - 1);
        var select = Orientation == RulerOrientation.Horizontal ?
            e.Key is Key.Up or Key.Down : e.Key is Key.Left or Key.Right;
        if (select)
        {
            _keyboardMarker = (_keyboardMarker + (e.Key is Key.Down or Key.Right ? 1 : markers.Count - 1)) % markers.Count;
            UpdateAutomationName(); InvalidateVisual(); e.Handled = true; return;
        }
        var adjust = Orientation == RulerOrientation.Horizontal ? e.Key is Key.Left or Key.Right : e.Key is Key.Up or Key.Down;
        if (!adjust) return;
        var delta = (e.Key is Key.Right or Key.Down ? 1 : -1) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 8);
        var marker = markers[_keyboardMarker];
        if (marker.Kind is RulerMarkerKind.TableColumnBoundary or RulerMarkerKind.TableRowBoundary)
        {
            if (BeginTableResize(metrics, marker))
            {
                var drag = new Drag(metrics, marker, 0, delta * metrics.Zoom);
                PreviewTableDrag(drag); editor.CommitTableResize();
            }
        }
        else RulerEdit.Apply(editor, metrics, marker, delta);
        InvalidateVisual(); e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OrientationProperty) { UpdateAutomationName(); InvalidateMeasure(); InvalidateVisual(); }
        else if (change.Property == RulerBackgroundProperty || change.Property == PaperBackgroundProperty ||
            change.Property == RulerForegroundProperty || change.Property == RulerBorderProperty) InvalidateVisual();
    }

    private double Coordinate(Point point) => Orientation == RulerOrientation.Horizontal ? point.X : point.Y;

    private static int MarkerCrossPosition(RulerMarkerKind kind) => kind switch
    {
        RulerMarkerKind.LeftMargin or RulerMarkerKind.RightMargin or
            RulerMarkerKind.TopMargin or RulerMarkerKind.BottomMargin => 1,
        RulerMarkerKind.FirstLineIndent or RulerMarkerKind.TabStop or RulerMarkerKind.ColumnBoundary => 8,
        _ => 15
    };
}

internal static class RulerEdit
{
    internal static void Apply(TextaloniaEditor editor, RulerMetrics metrics, RulerMarker marker, double delta)
    {
        if (editor.IsReadOnly || editor.Session.Revision != metrics.Revision || !double.IsFinite(delta) ||
            editor.Session.GetCapability(OperationFor(marker.Kind)) != CommandCapability.Enabled) return;
        var settings = metrics.Page.Settings;
        var margin = settings.Margins;
        var evenMirror = settings.MirrorMargins && (metrics.Page.Index + 1) % 2 == 0;
        switch (marker.Kind)
        {
            case RulerMarkerKind.LeftMargin:
                margin = evenMirror ? margin with { Right = Math.Max(0, margin.Right + delta) }
                    : margin with { Left = Math.Max(0, margin.Left + delta) };
                CommitPage(settings with { Margins = margin }); return;
            case RulerMarkerKind.RightMargin:
                margin = evenMirror ? margin with { Left = Math.Max(0, margin.Left - delta) }
                    : margin with { Right = Math.Max(0, margin.Right - delta) };
                CommitPage(settings with { Margins = margin }); return;
            case RulerMarkerKind.TopMargin:
                CommitPage(settings with { Margins = margin with { Top = Math.Max(0, margin.Top + delta) } }); return;
            case RulerMarkerKind.BottomMargin:
                CommitPage(settings with { Margins = margin with { Bottom = Math.Max(0, margin.Bottom - delta) } }); return;
            case RulerMarkerKind.LeftIndent:
                CommitParagraph(s => s with { Indent = Math.Clamp(s.Indent + delta, 0, 1000) }); return;
            case RulerMarkerKind.FirstLineIndent:
                CommitParagraph(s => s with { FirstLineIndent = Math.Clamp(s.FirstLineIndent + delta, -100000, 100000) }); return;
            case RulerMarkerKind.RightIndent:
                CommitParagraph(s => s with { RightIndent = Math.Clamp(s.RightIndent - delta, 0, 100000) }); return;
            case RulerMarkerKind.TabStop:
                var position = marker.Index < 0 ?
                    (marker.Position - metrics.Horizontal(metrics.TabOrigin)) / metrics.Zoom + delta :
                    metrics.Paragraph.TabStops[marker.Index].Position + delta;
                position = Math.Clamp(position, 0, 100000);
                if (metrics.Paragraph.TabStops.Where((_, index) => index != marker.Index)
                    .Any(tab => Math.Abs(tab.Position - position) < .001)) return;
                CommitParagraph(s =>
                {
                    var tabs = s.TabStops.ToList();
                    if (marker.Index >= 0 && marker.Index < tabs.Count) tabs[marker.Index] = tabs[marker.Index] with { Position = position };
                    else tabs.Add(new TabStop(position));
                    var ordered = tabs.OrderBy(tab => tab.Position).ToArray();
                    if (ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.Position >= pair.Second.Position)) return s;
                    return s with { TabStops = ordered.ToImmutableArray() };
                });
                return;
            case RulerMarkerKind.ColumnBoundary:
                var columns = settings.Columns;
                var i = marker.Index;
                if (i < 0 || i + 1 >= columns.Length) return;
                var first = metrics.Page.Columns[i].Width;
                var second = metrics.Page.Columns[i + 1].Width;
                if (first + delta < 8 || second - delta < 8) return;
                var pair = columns[i].Width + columns[i + 1].Width;
                var weight = pair * (first + delta) / (first + second);
                CommitPage(settings with { Columns = columns.SetItem(i, new PageColumn(weight))
                    .SetItem(i + 1, new PageColumn(pair - weight)) });
                return;
        }

        void CommitPage(PageSettings changed)
        {
            if (changed == settings) return;
            try { changed.Validate(); editor.Session.SetPageSettings(changed, metrics.Page.SectionId); }
            catch (FormatException) { /* A drag beyond the opposite margin is only a preview. */ }
        }
        void CommitParagraph(Func<ParagraphStyle, ParagraphStyle> change)
        {
            if (Math.Abs(delta) < .001 && !(marker.Kind == RulerMarkerKind.TabStop && marker.Index < 0)) return;
            editor.ApplyParagraphStyle(change);
        }
    }

    internal static EditOperation OperationFor(RulerMarkerKind kind) => kind switch
    {
        RulerMarkerKind.LeftMargin or RulerMarkerKind.RightMargin or RulerMarkerKind.TopMargin or
            RulerMarkerKind.BottomMargin or RulerMarkerKind.ColumnBoundary => EditOperation.Structure,
        RulerMarkerKind.TableColumnBoundary or RulerMarkerKind.TableRowBoundary => EditOperation.Tables,
        _ => EditOperation.Formatting
    };
}
