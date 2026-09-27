# Qualification targets and release decisions

This is a preview support policy, not platform certification. [Native procedures](NATIVE-BASELINES.md), [compatibility rules](COMPATIBILITY.md), and [measured workloads](PERFORMANCE.md) define the routes to evidence. Status is per capability: **supported** means a contract protected by automated regression tests; **experimental** means implemented but awaiting native qualification; **untested** means no execution evidence. Pass/fail/pending are individual test results, independent of these support tiers.

## Targets

| Target and pinned engine/backend | Tier | Reproducible test route and execution owner |
| --- | --- | --- |
| .NET 8 model, editing and native v1-v4 serialization; Avalonia 12.1.3 | Supported preview contracts | `dotnet test tests/Textalonia.Tests -c Release`; core maintainer. Current execution evidence is retained per [release candidate](RELEASE.md). |
| Windows 11 24H2, build 26100; Avalonia Win32/Skia 12.1.3 | Experimental native integration | Desktop demo, N01-N07 at 100%/150% DPI; Windows QA maintainer. Headless Skia tests pass locally; native evidence pending. |
| macOS 14, Cocoa/Skia 12.1.3 (x64/arm64 hosts) | Untested native target | Demo, N01-N07 at standard/Retina scaling; macOS QA maintainer must record exact OS patch and architecture before execution. CI `macos-latest` is a moving automated runner, not this qualification image. |
| Ubuntu 24.04 LTS, Avalonia X11/Skia 12.1.3 | Untested native target | Demo in an X11 session, N01-N07; Linux QA maintainer records OS point release, X server, desktop, display scale and font packages. Wayland/XWayland is a separate experimental configuration, not covered by an X11 result. |
| Android, Avalonia 12.1.3 qualification host | Unqualified; host compilation only | [Mobile procedures](MOBILE-QUALIFICATION.md); Android package archive/launch and emulator plus physical-device M01-M07 remain MOB-01, P6.5/P6.6/P8.2. |
| iOS, Avalonia 12.1.3 qualification host | Unqualified; launcher source only | [Mobile procedures](MOBILE-QUALIFICATION.md); macOS/Xcode build, simulator and physical-device M01-M07 remain MOB-01, P6.5/P6.6/P8.2. |

Avalonia dependencies are taken from `Directory.Build.props` and the package dependency is `[12.1.3,13.0.0)`. The test/benchmark version remains pinned to 12.1.3; that range does not certify later versions. Windows automation used .NET runtime 8.0.31 and SDK 10.0.204; CI selects SDK 8.0.x and must archive its actual version.

| Input/source/accessibility target | Status | Route, version inventory and owner |
| --- | --- | --- |
| Windows Microsoft Pinyin, Microsoft Japanese IME, Microsoft Korean IME | Experimental | OS-bundled build matching Windows build; record language-pack versions and keyboard mode. N01-N03, Windows QA. |
| macOS Pinyin, Japanese Romaji, Korean 2-Set | Untested | OS-bundled input sources; record OS patch. N01-N03, macOS QA. |
| Linux IBus Pinyin/Mozc/Hangul | Untested | Record `ibus version`, package versions (`dpkg-query`) and desktop session. N01-N03, Linux QA. Fcitx is outside the current qualification configuration until separately recorded. |
| US-International dead keys (Windows/Linux), ABC Extended (macOS) | Experimental / untested as above | N04; exact keyboard layout and input source in evidence, desktop QA. |
| Textalonia-to-Textalonia rich clipboard | Experimental | N06, two demo windows, exact commit/package version; desktop QA. |
| Microsoft Word + Edge (Windows), TextEdit + Safari (macOS), LibreOffice Writer + Firefox (Linux) | Untested | N06 in both directions using the checked-in HTML source. Record **exact application build** from About/version before running; interchange maintainer owns pinning installed versions by P5.6. No Office/browser fidelity claim before evidence. |
| Windows Narrator, macOS VoiceOver, Linux Orca | Experimental value provider; untested native announcements | N07; Narrator/VoiceOver versions follow exact OS build, record Orca package version. Accessibility maintainer. Text ranges/selection provider is a known P4.6 gap on every backend. |

Exact native application, IME package and unexecuted OS patch versions remain pending inventory fields, not fabricated baseline versions. `scripts/New-NativeRun.ps1` creates records with those fields; records cannot be marked pass until filled. Additional source apps/readers need independent evidence.

## Decision register

Roles identify responsibility, not a claimed assignment to a named person. Maintainers may nominate individuals without changing the gate.

| ID | Decision / current disposition | Responsible role | Required by |
| --- | --- | --- | --- |
| D01 | Preserve the preview tiers above; do not promote native/mobile support based on headless tests. Final advertised support scope remains open. | Product/core maintainer | Before expanding support claims; P8.2 |
| D02 | Preserve v1-v3 files, UTF-16 offsets, immutable public snapshots and eager `Text` compatibility binding. Contract changes follow the [migration process](COMPATIBILITY.md#native-schema-policy). | Core/schema maintainer | Every schema/API change |
| D03 | Retain the adopted [latency/memory budgets](PERFORMANCE.md#phase-2-budgets); current overruns remain visible. The implementation completion decision defers residual latency qualification as [PERF-01](PERFORMANCE.md#perf-01-residual-latency-qualification) without changing numeric targets. | Performance/core maintainer | Before release performance claims; P8.2 |
| D04 | MIT selected by the maintainer on 2026-09-27; LICENSE and package metadata added. Contributor attribution retained; copyright authority and named NuGet owner still require confirmation. | Project owner / release maintainer | P8.3, before redistribution/public publication |
| D05 | Local `origin` is `https://github.com/kstanki2/Textalonia.git`, confirmed public by GitHub API; Repository/project/source-link metadata now use that URL. Maintainers must confirm authority before publication. | Repository owner | P8.3 |
| D06 | Package ID `Textalonia`, version `0.1.0-preview.1`; authenticated package reservation/control remains **unverified**; a public package listing is not ownership evidence. | Package/release maintainer | P8.3 before reserving/publishing |
| D07 | Freeze exact native OS patches, source-app/IME/reader versions in each execution record; supply unavailable hosts and human operators. | Platform QA leads | Inventory before native execution; all advertised scope evidence by P6.6/P8.2 |

MIT and the repository/package metadata are configured. D05/D06 remain open until the release maintainer confirms repository authority and package control while authenticated as the intended owner. Follow [the release procedure](RELEASE.md); obsolete discovery receipts are not current ownership evidence.

## Interchange qualification

Automated codec and clipboard-adapter evidence is recorded in
[interchange contracts](INTERCHANGE.md#supported-subset-and-diagnosed-losses). The locally authored corpus records null source
application versions and is not an application export claim. These pairs remain
explicitly **unqualified in both directions**, including opening exported DOCX
without repair and application-rendered visual comparison:

| Platform | Application pairs | Evidence still required |
| --- | --- | --- |
| Windows | Textalonia ↔ Textalonia, Edge, Microsoft Word, LibreOffice Writer, Notepad | Exact installed builds, N06 native/HTML/text transfer, Unicode CF_HTML, images/resources, rejected-native fallback and atomic cut/paste undo |
| macOS | Textalonia ↔ Textalonia, Safari, Microsoft Word, LibreOffice Writer, TextEdit | Exact installed builds and native clipboard flavors; same bidirectional scenarios |
| Linux/X11 | Textalonia ↔ Textalonia, Firefox, LibreOffice Writer, plain-text editor | Exact distribution/desktop/application builds and native clipboard ownership; same scenarios |

Synthetic WordprocessingML/RTF/HTML specimens and headless clipboard tests protect
the supported model subset. They do not certify any of these applications or
platform pairs. P5.2 application-export corpus expansion and P5.6 native execution
remain tracked until those records and rendered comparisons are collected.

## Candidate qualification

[Release notes](RELEASE-NOTES.md) advertise managed preview contracts only.
[Candidate validation](RELEASE.md) retains exact source/package/symbol identity,
dependency terms, independent consumer results, extended workloads and pending
native records. The three-OS CI route must execute at the candidate commit before
publication. Windows-only local evidence does not certify macOS/Linux or mobile.
Outstanding performance, native, mobile, accessibility and application-corpus
gates remain indexed in the [roadmap](ROADMAP.md#open-work).
