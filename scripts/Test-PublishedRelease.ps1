#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$ManifestSha256,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$SymbolTool = 'dotnet-symbol'
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
& (Join-Path $PSScriptRoot 'Test-ReleaseCandidate.ps1') -CandidateDirectory $CandidateDirectory -ManifestSha256 $ManifestSha256
$root = [IO.Path]::GetFullPath($CandidateDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Public verification needs a new output directory.' }
$candidate = Get-Content -Raw -LiteralPath (Join-Path $root 'candidate.json') | ConvertFrom-Json
New-Item -ItemType Directory -Path $output | Out-Null
& (Join-Path $PSScriptRoot 'Test-PackageConsumer.ps1') -Version $candidate.version -PublicFeed -ConsumerSource (Join-Path $root 'examples') -OutputDirectory (Join-Path $output 'consumer')
$publicPackage = Join-Path $output "consumer/cache/textalonia/$($candidate.version)/textalonia.$($candidate.version).nupkg"
$reviewedPackage = Join-Path $root "packages/Textalonia.$($candidate.version).nupkg"
$reviewedSymbols = Join-Path $root "packages/Textalonia.$($candidate.version).snupkg"
# Repository signing can change the outer nupkg hash. Compare original entry bytes.
Add-Type -AssemblyName System.IO.Compression
$expected = [IO.Compression.ZipFile]::OpenRead($reviewedPackage)
$actual = [IO.Compression.ZipFile]::OpenRead($publicPackage)
try {
    foreach ($entry in $expected.Entries) {
        if ($entry.FullName -eq '.signature.p7s') { continue }
        $other = $actual.GetEntry($entry.FullName)
        if (!$other) { throw "Public package lacks $($entry.FullName)" }
        $left = $entry.Open(); $right = $other.Open()
        try {
            $hash = [Security.Cryptography.SHA256]::Create()
            try {
                $a = [Convert]::ToHexString($hash.ComputeHash($left))
                $b = [Convert]::ToHexString($hash.ComputeHash($right))
                if ($a -ne $b) { throw "Public package entry differs: $($entry.FullName)" }
            } finally { $hash.Dispose() }
        } finally { $left.Dispose(); $right.Dispose() }
    }
    foreach ($entry in $actual.Entries) {
        if ($entry.FullName -ne '.signature.p7s' -and !$expected.GetEntry($entry.FullName)) { throw 'Unexpected public package content.' }
    }
} finally { $expected.Dispose(); $actual.Dispose() }
$symbolOutput = Join-Path $output 'public-symbols'
New-Item -ItemType Directory -Path $symbolOutput | Out-Null
$assembly = Join-Path $output 'consumer/project/bin/Release/net8.0/Textalonia.dll'
& $SymbolTool --symbols --server-path https://symbols.nuget.org/download/symbols --cache-directory (Join-Path $output 'symbol-cache') --output $symbolOutput $assembly 2>&1 |
    Tee-Object -FilePath (Join-Path $output 'symbols.log') | Write-Host
if ($LASTEXITCODE -ne 0) { throw 'Public symbol download failed.' }
$pdb = Join-Path $symbolOutput 'Textalonia.pdb'
if (!(Test-Path -LiteralPath $pdb)) { throw 'Public PDB is unavailable. Wait for indexing, then use a fresh verification directory.' }
$zip = [IO.Compression.ZipFile]::OpenRead($reviewedSymbols)
try {
    $stream = $zip.GetEntry('lib/net8.0/Textalonia.pdb').Open()
    try {
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $expectedPdb = [Convert]::ToHexString($hash.ComputeHash($stream)).ToLowerInvariant() }
        finally { $hash.Dispose() }
    } finally { $stream.Dispose() }
} finally { $zip.Dispose() }
if ((Get-ReleaseHash $pdb) -ne $expectedPdb) { throw 'Public symbols do not match reviewed symbols.' }
Invoke-ReleaseDotNet (Join-Path $output 'sources.log') @((Join-Path $root 'tools/Textalonia.ReleaseChecks.dll'),
    $publicPackage, $reviewedSymbols, $candidate.version, $candidate.commit, (Join-Path $output 'inspection.json'), '--fetch-sources')
Write-ReleaseJson (Join-Path $output 'publication.json') ([ordered]@{
    status = 'verified'; version = $candidate.version; commit = $candidate.commit; candidateManifestSha256 = $ManifestSha256
    verifiedUtc = [DateTimeOffset]::UtcNow.ToString('o'); publicFeedOnly = $true
    packageUrl = "https://www.nuget.org/packages/Textalonia/$($candidate.version)"
    publicPackageSha256 = Get-ReleaseHash $publicPackage; publicPdbSha256 = Get-ReleaseHash $pdb
    sourceAndSymbols = 'verified'; examples = 'pass'
})
Write-Host 'Public installation/source/symbol checks passed. Archive the receipt and publish versioned notes before closing P8.6.'
