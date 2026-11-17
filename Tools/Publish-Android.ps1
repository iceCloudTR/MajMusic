#requires -Version 5.1

<#
.SYNOPSIS
Creates a signed ARM64 APK with .NET 10 and MajView.
.PARAMETER DotNet
Specifies the .NET 10 SDK command or executable. A compatible user installation is detected by default.
.PARAMETER AndroidSdkDirectory
Specifies an installed Android SDK containing platform and build tools.
.PARAMETER JavaSdkDirectory
Specifies a compatible JDK 17 or 21 directory.
.PARAMETER MajViewDirectory
Specifies a local MajView build folder with its runtime.json manifest.
.PARAMETER KeepBuild
Keeps this run's isolated intermediate build directory.
#>

[CmdletBinding()]
param(
    [string] $DotNet = '',
    [string] $AndroidSdkDirectory = '',
    [string] $JavaSdkDirectory = '',
    [string] $MajViewDirectory = '',
    [switch] $KeepBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-AndroidElfAlignment {
    param($Entry)

    $stream = $Entry.Open()
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $header = $reader.ReadBytes(64)
        if ($header.Length -ne 64 -or $header[0] -ne 0x7f -or $header[1] -ne 0x45 -or
            $header[2] -ne 0x4c -or $header[3] -ne 0x46 -or $header[4] -ne 2 -or
            $header[5] -ne 1 -or [BitConverter]::ToUInt16($header, 18) -ne 183) {
            throw "The APK contains an invalid ARM64 ELF library: $($Entry.FullName)."
        }

        $tableOffset = [BitConverter]::ToUInt64($header, 32)
        $entrySize = [BitConverter]::ToUInt16($header, 54)
        $entryCount = [BitConverter]::ToUInt16($header, 56)
        if ($tableOffset -lt 64 -or $entrySize -lt 56 -or $entryCount -eq 0 -or
            $tableOffset + [uint64]$entrySize * $entryCount -gt [uint64]$Entry.Length) {
            throw "The ELF program headers are incomplete: $($Entry.FullName)."
        }

        $remaining = $tableOffset - 64
        while ($remaining -gt 0) {
            $skip = $reader.ReadBytes([int][Math]::Min($remaining, 4096))
            if ($skip.Length -eq 0) {
                throw "The ELF program headers are unavailable: $($Entry.FullName)."
            }
            $remaining -= $skip.Length
        }

        $loadCount = 0
        $minimumAlignment = [uint64]::MaxValue
        for ($index = 0; $index -lt $entryCount; $index++) {
            $programHeader = $reader.ReadBytes($entrySize)
            if ($programHeader.Length -ne $entrySize) {
                throw "The ELF program header is truncated: $($Entry.FullName)."
            }
            if ([BitConverter]::ToUInt32($programHeader, 0) -eq 1) {
                $offset = [BitConverter]::ToUInt64($programHeader, 8)
                $address = [BitConverter]::ToUInt64($programHeader, 16)
                $alignment = [BitConverter]::ToUInt64($programHeader, 48)
                if ($alignment -lt 16384 -or ($alignment -band ($alignment - 1)) -ne 0 -or
                    ($offset % 16384) -ne ($address % 16384)) {
                    throw "The native ELF LOAD segment is not 16 KB aligned: $($Entry.FullName)."
                }
                $minimumAlignment = [Math]::Min($minimumAlignment, $alignment)
                $loadCount++
            }
        }
        if ($loadCount -eq 0) {
            throw "The native ELF library has no LOAD segments: $($Entry.FullName)."
        }
        return [PSCustomObject]@{ Name = $Entry.Name; LoadSegments = $loadCount; MinimumAlignment = $minimumAlignment }
    } finally {
        $reader.Dispose()
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Run this Android publishing script on Windows.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'MajMusic.Android/MajMusic.Android.csproj'
$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
if (!(Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw 'The MajMusic.Android host project is missing.'
}

if ([string]::IsNullOrWhiteSpace($DotNet)) {
    $sdkCandidates = @(
        @(Get-Command -Name 'dotnet' -CommandType Application -All -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
        (Join-Path $userProfile '.dotnet/dotnet.exe')
        (Join-Path $programFiles 'dotnet/dotnet.exe')
    ) | Select-Object -Unique
    foreach ($candidate in $sdkCandidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $installedSdks = @(& $candidate --list-sdks 2>$null)
            if ($LASTEXITCODE -eq 0 -and @($installedSdks | Where-Object { $_ -match '^10\.' }).Count -gt 0) {
                $DotNet = $candidate
                break
            }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($DotNet)) {
    throw 'Install the .NET 10 SDK or pass its executable with -DotNet.'
}
Get-Command -Name $DotNet -CommandType Application -ErrorAction Stop | Out-Null

$unityAndroidRoots = @()
$unityEditors = Join-Path $programFiles 'Unity/Hub/Editor'
if (Test-Path -LiteralPath $unityEditors) {
    $unityAndroidRoots = @(Get-ChildItem -LiteralPath $unityEditors -Directory | Sort-Object Name -Descending | ForEach-Object {
        Join-Path $_.FullName 'Editor/Data/PlaybackEngines/AndroidPlayer'
    })
}

if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) {
    $androidCandidates = @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME, (Join-Path $env:LOCALAPPDATA 'Android/Sdk'))
    $androidCandidates += @($unityAndroidRoots | ForEach-Object { Join-Path $_ 'SDK' })
    foreach ($candidate in @($androidCandidates | Where-Object { $_ } | Select-Object -Unique)) {
        if ((Test-Path -LiteralPath (Join-Path $candidate 'platforms')) -and (Test-Path -LiteralPath (Join-Path $candidate 'build-tools'))) {
            $AndroidSdkDirectory = [IO.Path]::GetFullPath($candidate)
            break
        }
    }
}
if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) {
    throw 'Install Android SDK platform and build tools, or pass -AndroidSdkDirectory.'
}

if ([string]::IsNullOrWhiteSpace($JavaSdkDirectory)) {
    $javaCandidates = @($env:JAVA_HOME, $env:JDK_HOME, (Join-Path $programFiles 'Android/Android Studio/jbr'))
    $javaCandidates += @($unityAndroidRoots | ForEach-Object { Join-Path $_ 'OpenJDK' })
    foreach ($root in @((Join-Path $programFiles 'Microsoft'), (Join-Path $programFiles 'Java'), (Join-Path $programFiles 'Eclipse Adoptium'), (Join-Path $userProfile '.dotnet/jdk'))) {
        if (Test-Path -LiteralPath $root) {
            $javaCandidates += @(Get-ChildItem -LiteralPath $root -Directory | Select-Object -ExpandProperty FullName)
        }
    }
    $compatibleJava = @()
    foreach ($candidate in @($javaCandidates | Where-Object { $_ } | Select-Object -Unique)) {
        $release = Join-Path $candidate 'release'
        if ((Test-Path -LiteralPath $release -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $candidate 'bin/javac.exe') -PathType Leaf)) {
            $versionLine = @(Get-Content -LiteralPath $release | Where-Object { $_ -match '^JAVA_VERSION="(17|21)\.[0-9]+\.[0-9]+' } | Select-Object -First 1)
            if ($versionLine.Count -gt 0 -and $versionLine[0] -match '"([0-9]+\.[0-9]+\.[0-9]+)') {
                $compatibleJava += [PSCustomObject]@{ Directory = $candidate; Version = [version]$Matches[1] }
            }
        }
    }
    if ($compatibleJava.Count -gt 0) {
        $JavaSdkDirectory = ($compatibleJava | Sort-Object Version -Descending | Select-Object -First 1).Directory
    }
}
if ([string]::IsNullOrWhiteSpace($JavaSdkDirectory) -or !(Test-Path -LiteralPath (Join-Path $JavaSdkDirectory 'bin/keytool.exe') -PathType Leaf)) {
    throw 'Install a compatible JDK 17 or 21, or pass -JavaSdkDirectory.'
}
$java = Join-Path $JavaSdkDirectory 'bin/java.exe'
$buildTools = @(Get-ChildItem -LiteralPath (Join-Path $AndroidSdkDirectory 'build-tools') -Directory |
    Where-Object Name -Match '^\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1)
if ($buildTools.Count -eq 0) {
    throw 'Android SDK build tools are missing.'
}
$signer = Join-Path $buildTools[0].FullName 'lib/apksigner.jar'
$aapt = Join-Path $buildTools[0].FullName 'aapt2.exe'
$zipalign = Join-Path $buildTools[0].FullName 'zipalign.exe'
foreach ($tool in @($java, $signer, $aapt, $zipalign)) {
    if (!(Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw 'Android SDK build tools or the selected Java installation are incomplete.'
    }
}

$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$runName = 'android-arm64-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $artifactsRoot $runName
$buildDirectory = Join-Path $runDirectory 'build'
$publishDirectory = Join-Path $buildDirectory 'package'
$renderer = & (Join-Path $PSScriptRoot 'Prepare-MajViewRuntime.ps1') -CustomDirectory $MajViewDirectory
$majView = $renderer.Manifest
New-Item -ItemType Directory -Path $publishDirectory | Out-Null
$publishArguments = @(
    'publish', $projectPath, '-c', 'Release', '-f', 'net10.0-android', '-r', 'android-arm64',
    '-p:RuntimeIdentifiers=android-arm64', '-p:AndroidPackageFormats=apk', '-p:AndroidKeyStore=false',
    '-p:EmbedAssembliesIntoApk=true', "-p:AndroidSdkDirectory=$AndroidSdkDirectory", "-p:JavaSdkDirectory=$JavaSdkDirectory",
    "-p:MajViewAssetsDirectory=$($renderer.Directory)", "-p:MajViewRuntimeManifest=$($renderer.ManifestPath)",
    '--artifacts-path', (Join-Path $buildDirectory 'sdk'), '-o', $publishDirectory
)
& $DotNet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Android ARM64 publish failed.'
}
$signedApks = @(Get-ChildItem -LiteralPath $publishDirectory -File -Filter '*-Signed.apk')
if ($signedApks.Count -ne 1) {
    throw 'Expected one signed ARM64 APK from the Android host.'
}
$signedApk = $signedApks[0].FullName
& $java '-jar' $signer 'verify' '--verbose' $signedApk
if ($LASTEXITCODE -ne 0) {
    throw 'The APK signature could not be verified.'
}
& $zipalign '-c' '-P' '16' '4' $signedApk
if ($LASTEXITCODE -ne 0) {
    throw 'The APK does not satisfy Android package alignment.'
}
$badging = @(& $aapt 'dump' 'badging' $signedApk)
if ($LASTEXITCODE -ne 0) {
    throw 'Android package metadata could not be read.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($signedApk)
try {
    foreach ($name in @('libmonosgen-2.0.so', 'libmonodroid.so')) {
        if (!$archive.GetEntry("lib/arm64-v8a/$name")) {
            throw "The .NET 10 runtime is incomplete: $name."
        }
    }
    foreach ($file in $majView.files) {
        $entry = $archive.GetEntry("assets/MajView/$($file.name)")
        if (!$entry -or $entry.Length -ne $file.length) {
            throw "The packaged MajView renderer is incomplete: $($file.name)."
        }
        $stream = $entry.Open()
        try {
            if ((Get-FileHash -InputStream $stream -Algorithm SHA256).Hash -ne $file.sha256) {
                throw "The packaged MajView renderer has an unexpected hash: $($file.name)."
            }
        } finally {
            $stream.Dispose()
        }
    }
    foreach ($name in @('MajView.LICENSE', 'MajView.NOTICES.md', 'MajView.Runtime.json')) {
        if (!$archive.GetEntry("assets/ThirdPartyNotices/$name")) {
            throw "The packaged MajView notice is missing: $name."
        }
    }
    $abis = @($archive.Entries | ForEach-Object { if ($_.FullName -match '^lib/([^/]+)/[^/]+\.so$') { $Matches[1] } } | Select-Object -Unique)
    if ($abis.Count -ne 1 -or $abis[0] -ne 'arm64-v8a') {
        throw 'The APK must contain only ARM64 native libraries.'
    }
    $nativeEntries = @($archive.Entries | Where-Object FullName -Match '^lib/arm64-v8a/[^/]+\.so$')
    $nativeCount = $nativeEntries.Count
    $elfReports = @($nativeEntries | ForEach-Object { Test-AndroidElfAlignment $_ })
} finally {
    $archive.Dispose()
}

$apkPath = Join-Path $runDirectory 'MajMusic-android-arm64.apk'
Copy-Item -LiteralPath $signedApk -Destination $apkPath
$manifest = [ordered]@{
    RuntimeIdentifier = 'android-arm64'
    SelfContained = $true
    MajViewRevision = $majView.revision
    NativeAbis = $abis
    NativeLibraryCount = $nativeCount
    ElfMinimumLoadAlignmentBytes = ($elfReports | Measure-Object -Property MinimumAlignment -Minimum).Minimum
    Package = ($badging | Where-Object { $_ -like 'package:*' } | Select-Object -First 1)
    MinimumSdk = ($badging | Where-Object { $_ -match '^(minSdkVersion|sdkVersion):' } | Select-Object -First 1)
    TargetSdk = ($badging | Where-Object { $_ -like 'targetSdkVersion:*' } | Select-Object -First 1)
    DevelopmentSigned = $true
    SignatureVerified = $true
    ApkBytes = (Get-Item -LiteralPath $apkPath).Length
    Sha256 = (Get-FileHash -LiteralPath $apkPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runDirectory 'package-manifest.json') -Encoding UTF8
Write-Output ('APK: ' + $apkPath)
Write-Output ('APK size: {0:N2} MiB' -f ($manifest.ApkBytes / 1MB))

if (!$KeepBuild) {
    $resolvedArtifacts = (Resolve-Path -LiteralPath $artifactsRoot).ProviderPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolvedRun = (Resolve-Path -LiteralPath $runDirectory).ProviderPath
    $resolvedBuild = (Resolve-Path -LiteralPath $buildDirectory).ProviderPath
    $expectedBuild = Join-Path $resolvedRun 'build'
    if (!$resolvedRun.StartsWith($resolvedArtifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        ![string]::Equals($resolvedBuild, $expectedBuild, [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $resolvedBuild).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to remove a build directory outside this isolated Android publish run.'
    }
    Remove-Item -LiteralPath $resolvedBuild -Recurse -Force
}
