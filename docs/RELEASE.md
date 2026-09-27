# Release procedure

Phase 8 prepares reviewable artifacts; publishing is a later explicit action.
Ordinary push/PR CI has no publication credentials or publishing step.
The current identity is Textalonia / 0.1.0-preview.1, MIT, attributed to
Textalonia contributors, with repository/project URL
https://github.com/kstanki2/Textalonia. MIT was selected by the maintainer on
2026-09-27. Contributor attribution preserves the existing Authors metadata;
confirm copyright authority and authenticated NuGet ownership before publication.
A public package search or HTTP 404 does not establish package control.

## Build a candidate

Use a clean checkout of the intended public commit, .NET SDK 8+ and PowerShell 7+.
The committed .gitattributes enforces LF for C#/XAML so PDB source checksums match GitHub bytes on Windows too.
Set the version in Directory.Build.props and RELEASE-NOTES.md together. Keep
preview versions distinct; never reuse an already published version. The local
default version is retained until a maintainer selects the next public version.

~~~powershell
pwsh -File scripts/Invoke-ReleaseCandidate.ps1 -LongCorpus -Performance -OutputDirectory artifacts/release-review
$hash = (Get-FileHash artifacts/release-review/candidate.json -Algorithm SHA256).Hash
pwsh -File scripts/Test-ReleaseCandidate.ps1 -CandidateDirectory artifacts/release-review -ManifestSha256 $hash
~~~

The script restores/builds/tests/packs, executes a copied consumer with its own
build properties, obj/bin and empty package cache, pins the minimum supported
dependencies, and maps Textalonia exclusively to the freshly built feed.
It verifies package metadata/docs/licenses, portable PDB identity/checksum and
Source Link commit, and compares the executed consumer assembly to the package.
The SDK also performs package API validation. The extended route runs 2,000
operations per fuzz seed, both text-binding performance modes (15 repetitions by
default), and interaction/Markdown probes. Budget misses are recorded, not hidden
or treated as certified latency. Review them against PERF-01.

candidate.json lists SHA-256 hashes for every retained artifact, logs, tests,
source inventory, dependency inventory, qualification records, examples and
the validation tool. Its own SHA-256 is the independent review identity. Keep that
hash outside the bundle in the release approval/CI summary. Output directories
must be new; scripts never overwrite a reviewed candidate. Source changes during
the run fail validation. A dirty working tree can generate development evidence
but cannot be published by the release script.

Run the manual build-test-package workflow with release_workloads=true at the
same commit/version to collect Windows, macOS and Linux artifacts. Ordinary CI
uses the same route with bounded workloads. Download and retain the three bundles
before their 30-day CI expiry. Matrix execution is a gate, not a promise inferred
from workflow YAML. Choose one exact bundle for publication; do not rebuild it on
the publishing machine.

## Review qualification and ownership

Use [release notes](RELEASE-NOTES.md) and [API contracts](API-CONTRACTS.md).
Review every package entry and dependency license inventory, including changes to
transitives. No optional integration package ships. Native pending records are
copied for continuity and a candidate-specific release-status.json records missing
execution; copying an old record never qualifies a new candidate.

Execute N01-N10 and M01-M07 where applicable using NATIVE-BASELINES.md,
INTERACTIONS.md and MOBILE-QUALIFICATION.md. Record exact OS, application,
IME/reader, device, theme/DPI, source commit and package version. Retain failures,
reproducers and deferred targets in QUALIFICATION.md/ROADMAP.md. A reduced
managed preview may explicitly exclude these claims; native parity remains open.

While authenticated as the intended NuGet owner, confirm control/reservation of
Textalonia and scope an API key to this package. Confirm contributor attribution,
repository authority and dependency redistribution terms. Fill a review JSON
outside the immutable bundle; relative matrix directory paths are resolved from
the invocation working directory:

~~~json
{
  "manifestSha256": "COPY_THE_REVIEWED_CANDIDATE_SHA256",
  "reviewer": "RELEASE_MAINTAINER",
  "nugetOwner": "AUTHENTICATED_PACKAGE_OWNER",
  "supportScope": "managed-preview",
  "packageControlConfirmed": false,
  "copyrightAttributionConfirmed": false,
  "dependencyNoticesReviewed": false,
  "releaseNotesReviewed": false,
  "performanceResultsReviewed": false,
  "nativeGapsAccepted": false,
  "matrix": [
    { "directory": "downloaded/windows", "manifestSha256": "WINDOWS_SHA256" },
    { "directory": "downloaded/linux", "manifestSha256": "LINUX_SHA256" },
    { "directory": "downloaded/macos", "manifestSha256": "MACOS_SHA256" }
  ]
}
~~~

Set the booleans only after review; they are attestations, not automatic
measurements. The publication verifier checks every referenced bundle's hashes,
same clean commit/version and three distinct OS platforms, and requires extended
corpus/performance evidence from the chosen bundle. It only permits the declared
managed-preview scope. Stable distribution needs a new reviewed support policy.

## Publish the reviewed artifacts

During an explicitly authorized release action, set TEXTALONIA_NUGET_API_KEY in
the process environment. Do not put credentials in review files or source control.

~~~powershell
# Without -Publish this performs integrity validation only.
pwsh -File scripts/Publish-Release.ps1 -CandidateDirectory downloaded/windows -ManifestSha256 $hash
# Explicit publication; verifies review gates and retrieves/checks public source first.
pwsh -File scripts/Publish-Release.ps1 -CandidateDirectory downloaded/windows -ManifestSha256 $hash -ReviewFile review.json -Publish
~~~

The script runs the bundled inspector, retrieves each mapped source file at the
exact commit and verifies its PDB checksum. It then pushes the one reviewed nupkg
and adjacent snupkg through NuGet. No build, repack, version rewriting or wildcard
upload occurs. Source/symbol failures block success. A partial upload is possible:
inspect the feed and symbol processing state before retrying; never rebuild or
silently skip an existing version. For a symbols-only retry, explicitly push the
same reviewed snupkg after comparing its recorded hash.

Publish versioned release notes on the chosen repository release page, tagged
v<version> at candidate.commit. Attach the exact nupkg/snupkg, candidate manifest,
review hash and qualification receipts. The GitHub release must stay marked
prerelease for a preview. Creating that tag/release is part of the explicit release
action, not CI validation.

## Verify public installation and debugging

Install Microsoft's dotnet-symbol tool into a separate tool directory, recording
the tool version (the procedure was authored against 9.0.661903). After package
and symbol indexing completes:

~~~powershell
dotnet tool install dotnet-symbol --tool-path .tools/symbols --version 9.0.661903
pwsh -File scripts/Test-PublishedRelease.ps1 -CandidateDirectory downloaded/windows -ManifestSha256 $hash -OutputDirectory artifacts/public-verification -SymbolTool .tools/symbols/dotnet-symbol
~~~

This uses a new consumer and empty cache with **only nuget.org**, runs the bundled
examples, compares published package entries to reviewed bytes (allowing NuGet's
repository signature), downloads the public PDB into a new symbol cache, compares
it to the reviewed PDB and checks Source Link against public source checksums.
Failures/404s remain failures; after indexing, retry in a new output directory.
See [NuGet symbols](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg)
and [dotnet-symbol](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-symbol).

Archive publication.json and its logs with versioned release notes. Update
README/ROADMAP and the release identity record with the actual public URL, source,
artifact hashes and support scope only after these checks pass. P8.6 stays open
until both public installation/debugging and versioned notes are verified.

## Correction / unlisting

If installation, symbols or behavior is broken, stop recommending the version and
record the failure on its release page. The authenticated owner may unlist it in
NuGet's Manage Package UI, documenting the reason and replacement version.
Unlisting does not erase existing installs or prevent exact-version restores.
Keep the original artifacts/evidence. Publish a corrected **new** preview version
through the same qualification procedure, then verify it from the public feed.
Never overwrite a published package or retag different source under its version.

Source Link and public checksum retrieval cover C# source. Avalonia 12.1.3 emits
an absolute unmapped XAML sequence-point path for Generic.axaml; the exact theme
source is included as sources/Themes/Generic.axaml in the package instead.
Automatic debugger Source Link for that XAML file is not claimed. The package
inspector records this bounded exception; other unmapped sources fail inspection.
Bit-for-bit equality across different checkout paths/OS hosts is not claimed for
Avalonia-generated debug metadata; publication always uses the exact reviewed
artifacts, with source/SDK/dependency identity retained for reproduction.