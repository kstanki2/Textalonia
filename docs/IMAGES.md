# Pictures, watermarks and embedded objects

DX-07 adds image placement and editing, section watermarks, and OLE packages with
supplied previews. These use immutable image data and the existing atomic inline
position; no general drawing-object model or embedded-application activation is involved.

## Editing and ownership

`InlineDescriptor.Width` and `Height` are the displayed size in DIP (96 per inch).
An optional `Placement` holds `ImageAnchorKind`, `ImageWrapKind`, X/Y offsets,
distance from text, rotation in degrees, source crop fractions and an optional
normalized contour. Null placement preserves existing inline behavior. A crop
must leave a nonempty source rectangle; contour paths contain zero or 3-256 points.
`UpdateImage` changes geometry in one undo step. With aspect locking, providing
one dimension scales the other; supplying both is an explicit size override.

```csharp
editor.UpdateImage(imageId, new ImagePlacement
{
    Anchor = ImageAnchorKind.Paragraph,
    Wrap = ImageWrapKind.Square,
    X = 12, Y = 0, Distance = 8,
    Crop = new ImageCrop { Left = .1, Right = .1 },
    Rotation = 15
}, width: 240);
editor.SetWatermark(Guid.Empty, new DocumentWatermark { Text = "DRAFT" });
```

The toolbar's Pictures menu inserts images, edits move/size/crop/rotation/wrapping
properties, inserts/extracts/removes OLE objects, and sets/removes watermarks.
In Print Layout, drag a positioned picture to move it or drag its corner handle to
resize it. Hold Alt to select a picture behind text. The gesture previews geometry
and commits once on release; Escape cancels it. The properties dialog also commits
once and supports cancellation. All mutations honor
read-only state. Extraction remains available for read-only documents. File pickers
read bounded data and reject stale insertion after the document has changed.

`InsertImage(descriptor, original, preview)` can retain an original and a separate
renderable preview. Set `ImageInlinePayload.PreviewResourceId` when providing that
preview. `InsertOle` takes an `OleInlinePayload` descriptor, embedded package and
supplied preview resource. `ExtractOle` returns immutable package data; it does
not run an application. `RemoveInline` removes the atomic object in the active
story. Header/footer edits share the document's undo stack and resource catalog.

Pruning includes image originals, image/OLE previews, OLE packages, watermarks,
secondary stories, covered table cells and hidden merge backups. Clipboard paste
remaps colliding resource IDs and assigns fresh object identities. Undo retains
encoded resources, with their bytes charged to history; it never retains decoded
bitmaps. Existing resource count/byte limits apply to packages as well as images.

## Layout and output

Physical pagination positions images relative to their anchor paragraph or the
physical page. Their U+FFFC positions remain part of selection, deletion and text
coordinates. Square and top/bottom modes share exclusion geometry with positioned
tables. Exclusions apply from the anchor forward to current/following text paragraphs;
earlier lines are not retrospectively reflowed. Wrapping takes precedence over
conflicting paragraph keep/widow constraints, with a diagnostic. Contours use a
bounded conservative approximation; rotated image bounds
include rotation. Behind/in-front modes do not exclude body text. Objects are
ordered by document anchor order and may overlap; exclusions accumulate in that
order. Page overflow clips with a layout diagnostic instead of generating unbounded
pages. Edits invalidate the immutable page snapshot; resource decoding does not
change geometry or trigger repagination.

Simple view displays positioned objects inline. Floating objects in table cells,
paragraph frames, secondary stories and Draft view
also use inline placement, with diagnostics in paginated output. Editor pages,
preview, print
and PDF use shared crop/rotation geometry and supplied OLE previews. Strict output
rejects missing or unsupported image data; tolerant output reports the fallback.

`DocumentSection.Watermark` is dedicated header-associated background content,
independent of linked header text and first/odd/even variants. It contains either
text or an image resource ID, plus dimensions, rotation and opacity; text also has
font and color settings. It is centered on each owning physical page, behind the
content. `SetWatermark(Guid.Empty, ...)` selects the current section (materializing
the implicit first section if necessary). Passing null removes only that section's
watermark. Continuous sections on one sheet use that sheet's owning section.

## Decoding and formats

Image decoding is bounded independently of descriptor size. The default embedded
resolver never fetches remote resources. Native vector originals may be retained
with a separate preview; retaining EMF/WMF bytes does not claim native rendering.
`BoundedImageDecoder` renders PNG, JPEG, GIF, BMP, WebP and ICO plus a static SVG
subset: groups, paths, basic shapes, solid paints, opacity, viewBox and affine
transforms. SVG scripts, styles, text, embedded/referenced images, use, filters and
other unsupported constructs fail explicitly. Defaults limit vectors to 4,096
elements, 32 nesting levels and 65,536 path characters in addition to byte/pixel
limits. TIFF, EMF and WMF rendering requires a supplied preview or host conversion.
No new third-party decoder dependency is added.
See [inline resources](INLINE-CONTENT.md) for cache ownership and limits.

Native JSON v10, data XAML v5 and clipboard v6 retain the expanded model. Readers
continue accepting native v4–v9, XAML v1–v4 and clipboard v1–v5. Existing models
need no changes; new optional properties have defaults. DOCX maps supported picture
anchors, wrapping, crop/rotation, watermark headers and embedded OLE relationships.
Positioned OLE preview geometry survives DOCX through Textalonia metadata; external
OLE display is inline and the export reports this loss. Watermark reconstruction
also uses Textalonia metadata alongside visible VML header content.
Other codecs report unsupported placement, watermark and OLE semantics through
strict/tolerant conversion reports. Strict export stages output before writing.

Managed tests cover model validation, undo, resource retention, clipboard collisions,
page geometry, preview/output and interchange. External Word/LibreOffice/DevExpress
render comparisons, native file dialogs, DPI/pointer interaction and native print
qualification remain DX-14 gates; these are not implied by managed tests.
