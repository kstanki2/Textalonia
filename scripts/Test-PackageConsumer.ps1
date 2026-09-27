#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$PackageDirectory,
    [string]$ConsumerSource,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$PublicFeed
)
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Invalid version.' }
if (!$PublicFeed -and !(Test-Path -LiteralPath (Join-Path $PackageDirectory "Textalonia.$Version.nupkg"))) {
    throw 'The exact candidate package is required.'
}
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Consumer output must be a new directory (fresh project, cache and obj).' }
New-Item -ItemType Directory -Path $output | Out-Null
$project = Join-Path $output 'project'
New-Item -ItemType Directory -Path $project | Out-Null
# These stop inheritance from any parent checkout. No project references, inherited build
# properties, stale obj/bin, shared global package cache, or fallback Textalonia source.
'<Project />' | Set-Content -LiteralPath (Join-Path $output 'Directory.Build.props')
'<Project />' | Set-Content -LiteralPath (Join-Path $output 'Directory.Build.targets')
if (!$ConsumerSource) { $ConsumerSource = Join-Path $repo 'tests/Textalonia.PackageSmoke' }
Get-ChildItem -LiteralPath $ConsumerSource -File |
    Where-Object { $_.Extension -in '.cs', '.axaml', '.csproj' } |
    Copy-Item -Destination $project
$localSource = ''
$localMapping = ''
if (!$PublicFeed) {
    $feed = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($PackageDirectory))
    $localSource = '<add key="candidate" value="{0}" />' -f $feed
    $localMapping = '<packageSource key="candidate"><package pattern="Textalonia" /></packageSource>'
}
@"
<configuration>
  <packageSources><clear />$localSource<add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <packageSourceMapping><clear />$localMapping<packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $output 'NuGet.Config')
$cache = Join-Path $output 'cache'
$csproj = Join-Path $project 'Textalonia.PackageSmoke.csproj'
$properties = @("-p:TextaloniaPackageVersion=$Version", '-p:AvaloniaVersion=12.1.3')
Invoke-ReleaseDotNet (Join-Path $output 'restore.log') (@('restore', $csproj, '--configfile', (Join-Path $output 'NuGet.Config'),
    '--packages', $cache, '--no-http-cache', '--force') + $properties)
Invoke-ReleaseDotNet (Join-Path $output 'consumer.log') (@('run', '--project', $csproj, '-c', 'Release', '--no-restore') + $properties)
$assets = Get-Content -Raw -LiteralPath (Join-Path $project 'obj/project.assets.json') | ConvertFrom-Json -AsHashtable
if (@($assets.libraries.Values | Where-Object { $_.type -eq 'project' }).Count -ne 0) { throw 'Consumer has a project dependency.' }
if (@($assets.libraries.Keys | Where-Object { $_ -match '^Avalonia\.(Desktop|Win32|X11|Native)/' }).Count -ne 0) { throw 'Consumer pulled in a desktop host.' }
if (!$assets.libraries.ContainsKey("Textalonia/$Version")) { throw 'Unexpected resolved Textalonia version.' }
foreach ($dependency in @('Avalonia/12.1.3', 'AngleSharp/1.8.2')) {
    if (!$assets.libraries.ContainsKey($dependency)) { throw "Minimum/pinned dependency not tested: $dependency" }
}
$installedAssembly = Join-Path $cache "textalonia/$Version/lib/net8.0/Textalonia.dll"
$loadedAssembly = Join-Path $project 'bin/Release/net8.0/Textalonia.dll'
if ((Get-ReleaseHash $installedAssembly) -ne (Get-ReleaseHash $loadedAssembly)) { throw 'Consumer output is not the installed assembly.' }
Write-ReleaseJson (Join-Path $output 'consumer.json') ([ordered]@{
    status = 'pass'; version = $Version; publicFeedOnly = [bool]$PublicFeed
    isolatedCache = $true; projectReferences = $false
    assemblySha256 = Get-ReleaseHash $loadedAssembly
    resolvedDependencies = @($assets.libraries.Keys | Sort-Object)
})
