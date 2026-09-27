# Mobile interaction qualification

The Android and iOS launchers in `samples/Textalonia.MobileHarness.Android` and
`samples/Textalonia.MobileHarness.iOS` host the shared
[`QualificationView`](../samples/Textalonia.MobileHarness/QualificationView.cs).
They are deliberately outside `Textalonia.sln`: ordinary library, demo and test
builds do not require mobile workloads. The launchers use .NET 10 and the repository's
pinned Avalonia 12.1.3; the shared screen and library continue to target .NET 8.

The hosts provide a long document containing mixed scripts, combining characters,
emoji, table and nested-cell text, an embedded keyboard target, selection/revision/viewport/DPI readouts,
read-only switching and labeled selection/clipboard/undo buttons. No mobile support
claim follows from compiling these hosts or injecting desktop/headless contacts.

## Build and launch

Use a .NET 10 SDK with the Android/iOS workloads matching its feature band. Android
needs an Android 36 SDK and compatible Java installation; iOS needs a matching
macOS/Xcode toolchain. Configure signing only for your own test device. The pinned
[Avalonia Android application entry point](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Android/Avalonia.Android/AvaloniaAndroidApplication.cs)
and [iOS delegate](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/iOS/Avalonia.iOS/AvaloniaAppDelegate.cs)
are used directly. Android creates a new view per activity using
`IActivityApplicationLifetime.MainViewFactory` and requests keyboard resize.

```powershell
dotnet restore samples/Textalonia.MobileHarness.Android --configfile NuGet.Config
dotnet build samples/Textalonia.MobileHarness.Android -c Debug
dotnet build samples/Textalonia.MobileHarness.Android -c Debug -t:Run

# On the macOS host; use the runtime matching the simulator or physical device.
dotnet restore samples/Textalonia.MobileHarness.iOS --configfile NuGet.Config
dotnet build samples/Textalonia.MobileHarness.iOS -c Debug -p:RuntimeIdentifier=iossimulator-arm64
dotnet build samples/Textalonia.MobileHarness.iOS -c Debug -p:RuntimeIdentifier=iossimulator-arm64 -t:Run

pwsh -File scripts/New-InteractionRun.ps1 -Target android -Owner 'Android QA maintainer'
pwsh -File scripts/New-InteractionRun.ps1 -Target ios -Owner 'iOS QA maintainer'
```

Choose and record the emulator/simulator/device explicitly when several are
available. Archive the exact source revision plus working diff, SDK/workload/OS/API
versions, device model, keyboard app/version and language modes, reader/version,
fonts, density, orientation and before/after keyboard viewport DIP sizes. Keep a
separate record for emulator, simulator and physical device. The script produces
only pending cases; it never converts a build or headless pass into native evidence.

## Gesture contract

A touch tap places a caret and shows its handle. A stationary 500 ms contact selects
the surrounding non-whitespace grapheme sequence; moving more than 12 DIP first
abandons the pending selection and leaves the contact to the ancestor ScrollViewer.
Pending contacts do not take focus or cancel an embedded control's composition.
A second finger cancels selection ownership. Once a handle or long press claims the
contact, the surface captures it. Each range drag fixes the opposite logical
endpoint, so crossing produces a reverse selection; a caret handle remains
collapsed. Range handles share the timer-driven edge scroller with mouse selection.

Handles use shared shaped caret geometry and live viewport coordinates. Their hit
radius is 22 DIP, independent of device pixel density. Geometry is recomputed after
layout/keyboard changes and offscreen endpoints are not materialized during drawing.
A viewport change cancels an unclaimed long press. Focus loss, pointer cancellation,
document replacement, component replacement and detach disarm the timer/capture.

Releasing an unmoved long press opens the editor's context menu, including copy,
cut, paste and select all. Command enablement preserves read-only rules: selection
and copying work, mutation commands remain disabled. The harness's labeled buttons
and an external keyboard provide alternatives to dragging. Native TalkBack/VoiceOver
text-range navigation remains a separate, unqualified backend capability.

## Device scenarios

Repeat in portrait/landscape, at two display/font scales, with the software keyboard
hidden/shown, on emulator/simulator and on physical hardware. Record each supported
configuration separately. Repeat N01-N10 from [the desktop procedures](NATIVE-BASELINES.md)
where applicable; N01 additionally attempts IME reconversion and records an explicit
not-supported disposition when the keyboard/backend lacks it.

| Case | Actions and required observations |
| --- | --- |
| M01 | Tap a Latin, RTL, emoji and combining-character caret. Drag its handle without creating a range. Long press a word; move either range handle through the opposite endpoint in both directions. Repeat across wrapped lines and table cells. Record visual positions and UTF-16 anchor/active values. No operation splits a grapheme or creates undo history. |
| M02 | Pan before the long-press deadline: scroll without selection/focus theft. Hold still: select and capture. Drag a range beyond either edge and hold the finger stationary: continue scrolling with fixed anchor. Cancel a contact, add another finger, move focus, navigate away and return: no stuck capture or later timer selection. |
| M03 | Scroll into virtualized content; tap and open CJK keyboard. Rotate and resize while composing, switch keyboard layouts, hide/reopen the keyboard, change density/font scale. Caret/handles/IME candidate window must follow shared screen geometry without duplicate DPI transforms or keyboard occlusion. Preserve committed text and selection. |
| M04 | Copy/cut/paste via long-press context menu and labeled buttons; transfer both directions to the named native text app. Verify native flavor, Unicode, styled/structural content where supported and one-step undo. Toggle read-only during a gesture/composition: allow copy/selection; never mutate through keyboard, paste, cut or handles. |
| M05 | Tap the embedded TextBox while editor composition is active, type/compose in it, then return to the document. Parent must not consume child touch, keyboard or IME input. Scroll the embedded view out/in, hide/show the app and repeat. No preedit leaks between targets and detached views retain no capture/timer. |
| M06 | With TalkBack/VoiceOver, find the document and each labeled command, invoke select-word/select-all/copy and external-keyboard movement. Record value announcements separately from character/word/line navigation and selection announcements. Missing native text-provider bridge is an explicit accessibility limitation, never a pass inferred from the managed API. |
| M07 | Repeat 100 long presses, forward/reverse handle drags, edge scrolls, keyboard opens/closes, orientation/focus changes and background/foreground cycles. Record frame/latency/memory and inline-view counts. Compare against the existing performance budgets; check after settling that no timer/capture or accumulating views remain. |

Passes require dated native evidence (video, speech transcript, logs, before/after
text and selection). A failure needs a reproducer, corrective task and affected
scope. Missing device/operator/backend evidence remains pending. Emulator/simulator
results alone do not qualify physical touch or software-keyboard behavior.

## Dated inventory and remaining gates

Inventory on 2026-09-26: Windows SDK 10.0.204, Android workload 36.1.53, iOS workload
26.4.10259, Android platform/build tools 36 and Java 17 were available. The installed
Android SDK contained no emulator directory. No native mobile gesture run or connected
device evidence was collected. macOS/Xcode were unavailable in this Windows session.

The shared screen built in Release. Android restore and the `Compile` target passed
against Avalonia 12.1.3 with zero warnings/errors. Full Android packaging reached the
archive task but failed with XABAA7000 (`ZipException: Renaming temporary file failed:
Permission denied`); no installable-package or launch pass is claimed. iOS build,
signing and launch remain unexecuted. Local Android verification used a workspace
CLI/package profile and isolated `artifacts/phase6-mobile-build` outputs to avoid
adding SDK/cache requirements to the solution build.

[Phase 6 pending records](baselines/native/phase6) preserve all desktop and mobile
cases as pending. **MOB-01** owns Android packaging/device and iOS build/device
qualification; **NATIVE-06** owns desktop IME/clipboard/accessibility reruns. Owners:
mobile and desktop QA maintainers respectively, gates P6.5/P6.6 and P8.2. P6.5/P6.6
remain incomplete until the claimed native scenarios receive dated results or an
explicit narrower support disposition.
