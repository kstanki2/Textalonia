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
    # Line-based native output C-quotes non-ASCII names and cannot preserve paths
    # containing newlines. Read Git's NUL-delimited UTF-8 output directly instead.
    $startInfo = [Diagnostics.ProcessStartInfo]::new('git')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @('-C', $Repository, 'ls-files', '-z', '--cached', '--others', '--exclude-standard')) {
        $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (!$process.Start()) { throw 'Could not start source inventory.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Could not inventory source files: $errorOutput" }
        $files = $output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
    }
    finally { $process.Dispose() }
    $paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $files) { [void]$paths.Add($file) }
    @($paths | ForEach-Object {
        $path = Join-Path $Repository $_
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [ordered]@{ path = $_; sha256 = Get-ReleaseHash $path }
        }
    })
}
