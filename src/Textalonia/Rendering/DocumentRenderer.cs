using System.Collections.Immutable;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Textalonia.Controls;
using Textalonia.Export;
using Textalonia.Layout;
using Textalonia.Model;

namespace Textalonia.Rendering;

public enum UnsupportedContentPolicy { Strict, Tolerant }

public sealed record DocumentRenderOptions
{
    public UnsupportedContentPolicy UnsupportedContent { get; init; } = UnsupportedContentPolicy.Strict;
    public IInlinePrintProvider? InlineProvider { get; init; }
    public InlineImageOptions ImageLimits { get; init; } = new();
}

/// <summary>Creates a stable, noninteractive representation. Ownership transfers to the renderer.
/// Return null for unsupported content. No UI control is created or captured by the output pipeline.</summary>
public interface IInlinePrintProvider
{
    IInlinePrintRepresentation? TryCreateRepresentation(FlowDocument document, InlineDescriptor descriptor);
}

public interface IInlinePrintRepresentation : IDisposable
{
    void Draw(DrawingContext context, Rect bounds);
}

public sealed record PageLink(Rect Bounds, string Target);

/// <summary>Optional glyph/Unicode adapter. Draw the supplied shaped line at the exact origin;
/// image and control print representations are drawn separately by DocumentRenderer.</summary>
public interface IPageTextRenderer
{
    void DrawLine(DrawingContext context, TextLine line, Point origin);
}

/// <summary>Source text and exact page-local geometry. Body fragments precede secondary-story fragments.</summary>
public sealed record PageTextLine(Guid StoryId, Guid ParagraphId, int TextStart, string Text,
    Rect Bounds, Rect Clip, double Baseline);

public sealed class PagedOutputException : NotSupportedException
{
    public ImmutableArray<PagedOutputDiagnostic> Diagnostics { get; }
    public PagedOutputException(ImmutableArray<PagedOutputDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => d.Message))) => Diagnostics = diagnostics;
}

/// <summary>
/// Renders the same immutable page geometry to preview, print and export. All coordinates are DIP
/// relative to the physical sheet, independent of editor zoom and page arrangement. Use on the UI
/// thread. Dispose releases print representations and, when requested, the owned snapshot.
/// </summary>
public sealed class DocumentRenderer : IDisposable
{
    private readonly bool _ownsSnapshot;
    private readonly Dictionary<Guid, IInlinePrintRepresentation> _representations = [];
    private bool _disposed;
    public PageLayoutSnapshot Snapshot { get; }
    public int PageCount => Snapshot.Pages.Length;
    public ImmutableArray<PagedOutputDiagnostic> Diagnostics { get; }

    public DocumentRenderer(PageLayoutSnapshot snapshot, DocumentRenderOptions? options = null, bool ownsSnapshot = false)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _ownsSnapshot = ownsSnapshot;
        options ??= new();
        Validate();
        if (!Enum.IsDefined(options.UnsupportedContent)) throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentNullException.ThrowIfNull(options.ImageLimits);
        options.ImageLimits.Validate();
        var diagnostics = ImmutableArray.CreateBuilder<PagedOutputDiagnostic>();
        foreach (var message in snapshot.LayoutDiagnostics)
            diagnostics.Add(new("layout.fallback", message));
        foreach (var diagnostic in snapshot.FontDiagnostics)
            diagnostics.Add(new(diagnostic.Code, diagnostic.Message + " Family: " + diagnostic.FamilyName));
        long decodedPixels = 0;
        var embeddedImages = new Dictionary<string, IInlinePrintRepresentation>(StringComparer.Ordinal);
        try
        {
            if (snapshot.IsDraft) diagnostics.Add(new("output.draft", "Output requires physical pagination, not a Draft snapshot."));
            foreach (var visual in snapshot.InlineVisuals().DistinctBy(v => v.Descriptor.Id))
            {
                var descriptor = visual.Descriptor;
                if (descriptor.Payload is not (ImageInlinePayload or ControlInlinePayload)) continue;
                var representation = options.InlineProvider?.TryCreateRepresentation(snapshot.Document, descriptor);
                if (representation is null && descriptor.Payload is ImageInlinePayload image &&
                    snapshot.Document.Resources.TryGetValue(image.ResourceId, out var resource) && resource.Kind == DocumentResourceKind.Embedded)
                {
                    try
                    {
                        if (embeddedImages.TryGetValue(image.ResourceId, out var shared))
                        {
                            _representations.Add(descriptor.Id, shared);
                            continue;
                        }
                        if (resource.Data.Length > options.ImageLimits.MaximumEncodedBytes)
                            throw new InvalidDataException("Encoded image exceeds the output byte limit.");
                        var (width, height) = ImageDimensions.Read(resource.Data.AsSpan());
                        CheckSize(width, height);
                        using var stream = new MemoryStream(resource.Data.ToArray(), false);
                        var bitmap = new Bitmap(stream);
                        try { CheckSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height); }
                        catch { bitmap.Dispose(); throw; }
                        decodedPixels += (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;
                        representation = new BitmapRepresentation(bitmap);
                        embeddedImages.Add(image.ResourceId, representation);
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidDataException or NotSupportedException or InvalidOperationException)
                    { diagnostics.Add(new("output.image.unavailable", "Image cannot be printed: " + error.Message, visual.PageIndex)); }
                }
                if (representation is not null) _representations.Add(descriptor.Id, representation);
                else diagnostics.Add(new(descriptor.Payload is ControlInlinePayload ? "output.control.unsupported" : "output.image.unsupported",
                    "No print representation is available for inline '" + descriptor.AltText + "' (" + descriptor.Id + ").", visual.PageIndex));
            }
            Diagnostics = diagnostics.ToImmutable();
            if (snapshot.IsDraft || options.UnsupportedContent == UnsupportedContentPolicy.Strict && !Diagnostics.IsEmpty)
                throw new PagedOutputException(Diagnostics);
        }
        catch
        {
            foreach (var representation in _representations.Values.Distinct<IInlinePrintRepresentation>(ReferenceEqualityComparer.Instance)) representation.Dispose();
            _representations.Clear();
            // Ownership transfers only after successful construction.
            throw;
        }

        void CheckSize(int width, int height)
        {
            if (width < 1 || height < 1 || width > options.ImageLimits.MaximumDimension || height > options.ImageLimits.MaximumDimension ||
                (long)width * height > options.ImageLimits.MaximumDecodedPixels - decodedPixels)
                throw new InvalidDataException("Decoded images exceed the output pixel limit.");
        }
    }

    public void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Dispatcher.UIThread.VerifyAccess();
        Snapshot.VerifyAlive();
    }

    public void DrawPage(DrawingContext context, int pageIndex, IPageTextRenderer? textRenderer = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var page = Page(pageIndex);
        using var pageClip = context.PushClip(new Rect(page.Bounds.Size));
        context.DrawRectangle(Brushes.White, null, new Rect(page.Bounds.Size));
        using var transform = context.PushTransform(Matrix.CreateTranslation(-page.Bounds.X, -page.Bounds.Y));
        using var scope = new InlineOutputScope((descriptor, _, _) => _representations.ContainsKey(descriptor.Id));
        Snapshot.DrawBackgrounds(context, page.Bounds);
        Snapshot.DrawContentForOutput(context, page.Bounds, textRenderer);
        foreach (var visual in Snapshot.InlineVisuals().Where(v => v.PageIndex == pageIndex))
            if (_representations.TryGetValue(visual.Descriptor.Id, out var representation))
            {
                using var clip = context.PushClip((visual.Clip ?? page.Bounds).Intersect(page.Bounds));
                representation.Draw(context, visual.Bounds);
            }
    }

    public ImmutableArray<PageLink> GetLinks(int pageIndex)
    {
        var page = Page(pageIndex);
        var links = ImmutableArray.CreateBuilder<PageLink>();
        foreach (var fragment in Fragments(pageIndex))
        {
            using var lease = fragment.Acquire();
            var line = lease.Layout.TextLines[fragment.Line.Index];
            var offset = fragment.Position.Start;
            foreach (var run in fragment.Measurement.Source.Runs)
            {
                var from = Math.Max(offset, fragment.TextStart);
                var to = Math.Min(offset + run.Storage.Length, fragment.TextEnd);
                if (to > from && run.Style.Hyperlink is { } link && FlowDocument.IsSafeHyperlink(link))
                    foreach (var bounds in line.GetTextBounds(from - fragment.SourceStart, to - from))
                    {
                        var rect = new Rect(fragment.Origin.X + bounds.Rectangle.X, fragment.Origin.Y,
                            bounds.Rectangle.Width, fragment.Bounds.Height).Intersect(fragment.Clip).Intersect(page.Bounds);
                        if (rect.Width > 0 && rect.Height > 0) links.Add(new(Local(rect, page), link));
                    }
                offset += run.Storage.Length;
            }
        }
        return links.ToImmutable();
    }

    public ImmutableArray<PageTextLine> GetTextLines(int pageIndex)
    {
        var page = Page(pageIndex);
        return Fragments(pageIndex).Select(fragment =>
        {
            var source = fragment.Measurement.Source;
            var start = Math.Clamp(fragment.TextStart - fragment.Position.Start, 0, source.Length);
            var length = Math.Min(fragment.Length, source.Length - start);
            var text = string.Concat(source.Slice(start, length).Select(run => run.PlainText));
            return new PageTextLine(fragment.StoryKey, fragment.ParagraphId, fragment.TextStart, text,
                Local(fragment.Bounds, page), Local(fragment.Clip.Intersect(page.Bounds), page), fragment.Baseline - page.Bounds.Y);
        }).ToImmutableArray();
    }

    private IEnumerable<LineFragment> Fragments(int pageIndex) => Snapshot.Fragments.Concat(Snapshot.StoryFragments).Where(f => f.PageIndex == pageIndex);
    private PageLayout Page(int index)
    {
        Validate();
        if ((uint)index >= (uint)PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        return Snapshot.Pages[index];
    }
    private static Rect Local(Rect rect, PageLayout page) => rect.Translate(new Vector(-page.Bounds.X, -page.Bounds.Y));
    public void Dispose()
    {
        if (_disposed) return;
        Dispatcher.UIThread.VerifyAccess();
        _disposed = true;
        foreach (var representation in _representations.Values.Distinct<IInlinePrintRepresentation>(ReferenceEqualityComparer.Instance)) representation.Dispose();
        _representations.Clear();
        if (_ownsSnapshot) Snapshot.Dispose();
    }

    private sealed class BitmapRepresentation(Bitmap bitmap) : IInlinePrintRepresentation
    {
        public void Draw(DrawingContext context, Rect bounds) => context.DrawImage(bitmap, new Rect(bitmap.Size), bounds);
        public void Dispose() => bitmap.Dispose();
    }
}

// Scoped to synchronous drawing on the UI thread; nested output restores the previous renderer.
internal sealed class InlineOutputScope : IDisposable
{
    [ThreadStatic] internal static Func<InlineDescriptor, DrawingContext, Rect, bool>? Current;
    [ThreadStatic] private static InlineOutputScope? _active;
    private readonly Func<InlineDescriptor, DrawingContext, Rect, bool>? _previous = Current;
    private readonly InlineOutputScope? _previousScope = _active;
    private readonly List<IDisposable> _retained = [];
    private bool _disposed;
    public InlineOutputScope(Func<InlineDescriptor, DrawingContext, Rect, bool> draw)
    { Current = draw; _active = this; }

    // Recording backends replay glyphs after TextLine.Draw returns. Temporary field/alt-text
    // layouts must survive that replay; ordinary screen drawing still disposes them immediately.
    internal static IDisposable Retain(IDisposable value)
    {
        if (_active is not { } scope) return value;
        scope._retained.Add(value);
        return DeferredDisposal.Instance;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Current = _previous; _active = _previousScope;
        foreach (var value in _retained) value.Dispose();
        _retained.Clear();
    }
    private sealed class DeferredDisposal : IDisposable
    {
        internal static readonly DeferredDisposal Instance = new();
        public void Dispose() { }
    }
}
