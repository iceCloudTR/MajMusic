#requires -Version 5.1

<#
.SYNOPSIS
Builds and runs real Android Keystore regressions in the independent MajMusic test application.
.PARAMETER Serial
Specifies the exact authorized adb device.
.PARAMETER CredentialStoreSource
Optionally links an older credential implementation for a native failing-control comparison.
.PARAMETER KeepInstalled
Retains the test app after the result is recorded.
.PARAMETER DotNet
Specifies a .NET 10 SDK executable or command.
.PARAMETER AndroidSdkDirectory
Specifies the Android SDK used to build and inspect the test APK.
.PARAMETER JavaSdkDirectory
Specifies a JDK 17 or 21. Android Studio, Unity and common JDK installations are detected by default.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Serial,
    [string] $CredentialStoreSource = '',
    [string] $DotNet = '',
    [string] $AndroidSdkDirectory = '',
    [string] $JavaSdkDirectory = '',
    [switch] $KeepInstalled
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packageName = 'com.icecloud.majmusic.tests'
$tag = 'MajMusic.CredTests'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
if (!$DotNet) {
    $userDotNet = Join-Path $userProfile '.dotnet/dotnet.exe'
    $DotNet = if (Test-Path -LiteralPath $userDotNet) { $userDotNet } else { 'dotnet' }
}
if (!$AndroidSdkDirectory) {
    $AndroidSdkDirectory = Join-Path $env:LOCALAPPDATA 'Android/Sdk'
}
function Find-CredentialTestJava {
    param([string] $ExplicitDirectory)

    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $candidates = @($ExplicitDirectory)
    if ([string]::IsNullOrWhiteSpace($ExplicitDirectory)) {
        $candidates = @($env:JAVA_HOME, $env:JDK_HOME, (Join-Path $programFiles 'Android/Android Studio/jbr'))
        $unityEditors = Join-Path $programFiles 'Unity/Hub/Editor'
        if (Test-Path -LiteralPath $unityEditors) {
            $candidates += @(Get-ChildItem -LiteralPath $unityEditors -Directory | ForEach-Object {
                Join-Path $_.FullName 'Editor/Data/PlaybackEngines/AndroidPlayer/OpenJDK'
            })
        }
        foreach ($root in @((Join-Path $programFiles 'Microsoft'), (Join-Path $programFiles 'Java'),
            (Join-Path $programFiles 'Eclipse Adoptium'), (Join-Path $userProfile '.dotnet/jdk'))) {
            if (Test-Path -LiteralPath $root) {
                $candidates += @(Get-ChildItem -LiteralPath $root -Directory | Select-Object -ExpandProperty FullName)
            }
        }
    }
    $compatible = @()
    foreach ($candidate in @($candidates | Where-Object { $_ } | Select-Object -Unique)) {
        $release = Join-Path $candidate 'release'
        $complete = (Test-Path -LiteralPath $release -PathType Leaf)
        foreach ($tool in @('java.exe', 'javac.exe', 'keytool.exe')) {
            $complete = $complete -and (Test-Path -LiteralPath (Join-Path $candidate ('bin/' + $tool)) -PathType Leaf)
        }
        if ($complete) {
            $versionLine = @(Get-Content -LiteralPath $release | Where-Object { $_ -match '^JAVA_VERSION="(17|21)\.[0-9]+\.[0-9]+' } | Select-Object -First 1)
            if ($versionLine.Count -gt 0 -and $versionLine[0] -match '"([0-9]+\.[0-9]+\.[0-9]+)') {
                $compatible += [PSCustomObject]@{ Directory = [IO.Path]::GetFullPath($candidate); Version = [version]$Matches[1] }
            }
        }
    }
    if ($compatible.Count -eq 0) {
        throw 'Install a compatible JDK 17 or 21 with java, javac and keytool, or pass -JavaSdkDirectory.'
    }
    return ($compatible | Sort-Object Version -Descending | Select-Object -First 1).Directory
}
$JavaSdkDirectory = Find-CredentialTestJava -ExplicitDirectory $JavaSdkDirectory
$adb = Join-Path $AndroidSdkDirectory 'platform-tools/adb.exe'
if (!(Test-Path -LiteralPath $adb)) {
    throw 'The selected Android SDK has no adb executable.'
}
$buildTools = @(Get-ChildItem -LiteralPath (Join-Path $AndroidSdkDirectory 'build-tools') -Directory |
    Where-Object Name -Match '^\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1)
if ($buildTools.Count -eq 0) {
    throw 'Android SDK build tools are missing.'
}
$aapt = Join-Path $buildTools[0].FullName 'aapt2.exe'
$runId = [Guid]::NewGuid().ToString('N')
$runDirectory = Join-Path (Join-Path $repositoryRoot 'artifacts') ('android-credentials-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $runId.Substring(0, 8))
$publishDirectory = Join-Path $runDirectory 'app'
New-Item -ItemType Directory -Path $publishDirectory | Out-Null
$project = Join-Path $repositoryRoot 'MajMusic.Android.Tests/MajMusic.Android.Tests.csproj'
$publishArguments = @('publish', $project, '-c', 'Release', '-f', 'net10.0-android', '-r', 'android-arm64',
    '-p:AndroidKeyStore=false', '-p:AndroidPackageFormats=apk', "-p:AndroidSdkDirectory=$AndroidSdkDirectory",
    "-p:JavaSdkDirectory=$JavaSdkDirectory", '--artifacts-path', (Join-Path $runDirectory 'build'), '-o', $publishDirectory)
if ($CredentialStoreSource) {
    $sourcePath = (Resolve-Path -LiteralPath $CredentialStoreSource -ErrorAction Stop).ProviderPath
    $publishArguments += "-p:CredentialStoreSource=$sourcePath"
}
& $DotNet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw 'The isolated native credential test app could not be built.'
}
$apks = @(Get-ChildItem -LiteralPath $publishDirectory -File -Filter '*-Signed.apk')
if ($apks.Count -ne 1) {
    throw 'Expected exactly one signed native test APK.'
}
$badging = @(& $aapt dump badging $apks[0].FullName)
if ($LASTEXITCODE -ne 0 -or !($badging | Where-Object { $_ -match "^package:\s+name='com\.icecloud\.majmusic\.tests'\s" })) {
    throw 'The native test APK must have the independent com.icecloud.majmusic.tests package ID.'
}

function Invoke-CredentialTestAdb {
    param([string[]] $Arguments)
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& $adb '-s' $Serial @Arguments 2>&1 | ForEach-Object { $_.ToString().Replace($Serial, '<device>') })
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    return [PSCustomObject]@{ ExitCode = $code; Lines = $lines }
}

$state = Invoke-CredentialTestAdb -Arguments @('get-state')
if ($state.ExitCode -ne 0 -or $state.Lines -notcontains 'device') {
    throw 'The explicitly selected adb device is not connected and authorized.'
}
$installedByRun = $false
try {
    $install = Invoke-CredentialTestAdb -Arguments @('install', '-r', $apks[0].FullName)
    $install.Lines | Set-Content -LiteralPath (Join-Path $runDirectory 'install.txt') -Encoding UTF8
    if ($install.ExitCode -ne 0 -or !($install.Lines | Where-Object { $_ -eq 'Success' })) {
        throw 'The independent native test app could not be installed.'
    }
    $installedByRun = $true
    $time = Invoke-CredentialTestAdb -Arguments @('shell', "date '+%m-%d %H:%M:%S.000'")
    $start = $time.Lines | Where-Object { $_ -match '^\d\d-\d\d \d\d:\d\d:\d\d\.\d\d\d$' } | Select-Object -First 1
    if (!$start) {
        throw 'The device log timestamp could not be read.'
    }
    $stop = Invoke-CredentialTestAdb -Arguments @('shell', 'am', 'force-stop', $packageName)
    if ($stop.ExitCode -ne 0) {
        throw 'The independent test process could not be stopped before its run.'
    }
    $launch = Invoke-CredentialTestAdb -Arguments @('shell', 'am', 'start', '-W', '-n', ($packageName + '/' + $packageName + '.MainActivity'), '--es', 'run-id', $runId)
    $launch.Lines | Set-Content -LiteralPath (Join-Path $runDirectory 'launch.txt') -Encoding UTF8
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $result = $null
    do {
        $logs = Invoke-CredentialTestAdb -Arguments @('logcat', '-d', '-v', 'raw', '-T', $start, '-s', ($tag + ':I'), '*:S')
        $logs.Lines | Set-Content -LiteralPath (Join-Path $runDirectory 'native-results.txt') -Encoding UTF8
        if ($logs.ExitCode -ne 0) {
            throw 'The native test result log could not be read.'
        }
        $result = $logs.Lines | Where-Object { $_ -match ('^(PASS|FAIL) run=' + $runId + '\b') } | Select-Object -Last 1
        if (!$result) {
            Start-Sleep -Milliseconds 250
        }
    } while (!$result -and [DateTime]::UtcNow -lt $deadline)
    $passed = [bool]($result -and $result -match ('^PASS run=' + $runId + ' checks=12$'))
    [ordered]@{ Package = $packageName; Passed = $passed; SourceOverride = [bool]$CredentialStoreSource; Result = $result } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'result.json') -Encoding UTF8
    Write-Output ('Native credential logs: ' + $runDirectory)
    if (!$passed) {
        if ($result) {
            Write-Output $result
        }
        throw 'Native Android credential persistence checks failed.'
    }
    Write-Output $result
} finally {
    if ($installedByRun -and !$KeepInstalled) {
        $stopped = Invoke-CredentialTestAdb -Arguments @('shell', 'am', 'force-stop', $packageName)
        $uninstall = Invoke-CredentialTestAdb -Arguments @('uninstall', $packageName)
        $uninstall.Lines | Set-Content -LiteralPath (Join-Path $runDirectory 'uninstall.txt') -Encoding UTF8
        if ($uninstall.ExitCode -ne 0) {
            Write-Warning 'The independent test app remains installed; remove com.icecloud.majmusic.tests when convenient.'
        }
    }
}
