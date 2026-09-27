#requires -Version 7.0
param(
    [string]$Version,
    [string]$OutputDirectory,
    [switch]$LongCorpus,
    [switch]$Performance,
    [ValidateRange(3, 200)][int]$Repetitions = 15
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$repo = Split-Path $PSScriptRoot -Parent
if (!$Version) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props')
    $Version = [string]$props.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+-[0-9A-Za-z.-]+$') { throw 'This release route currently qualifies preview versions only.' }
$notes = Get-Content -Raw -LiteralPath (Join-Path $repo 'docs/RELEASE-NOTES.md')
if (!$notes.Contains("# Release notes: $Version")) { throw 'Release notes must identify the selected version before validation.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo ("artifacts/release/$Version/" + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Candidate output must be a new directory; reviewed artifacts are never overwritten.' }
New-Item -ItemType Directory -Path $output | Out-Null
$logs = Join-Path $output 'logs'
$packages = Join-Path $output 'packages'
New-Item -ItemType Directory -Path $logs, $packages | Out-Null
$saved = @{}
foreach ($key in @('AVALONIA_TELEMETRY_OPTOUT','TEXTALONIA_FUZZ_STEPS','TEXTALONIA_FAILURE_DIR','TEXTALONIA_REPLAY','TEXTALONIA_REVISION','TEXTALONIA_SDK')) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
}
Push-Location $repo
try {
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not read source revision.' }
    $dirty = [bool](& git status --porcelain)
    $sources = Get-ReleaseSources $repo
    Write-ReleaseJson (Join-Path $output 'sources.json') $sources
    & dotnet --info | Set-Content -LiteralPath (Join-Path $output 'dotnet-info.txt')
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:TEXTALONIA_FUZZ_STEPS = if ($LongCorpus) { '2000' } else { '120' }
    $env:TEXTALONIA_FAILURE_DIR = Join-Path $output 'fixture-failures'
    $env:TEXTALONIA_REPLAY = $null
    $env:TEXTALONIA_REVISION = "$revision (package $Version; dirty=$dirty)"
    $env:TEXTALONIA_SDK = (& dotnet --version).Trim()
    $properties = @("-p:Version=$Version", '-p:ContinuousIntegrationBuild=true')
    Invoke-ReleaseDotNet (Join-Path $logs 'restore.log') (@('restore', 'Textalonia.sln', '--configfile', 'NuGet.Config') + $properties)
    Invoke-ReleaseDotNet (Join-Path $logs 'build.log') (@('build', 'Textalonia.sln', '-c', 'Release', '--no-restore') + $properties)
    Invoke-ReleaseDotNet (Join-Path $logs 'tests.log') (@('test', 'tests/Textalonia.Tests', '-c', 'Release', '--no-build',
        '--logger', 'trx', '--results-directory', (Join-Path $output 'test-results')) + $properties)
    Invoke-ReleaseDotNet (Join-Path $logs 'pack.log') (@('pack', 'src/Textalonia', '-c', 'Release', '--no-build', '-o', $packages) + $properties)
    $package = Join-Path $packages "Textalonia.$Version.nupkg"
    $symbols = Join-Path $packages "Textalonia.$Version.snupkg"
    $inspection = Join-Path $output 'package-inspection.json'
    Invoke-ReleaseDotNet (Join-Path $logs 'inspection.log') @('run', '--project', 'tools/Textalonia.ReleaseChecks', '-c', 'Release',
        '--no-build', '--', $package, $symbols, $Version, $revision, $inspection)
    & (Join-Path $PSScriptRoot 'Test-PackageConsumer.ps1') -Version $Version -PackageDirectory $packages -OutputDirectory (Join-Path $output 'consumer')
    $check = Get-Content -Raw -LiteralPath $inspection | ConvertFrom-Json
    $consumer = Get-Content -Raw -LiteralPath (Join-Path $output 'consumer/consumer.json') | ConvertFrom-Json
    if ($consumer.assemblySha256 -ne $check.assemblySha256) { throw 'Consumer did not execute the inspected package.' }

    $assets = Get-Content -Raw -LiteralPath 'src/Textalonia/obj/project.assets.json' | ConvertFrom-Json -AsHashtable
    $cache = @($assets.packageFolders.Keys)[0]
    $inventory = @($assets.libraries.GetEnumerator() | Sort-Object Key | ForEach-Object {
        $id = $_.Key.Split('/')[0]
        $nuspec = Join-Path $cache ($_.Value.path + '/' + $id.ToLowerInvariant() + '.nuspec')
        [xml]$spec = Get-Content -LiteralPath $nuspec
        if ($spec.package.metadata.license.InnerText -ne 'MIT') { throw "Unreviewed dependency license: $id" }
        [ordered]@{ package = $_.Key; license = $spec.package.metadata.license.InnerText; nuspecSha256 = Get-ReleaseHash $nuspec }
    })
    Write-ReleaseJson (Join-Path $output 'dependency-inventory.json') $inventory
    New-Item -ItemType Directory -Path (Join-Path $output 'qualification') | Out-Null
    Write-ReleaseJson (Join-Path $output 'qualification/release-status.json') ([ordered]@{
        version = $Version; commit = $revision; workingTreeDirty = $dirty; status = 'pending'
        targets = @('windows','macos','linux','android','ios')
        cases = @('native IME','clipboard/application corpus','keyboard/pointer/touch','screen readers','theme/DPI','mobile keyboards')
        reason = 'No candidate native execution claimed. Prior records are retained for continuity; execute the documented native procedures.'
    })
    Copy-Item -LiteralPath 'docs/baselines/native' -Destination (Join-Path $output 'qualification/native') -Recurse
    Copy-Item -LiteralPath 'docs/QUALIFICATION.md','docs/PERFORMANCE.md','docs/RELEASE-NOTES.md','docs/API-CONTRACTS.md','docs/RELEASE.md',
        'tests/Textalonia.Tests/Fixtures/public-api.txt','tests/Textalonia.Tests/Fixtures/public-api-contracts.txt' -Destination $output
    Invoke-ReleaseDotNet (Join-Path $logs 'fixtures.log') @('run', '--project', 'benchmarks/Textalonia.Benchmarks', '-c', 'Release',
        '--no-build', '--', '--export-fixtures', (Join-Path $output 'qualification/fixtures'))
    if ($Performance) {
        foreach ($mode in @('compatibility', 'document')) {
            $destination = Join-Path $output "performance/$mode"
            Invoke-ReleaseDotNet (Join-Path $logs "performance-$mode.log") @('run', '--project', 'benchmarks/Textalonia.Benchmarks',
                '-c', 'Release', '--no-build', '--', '--text-mode', $mode, '--warmups', '3', '--repetitions', "$Repetitions", '--output', $destination)
            & (Join-Path $PSScriptRoot 'Compare-BaselineBudgets.ps1') -Results $destination |
                Tee-Object -FilePath (Join-Path $logs "budgets-$mode.log") | Write-Host
        }
        foreach ($probe in @('interaction', 'markdown')) {
            Invoke-ReleaseDotNet (Join-Path $logs "$probe.log") @('run', '--project', 'benchmarks/Textalonia.Benchmarks',
                '-c', 'Release', '--no-build', '--', "--$probe-probe", (Join-Path $output "performance/$probe"))
        }
    }
    $finalSources = Get-ReleaseSources $repo
    if (($sources | ConvertTo-Json -Depth 5 -Compress) -cne ($finalSources | ConvertTo-Json -Depth 5 -Compress)) {
        throw 'Source changed during validation. Run a fresh candidate after completing edits.'
    }
    # Consumers/caches are intentionally excluded from the review bundle; retain their receipts/logs.
    Copy-Item -LiteralPath (Join-Path $output 'consumer/consumer.json') -Destination (Join-Path $output 'consumer.json')
    Copy-Item -LiteralPath (Join-Path $output 'consumer/restore.log') -Destination (Join-Path $logs 'consumer-restore.log')
    Copy-Item -LiteralPath (Join-Path $output 'consumer/consumer.log') -Destination (Join-Path $logs 'consumer.log')
    New-Item -ItemType Directory -Path (Join-Path $output 'tools') | Out-Null
    Get-ChildItem -LiteralPath 'tools/Textalonia.ReleaseChecks/bin/Release/net8.0' -File |
        Where-Object { $_.Extension -in '.dll', '.json' } | Copy-Item -Destination (Join-Path $output 'tools')
    New-Item -ItemType Directory -Path (Join-Path $output 'examples') | Out-Null
    Get-ChildItem -LiteralPath 'tests/Textalonia.PackageSmoke' -File |
        Where-Object { $_.Extension -in '.cs', '.axaml', '.csproj' } | Copy-Item -Destination (Join-Path $output 'examples')
    $platform = if ($IsWindows) { 'windows' } elseif ($IsMacOS) { 'macos' } else { 'linux' }
    $files = @(Get-ChildItem -LiteralPath $output -File -Recurse |
        Where-Object { !$_.FullName.StartsWith((Join-Path $output 'consumer') + [IO.Path]::DirectorySeparatorChar) } |
        Sort-Object FullName | ForEach-Object {
            [ordered]@{ path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\','/'); sha256 = Get-ReleaseHash $_.FullName }
        })
    Write-ReleaseJson (Join-Path $output 'candidate.json') ([ordered]@{
        schemaVersion = 1; status = 'validated'; createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
        packageId = 'Textalonia'; version = $Version; commit = $revision; workingTreeDirty = $dirty
        platform = $platform; os = [Runtime.InteropServices.RuntimeInformation]::OSDescription; sdk = $env:TEXTALONIA_SDK
        fuzzStepsPerSeed = [int]$env:TEXTALONIA_FUZZ_STEPS; performanceExecuted = [bool]$Performance
        supportScope = 'managed-preview'; nativeQualification = 'pending'; packageControl = 'unverified'; published = $false
        files = $files
    })
    Write-Host "Validated candidate: $output"
    Write-Host 'Publication remains a separate maintainer action; inspect qualification gaps and approve exact hashes.'
}
catch {
    Write-ReleaseJson (Join-Path $output 'failure.json') @{ status = 'failed'; error = $_.Exception.Message }
    throw
}
finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    Pop-Location
}
