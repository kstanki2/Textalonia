using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Threading;
using Textalonia.Model;

namespace Textalonia.Controls;

public partial class DocumentSurface
{
    private sealed class InlineChild(Control control, IInlineControlFactory factory, InlineDescriptor descriptor)
    {
        public Control Control { get; } = control;
        public IInlineControlFactory Factory { get; } = factory;
        public InlineDescriptor Descriptor { get; set; } = descriptor;
        public Geometry? HostClip { get; set; } = control.Clip;
        public Geometry? AppliedClip { get; set; }
        public ITransform? HostTransform { get; set; } = control.RenderTransform;
        public RelativePoint HostTransformOrigin { get; set; } = control.RenderTransformOrigin;
        public ITransform? AppliedTransform { get; set; }
        public bool Released { get; set; }
        public bool LogicalAttachmentAttempted { get; set; }
        public bool VisualAttachmentAttempted { get; set; }
    }
    private readonly Dictionary<Guid, InlineChild> _inlineChildren = [];
    private readonly List<InlineVisual> _inlineVisuals = [];
    private InlineImageCache? _inlineImages;
    internal InlineImageCache? InlineImageCache => _inlineImages;
    private InlineControlFactoryRegistry? _inlineFactories;
    private IInlineResourceResolver? _inlineResolver;
    private InlineImageOptions? _inlineOptions;
    private FlowDocument? _inlineDocument;
    private bool _inlineUpdatePosted;

    internal void ResetInlineViews()
    {
        if (_inlineFactories is not null) _inlineFactories.Changed -= InlineFactoriesChanged;
        _inlineFactories = null;
        if (_inlineImages is not null)
        {
            _inlineImages.Changed -= InlineImagesChanged;
            _inlineImages.Dispose(); _inlineImages = null;
        }
        var children = _inlineChildren.Values.ToArray();
        _inlineChildren.Clear(); _inlineVisuals.Clear(); _inlineDocument = null;
        foreach (var child in children) ReleaseInlineChild(child);
    }

    private void InlineImagesChanged(object? sender, EventArgs e) => InvalidateVisual();
    private void InlineFactoriesChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        { Dispatcher.UIThread.Post(() => InlineFactoriesChanged(sender, e)); return; }
        if (!ReferenceEquals(sender, _inlineFactories)) return;
        ResetInlineViews(); Refresh();
    }

    private void ReleaseInlineChild(InlineChild child)
    {
        if (child.Released) return;
        child.Released = true;
        // Detach notifications are host code too. Failure in one cleanup step must
        // not skip the factory's matching Release or leave the other tree attached.
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception exception) { Editor?.ReportError(exception); }
        }
        Cleanup(() => { if (child.Control.IsKeyboardFocusWithin && Editor is not null) Focus(); });
        if (child.VisualAttachmentAttempted) Cleanup(() => VisualChildren.Remove(child.Control));
        if (child.LogicalAttachmentAttempted) Cleanup(() => LogicalChildren.Remove(child.Control));
        Cleanup(() =>
        {
            if (child.AppliedClip is not null && ReferenceEquals(child.Control.Clip, child.AppliedClip))
                child.Control.SetCurrentValue(ClipProperty, child.HostClip);
            if (child.AppliedTransform is not null && ReferenceEquals(child.Control.RenderTransform, child.AppliedTransform))
            {
                child.Control.SetCurrentValue(RenderTransformProperty, child.HostTransform);
                child.Control.SetCurrentValue(RenderTransformOriginProperty, child.HostTransformOrigin);
            }
        });
        Cleanup(() => child.Factory.Release(child.Control));
    }

    private void UpdateInlineViews()
    {
        if (_rendering)
        {
            // Child attachment/arrangement cannot invalidate the visual tree in
            // a compositor callback. Synchronize against the latest layout next turn.
            if (!_inlineUpdatePosted)
            {
                _inlineUpdatePosted = true;
                Dispatcher.UIThread.Post(() =>
                {
                    _inlineUpdatePosted = false;
                    if (!_isAttached || Editor is null) return;
                    EnsureLayout(Bounds.Width);
                    UpdateInlineViews();
                    InvalidateVisual();
                }, DispatcherPriority.Loaded);
            }
            return;
        }
        if (Editor is null || TopLevel.GetTopLevel(this) is null) return;
        if (!ReferenceEquals(_inlineFactories, Editor.InlineControlFactories) ||
            !ReferenceEquals(_inlineResolver, Editor.InlineResourceResolver) || _inlineOptions != Editor.InlineImageOptions)
            ResetInlineViews();
        if (_inlineImages is null)
        {
            _inlineResolver = Editor.InlineResourceResolver; _inlineOptions = Editor.InlineImageOptions;
            _inlineImages = new(_inlineResolver, _inlineOptions);
            _inlineImages.Changed += InlineImagesChanged;
            _inlineFactories = Editor.InlineControlFactories;
            if (_inlineFactories is not null) _inlineFactories.Changed += InlineFactoriesChanged;
        }
        if (!ReferenceEquals(_inlineDocument, Editor.Document))
        {
            if (Editor.Session.LastEdit is { Reset: true }) _inlineImages.Reset();
            _inlineDocument = Editor.Document;
        }
        _inlineVisuals.Clear();
        var viewport = _viewport.Width > 0 && _viewport.Height > 0 ? _viewport : new Rect(0, 0, Math.Max(1, Bounds.Width), 500);
        _inlineVisuals.AddRange(GeometryInlineVisuals().Where(v => v.Bounds.Intersects(viewport) &&
            (v.Clip is null || v.Clip.Value.Intersects(v.Bounds))));
        var visibleControls = _inlineVisuals.Where(v => v.Descriptor.Payload is ControlInlinePayload).Select(v => v.Descriptor.Id).ToHashSet();
        foreach (var pair in _inlineChildren.ToArray())
            if (!visibleControls.Contains(pair.Key)) { _inlineChildren.Remove(pair.Key); ReleaseInlineChild(pair.Value); }
        var resources = _inlineVisuals.Select(v => v.Descriptor.Payload).OfType<ImageInlinePayload>().Select(p => p.ResourceId).ToHashSet(StringComparer.Ordinal);
        _inlineImages.Retain(resources);
        foreach (var visual in _inlineVisuals)
        {
            var descriptor = visual.Descriptor;
            if (descriptor.Payload is ImageInlinePayload image)
            {
                Editor.Document.Resources.TryGetValue(image.ResourceId, out var resource);
                _inlineImages.Request(image.ResourceId, resource);
            }
            else if (descriptor.Payload is ControlInlinePayload control)
            {
                var factory = _inlineFactories?.TryGet(control.Type, out var found) == true ? found : null;
                _inlineChildren.TryGetValue(descriptor.Id, out var child);
                if (child is not null && !ReferenceEquals(factory, child.Factory))
                { _inlineChildren.Remove(descriptor.Id); ReleaseInlineChild(child); child = null; }
                if (factory is null) continue;
                try
                {
                    if (child is null)
                    {
                        var view = factory.Create(descriptor) ?? throw new InvalidOperationException("An inline factory returned no control.");
                        child = new(view, factory, descriptor);
                        if (view.GetVisualParent() is not null || view.GetLogicalParent() is not null)
                            throw new InvalidOperationException("An inline factory must return a control without a parent.");
                        child.LogicalAttachmentAttempted = true; LogicalChildren.Add(view);
                        child.VisualAttachmentAttempted = true; VisualChildren.Add(view);
                        _inlineChildren.Add(descriptor.Id, child);
                    }
                    else if (child.Descriptor != descriptor)
                    {
                        factory.Update(child.Control, descriptor);
                        child.Descriptor = descriptor;
                    }
                    AutomationProperties.SetName(child.Control, descriptor.AltText);
                    KeyboardNavigation.SetTabIndex(child.Control, visual.Position);
                    child.Control.Measure(new Size(visual.Bounds.Width / ViewZoom, visual.Bounds.Height / ViewZoom));
                }
                catch (Exception exception)
                {
                    _inlineChildren.Remove(descriptor.Id);
                    if (child is not null) ReleaseInlineChild(child);
                    Editor.ReportError(exception);
                }
            }
        }
        InvalidateArrange();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ArrangeInlineViews();
        return finalSize;
    }

    private void ArrangeInlineViews()
    {
        foreach (var visual in _inlineVisuals)
            if (_inlineChildren.TryGetValue(visual.Descriptor.Id, out var child))
            {
                try
                {
                    var size = new Size(visual.Bounds.Width / ViewZoom, visual.Bounds.Height / ViewZoom);
                    if (child.AppliedTransform is null || !ReferenceEquals(child.Control.RenderTransform, child.AppliedTransform))
                    {
                        child.HostTransform = child.Control.RenderTransform;
                        child.HostTransformOrigin = child.Control.RenderTransformOrigin;
                    }
                    if (ViewZoom == 1)
                    {
                        child.Control.SetCurrentValue(RenderTransformProperty, child.HostTransform);
                        child.Control.SetCurrentValue(RenderTransformOriginProperty, child.HostTransformOrigin);
                        child.AppliedTransform = null;
                    }
                    else
                    {
                        var origin = child.HostTransformOrigin.ToPixels(size);
                        var matrix = Matrix.CreateTranslation(-origin.X, -origin.Y) * (child.HostTransform?.Value ?? Matrix.Identity) *
                            Matrix.CreateTranslation(origin.X, origin.Y) * Matrix.CreateScale(ViewZoom, ViewZoom);
                        child.AppliedTransform = new MatrixTransform(matrix);
                        child.Control.SetCurrentValue(RenderTransformOriginProperty, new RelativePoint(0, 0, RelativeUnit.Absolute));
                        child.Control.SetCurrentValue(RenderTransformProperty, child.AppliedTransform);
                    }
                    child.Control.Arrange(new Rect(visual.Bounds.Position, size));
                    if (!ReferenceEquals(child.Control.Clip, child.AppliedClip)) child.HostClip = child.Control.Clip;
                    var localClip = visual.Clip is { } clip
                        ? ToDocument(clip.Intersect(visual.Bounds).Translate(new Vector(-visual.Bounds.X, -visual.Bounds.Y))) : (Rect?)null;
                    var surfaceClip = localClip is { } bounds ? new RectangleGeometry(bounds) : null;
                    child.AppliedClip = surfaceClip is null ? child.HostClip : child.HostClip is null ? surfaceClip :
                        new CombinedGeometry(GeometryCombineMode.Intersect, child.HostClip, surfaceClip);
                    if (!ReferenceEquals(child.Control.Clip, child.AppliedClip))
                        child.Control.SetCurrentValue(ClipProperty, child.AppliedClip);
                }
                catch (Exception exception)
                {
                    _inlineChildren.Remove(visual.Descriptor.Id);
                    ReleaseInlineChild(child);
                    Editor?.ReportError(exception);
                }
            }
    }

    private void DrawInlineImages(DrawingContext context, Rect viewport)
    {
        if (Editor is null || _inlineImages is null) return;
        foreach (var visual in _inlineVisuals)
        {
            if (!visual.Bounds.Intersects(viewport)) continue;
            if (visual.Descriptor.Payload is ImageInlinePayload image)
            {
                Editor.Document.Resources.TryGetValue(image.ResourceId, out var resource);
                if (_inlineImages.Request(image.ResourceId, resource) is { } bitmap)
                {
                    using var clip = context.PushClip(visual.Clip?.Intersect(visual.Bounds) ?? visual.Bounds);
                    context.DrawImage(bitmap, new Rect(bitmap.Size), visual.Bounds);
                }
            }
            var selected = !HasComposition && Editor.Session.Selection.Start <= visual.Position && Editor.Session.Selection.End > visual.Position;
            if (selected)
            {
                using var selectionClip = context.PushClip(visual.Clip ?? viewport);
                context.DrawRectangle(null, new Pen(Editor.SelectionBrush, 3), visual.Bounds.Inflate(1.5));
            }
        }
    }
}
