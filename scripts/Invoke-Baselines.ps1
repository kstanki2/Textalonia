param(
    [switch]$LongCorpus,
    [switch]$Performance,
    [ValidateRange(1, 100000)][int]$FuzzSteps = 2000,
    [ValidateRange(3, 200)][int]$Repetitions = 15
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Push-Location $repo
$savedSteps = $env:TEXTALONIA_FUZZ_STEPS
$savedReplay = $env:TEXTALONIA_REPLAY
$savedFailures = $env:TEXTALONIA_FAILURE_DIR
$savedRevision = $env:TEXTALONIA_REVISION
$savedSdk = $env:TEXTALONIA_SDK
try {
    $env:TEXTALONIA_REPLAY = $null
    $env:TEXTALONIA_FAILURE_DIR = Join-Path $repo 'artifacts/fixture-failures'
    if ($LongCorpus) { $env:TEXTALONIA_FUZZ_STEPS = "$FuzzSteps" }
    else { $env:TEXTALONIA_FUZZ_STEPS = '120' }
    Invoke-DotNet restore Textalonia.sln --configfile NuGet.Config
    Invoke-DotNet build Textalonia.sln -c Release --no-restore
    Invoke-DotNet test tests/Textalonia.Tests -c Release --no-build --logger trx --results-directory artifacts/test-results
    Invoke-DotNet pack src/Textalonia -c Release --no-build -o artifacts/packages
    # A fresh package cache prevents a previous package with the same preview version winning.
    $consumerCache = Join-Path $repo ('artifacts/consumer-cache/' + [Guid]::NewGuid().ToString('N'))
    Invoke-DotNet restore tests/Textalonia.PackageSmoke --configfile tests/Textalonia.PackageSmoke/NuGet.Config --packages $consumerCache --force
    Invoke-DotNet run --project tests/Textalonia.PackageSmoke -c Release --no-restore
    Invoke-DotNet run --project benchmarks/Textalonia.Benchmarks -c Release --no-build -- --export-fixtures artifacts/native/fixtures
    if ($Performance) {
        $env:TEXTALONIA_REVISION = (& git rev-parse HEAD)
        if (& git status --porcelain) { $env:TEXTALONIA_REVISION += ' (working tree modified)' }
        $env:TEXTALONIA_SDK = (& dotnet --version)
        Invoke-DotNet run --project benchmarks/Textalonia.Benchmarks -c Release --no-build -- --warmups 3 --repetitions $Repetitions --output artifacts/benchmarks/latest
        & (Join-Path $PSScriptRoot 'Compare-BaselineBudgets.ps1') -Results artifacts/benchmarks/latest
    }
}
finally {
    $env:TEXTALONIA_FUZZ_STEPS = $savedSteps
    $env:TEXTALONIA_REPLAY = $savedReplay
    $env:TEXTALONIA_FAILURE_DIR = $savedFailures
    $env:TEXTALONIA_REVISION = $savedRevision
    $env:TEXTALONIA_SDK = $savedSdk
    Pop-Location
}
