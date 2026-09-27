#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$root = [IO.Path]::GetFullPath($CandidateDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Tooling tests require a new scratch directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$verifier = Join-Path $PSScriptRoot 'Test-ReleaseCandidate.ps1'
$manifest = Join-Path $root 'candidate.json'
$originalHash = Get-ReleaseHash $manifest
& $verifier -CandidateDirectory $root -ManifestSha256 $originalHash
$passed = [Collections.Generic.List[string]]::new()
function Expect-Failure {
    param([string]$Name, [scriptblock]$Action, [string]$Message)
    try { & $Action }
    catch {
        if (!$_.Exception.Message.Contains($Message)) { throw "Unexpected failure in $Name -- $_" }
        $passed.Add($Name)
        return
    }
    throw "Expected rejection: $Name"
}
Expect-Failure 'unreviewed manifest' { & $verifier -CandidateDirectory $root -ManifestSha256 ('0' * 64) } 'manifest differs'
$candidate = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
# Copy only reviewed files, not package caches. All mutations are in this new scratch tree.
$copy = Join-Path $output 'bundle'
New-Item -ItemType Directory -Path $copy | Out-Null
foreach ($file in $candidate.files) {
    $destination = Join-Path $copy $file.path
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $file.path) -Destination $destination
}
$copyManifest = Join-Path $copy 'candidate.json'
Copy-Item -LiteralPath $manifest -Destination $copyManifest
$notes = Join-Path $copy 'RELEASE-NOTES.md'
[IO.File]::AppendAllText($notes, 'modified after review')
Expect-Failure 'changed release notes' { & $verifier -CandidateDirectory $copy -ManifestSha256 $originalHash } 'Artifact changed'
Copy-Item -LiteralPath (Join-Path $root 'RELEASE-NOTES.md') -Destination $notes
$candidate.workingTreeDirty = $true
Write-ReleaseJson $copyManifest $candidate
$dirtyHash = Get-ReleaseHash $copyManifest
Expect-Failure 'dirty publication candidate' { & $verifier -CandidateDirectory $copy -ManifestSha256 $dirtyHash -ForPublication } 'clean committed source'
$candidate.files[0].path = '../escaped'
Write-ReleaseJson $copyManifest $candidate
$escapeHash = Get-ReleaseHash $copyManifest
Expect-Failure 'artifact traversal' { & $verifier -CandidateDirectory $copy -ManifestSha256 $escapeHash } 'Invalid or duplicate'
$candidate = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
$candidate.files = @($candidate.files | Where-Object { $_.path -ne 'package-inspection.json' })
Write-ReleaseJson $copyManifest $candidate
$missingHash = Get-ReleaseHash $copyManifest
Expect-Failure 'missing inspection evidence' { & $verifier -CandidateDirectory $copy -ManifestSha256 $missingHash } 'Missing reviewed artifact'
Write-ReleaseJson (Join-Path $output 'tooling-tests.json') @{ status = 'pass'; tests = $passed.ToArray(); count = $passed.Count; candidateManifestSha256 = $originalHash }
Write-Host "Release tooling negative checks passed: $($passed.Count)"
