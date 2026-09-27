# Phase 6 implementation report

Phase 6 adds managed editing gestures and isolated mobile qualification hosts.
Desktop native input/accessibility and mobile device qualification remain open.
The new hosts and automated contact injection do not certify platform behavior.
[Interaction contracts](INTERACTIONS.md) document gestures, new APIs and history ownership.

| Task | Implementation | Qualification disposition |
| --- | --- | --- |
| P6.1 | Shared visual caret/navigation geometry for bidi text, keyboard movement and selection | Managed regression coverage; repeat N05 on native platforms |
| P6.2 | Table sizing/selection and richer formatting actions with preview/commit/cancel boundaries | Managed interaction coverage; native pointer/keyboard equivalence remains in qualification |
| P6.3 | Timer-driven edge scrolling, distance-based speed, stable anchor and capture/focus/detach cancellation | Headless state transitions and geometry coverage; native stress/performance execution pending |
| P6.4 | Structured content transfer, modifier semantics, insertion preview and guarded history ownership | Managed success/failure/read-only/undo coverage; native drag/clipboard reruns pending |
| P6.5 | Touch tap/long press, caret/range handles, crossing, pan arbitration, context actions and shared Android/iOS qualification screen | Touch headless tests and shared/Android compilation; device/emulator gates remain incomplete (MOB-01) |
| P6.6 | Extended native/mobile run generator, exact inventory fields, per-scenario evidence and explicit support dispositions | Every native scenario remains pending (NATIVE-06/MOB-01); existing accessibility backend gap retained |

## Touch and host implementation

The default pointer component owns a touch-only controller; custom pointer
components retain their existing contract. Pending contacts remain unhandled and
uncaptured until a 500 ms hold or an existing handle claims them. Motion over
12 DIP yields to panning without changing selection. Range handles hold the opposite
UTF-16 endpoint across crossing and reuse the distance-based edge scroll clock;
caret handles keep a collapsed selection. Drawing and hit testing use shared caret
geometry and current DIP viewport, with 44 DIP handle hit targets. Focus, capture,
document replacement, retemplating and detach cancel active work and subscriptions.
Embedded control events retain the existing source/routing ownership rules.

An unmoved long press opens the standard command context menu. Read-only selection
and copying remain available; command checks prevent mutations. The mobile screen
adds labeled selection/copy/cut/paste/undo alternatives and both embedded and external
focus targets. The Android launcher uses Avalonia 12's application lifetime/factory;
the iOS launcher uses its native delegate. Both remain outside the default solution.
See [mobile procedures and build routes](MOBILE-QUALIFICATION.md).

## Verification and native limitations

On 2026-09-26:

- `TouchSelectionTests`: **10 passed**, including tap/pan arbitration, long-press
  read-only context actions, caret dragging, range-handle crossing, focus/viewport/
  capture cancellation, document replacement and detach, preedit cancellation before
  hit testing, ambiguous bidi affinity and clipped exact-row overflow.
- Shared `Textalonia.MobileHarness` Release build: **passed**, zero warnings/errors.
- Android dependency restore and `Compile` target: **passed**, zero warnings/errors,
  .NET SDK 10.0.204 / Android workload 36.1.53 / Avalonia 12.1.3 / Android API 36.
- Android full package build: **blocked at archive creation**, XABAA7000 temporary ZIP
  rename permission error. No package installation or native gesture result claimed.
- iOS: launcher source present; build/launch unexecuted without macOS/Xcode.
- Final `Textalonia.sln` Release build: **passed**, zero warnings/errors.
- Full Release suite: **438 passed, 0 failed, 0 skipped**, including the updated
  public API baseline and shared IME/accessibility/selection geometry regressions.
- Local NuGet and symbols pack: **passed**. Fresh-cache package consumer: **passed**,
  including compiled XAML, rendering, visual bidi navigation, rectangular merged-cell
  selection, resize previews, commit/undo and the existing codec/resource contracts.
- Native record generator and five checked-in pending record schemas: **passed**;
  desktop records contain N01-N10 and mobile records also contain M01-M07.
- [Verification receipt](baselines/phase6-verification.json) records commands and
  local output paths. Avalonia build telemetry was disabled for workspace builds;
  restored artifact directories required the environment's approved build access.

[Pending records](baselines/native/phase6) include N01-N10 for Windows/macOS/Linux
and N01-N10 plus M01-M07 for Android/iOS. Missing runs carry explicit unqualified
support-scope dispositions and corrective tasks. They retain null execution times
and empty evidence arrays; no headless test is substituted for a native pass.

Native text accessibility remains constrained by the pinned Avalonia public
text-provider bridge, as recorded in [Phase 4](PHASE4-ACCESSIBILITY.md). Managed
ranges, labeled buttons and value-provider tests do not prove TalkBack/VoiceOver,
Narrator or Orca text-navigation/selection announcements. Candidate windows,
reconversion, native clipboard and device keyboard occlusion require actual native
runs after this integration. Performance budgets are unchanged; mobile/desktop
repeated-gesture native measurements remain pending.

## Managed interaction measurements

The [dated probe and raw samples](baselines/performance/windows-2026-09-26-phase6-interactions/README.md)
cover 10,000 paragraphs, the long-paragraph corpus and 1,000 table cells, including
rendered frames. Single-tick autoscroll p95 was **4.60 / 4.39 / 12.88 ms** against
16 ms; table resize preview p95 was **59.64 ms** against 100 ms. All history,
selection-anchor, glyph-cache and timer cleanup assertions passed. The rendered
nested-table preview was inspected.

**PERF-06 remains open:** long-paragraph width-resize p95 was **103.09 ms** against
100 ms in the final run. A separate amplified stress driver combines explicit ticks
with the live timer and also misses the scroll target for some workloads; those
results and an earlier run are preserved, not presented as single-tick measurements.
No numeric budgets were changed. Native latency and general managed-heap leak
qualification remain outside this probe.

The probe also exposed render-pass mutations during virtualized resize: scroll-anchor
corrections and inline-control synchronization now defer until the compositor has
finished. Tests cover repeated viewport resizing and detach after queued work.

## Open gates

- **PERF-06:** repeat and profile residual long-paragraph resize and sustained native
  gesture latency on a recorded idle host; retain existing targets and disclose all
  samples. Performance/core maintainer, P6 exit gate/P8.2.
- **MOB-01:** finish Android package/launch verification and iOS build/signing,
  run M01-M07 with the software keyboard and reader on emulator/simulator and
  physical devices, record exact versions and fix failures. Mobile QA maintainer;
  P6.5/P6.6 and release scope P8.2. Mobile support remains unqualified.
- **NATIVE-06:** repeat N01-N10 across advertised desktop configurations, including
  supported IME reconversion, virtualized/resized candidate placement, native
  clipboard and native screen-reader text navigation. Desktop/accessibility QA;
  P6.6/P8.2. Record unavailable backend capabilities as explicit scope limitations.

Phase 7 model/integration work can proceed while these platform qualification
gates stay visible. The Phase 6 exit gate is not fully met until native evidence
or an approved narrower advertised scope resolves the open cases.
