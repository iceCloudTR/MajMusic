#requires -Version 5.1

[CmdletBinding()]
param(
    [string] $SourceDirectory = '',
    [string] $CustomDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-RuntimeFile($Path, $File) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    return (Get-Item -LiteralPath $Path).Length -eq $File.length -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $File.sha256
}

function Read-RuntimeManifest($Path) {
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $expectedFiles = @('Build.loader.js', 'Build.framework.js', 'Build.data', 'Build.wasm')
    if ($manifest.revision -notmatch '^[a-fA-F0-9]{40}([-+][a-zA-Z0-9.-]+)?$' -or
        @(Compare-Object -ReferenceObject $expectedFiles -DifferenceObject @($manifest.files.name) -CaseSensitive).Count -ne 0) {
        throw "Invalid MajView runtime manifest: $Path."
    }
    foreach ($file in $manifest.files) {
        if ($file.length -le 0 -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$') {
            throw "Invalid MajView runtime file in manifest: $($file.name)."
        }
    }
    return $manifest
}

$defaultCustomDirectory = Join-Path $PSScriptRoot '../.work/majview-runtime-custom'
if (!$CustomDirectory -and !$SourceDirectory -and (Test-Path -LiteralPath (Join-Path $defaultCustomDirectory 'runtime.json') -PathType Leaf)) {
    $CustomDirectory = $defaultCustomDirectory
}
if ($CustomDirectory) {
    $directory = (Resolve-Path -LiteralPath $CustomDirectory).ProviderPath
    $manifestPath = Join-Path $directory 'runtime.json'
    $manifest = Read-RuntimeManifest $manifestPath
    foreach ($file in $manifest.files) {
        if (!(Test-RuntimeFile (Join-Path $directory $file.name) $file)) {
            throw "Custom MajView runtime verification failed: $($file.name)."
        }
    }
    return [PSCustomObject]@{ Directory = $directory; ManifestPath = $manifestPath; Manifest = $manifest }
}

$manifestPath = Join-Path $PSScriptRoot 'MajViewRuntime.json'
$manifest = Read-RuntimeManifest $manifestPath
$directory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.work/majview-runtime'))
$sourceUrl = "https://raw.githubusercontent.com/TeamMajdata/MajdataNet/$($manifest.revision)/public/WebGLBuild"
New-Item -Path $directory -ItemType Directory -Force | Out-Null

# Concurrent Windows and Android publishes share this cache.
$lockPath = Join-Path $directory '.prepare.lock'
$deadline = [DateTime]::UtcNow.AddMinutes(8)
while ($true) {
    try {
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        break
    } catch [IO.IOException] {
        if ([DateTime]::UtcNow -ge $deadline) { throw }
        Start-Sleep -Milliseconds 250
    }
}

try {
    foreach ($file in $manifest.files) {
        $path = Join-Path $directory $file.name
        if (Test-RuntimeFile $path $file) { continue }

        $temporary = "$path.tmp"
        try {
            if ($SourceDirectory) {
                Copy-Item -LiteralPath (Join-Path $SourceDirectory $file.name) -Destination $temporary -Force
            } else {
                Invoke-WebRequest -UseBasicParsing -Uri "$sourceUrl/$($file.name)" -OutFile $temporary -TimeoutSec 120
            }
            if (!(Test-RuntimeFile $temporary $file)) {
                throw "MajView runtime verification failed: $($file.name)."
            }
            Move-Item -LiteralPath $temporary -Destination $path -Force
        } finally {
            if (Test-Path -LiteralPath $temporary -PathType Leaf) {
                Remove-Item -LiteralPath $temporary -Force
            }
        }
    }
} finally {
    $lock.Dispose()
}

[PSCustomObject]@{ Directory = $directory; ManifestPath = $manifestPath; Manifest = $manifest }
