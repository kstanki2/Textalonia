# Compatibility baseline

This records the preview contract preserved by the Phase 2 engine and extended by the [Phase 3 document model](DOCUMENT-MODEL.md). Test names below are in [EditingTests](../tests/Textalonia.Tests/EditingTests.cs), [ControlTests](../tests/Textalonia.Tests/ControlTests.cs), [SerializationTests](../tests/Textalonia.Tests/SerializationTests.cs), and the baseline classes. A missing test is explicitly a gap, not evidence of support.

## Invariants and coverage

| Invariant | Current behavior and evidence |
| --- | --- |
| Immutable snapshots | Editing publishes a new `FlowDocument`; old block/run arrays and strings are never mutated. Unchanged objects may be shared. `Deterministic_fixtures_validate_round_trip_and_keep_original_snapshots` and `Fixed_seed_edits_validate_every_snapshot_and_restore_content_and_selection` compare old JSON after every operation. Model/session use requires no controls. |
| IDs and limits | All live blocks/cells, including covered cells, need unique nonempty IDs. Merge backups are separately validated historical snapshots and may reuse their original IDs. Validation limits: 100,000 elements including retained backups, depth 32, table 1-1,000 rows and 1-100 columns; native import is at most 32 MiB. `Duplicate_and_empty_IDs_are_rejected_including_hidden_cells`; missing boundary tests for every depth/size limit remain with P3/P5. |
| Coordinates | `DocumentIndex.Text`, selection, search, highlights and editing use UTF-16 code units. CRLF/CR normalize to LF; one LF separates visible paragraphs, including cells; U+2028 stays inside a paragraph. Hidden cells are excluded from visible coordinates. `Coordinates_are_UTF16_with_LF_and_soft_breaks_and_reverse_selection`, fixture round trips and cross-cell editing tests. `InsertText` strips NUL; `FromText` does not. |
| Directional selection | Anchor is the fixed endpoint; Active is the caret. `Start/End` are sorted views, not a loss of direction. `Select` clamps and snaps backward to a .NET grapheme boundary. Undo/redo must restore the original direction, content, and typing style. Baseline corpus checks every committed operation, including reversed selections. |
| Graphemes and bidi | Caret/deletion use .NET text elements (including combining sequences and emoji). `Grapheme_navigation_and_deletion_keep_emoji_and_combining_marks_intact` and the independent string oracle in the corpus. Arrow navigation currently follows logical offsets; visual bidi navigation is missing (P6.1), and native N05 remains pending. Explicit edits may form a new cluster across the insertion boundary; Phase 2 must preserve text and not split surrogate pairs. |
| Merge restoration | Physical hidden cells remain. Unedited split restores original paragraphs/IDs; edited split keeps edited anchor paragraphs and restores other cells. `Merging_and_splitting_cells_is_lossless_and_keeps_edited_merged_text`, `Frozen_v1_fixture_preserves_all_fields_and_merge_restoration`, generated merge/split/round trips. Phase 3 supports merge-aware structural edits; `TableModelTests` covers every insertion/deletion boundary and exact history restoration. |
| Undo grouping | Adjacent `InsertText(..., true)` calls coalesce for less than 800 ms until navigation, formatting, explicit `BreakUndoGroup`, or another operation. New edits clear redo. Phase 2 retains the default 100-entry limit and adds a 64 MiB estimate budget across undo/redo; shared storage is counted once, excluding the current snapshot. Existing typing, history-limit and replace-all tests plus corpus undo/redo. Phase 2 adds an injectable internal timestamp and a deterministic test of the 799/800 ms boundary, navigation breaks and coalescing under a byte limit. |
| Read-only | User/session edits and undo/redo are blocked; selection/copy are allowed. Host `Load`, `Document` and `Text` assignment still replace content and reset history, even in read-only mode. Existing read-only tests plus `Host_load_is_allowed_in_readonly_and_resets_history_and_selection`. Native clipboard/read-only interaction still needs N06. |
| Streams | Caller owns streams on success, cancellation and error. Codecs read from current position, never rewind/close the caller stream. `Formats_round_trip_text_and_leave_streams_open` and `Cancelled_codecs_leave_caller_streams_open`. **Missing:** injected mid-write I/O failure test (P5.1/P8.1). |
| Cancellation | Built-in async formats propagate cancellation during I/O and task scheduling. Cancellation is cooperative; it does not promise interruption inside an already-running synchronous parser. Partial output can exist: callers needing atomic save must write a temporary file and rename. Pre-canceled streams covered by the cancellation theory; mid-parse responsiveness is a P5.1/P8.1 gap. Custom formats must honor the token themselves. |
| Concurrent loads | Control captures `Session.Revision` before awaiting. First successfully completed load changes revision; another load with an older revision is rejected, regardless of start order. Edits/load/undo cause rejection; selection-only changes do not. `Concurrent_loads_accept_first_completion_and_reject_stale_results` and `Editing_during_load_rejects_result_but_selection_changes_allow_it`. No latest-request-wins guarantee. |
| Text binding | `Text` defaults to eager full visible text on document revisions; selection-only notifications reuse the published value. Phase 2 adds opt-in `SynchronizeText=false`, which leaves that Avalonia property at its last assigned/published value; explicit current reads use `Document.Text` or `Session.Index.ReadText`. Assigning different `Text` replaces structure and formatting and clears history. `Text_binding_survives_edits_and_host_replacement` plus existing Document binding tests. Benchmarks cover bound/unbound edits and source-to-control assignment. Avoid competing bindings to both properties. |
| Composition | Preedit is transient, not persisted/undoable until commit. Replacement and read-only transitions cancel preedit. Existing IME control tests cover the client contract; OS composition, focus/candidate placement/cancellation remain N01-N03 pending. |
| Structured replacement/clipboard | Partial cross-container replacement retains containers; select-all replacement clears them. Clipboard fragments flatten tables/sections while retaining runs and paragraph metadata. Existing cross-cell, full replacement, rich fragment and headless clipboard tests. Structured clipboard is P5.5, native interchange N06. |

## Public surface capture

[`Fixtures/public-api.txt`](../tests/Textalonia.Tests/Fixtures/public-api.txt) captures exported types, base types/interfaces, declared public/protected constructors, methods, properties, fields, events, enum constants and parameter defaults. `Public_and_protected_surface_matches_preview_baseline` checks it on every run and writes an actual snapshot on failure. It includes generated record members, public `DocumentSurface`, model records, `EditorSession`, commands/properties/events on editor/toolbar/viewer, and `IDocumentFormat`/built-in codecs. The independent NuGet consumer checks compiled XAML, resources, input, formatting, JSON and rendering.

This reflection snapshot is an early change detector. It does not encode nullable annotations, all custom attributes, or binary compatibility of every Avalonia dependency; P8.1 must add full API/package compatibility validation. Do not remove public model members to accommodate private storage without a reviewed migration note and consumer coverage.

## Native schema policy

The writer emits envelope `{ "version": 2, "document": ... }`. The reader dispatches on the version before decoding the document: frozen strict v1 DTOs explicitly migrate old documents, while v2 reads the current model. Both reject unknown members. Missing or unsupported versions throw `NotSupportedException`, including newer envelopes with unknown document members. See [schema tests](../tests/Textalonia.Tests/SchemaEvolutionTests.cs), the unchanged v1 fixture and the new v2 fixture.

Before extending the schema:

1. Keep the frozen [`native-v1.json`](../tests/Textalonia.Tests/Fixtures/native-v1.json) unchanged and add fixtures for every new shape, including merge backups. Do not regenerate old fixtures to make a new reader pass.
2. Read and validate the envelope version first. Dispatch to an explicit version-specific DTO reader; keep strict validation within each version. Reject unsupported newer schemas with `NotSupportedException` before interpreting document members.
3. Migrate each older DTO to the current in-memory model with documented defaults. Preserve text, IDs, direction, runs, lists, sections, spans and merge restoration. Test old read -> migration -> new write -> new read, plus undo/redo. Identity migration suffices while the current model remains v1-compatible.
4. Bump writer version when a new persisted member/meaning cannot be read by v1. Do not silently write extra fields into version 1: its reader rejects them. Any down-export must be explicit about lost features.
5. Run the frozen corpus, API check, all serializers, control tests and independent package consumer. Document the version/support window and publish migration notes before changing the writer default. Migration never overwrites the caller's source file automatically.

Phase 2 retained schema v1. Phase 3 introduces v2 and continues reading v1. License/package ownership decisions do not block this compatibility policy.

## Phase 3 additive API migration

The public surface adds selection formatting aggregation, list definitions and
model numbering, richer styles, table sizing, recursive cloning and cell block
collections. Existing public members remain available. `TableCell.Paragraphs` and
`MergeOriginal` are init-capable paragraph projections over authoritative `Blocks`
and `MergeOriginalBlocks`; use the block properties to preserve nested content.
The v2 wire format rejects legacy paragraph collection names rather than accepting
competing representations. The package consumer exercises nested editing,
merge-aware insertion, schema v2 round trips and exact undo restoration.

Collapsed caret formatting now creates an undoable typing-style operation.
Mixed emphasis toggles apply a uniform chosen value, preserving unrelated styles.
Merged row/column edits are supported with documented deletion/promotion rules;
backups retain historical ID scopes while sharing the document depth/element
budget. See [document semantics](DOCUMENT-MODEL.md) for details and
[codec gaps](PHASE-03-CODEC-GAPS.md) for losses in external formats.


## Phase 2 additive API migration

The API snapshot adds `SynchronizeText`, `HistoryByteLimit`,
`RetainedHistoryBytes`, `DocumentPosition`, `CreatePosition`, `TryResolvePosition`,
index `ReadText`, `CharAt`, `ParagraphCount`, and the optional shaping policy below.
No P1 public member was removed.
The independent package consumer exercises these contracts. Existing native-v1
fixtures remain unchanged. Public model arrays and init/with expressions remain
available; internal edits materialize compatibility arrays on demand. RichRun
retains its string constructor, init-capable Text, deconstruction and value equality.
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
