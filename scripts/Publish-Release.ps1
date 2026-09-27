#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$ManifestSha256,
    [string]$ReviewFile,
    [switch]$Publish
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
& (Join-Path $PSScriptRoot 'Test-ReleaseCandidate.ps1') -CandidateDirectory $CandidateDirectory -ManifestSha256 $ManifestSha256 -ReviewFile $ReviewFile -ForPublication:$Publish
if (!$Publish) { Write-Host 'Validation only. Pass -Publish during the explicit release action.'; return }
if ([string]::IsNullOrWhiteSpace($env:TEXTALONIA_NUGET_API_KEY)) { throw 'Set TEXTALONIA_NUGET_API_KEY for the approved NuGet owner.' }
$root = [IO.Path]::GetFullPath($CandidateDirectory)
$candidate = Get-Content -Raw -LiteralPath (Join-Path $root 'candidate.json') | ConvertFrom-Json
$package = Join-Path $root "packages/Textalonia.$($candidate.version).nupkg"
$symbols = Join-Path $root "packages/Textalonia.$($candidate.version).snupkg"
# All mapped source checksums must resolve at the exact public commit before upload.
$sourceReceipt = Join-Path ([IO.Path]::GetTempPath()) ("textalonia-source-" + [Guid]::NewGuid().ToString('N') + '.json')
& dotnet (Join-Path $root 'tools/Textalonia.ReleaseChecks.dll') $package $symbols $candidate.version $candidate.commit $sourceReceipt --fetch-sources
if ($LASTEXITCODE -ne 0) { throw 'Public Source Link verification failed; nothing was published.' }
# No build/pack and no wildcard. NuGet also uploads the adjacent reviewed .snupkg.
# Do not echo the command arguments or write the API key into a receipt.
& dotnet nuget push $package --source https://api.nuget.org/v3/index.json --api-key $env:TEXTALONIA_NUGET_API_KEY
if ($LASTEXITCODE -ne 0) { throw 'NuGet push failed or partially completed. Inspect feed state before retrying; do not rebuild this version.' }
Write-Host 'Upload finished. P8.6 remains open until clean public installation, public symbols, and versioned release notes are verified.'
