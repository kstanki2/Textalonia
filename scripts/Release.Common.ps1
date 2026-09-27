# Shared release helpers. Commands are argument arrays, never evaluated shell strings.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Invoke-ReleaseDotNet {
    param([string]$Log, [string[]]$Arguments)
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath $Log | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed ($LASTEXITCODE). See $Log" }
}
function Write-ReleaseJson {
    param([string]$Path, $Value)
    $Value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}
function Get-ReleaseHash {
    param([string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Get-ReleaseSources {
    param([string]$Repository)
    $files = @(& git -C $Repository ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inventory source files.' }
    @($files | Sort-Object -Unique | ForEach-Object {
        $path = Join-Path $Repository $_
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [ordered]@{ path = $_; sha256 = Get-ReleaseHash $path }
        }
    })
}
