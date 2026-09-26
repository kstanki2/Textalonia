# Phase 4: Extensible input and inline resources

[Roadmap and dependencies](ROADMAP.md#implementation-phases)

**Outcome:** host-customizable editing and embedded content without live controls in document snapshots. **Entry:** Phases 2-3 index, layout, and model contracts. **Status:** P4.1-P4.5 implemented; P4.6 managed contract implemented, native text-provider bridge blocked. See [implementation report](PHASE4-REPORT.md).

## Starting points

- [DocumentSurface](../src/Textalonia/Controls/DocumentSurface.cs): input routing, caret, composition, and automation peer.
- [Editor/session integration](../src/Textalonia/Controls/TextaloniaEditor.cs), [layout](../src/Textalonia/Controls/DocumentLayout.cs), and [model](../src/Textalonia/Model/Blocks.cs).
- [Default theme](../src/Textalonia/Themes/Generic.axaml) and [demo](../samples/Textalonia.Demo/MainWindow.axaml.cs).

## Tasks

- [x] **P4.1 - Define replaceable component contracts.** Specify keyboard, pointer/selection, caret, and composition components with clear routing precedence, handled-state behavior, shared geometry access, and attach/detach ownership. Keep defaults equivalent to current behavior. **Done when:** a host can replace one component without subclassing the entire surface, and component replacement/retemplating cannot leave event handlers or timers attached.

- [x] **P4.2 - Extract the default input implementation.** Move existing behavior into default components in small steps, preserving composition cancellation, shortcuts, focus, pointer capture, read-only rules, and undo grouping. Keep composition separate from committed snapshots and compatible with alternate keyboard bindings. **Done when:** existing control tests pass through the new components and a sample custom keymap/caret demonstrates replacement without disabling clipboard, IME, or selection.

- [x] **P4.3 - Define inline data and resource ownership.** Introduce serializable inline descriptors with stable resource IDs, alt text, dimensions, and typed payloads. Decide atomic selection/deletion and UTF-16 projection for non-text objects, distinguishing index coordinates from plain-text alt-text export. Define local/embedded/host-resolved resources, size limits, missing-resource behavior, and retention across snapshots, background saves, and history eviction. **Done when:** native migration, resource round trips, index/grapheme behavior, history byte accounting, and missing-resource fallbacks are tested without creating any visual controls.

- [x] **P4.4 - Render and edit inline images.** Resolve descriptors through a host-supplied resource service, decode asynchronously, and place images using shared line layout with correct baseline, bounds, caret stops, and selection. Cancel stale work after replacement/detach and bound decoded-image caches. **Done when:** insert/delete, resize data changes, undo/redo, save/load, viewer display, and failed image loads work; disposed views and evicted history do not retain decoded resources unnecessarily.

- [x] **P4.5 - Host arbitrary inline controls through factories.** Add a host registration/factory API mapping descriptor types to Avalonia controls. Define focus traversal, editor shortcut precedence, measurement invalidation, virtualization recycling, accessibility names, and fallbacks when a factory is absent. **Done when:** a sample inline interactive control survives document editing and viewport recycling, while serializers and background export only see descriptor data and never instantiate arbitrary document-specified types.

- [ ] **P4.6 - Implement text accessibility.** First verify the pinned Avalonia/backend text-provider capabilities and record any platform bridge dependency. Add supported document ranges, selection/caret reporting, text navigation, range bounds, scroll-into-view, change notifications, and descriptions for inline objects/tables. Ensure offscreen range queries cooperate with virtualization. **Done when:** provider contract tests and available native screen-reader scripts pass; missing backend support remains an explicit blocker to a complete text-accessibility claim.

## Exit gate

- Headless session/model/codecs require no visual factory or live control to manipulate or save embedded content.
- Input substitution, template replacement, virtualization, and repeated attach/detach preserve editing behavior and resource ownership.
- Inline object coordinates are consistent across indexing, IME, clipboard, selection, accessibility, and history.
- Accessibility has documented per-platform evidence and outstanding bridge limitations, rather than a value-only provider being labeled complete.

## Handoff

P4.1-P4.2 form the input track; P4.3 precedes images/controls and requires Phase 2 history integration. Complete resource and diagnostics-facing contracts before Phase 5 codecs. Phase 6 builds new gestures on these components, and final native qualification repeats accessibility checks after those interactions land.
