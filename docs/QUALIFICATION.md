# Qualification targets and release decisions

Phase 1 baseline, 2026-09-26. This is a preview support policy, not platform certification. [Native procedures](NATIVE-BASELINES.md), [compatibility rules](COMPATIBILITY.md), and [measured workloads](PERFORMANCE.md) define the routes to evidence. Status is per capability: **supported** means a contract protected by automated regression tests; **experimental** means implemented but awaiting native qualification; **untested** means no execution evidence. Pass/fail/pending are individual test results, independent of these support tiers.

## Targets

| Target and pinned engine/backend | Tier | Reproducible test route and execution owner |
| --- | --- | --- |
| .NET 8 model, editing and native v1 serialization; Avalonia 12.1.3 | Supported preview contracts | `dotnet test tests/Textalonia.Tests -c Release`; core maintainer. Local Windows execution in [report](BASELINE-REPORT.md). |
| Windows 11 24H2, build 26100; Avalonia Win32/Skia 12.1.3 | Experimental native integration | Desktop demo, N01-N07 at 100%/150% DPI; Windows QA maintainer. Headless Skia tests pass locally; native evidence pending. |
| macOS 14, Cocoa/Skia 12.1.3 (x64/arm64 hosts) | Untested native target | Demo, N01-N07 at standard/Retina scaling; macOS QA maintainer must record exact OS patch and architecture before execution. CI `macos-latest` is a moving automated runner, not this qualification image. |
| Ubuntu 24.04 LTS, Avalonia X11/Skia 12.1.3 | Untested native target | Demo in an X11 session, N01-N07; Linux QA maintainer records OS point release, X server, desktop, display scale and font packages. Wayland/XWayland is a separate experimental configuration, not covered by an X11 result. |
| Android, planned Avalonia 12 host on an emulator and physical device | Untested; host absent | Mobile maintainer creates P6.5 host, pins SDK/API/device/backend/keyboard versions, then executes composition, clipboard, keyboard occlusion and touch selection tests; P6.6/P8.2. |
| iOS, planned Avalonia 12 host on simulator and physical device | Untested; host absent | Mobile maintainer creates P6.5 host and pins Xcode/iOS/device/backend/keyboard versions; repeat P6.6/P8.2. Simulator alone cannot qualify touch and software-keyboard behavior. |

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
| D01 | Adopt the preview tiers above for baseline work. Preserve current model/API contracts in Phase 2; do not promote native/mobile support based on headless tests. Final advertised support scope remains open. | Product/core maintainer | Baseline policy adopted for P2.1; final scope P8.2 |
| D02 | Preserve v1 files, UTF-16 offsets, immutable public snapshots and eager `Text` binding during Phase 2; any opt-in alternative needs the [migration process](COMPATIBILITY.md#native-schema-policy). | Core/schema maintainer | Adopted for P2.1; version-2 design before P3 model additions |
| D03 | Adopt [latency/memory budgets](PERFORMANCE.md#phase-2-budgets) as Phase 2 acceptance targets; current overruns remain visible. The [completion decision](PHASE2-REPORT.md#completion-decision) accepts residual latency qualification as PERF-01 with numeric targets unchanged. | Performance/core maintainer | P2.7 complete; PERF-01 review with P3 tables and P8.2 |
| D04 | Project copyright ownership and project license remain **unselected**. `Authors=Textalonia contributors` is descriptive metadata, not legal ownership or a redistribution license. | Project owner / release maintainer | P8.3, before redistribution/public publication |
| D05 | Local `origin` is `https://github.com/kstanki2/Textalonia.git`, confirmed public by GitHub API; `RepositoryUrl` is absent from package metadata. Owner must confirm canonical repository and authority before adding release metadata. | Repository owner | P8.3 |
| D06 | Package ID `Textalonia`, version `0.1.0-preview.1`; public NuGet version endpoint returned 404 on 2026-09-26. Availability/reservation/control remains **unverified**; a missing version listing is not ownership evidence. | Package/release maintainer | P8.3 before reserving/publishing |
| D07 | Freeze exact native OS patches, source-app/IME/reader versions in each execution record; supply unavailable hosts and human operators. | Platform QA leads | Inventory before P1.4 execution; all advertised scope evidence by P6.6/P8.2 |

Release identity was checked on 2026-09-26: local project metadata and `git remote -v` were inspected. Read-only requests to the [NuGet version endpoint](https://api.nuget.org/v3-flatcontainer/textalonia/index.json) returned 404; the [GitHub repository API](https://api.github.com/repos/kstanki2/Textalonia) returned 200 with public repository `kstanki2/Textalonia`, default branch `main`, and no detected license. The dated [identity record](baselines/release-identity.json) preserves these results. This does not prove that a package ID is free or determine legal ownership. D05/D06 remain open; the release maintainer must recheck while authenticated as the intended owner. No license was chosen and nothing was published in Phase 1.

## Phase 5 interchange qualification

Automated codec and clipboard-adapter evidence is recorded in
[Phase 5](PHASE5-REPORT.md). The locally authored corpus records null source
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
