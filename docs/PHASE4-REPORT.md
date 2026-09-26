# Phase 4 implementation report

P4.1–P4.5 are implemented. P4.6 has a tested document text-range contract and
supported value-provider notifications; native text-pattern integration remains
blocked by the pinned Avalonia 12.1.3 public automation API. This milestone does
not claim complete native screen-reader accessibility.

| Task | Implementation | Evidence |
| --- | --- | --- |
| P4.1–P4.2 | Independent keyboard, pointer, caret, composition components; exclusive attach/detach; default behavior extracted; demo replacement | Existing ControlTests, InputComponentTests, [contracts](INPUT-COMPONENTS.md) |
| P4.3 | Atomic inline descriptors, immutable resources, schema v3 with v1/v2 migration, alt-text export, history accounting | InlineModelTests, SchemaEvolutionTests, SerializationTests, [ownership](INLINE-CONTENT.md) |
| P4.4 | Shared text-line geometry, asynchronous bounded image decode, fallback, resize/undo/viewer support | InlineLayoutTests, InlineResourceTests, InlineImageViewTests |
| P4.5 | Explicit factory registry, viewport lifetime, focus routing, data-backed counter sample | InlineLayoutTests, InlineViewLifecycleTests, SampleInlineControls |
| P4.6 | Revision-scoped ranges, text navigation, selection/caret, offscreen bounds/scroll, descriptions and notifications | AccessibilityTests; [platform evidence and outstanding bridge work](PHASE4-ACCESSIBILITY.md) |

The native schema and public API baseline intentionally advance. Existing frozen
v1/v2 native fixtures remain migration inputs. Independent package-consumer checks
exercise inline insertion/resize, native serialization, replaceable defaults, and
the text-range API as well as the previous preview contracts.

Automated verification is performed on the current Windows environment; it does
not substitute for native Windows/macOS/Linux IME, clipboard, or screen-reader
qualification. See the accessibility report for the specific backend blocker and
native N07 procedure. Phase 5 can use the descriptor/resource contracts while that
bridge work remains open.


## Verification on 2026-09-26

- Release solution build: passed, zero warnings/errors.
- Complete Release suite: **257 passed**, zero failures/skips. Results:
  `artifacts/test-results/phase4-release.trx`.
- NuGet and symbol packages: created in `artifacts/packages`.
- Independent package consumer: restored into a fresh
  `artifacts/phase4-consumer-cache` and passed compiled XAML/theme, editing,
  schema v3, inline insert/resize/undo/redo, input component, accessibility
  contract and rendering checks. Dependencies came from the existing local
  NuGet cache; the Textalonia package came from the newly packed local feed.
- API baseline: updated additively; no previous public/protected declaration
  was removed.

The regression review additionally covers hidden merged-cell resource cleanup,
caret snapping when object deletion joins a grapheme, structural payload/resource
equality across native round trips, final resource-budget validation, exactly-once
factory release after failures, and preservation of host opacity/clips. A resource-
bearing 10,000-paragraph typing case verifies that local edits retain the scalable
index path instead of scanning every paragraph to prune resources.

Build/test commands used `-p:UsedAvaloniaProducts=` to keep Avalonia telemetry
writes out of the restricted user profile. The package consumer used a workspace
build profile and explicit NuGet configuration. These environment adjustments do
not change the shipped project settings.
