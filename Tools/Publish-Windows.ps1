#requires -Version 5.1

<#
.SYNOPSIS
Publishes an isolated Windows x64 application and distribution ZIP.
.PARAMETER Mode
Includes the .NET runtime by default; FrameworkDependent uses an installed .NET 10 x64 runtime.
.PARAMETER DotNet
Specifies the dotnet command or SDK executable path.
.PARAMETER SkipZip
Produces the complete application folder without compressing it.
.PARAMETER KeepBuild
Keeps this run's intermediate build directory for diagnostics.
#>

[CmdletBinding()]
param(
    [ValidateSet('SelfContained', 'FrameworkDependent')]
    [string] $Mode = 'SelfContained',
    [string] $DotNet = '',
    [switch] $SkipZip,
    [switch] $KeepBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Publish this Windows package on Windows.'
}

if ([string]::IsNullOrWhiteSpace($DotNet)) {
    $sdkCandidates = @(
        @(Get-Command -Name 'dotnet' -CommandType Application -All -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.dotnet/dotnet.exe')
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'dotnet/dotnet.exe')
    ) | Select-Object -Unique
    foreach ($candidate in $sdkCandidates) {
        if (!(Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $installedSdks = @(& $candidate --list-sdks 2>$null)
        if ($LASTEXITCODE -eq 0 -and @($installedSdks | Where-Object { $_ -match '^10\.' }).Count -gt 0) {
            $DotNet = $candidate
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($DotNet)) {
        throw 'Install the .NET 10 SDK or pass its executable with -DotNet.'
    }
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'MajMusic/MajMusic.csproj'
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$flavor = if ($Mode -eq 'SelfContained') { 'self-contained' } else { 'framework-dependent' }
$runName = 'win-x64-' + $flavor + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $artifactsRoot $runName
$applicationDirectory = Join-Path $runDirectory 'app'
$buildDirectory = Join-Path $runDirectory 'build'
$selfContained = ($Mode -eq 'SelfContained').ToString().ToLowerInvariant()

Get-Command -Name $DotNet -CommandType Application -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path $applicationDirectory | Out-Null

$publishArguments = @(
    'publish', $projectPath,
    '-c', 'Release', '-r', 'win-x64',
    '--self-contained', $selfContained,
    '-p:PublishProfile=WindowsX64',
    '--artifacts-path', $buildDirectory,
    '-o', $applicationDirectory
)
& $DotNet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Windows publish failed.'
}

$files = @(Get-ChildItem -LiteralPath $applicationDirectory -File -Recurse)
$forbiddenFiles = @($files | Where-Object {
    $_.Extension -eq '.pdb' -or $_.Name -in @('settings.json', 'system-settings.json') -or
    $_.Name -match '\.(db|sqlite|sqlite3)(-wal|-shm|-journal)?$'
})
if ($forbiddenFiles.Count -gt 0) {
    throw 'The package contains symbols or local application data.'
}

$dependencyManifest = Join-Path $applicationDirectory 'MajMusic.deps.json'
if ((Get-Content -LiteralPath $dependencyManifest -Raw) -match '"[^"]+\.pdb"\s*:') {
    throw 'The dependency manifest still references debug symbols.'
}

foreach ($name in @('Cache', 'Downloads', 'Library', 'AudioMetadata', 'Logs')) {
    if (Test-Path -LiteralPath (Join-Path $applicationDirectory $name)) {
        throw "The package contains an application data directory: $name."
    }
}

$vlcDirectory = Join-Path $applicationDirectory 'libvlc/win-x64'
foreach ($name in @('libvlc.dll', 'libvlccore.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $vlcDirectory $name) -PathType Leaf)) {
        throw "The Windows x64 VLC runtime is incomplete: $name."
    }
}

$otherVlcArchitectures = @(Get-ChildItem -LiteralPath (Join-Path $applicationDirectory 'libvlc') -Directory | Where-Object Name -ne 'win-x64')
if ($otherVlcArchitectures.Count -gt 0) {
    throw 'The package contains VLC files for another architecture.'
}

$plugins = @(Get-ChildItem -LiteralPath (Join-Path $vlcDirectory 'plugins') -File -Filter '*.dll' -Recurse)
if ($plugins.Count -eq 0 -or !(Test-Path -LiteralPath (Join-Path $applicationDirectory 'MajMusic.exe') -PathType Leaf)) {
    throw 'The published application or VLC plugins are missing.'
}

$packageBytes = ($files | Measure-Object -Property Length -Sum).Sum
$manifest = [ordered]@{
    RuntimeIdentifier = 'win-x64'
    Mode = $Mode
    ApplicationBytes = $packageBytes
    FileCount = $files.Count
    VlcPluginCount = $plugins.Count
}
Write-Output ('Application: ' + $applicationDirectory)
Write-Output ('Application size: {0:N2} MiB' -f ($packageBytes / 1MB))
if ($Mode -eq 'FrameworkDependent') {
    Write-Output 'Requires the .NET 10 x64 runtime on the target computer.'
}

if (!$SkipZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $runDirectory ('MajMusic-win-x64-' + $flavor + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($applicationDirectory, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $manifest.ZipBytes = (Get-Item -LiteralPath $zipPath).Length
    Write-Output ('ZIP: ' + $zipPath)
    Write-Output ('ZIP size: {0:N2} MiB' -f ($manifest.ZipBytes / 1MB))
}

$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'package-manifest.json') -Encoding UTF8

if (!$KeepBuild -and (Test-Path -LiteralPath $buildDirectory -PathType Container)) {
    $resolvedArtifacts = (Resolve-Path -LiteralPath $artifactsRoot).ProviderPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolvedRun = (Resolve-Path -LiteralPath $runDirectory).ProviderPath
    $resolvedBuild = (Resolve-Path -LiteralPath $buildDirectory).ProviderPath
    $resolvedApplication = (Resolve-Path -LiteralPath $applicationDirectory).ProviderPath
    $expectedBuild = Join-Path $resolvedRun 'build'
    $buildItem = Get-Item -LiteralPath $resolvedBuild
    if (!$resolvedRun.StartsWith($resolvedArtifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        ![string]::Equals($resolvedBuild, $expectedBuild, [StringComparison]::OrdinalIgnoreCase) -or
        $resolvedApplication.StartsWith($resolvedBuild + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($resolvedApplication, $resolvedBuild, [StringComparison]::OrdinalIgnoreCase) -or
        ($buildItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to remove a build directory outside this isolated publish run.'
    }

    Remove-Item -LiteralPath $resolvedBuild -Recurse -Force
}
