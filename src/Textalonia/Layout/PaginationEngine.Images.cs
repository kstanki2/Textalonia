using System.Collections.Immutable;
using Avalonia;
using Textalonia.Controls;
using Textalonia.Model;
using Textalonia.Rendering;

namespace Textalonia.Layout;

public sealed partial class PaginationEngine
{
    private sealed partial class Builder
    {
        // Zero-size descriptors exist only in the private shaping projection. The document and
        // image visuals retain validated dimensions and the atomic UTF-16 anchor.
        private static Paragraph SuppressPositionedImages(Paragraph paragraph) => paragraph with
        {
            Runs = paragraph.Runs.Select(run => run.Inline is { Placement.Anchor: not ImageAnchorKind.Inline } descriptor
                ? new RichRun(descriptor with { Width = 0, Height = 0 }, run.Style) : run).ToImmutableArray()
        };

        private void PlaceImages(Paragraph source, Paragraph paragraph, Item item, LineFragment? anchor = null)
        {
            var offset = _index.ById(source.Id).Start;
            var pageIndex = anchor?.PageIndex ?? _pages.Count - 1;
            var columnIndex = anchor?.ColumnIndex ?? _column;
            var page = _pages[pageIndex];
            var anchorColumn = page.Columns[columnIndex];
            var paragraphTop = anchor?.Bounds.Top ?? _y + paragraph.Style.SpaceBefore;
            foreach (var run in source.Runs)
            {
                if (run.Inline is { Placement: { Anchor: not ImageAnchorKind.Inline } placement } descriptor)
                {
                    var origin = placement.Anchor == ImageAnchorKind.Page ? new Point(placement.X, placement.Y) :
                        new Point(anchorColumn.Left + item.Left + Indent(paragraph) + placement.X, paragraphTop + placement.Y);
                    var bounds = new Rect(origin, new Size(descriptor.Width, descriptor.Height));
                    var pageBounds = new Rect(0, 0, page.Width, page.Height);
                    _positionedImages.Add(new(descriptor, offset, bounds, pageBounds) { PageIndex = pageIndex, IsPositioned = true });
                    var rotated = ImageDrawing.RotatedBounds(bounds, placement.Rotation);
                    if (!pageBounds.Contains(rotated))
                        _layoutDiagnostics.Add($"Positioned image {descriptor.Id} exceeds its anchor page and is clipped without adding overflow pages.");
                    if (placement.Wrap is not (ImageWrapKind.BehindText or ImageWrapKind.InFrontOfText))
                    {
                        IEnumerable<Rect> exclusions = placement.Wrap == ImageWrapKind.Contour && !placement.Contour.IsEmpty
                            ? WrapExclusionGeometry.Contour(bounds, placement)
                            : [rotated.Inflate(placement.Distance)];
                        for (var column = columnIndex; column < page.Columns.Length; column++)
                            foreach (var exclusion in exclusions)
                            {
                                var region = placement.Wrap == ImageWrapKind.TopBottom
                                    ? new Rect(page.Columns[column].Left, exclusion.Top, page.Columns[column].Width, exclusion.Height) : exclusion;
                                _wrapExclusions.Add((pageIndex, column, region));
                            }
                    }
                }
                offset += run.Storage.Length;
            }
        }
    }
}
