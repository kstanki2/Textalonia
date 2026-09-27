#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ManifestSha256,
    [string]$ReviewFile,
    [switch]$ForPublication
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$root = [IO.Path]::GetFullPath($CandidateDirectory)
$manifest = Join-Path $root 'candidate.json'
if ((Get-ReleaseHash $manifest) -ne $ManifestSha256.ToLowerInvariant()) { throw 'Candidate manifest differs from the reviewed hash.' }
$candidate = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
if ($candidate.schemaVersion -ne 1 -or $candidate.status -ne 'validated' -or $candidate.packageId -ne 'Textalonia' -or
    $candidate.version -notmatch '^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$' -or $candidate.commit -notmatch '^[0-9a-f]{40}$' -or
    $candidate.supportScope -ne 'managed-preview') { throw 'Unrecognized candidate identity/status/scope.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $candidate.files) {
    if (!$seen.Add($file.path) -or [IO.Path]::IsPathRooted($file.path) -or $file.path -match '(^|[/\\])\.\.([/\\]|$)') { throw 'Invalid or duplicate artifact path.' }
    $path = [IO.Path]::GetFullPath((Join-Path $root $file.path))
    if (!$path.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Artifact escaped the candidate directory.'
    }
    if ((Get-ReleaseHash $path) -ne $file.sha256) { throw "Artifact changed: $($file.path)" }
}
foreach ($required in @("packages/Textalonia.$($candidate.version).nupkg", "packages/Textalonia.$($candidate.version).snupkg",
    'package-inspection.json', 'consumer.json', 'sources.json', 'dependency-inventory.json', 'RELEASE-NOTES.md',
    'tools/Textalonia.ReleaseChecks.dll', 'tools/Textalonia.ReleaseChecks.deps.json', 'tools/Textalonia.ReleaseChecks.runtimeconfig.json')) {
    if (!$seen.Contains($required)) { throw "Missing reviewed artifact: $required" }
}
$inspection = Get-Content -Raw -LiteralPath (Join-Path $root 'package-inspection.json') | ConvertFrom-Json
$consumer = Get-Content -Raw -LiteralPath (Join-Path $root 'consumer.json') | ConvertFrom-Json
if ($inspection.status -ne 'pass' -or $inspection.version -ne $candidate.version -or $inspection.commit -ne $candidate.commit -or
    $inspection.packageSha256 -ne (Get-ReleaseHash (Join-Path $root "packages/Textalonia.$($candidate.version).nupkg")) -or
    $inspection.symbolsSha256 -ne (Get-ReleaseHash (Join-Path $root "packages/Textalonia.$($candidate.version).snupkg")) -or
    $consumer.status -ne 'pass' -or $consumer.version -ne $candidate.version -or $consumer.assemblySha256 -ne $inspection.assemblySha256) {
    throw 'Package and consumer evidence do not describe the same candidate.'
}
if ($ForPublication) {
    if ($candidate.workingTreeDirty -or $candidate.fuzzStepsPerSeed -lt 2000 -or !$candidate.performanceExecuted) {
        throw 'Publication requires a clean committed source, extended corpus, and recorded performance run.'
    }
    if (!$ReviewFile) { throw 'Publication requires the completed maintainer review file described in docs/RELEASE.md.' }
    $review = Get-Content -Raw -LiteralPath $ReviewFile | ConvertFrom-Json
    if ($review.manifestSha256 -ne $ManifestSha256 -or [string]::IsNullOrWhiteSpace($review.reviewer) -or
        [string]::IsNullOrWhiteSpace($review.nugetOwner) -or $review.supportScope -ne $candidate.supportScope) {
        throw 'Review identity/scope does not match the candidate.'
    }
    foreach ($gate in @('packageControlConfirmed','copyrightAttributionConfirmed','dependencyNoticesReviewed',
        'releaseNotesReviewed','performanceResultsReviewed','nativeGapsAccepted')) {
        if ($review.$gate -cne $true) { throw "Maintainer gate is unresolved: $gate" }
    }
    $platforms = [Collections.Generic.HashSet[string]]::new()
    foreach ($entry in $review.matrix) {
        & $PSCommandPath -CandidateDirectory $entry.directory -ManifestSha256 $entry.manifestSha256
        $other = Get-Content -Raw -LiteralPath (Join-Path $entry.directory 'candidate.json') | ConvertFrom-Json
        if ($other.commit -ne $candidate.commit -or $other.version -ne $candidate.version -or $other.workingTreeDirty) {
            throw 'OS matrix evidence must describe the same clean commit and version.'
        }
        [void]$platforms.Add($other.platform)
    }
    foreach ($platform in @('windows','linux','macos')) {
        if (!$platforms.Contains($platform)) { throw "Missing OS matrix evidence: $platform" }
    }
}
Write-Host "Candidate integrity verified: $($candidate.version) at $($candidate.commit)"
