param(
    [Parameter(Mandatory)][ValidateSet('windows', 'macos', 'linux', 'android', 'ios')][string]$Target,
    [string]$Owner = 'Platform QA maintainer',
    [string]$Output = 'artifacts/native/phase6'
)
$ErrorActionPreference = 'Stop'
$cases = @(
    @{ id = 'N01'; name = 'CJK composition, candidate commit and reconversion where available' },
    @{ id = 'N02'; name = 'Composition cancellation, read-only and focus changes' },
    @{ id = 'N03'; name = 'IME geometry after virtualization, resize and DPI change' },
    @{ id = 'N04'; name = 'Dead keys and grapheme deletion' },
    @{ id = 'N05'; name = 'Bidi visual navigation and table-cell selection' },
    @{ id = 'N06'; name = 'Native clipboard and structural transfers' },
    @{ id = 'N07'; name = 'Screen-reader value, text navigation and selection announcements' },
    @{ id = 'N08'; name = 'Table resize, rectangular formatting and shared input geometry' },
    @{ id = 'N09'; name = 'Native structured drag/drop, modifiers and undo ownership' },
    @{ id = 'N10'; name = 'Stationary edge scrolling and repeated gesture cleanup' }
)
if ($Target -in @('android', 'ios')) {
    $cases += @(
        @{ id = 'M01'; name = 'Tap, long press, caret and range handles, handle crossing' },
        @{ id = 'M02'; name = 'Touch pan arbitration, edge scroll and cancellation' },
        @{ id = 'M03'; name = 'Keyboard viewport, rotation, density and candidate geometry' },
        @{ id = 'M04'; name = 'Context actions, read-only and native clipboard' },
        @{ id = 'M05'; name = 'Embedded-control focus and composition ownership' },
        @{ id = 'M06'; name = 'TalkBack/VoiceOver and external-keyboard alternatives' },
        @{ id = 'M07'; name = 'Repeated gestures and viewport/lifecycle resource stability' }
    )
}
$record = [ordered]@{
    schemaVersion = 2
    phase = 6
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
    target = $Target
    owner = $Owner
    revision = (& git rev-parse HEAD)
    workingTree = 'Record source diff or source archive when qualifying uncommitted changes'
    osBuild = ''
    deviceModel = ''
    deviceKind = ''
    sdkAndToolchain = ''
    backend = 'Avalonia 12.1.3; record native backend/build'
    keyboardAndVersion = ''
    keyboardModes = ''
    sourceApplicationsAndVersions = ''
    screenReaderAndVersion = ''
    displayScaleAndDensity = ''
    fontsAndVersions = ''
    viewportBeforeAndAfterKeyboardDip = ''
    orientation = ''
    results = @($cases | ForEach-Object {
        [ordered]@{
            case = $_.id; name = $_.name; status = 'pending'
            reason = 'No native execution evidence collected; exact device, keyboard, reader and backend inventory required'
            supportScope = 'Unqualified native/mobile integration; managed/headless results do not satisfy this case'
            correctiveTask = 'P6.5/P6.6/P8.2'; evidence = @(); executedUtc = $null
        }
    })
}
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$path = Join-Path $Output ($Target + '-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.json')
if (Test-Path -LiteralPath $path) { throw "Evidence record already exists: $path" }
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
Write-Output $path
