# Inline content and resource ownership

Inline content is immutable document data. `RichRun.Inline` holds an
`InlineDescriptor` with a stable `Id`, `AltText`, positive `Width`/`Height` in DIPs,
and an `ImageInlinePayload`, `ControlInlinePayload`, or `MergeFieldInlinePayload`. Neither snapshots nor
codecs contain controls, bitmaps, factories, callbacks, or CLR type names to load.

```csharp
var image = new InlineDescriptor
{
    AltText = "Quarterly results chart", Width = 320, Height = 180,
    Payload = new ImageInlinePayload("chart-q1")
};
editor.InsertInline(image, new DocumentResource
{
    Kind = DocumentResourceKind.Embedded, MediaType = "image/png",
    Data = System.Collections.Immutable.ImmutableArray.CreateRange(pngBytes)
});
editor.UpdateInline(image.Id, value => value with { Width = 480, Height = 270 });
```

Insertion, deletion and resizing enter session history. `UpdateInline` preserves
identity. A rich paste assigns new inline IDs and remaps conflicting resource IDs.
Missing resource IDs are valid and show the descriptor's alternative text.

## Coordinates and export

Every object occupies exactly one U+FFFC UTF-16 position, with grapheme boundaries
on both sides even when adjacent text starts with a combining mark. Index text,
`FlowDocument.Text`, the editor's `Text` binding, IME surrounding text, search,
selection, accessibility ranges, and history share these coordinates. Selection
and deletion are atomic. Alt text is descriptive data, not searchable indexed text.

`FlowDocument.PlainText`, `DocumentIndex.ReadPlainText`, `SelectedText`, plain-text
clipboard data and text export replace each object with its alt text. These output
strings can have different lengths; their offsets must not be used as document
positions. Native clipboard data preserves descriptors/resources. HTML and DOCX transfer supported embedded raster images; RTF transfers embedded PNG/JPEG. Unsupported encodings, host controls and unavailable or external images degrade to alternative text with conversion diagnostics. See [interchange contracts](INTERCHANGE.md).

## Encoded data

`FlowDocument.Resources` maps stable, ordinal string IDs to `DocumentResource`.
Embedded resources own immutable encoded bytes. Local and Host resources store an
opaque location; only a host resolver decides whether and how to access it.
The default `EmbeddedInlineResourceResolver` never opens files or networks.
Resource IDs and MIME types are bounded to 256 characters, locations to 4,096,
embedded data to 8 MiB per resource and 16 MiB per document, and the resource table
to 4,096 entries. Inline dimensions are positive finite values up to 10,000 DIP.

Snapshots and background saves own their immutable data independently of views.
History accounts for resource tables, descriptors, payload strings and shared byte
arrays. Session edits prune resources no longer referenced by live content,
covered cells or merge backups. Undo snapshots retain their resources until
history eviction; caller-held snapshots remain valid. Hosts can call
`PruneUnusedResources` on snapshots directly. A save never requires a resolver or
visual factory.

Native JSON reads and writes only current prerelease schema v4, preserving inline
descriptors and resources. Other versions are rejected; unused development schemas
have no migration support. Encoded bytes are base64 and inline payload kinds are an
explicit allowlist. Unknown members, invalid payloads and excessive resources are rejected.

## Images and view ownership

Set `InlineResourceResolver` to implement `IInlineResourceResolver.OpenReadAsync`.
The resolver runs off the UI thread, honors cancellation, and transfers ownership
of its returned stream to the caller. Layout uses descriptor dimensions immediately;
decoding never changes document size or history. Images align their bottom with
the text baseline. One text layout supplies wrapping, bounds, selection and caret
stops, including table clips and long paragraph windows.

Each surface owns an asynchronous `InlineImageCache`. Defaults bound encoded
reads to 16 MiB, individual dimensions to 8,192 pixels, retained decoded pixels
to 16 million, cache entries to 32, and concurrent loads to two. Supported
raster headers are checked before decoding; malformed, oversized and missing
images keep the alt-text fallback. The pixel budget is a cache-accounting limit,
not a bound on native decoder working memory. Pending decodes have independent
per-image limits. `InlineImageOptions` customizes these limits.

Only visible images are admitted. When a visible set exceeds the cache budget,
additional images keep their fallback until space becomes available, avoiding
continuous decode/eviction loops. Ordinary edits retain unchanged resources;
replacement, leaving the viewport, retemplating and detach cancel stale work and
release decoded images. Stale results are disposed. History owns encoded data,
never decoded images. A host can reset its public cache to retry terminal failures;
replacing the editor resolver also retries visible resources.

## Inline controls

Register explicit keys in `InlineControlFactoryRegistry`, assign it to
`InlineControlFactories`, and implement `IInlineControlFactory.Create`, `Update`,
and `Release`. A factory receives descriptor data and creates an unparented control
owned by that view. Factories receive Release when a control leaves the viewport,
its type registration changes, the template changes, or the surface detaches.
Unknown keys render the same alt-text placeholder as unavailable images.

Descriptor dimensions govern measurement and arrangement. Changes to child desired
size remain inside that allocation; persist size changes through `UpdateInline` to
reflow text and support undo. Store interactive state in typed descriptor properties,
not only in a recycled control. `SampleInlineControls` demonstrates an undoable
counter that survives scrolling and native save/load.

Focusable children participate in Avalonia tab traversal in document order.
Keyboard/text/pointer events from children belong to those children; surface
components handle events originating on the document surface. Child shortcuts
therefore do not also edit document text. Editor commands remain available to host
controls. Alt text becomes the child's automation name. Read-only document edits
are enforced by the session; factories decide whether non-document interactions
remain enabled in a viewer.

See [input contracts](INPUT-COMPONENTS.md) and the
[accessibility evidence and bridge limitations](ACCESSIBILITY.md).

Merge fields use measured run typography for their label geometry rather than fixed
descriptor dimensions. See [mail merge](MAIL-MERGE.md) for preview, generation and
field interchange contracts.
