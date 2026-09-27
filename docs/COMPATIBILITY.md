# Compatibility baseline

This records the preview contract preserved by the Phase 2 engine and extended by the [Phase 3 document model](DOCUMENT-MODEL.md). Test names below are in [EditingTests](../tests/Textalonia.Tests/EditingTests.cs), [ControlTests](../tests/Textalonia.Tests/ControlTests.cs), [SerializationTests](../tests/Textalonia.Tests/SerializationTests.cs), and the baseline classes. A missing test is explicitly a gap, not evidence of support.

## Invariants and coverage

| Invariant | Current behavior and evidence |
| --- | --- |
| Immutable snapshots | Editing publishes a new `FlowDocument`; old block/run arrays and strings are never mutated. Unchanged objects may be shared. `Deterministic_fixtures_validate_round_trip_and_keep_original_snapshots` and `Fixed_seed_edits_validate_every_snapshot_and_restore_content_and_selection` compare old JSON after every operation. Model/session use requires no controls. |
| IDs and limits | All live blocks/cells, including covered cells, need unique nonempty IDs. Merge backups are separately validated historical snapshots and may reuse their original IDs. Validation limits: 100,000 elements including retained backups, depth 32, table 1-1,000 rows and 1-100 columns; native import is at most 32 MiB. `Duplicate_and_empty_IDs_are_rejected_including_hidden_cells`; missing boundary tests for every depth/size limit remain with P3/P5. |
| Coordinates | `DocumentIndex.Text`, selection, search, highlights and editing use UTF-16 code units. CRLF/CR normalize to LF; one LF separates visible paragraphs, including cells; U+2028 stays inside a paragraph. Hidden cells are excluded from visible coordinates. `Coordinates_are_UTF16_with_LF_and_soft_breaks_and_reverse_selection`, fixture round trips and cross-cell editing tests. `InsertText` strips NUL; `FromText` does not. |
| Directional selection | Anchor is the fixed endpoint; Active is the caret. `Start/End` are sorted views, not a loss of direction. `Select` clamps and snaps backward to a .NET grapheme boundary. Undo/redo must restore the original direction, content, and typing style. Baseline corpus checks every committed operation, including reversed selections. |
| Graphemes and bidi | Caret/deletion use .NET text elements (including combining sequences and emoji). `Grapheme_navigation_and_deletion_keep_emoji_and_combining_marks_intact` and the independent string oracle in the corpus. Default arrow navigation uses visual bidi stops (P6.1); session coordinates remain logical, and native N05 remains pending. Explicit edits may form a new cluster across the insertion boundary; Phase 2 must preserve text and not split surrogate pairs. |
| Merge restoration | Physical hidden cells remain. Unedited split restores original paragraphs/IDs; edited split keeps edited anchor paragraphs and restores other cells. `Merging_and_splitting_cells_is_lossless_and_keeps_edited_merged_text`, current-schema fixture coverage, generated merge/split/round trips. Phase 3 supports merge-aware structural edits; `TableModelTests` covers every insertion/deletion boundary and exact history restoration. |
| Undo grouping | Adjacent `InsertText(..., true)` calls coalesce for less than 800 ms until navigation, formatting, explicit `BreakUndoGroup`, or another operation. New edits clear redo. Phase 2 retains the default 100-entry limit and adds a 64 MiB estimate budget across undo/redo; shared storage is counted once, excluding the current snapshot. Existing typing, history-limit and replace-all tests plus corpus undo/redo. Phase 2 adds an injectable internal timestamp and a deterministic test of the 799/800 ms boundary, navigation breaks and coalescing under a byte limit. |
| Read-only | User/session edits and undo/redo are blocked; selection/copy are allowed. Host `Load`, `Document` and `Text` assignment still replace content and reset history, even in read-only mode. Existing read-only tests plus `Host_load_is_allowed_in_readonly_and_resets_history_and_selection`. Native clipboard/read-only interaction still needs N06. |
| Streams | Caller owns streams on success, cancellation and error. Codecs read from current position, never rewind/close the caller stream. `Formats_round_trip_text_and_leave_streams_open` and `Cancelled_codecs_leave_caller_streams_open`. Phase 8 ReleaseContractTests inject partial destination failures into every codec, including reporting and legacy paths. |
| Cancellation | Built-in async formats propagate cancellation during I/O and task scheduling. Cancellation is cooperative; it does not promise interruption inside an already-running synchronous parser. Partial output can exist: callers needing atomic save must write a temporary file and rename. Pre-canceled streams covered by the cancellation theory; mid-parse responsiveness is a P5.1/P8.1 gap. Custom formats must honor the token themselves. |
| Concurrent loads | Control captures `Session.Revision` before awaiting. First successfully completed load changes revision; another load with an older revision is rejected, regardless of start order. Edits/load/undo cause rejection; selection-only changes do not. `Concurrent_loads_accept_first_completion_and_reject_stale_results` and `Editing_during_load_rejects_result_but_selection_changes_allow_it`. No latest-request-wins guarantee. |
| Text binding | `Text` defaults to eager full visible text on document revisions; selection-only notifications reuse the published value. Phase 2 adds opt-in `SynchronizeText=false`, which leaves that Avalonia property at its last assigned/published value; explicit current reads use `Document.Text` or `Session.Index.ReadText`. Assigning different `Text` replaces structure and formatting and clears history. `Text_binding_survives_edits_and_host_replacement` plus existing Document binding tests. Benchmarks cover bound/unbound edits and source-to-control assignment. Avoid competing bindings to both properties. |
| Composition | Preedit is transient, not persisted/undoable until commit. Replacement and read-only transitions cancel preedit. Existing IME control tests cover the client contract; OS composition, focus/candidate placement/cancellation remain N01-N03 pending. |
| Structured replacement/clipboard | Partial cross-container replacement retains containers; select-all replacement clears them. Versioned fragments preserve clipped sections, nested/merged tables and inline resources, remapping IDs/lists/resources on insertion. Existing cross-cell/full replacement semantics remain; native/HTML/text fallback and stale async operations have regression tests. See [fragment contracts](INTERCHANGE.md); native qualification remains N06. |

## Public surface capture

[`Fixtures/public-api.txt`](../tests/Textalonia.Tests/Fixtures/public-api.txt) captures exported types, base types/interfaces, declared public/protected constructors, methods, properties, fields, events, enum constants and parameter defaults. `Public_and_protected_surface_matches_preview_baseline` checks it on every run and writes an actual snapshot on failure. It includes generated record members, public `DocumentSurface`, model records, `EditorSession`, commands/properties/events on editor/toolbar/viewer, and `IDocumentFormat`/built-in codecs. The independent NuGet consumer checks compiled XAML, resources, input, formatting, JSON and rendering.

Phase 8 adds public-api-contracts.txt for nested nullability, attributes, accessor/ref/init modifiers and generic constraints, and enables SDK package validation. A comparison against a previous public binary is deferred until one exists. See [release API contracts](API-CONTRACTS.md). These checks do not certify binary compatibility of every Avalonia version. Do not remove public model members to accommodate private storage without a reviewed migration note and consumer coverage.

## Native schema policy

The writer emits envelope `{ "version": 7, "document": ... }`, and the reader
accepts v4-v7. Versions 4-6 load with default secondary-story settings; v4 concrete
formatting remains explicit. Version checks precede model decoding, unknown members
are rejected, and unsupported or missing versions throw `NotSupportedException`.
Versions 1-3 were unused development schemas and have no migration support.
This retained-reader policy explicitly supersedes the earlier v4-only prerelease
policy; it is not a promise to accept arbitrary future schemas.
See [schema tests](../tests/Textalonia.Tests/NativeSchemaTests.cs) and
[current native semantics](INTEGRATIONS.md#current-native-schema).

When changing the prerelease schema:

1. Keep strict envelope and model validation, including rejection of unsupported versions and unknown members.
2. Update current-schema fixtures and round-trip tests to cover every persisted shape, including nested content, inline resources and merge backups. Verify text, IDs, formatting and undo/redo restoration.
3. Run the corpus, API check, serializers, control tests and independent package consumer. Document the current format and any deliberate changes.

There is no obligation to retain readers or migration fixtures for unused
development schemas. Establish a published-data compatibility policy before
making commitments for future releases.

## Document model APIs

The public surface adds selection formatting aggregation, list definitions and
model numbering, richer styles, table sizing, recursive cloning and cell block
collections. Existing public members remain available. `TableCell.Paragraphs` and
`MergeOriginal` are init-capable paragraph projections over authoritative `Blocks`
and `MergeOriginalBlocks`; use the block properties to preserve nested content.
The native wire format rejects paragraph projection property names rather than
accepting competing representations. The package consumer exercises nested editing,
merge-aware insertion, current-schema round trips and exact undo restoration.

Collapsed caret formatting now creates an undoable typing-style operation.
Mixed emphasis toggles apply a uniform chosen value, preserving unrelated styles.
Merged row/column edits are supported with documented deletion/promotion rules;
backups retain historical ID scopes while sharing the document depth/element
budget. See [document semantics](DOCUMENT-MODEL.md) for details and
[current interchange limits](INTERCHANGE.md#supported-subset-and-diagnosed-losses) for losses in external formats.


## Scalable editing APIs

The API snapshot adds `SynchronizeText`, `HistoryByteLimit`,
`RetainedHistoryBytes`, `DocumentPosition`, `CreatePosition`, `TryResolvePosition`,
index `ReadText`, `CharAt`, `ParagraphCount`, and the optional shaping policy below.
No P1 public member was removed.
The independent package consumer exercises these contracts. Public model arrays
and init/with expressions remain available; internal edits materialize compatibility
arrays on demand. RichRun retains its string constructor, init-capable Text,
deconstruction and value equality.
See [ADR 002](ADR-002-SCALABLE-CORE.md) for linear compatibility operations and
[architecture](ARCHITECTURE.md#performance-boundaries) for budget semantics.

### Optional shaping limit

`TextaloniaEditor.MaxShapingCharacters` is independent of `SynchronizeText`.
It defaults to `0`, retaining exact unrestricted rendering. The policy applies
to document paragraphs, not host-supplied template controls or placeholder text.
A positive value
(at least 2,048) limits each shaping input, including bidi context, grapheme
boundary discovery, lookahead and retries. Ordinary long paragraphs still use
small windows; this is not a paragraph-length or document-length limit.

If exact rendering would exceed the allowance, the control releases partial
layouts, suspends document rendering, and shows a rendering-limit message.
`LayoutError` exposes a `ShapingLimitExceededException` with `ParagraphId`,
`CharacterLimit` and `RequestedCharacters`; `OperationFailed` also reports it.
The same error is not reported on every repaint. No text is truncated or replaced,
and the document, logical selection, history, copy and export remain available.
Geometry-dependent pointer/navigation operations are ignored and the IME caret
rectangle is empty while rendering is suspended. Text edits and undo still work.

Changing the limit, editing/undoing the offending content, or loading another
document retries layout; a successful build clears `LayoutError`. Hosts can
bind this property to their own error UI. For example:

```xml
<textalonia:TextaloniaEditor Document="{Binding Document}"
    SynchronizeText="False" MaxShapingCharacters="65536" />
```

The bound covers shaping inputs and the documented engine memory estimate,
not native font-library allocations, total process memory or total time spent
finding a distant line. See [performance boundaries](ARCHITECTURE.md#performance-boundaries).

## Inline content and input APIs

The current native schema preserves inline descriptors and resource tables.
Text-only documents and default editing behavior remain supported. The public API
baseline includes input component contracts, inline model/session/view APIs,
resource resolver/cache/factory APIs, and a managed text-range contract.

Objects occupy one U+FFFC position in `Text` and the index. Use `PlainText` or
`ReadPlainText` for alt-text export, and never feed their offsets back into indexed
selection APIs. `SelectedText` and plain-text clipboard data use alt text.
Custom input components are owned by one attached surface and must release their
subscriptions in Detach. Registered inline factories release views on recycling;
store state in descriptors. See [inline ownership](INLINE-CONTENT.md),
[input replacement](INPUT-COMPONENTS.md), and the
[explicit native accessibility blocker](ACCESSIBILITY.md).
