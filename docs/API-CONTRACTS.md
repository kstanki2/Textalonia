# Public API and data contracts

The 0.1 preview contract is the exported API in public-api.txt plus the nullable,
attribute and modifier baseline in public-api-contracts.txt (both under
tests/Textalonia.Tests/Fixtures). Additive merge-field, mail-merge, and proofing APIs are included in these baselines.
The model, session, commands, input, resources, diagnostics, editor, viewers,
toolbar and codecs are all captured. Generated record members and protected
extension members participate in the checks.

Both snapshots must pass before packing. Review a snapshot diff with its migration
example and consumer coverage; never replace a baseline just to make a failure
disappear. The .NET SDK package validator also runs at pack time. After the first
public package exists, set PackageValidationBaselineVersion to the previous
published compatible version during restore/build/pack. No previous public binary
is currently available, so no historical binary comparison is claimed.
[SDK package validation](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/overview)
and [Source Link](https://github.com/dotnet/sourcelink/blob/main/docs/README.md)
describe the build mechanisms.

## Threading, ownership and failures

| Surface | Supported behavior |
| --- | --- |
| FlowDocument, blocks, styles, resources | Immutable snapshots may be shared between threads. Pure transforms return new values. Validate data before handing it to the editor. No control, stream, bitmap or live host service is persisted. |
| EditorSession | Mutable single-owner object. Serialize its calls and event handlers on one thread. A session attached to an editor belongs on that editor's UI thread. Events run synchronously on the caller's thread; concurrent mutation and reentrant edits in callbacks are not supported. Sessions/snapshots do not require disposal. |
| Editor, DocumentSurface, toolbar, viewers, commands | Create, bind, edit and access Avalonia controls on the UI thread. Commands follow read-only/can-execute state. Async command failures set LastError and raise OperationFailed; awaited load/save methods propagate failures to the caller. The host owns link activation. |
| Text / Document binding | Text is eager by default. SynchronizeText=false stops publishing full text after edits; use Document.Text or Session.Index.ReadText for current content. Assigning Text replaces rich structure and resets history. Bind one authoritative source, avoiding competing Text and Document bindings. Document assignment is a host load, even while read-only. |
| Selection | Directional UTF-16 Anchor/Active, sorted Start/End, clamped grapheme boundaries; one U+FFFC represents an inline object. PlainText/clipboard substitute its alternative text. Undo restores directional selection. Default key navigation is visual bidi; explicit session offsets remain logical. Highlights are snapshot offsets and need updating after edits. |
| Input components | UI thread; one attached surface per instance. Replacing a component or detaching the view calls Detach; release captures, timers and subscriptions there. The host owns injected service objects; they are not automatically disposed. |
| Proofing services | The editor calls AutoCorrect synchronously on its UI thread after committed input; callbacks should finish promptly. Spelling checks run asynchronously with cancellation and revision checks. Host spelling and hyphenation services own dictionaries, persistence, change notifications and their own thread safety. The editor does not dispose host services. See [PROOFING.md](PROOFING.md). |
| Inline resources | Resolver runs on a worker and must honor cancellation. A returned stream transfers ownership to the image loader, which disposes it. Embedded-only resolution is the default. UI-thread InlineImageCache owns bitmaps; returned bitmaps are borrowed until eviction/reset/disposal. Dispose a cache created directly by the host. |
| Inline control factories | UI thread, explicitly registered type keys only. Return a new unparented control for each Create. Each successful creation is paired with Release on eviction/detach. Release owns view resources; unregister/replacement does not dispose the factory service itself. |
| MarkdownViewer / ICodeHighlighter | Source updates originate on UI thread; parsing/tokenization may run on workers. Adapters must tolerate cancellation and concurrent calls. Latest revision wins; await WaitForParsingAsync/WaitForHighlightingAsync. Detachment cancels pending work/releases caches; the host retains ownership of its highlighter. Parsing/highlighting errors are reported separately, with canonical document text preserved. |
| IDocumentFormat / reports | Caller owns input/output streams, including failures/cancellation. Read/write at the current position without rewinding. Synchronous parsing is not interruptible; async cancellation is cooperative. Custom implementations must enforce their own limits/cancellation. Strict reporting stages output before writing; final I/O failure may leave partial bytes. Use a temporary file and rename for atomic saves. |
| Paged output | Capture and draw exact physical snapshots on the Avalonia UI thread. Dispose caller-created renderers after preview/export/printing completes. Preview and print jobs borrow them. Exporters leave caller streams open at their current position; final writes may fail after partial output. Native print adapters and PDF exporter services are host-owned. See [OUTPUT.md](OUTPUT.md) for strict/tolerant diagnostics and capability validation. |
| Exceptions | Invalid models/unsupported content use FormatException, JsonException, InvalidDataException or parser-specific exceptions; unsupported schema/extension uses NotSupportedException. Invalid API arguments use argument exceptions. Cancellation propagates OperationCanceledException; I/O errors propagate. Strict loss throws DocumentConversionException with its report. Do not match exception message text. Stable diagnostic codes are documented in INTERCHANGE.md. |
| Pending loads | Editor load accepts the first completed result whose captured session revision still matches. Edits/load/undo invalidate old results; selection-only changes do not. This differs from MarkdownViewer's latest-source revision policy. |

See [input](INPUT-COMPONENTS.md), [resources](INLINE-CONTENT.md),
[viewer lifecycle](MARKDOWN-VIEWER.md), and [interchange](INTERCHANGE.md)
for the detailed extension contracts. Headless managed accessibility text ranges
are covered; native screen-reader text navigation remains unqualified.

## Current native schema

Native JSON writes **schema v11** and reads **v4, v5, v6, v7, v8, v9, v10 and v11**. The full model includes
named style definitions, sparse overrides, document themes/fonts, nested cells and
merge backups, inline descriptors and resources, semantic metadata, merge fields, physical sections, secondary stories, anchored bookmarks/general fields,
internal hyperlinks, string document properties, image placement/crop/rotation, section watermarks, OLE package/preview descriptors, structured form controls,
protection settings and permission ranges. See [forms contracts](FORMS.md) for
policy and value APIs. See [image contracts](IMAGES.md) for migration examples and resource ownership.
Version 4 concrete formatting remains explicit when loaded. Versions 1-3 remain
unsupported. See [style contracts](STYLES.md).

The reader checks the envelope version before interpreting document members.
Missing or unsupported versions throw `NotSupportedException`; unknown members
are rejected. Current-schema round trips and rejection of other versions are
covered by tests. The v4 reader is an explicit compatibility path added for DX-01;
versions 1-3 remain unsupported. Establish a compatibility policy for published
data before making future release commitments.

The `.textalonia`, `.json`, and `.art` extensions all select the current native
codec. The data XAML vocabulary writes v5 and reads v1/v2/v3/v4/v5; it is neither Avalonia
object XAML nor another editor's format.

For nested cell data use cell.Blocks and cell.MergeOriginalBlocks instead of the
legacy Paragraphs/MergeOriginal projections. Keep projection-based code only
where cells intentionally contain paragraphs alone. Current-schema fixtures verify
round trips, resources, undo and reverse selection. Tests also cover hidden merge
restoration and rejection of unsupported versions and unknown members.

For scalable document binding, replace a Text binding with a Document binding,
set SynchronizeText=false, and explicitly read ranges when needed. For conversion
adoption, legacy IDocumentFormat still compiles; implement IReportingDocumentFormat
to report fidelity. Strict conversion rejects a legacy custom format's unknown
fidelity rather than silently promising losslessness.

The executable examples in tests/Textalonia.PackageSmoke cover these APIs against
a freshly restored NuGet package, including custom codecs, replacement keyboard,
host resource streams, control factories, proofing services, editor/viewer and optional highlighting.
