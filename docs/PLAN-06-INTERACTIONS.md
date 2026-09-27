# Phase 6: Editing and table interactions

[Roadmap and dependencies](ROADMAP.md#implementation-phases)

**Outcome:** complete, consistent editing gestures built on the new model and input components. **Entry:** Phases 2-4; structural drag/drop also needs P5.5. **Status:** desktop interaction implementation complete; native/device and residual performance qualification gates remain open. See [implementation and evidence](PHASE6-REPORT.md) and [interaction contracts](INTERACTIONS.md).

## Starting points

- The components extracted from [DocumentSurface](../src/Textalonia/Controls/DocumentSurface.cs) and shared geometry in [DocumentLayout](../src/Textalonia/Controls/DocumentLayout.cs).
- [Toolbar](../src/Textalonia/Controls/TextaloniaToolbar.cs), [theme](../src/Textalonia/Themes/Generic.axaml), and [table session commands](../src/Textalonia/Editing/EditorSession.cs).
- [Control tests](../tests/Textalonia.Tests/ControlTests.cs) and the qualification scripts introduced in Phase 1.

## Tasks

- [x] **P6.1 - Implement visual bidi navigation.** Use shaped line/caret stops for left/right movement, selection collapse, Home/End, and vertical preferred-column behavior across LTR/RTL runs. Define visual affinity at ambiguous boundaries while retaining logical UTF-16 selection coordinates and grapheme-safe deletion. **Done when:** Arabic/Hebrew plus Latin, numbers, punctuation, wraps, emoji, and reverse selections pass keyboard/pointer cases in paragraphs and table cells.

- [x] **P6.2 - Expose table and formatting interactions.** Add column/row resize affordances using Phase 3 sizing rules, rectangular cell selection, border/padding controls, merge-aware insert/delete commands, and nested-table targeting. Expose list restart/continue, richer typography/block styles, and mixed selection indicators. Preview resizing without creating a history entry for every pointer move; commit once or cancel exactly. **Done when:** pointer and keyboard alternatives give the same result, read-only mode prevents edits, and layout/selection/undo remain correct across spans and nested tables.

- [x] **P6.3 - Refine drag selection autoscroll.** Add timer-driven edge scrolling with distance-based speed, viewport-clamped hit testing, correct capture release, and cancellation on detach/focus changes. Preserve the original selection anchor while virtualized content is measured. **Done when:** holding a stationary pointer beyond an edge continues scrolling, reversed drags behave correctly, and release/capture loss stops timers without leaving a stuck gesture.

- [x] **P6.4 - Add content drag/drop.** Reuse structured fragment extraction/insertion for same-document moves, copies, cross-editor transfers, and external HTML/text drops. Define modifier semantics, insertion previews, drops inside the source selection, stale revisions, and unsupported payloads. Commit an internal move as one undo step and remove source content only after successful insertion; document cross-editor history ownership. **Done when:** failure/cancellation cannot lose content, readonly targets reject mutation, and nested structures/resources survive supported transfers.

- [ ] **P6.5 - Add touch selection and mobile hosts.** Implementation and harnesses are present; device/emulator qualification remains pending. Create minimal Android/iOS qualification hosts or integration harnesses, then implement long-press selection, caret/range handles, handle crossing, touch scrolling arbitration, and context actions. Account for DPI, viewport changes when the software keyboard opens, and embedded control focus. **Done when:** device/emulator tests demonstrate gesture behavior and accessible alternatives; desktop mouse simulation alone does not qualify mobile support.

- [ ] **P6.6 - Qualify native input and accessibility after integration.** Native evidence remains pending; dated dispositions and executable scripts are recorded in the Phase 6 report. Repeat Phase 1 desktop scripts with IME composition/reconversion where supported, candidate geometry after virtualization/resize, native clipboard, and screen-reader text navigation. Add Android/iOS keyboard, selection, focus, and composition scenarios from P6.5. Fix regressions and record application/backend limitations. **Done when:** every claimed platform scenario has a dated result and unresolved cases have an explicit fix or support-scope disposition.

## Exit gate

- Headless tests exercise gesture state transitions, cancellation, read-only behavior, and undo boundaries; native evidence covers the platform integrations those tests cannot exercise.
- Bidi caret positions, object selection, table handles, IME rectangles, and accessibility ranges all agree with shared geometry after scrolling and resizing.
- Desktop targets pass their declared checks. Mobile remains an uncompleted roadmap item if a host/device qualification gate is deferred.
- Performance budgets still hold during repeated drags/resizes and viewport transitions, with no leaked timers, event handlers, or resource views.

## Handoff

P6.1-P6.3 can begin after the Phase 4 contracts; P6.4 waits for P5.5 and P6.5 requires a mobile-capable host. Feed the final native results into Phase 8. Phase 7 integrations can proceed independently against the same completed model and diagnostics contracts.
