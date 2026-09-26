param(
    [Parameter(Mandatory)][ValidateSet('windows', 'macos', 'linux', 'android', 'ios')][string]$Target,
    [string]$Owner = 'Desktop QA maintainer',
    [string]$Output = 'artifacts/native'
)
$ErrorActionPreference = 'Stop'
# Creates an evidence record only. A human operator executes N01-N07 in NATIVE-BASELINES.md.
$cases = @(
    @{ id = 'N01'; name = 'CJK commit'; task = 'P4.2/P6.6' },
    @{ id = 'N02'; name = 'Composition cancellation'; task = 'P4.2/P6.6' },
    @{ id = 'N03'; name = 'Candidate position after scroll'; task = 'P2.4/P6.6' },
    @{ id = 'N04'; name = 'Dead keys'; task = 'P6.6' },
    @{ id = 'N05'; name = 'Mixed bidi navigation'; task = 'P6.1' },
    @{ id = 'N06'; name = 'Rich clipboard'; task = 'P5.5/P5.6' },
    @{ id = 'N07'; name = 'Screen-reader value and selection'; task = 'P4.6' }
)
$record = [ordered]@{
    schemaVersion = 1
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
    target = $Target
    owner = $Owner
    revision = (& git rev-parse HEAD)
    osBuild = ''
    backend = 'Avalonia 12.1.3; fill native backend and build'
    imeAndVersion = ''
    sourceApplications = ''
    screenReaderAndVersion = ''
    font = 'Inter 16 DIP; record fallback fonts'
    dpi = '96 and 144 on Windows; record native scaling on other targets'
    theme = 'Light and Dark'
    window = '1000 x 700 DIP; repeat candidate geometry at 700 x 500'
    results = @($cases | ForEach-Object {
        [ordered]@{ case = $_.id; name = $_.name; status = 'pending'; reason = 'Awaiting native operator and exact environment inventory'; evidence = @(); correctiveTask = $_.task; executedUtc = $null }
    })
}
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$path = Join-Path $Output ($Target + '-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.json')
if (Test-Path -LiteralPath $path) { throw "Evidence record already exists: $path" }
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
Write-Output $path
