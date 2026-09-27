# Release notes: 0.1.0-preview.1

**Candidate only; not yet published.** This is an independently implemented,
managed-contract preview for evaluating Textalonia, under the MIT license.
It is not a stable, production-parity or native-platform certification claim.
The exact candidate version/commit and package/symbol hashes are in candidate.json
and package-inspection.json in the retained validation bundle. A version override
requires updating these notes before creating the publication candidate.

## Included

Immutable documents and editing sessions; formatting, lists, nested/merged
tables and undo; editor, selectable viewer and replaceable toolbar/input;
inline image/control descriptors and host resource services; diagnosed
text/HTML/RTF/DOCX interchange; native JSON v4 with v1-v3 migration; data XAML;
bounded Markdown codec/viewer and optional host highlighting. There is one shipped
package, Textalonia. No optional integration package or desktop host is required.

Minimum framework is .NET 8. Minimum and pinned tested Avalonia version is 12.1.3;
the declared <13 range is a restore constraint, not certification of every version.
AngleSharp's minimum/pinned tested version is 1.8.2.

## Support and known limits

| Capability / platform | Candidate claim |
| --- | --- |
| Model, session, schemas, codecs, headless editor/viewer and extension contracts | Automated preview contract coverage; exact pass counts and environment are recorded per candidate. |
| Windows, macOS, Linux automated package consumers | Required three-OS CI matrix. A local Windows run alone does not complete this gate. |
| Desktop native IME, clipboard, keyboard/pointer/touch, DPI/themes | Experimental or untested per QUALIFICATION.md; pending cases remain open. |
| Native screen-reader text navigation | Blocked by the public Avalonia text-provider bridge gap; managed ranges do not certify native readers. |
| Android/iOS | Unqualified device targets; harnesses are development assets. |
| Performance | Measured headless workloads only. PERF-01 remains open; inspect numeric misses, host load and native latency limits. |
| Office/browser interchange | Documented subsets with loss reports. Application-export corpus and native bidirectional tests remain incomplete. |
| Markdown / XAML | Explicit bounded dialect / data vocabulary; no CommonMark/GFM, executable XAML or arbitrary document fidelity claim. |

Full evidence and deferred cases remain in [qualification](QUALIFICATION.md),
[performance](PERFORMANCE.md), [roadmap](ROADMAP.md) and [retained benchmark summaries](BENCHMARK-BASELINES.md).
No unresolved case is converted into a pass by packing the library.

## Adoption and upgrade

Add the exact preview package version, use the README's Avalonia theme registration,
and start with TextaloniaEditor or TextaloniaViewer. MarkdownViewer and all codecs
are included in the same package. Use the independent PackageSmoke program as a
compiling example for extension services and input replacement.

Read [API contracts](API-CONTRACTS.md) before attaching mutable sessions/services
to controls. Older native files load without modifying their source; save a new
file to migrate to schema v4. New files require a reader supporting v4.
Preserve originals and use strict conversion reports when export loss matters.

Preview versions may add APIs or deliberately change behavior only with release
notes, migration examples and refreshed reviewed baselines. Published versions
are immutable; a correction uses a new preview version. Stable 1.0 requires a
separate support/API decision and completed qualification for every advertised
claim. See [release procedure](RELEASE.md).

Source Link and public checksum retrieval cover C# source. Avalonia 12.1.3 emits
an absolute unmapped XAML sequence-point path for Generic.axaml; the exact theme
source is included as sources/Themes/Generic.axaml in the package instead.
Automatic debugger Source Link for that XAML file is not claimed. The package
inspector records this bounded exception; other unmapped sources fail inspection.
Bit-for-bit equality across different checkout paths/OS hosts is not claimed for
Avalonia-generated debug metadata; publication always uses the exact reviewed
artifacts, with source/SDK/dependency identity retained for reproduction.
