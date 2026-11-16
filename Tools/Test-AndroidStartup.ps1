#requires -Version 5.1

<#
.SYNOPSIS
Checks repeated real-device cold starts using process, foreground activity and fatal-log evidence.
.PARAMETER Serial
Specifies the exact adb device. The script never chooses a device automatically.
.PARAMETER ApkPath
Optionally replaces the installed MajMusic package after checking the APK application ID.
.PARAMETER Attempts
Specifies the number of cold starts; the default is three.
.PARAMETER SettleSeconds
Specifies the post-launch observation period; the default is five seconds.
.PARAMETER Adb
Specifies the adb command or executable path.
.PARAMETER AndroidSdkDirectory
Specifies the SDK used to inspect an optional replacement APK.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Serial,
    [string] $ApkPath = '',
    [ValidateRange(1, 10)]
    [int] $Attempts = 3,
    [ValidateRange(1, 60)]
    [int] $SettleSeconds = 5,
    [string] $Adb = 'adb',
    [string] $AndroidSdkDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packageName = 'com.icecloud.majmusic'
$activityName = 'com.icecloud.majmusic.MainActivity'
$adbExecutable = (Get-Command -Name $Adb -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runName = 'android-startup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path (Join-Path $repositoryRoot 'artifacts') $runName
New-Item -ItemType Directory -Path $runDirectory | Out-Null

function Invoke-StartupAdb {
    param([string[]] $Arguments)

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& $adbExecutable '-s' $Serial @Arguments 2>&1 | ForEach-Object { $_.ToString().Replace($Serial, '<device>') })
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    return [PSCustomObject]@{ ExitCode = $exitCode; Lines = $lines }
}

function Get-StartupProcessIds {
    $result = Invoke-StartupAdb -Arguments @('shell', 'pidof', $packageName)
    return @($result.Lines -split '\s+' | Where-Object { $_ -match '^\d+$' } | Select-Object -Unique)
}

$state = Invoke-StartupAdb -Arguments @('get-state')
if ($state.ExitCode -ne 0 -or $state.Lines -notcontains 'device') {
    throw 'The explicitly selected adb device is not connected and authorized.'
}

if (![string]::IsNullOrWhiteSpace($ApkPath)) {
    $resolvedApk = (Resolve-Path -LiteralPath $ApkPath -ErrorAction Stop).ProviderPath
    if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) {
        $sdkCandidates = @(
            (Split-Path -Parent (Split-Path -Parent $adbExecutable)),
            $env:ANDROID_SDK_ROOT, $env:ANDROID_HOME,
            (Join-Path $env:LOCALAPPDATA 'Android/Sdk')
        ) | Where-Object { $_ } | Select-Object -Unique
        foreach ($candidate in $sdkCandidates) {
            if (Test-Path -LiteralPath (Join-Path $candidate 'build-tools') -PathType Container) {
                $AndroidSdkDirectory = $candidate
                break
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) {
        throw 'Pass -AndroidSdkDirectory to verify the APK package before replacement.'
    }
    $buildTools = @(Get-ChildItem -LiteralPath (Join-Path $AndroidSdkDirectory 'build-tools') -Directory |
        Where-Object Name -Match '^\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1)
    if ($buildTools.Count -eq 0) {
        throw 'Android SDK build tools are required to verify the replacement APK.'
    }
    $aapt = Join-Path $buildTools[0].FullName 'aapt2.exe'
    $badging = @(& $aapt 'dump' 'badging' $resolvedApk)
    if ($LASTEXITCODE -ne 0) {
        throw 'The replacement APK manifest could not be read.'
    }
    $packageLine = @($badging | Where-Object { $_ -like 'package:*' } | Select-Object -First 1)
    if ($packageLine.Count -ne 1 -or $packageLine[0] -notmatch "^package:\s+name='com\.icecloud\.majmusic'\s") {
        throw 'The replacement APK must have application ID com.icecloud.majmusic.'
    }
    $installation = Invoke-StartupAdb -Arguments @('install', '-r', $resolvedApk)
    $installation.Lines | Set-Content -LiteralPath (Join-Path $runDirectory 'install.txt') -Encoding UTF8
    if ($installation.ExitCode -ne 0 -or !($installation.Lines | Where-Object { $_ -eq 'Success' })) {
        throw 'Replacing the MajMusic APK failed; see the install log in the startup artifact directory.'
    }
}

$installed = Invoke-StartupAdb -Arguments @('shell', 'pm', 'path', $packageName)
if ($installed.ExitCode -ne 0 -or !($installed.Lines | Where-Object { $_ -like 'package:*' })) {
    throw 'MajMusic is not installed on the selected device.'
}

$results = @()
for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
    $attemptDirectory = Join-Path $runDirectory ('attempt-' + $attempt.ToString('D2'))
    New-Item -ItemType Directory -Path $attemptDirectory | Out-Null
    $result = [ordered]@{ Attempt = $attempt; Passed = $false; ProcessStable = $false; ForegroundMatches = $false; FatalLogCount = 0 }
    try {
        $deviceTime = Invoke-StartupAdb -Arguments @('shell', "date '+%m-%d %H:%M:%S.000'")
        $startTime = ($deviceTime.Lines | Where-Object { $_ -match '^\d\d-\d\d \d\d:\d\d:\d\d\.\d\d\d$' } | Select-Object -First 1)
        if ($deviceTime.ExitCode -ne 0 -or !$startTime) {
            throw 'The device log time could not be read.'
        }
        $stopped = Invoke-StartupAdb -Arguments @('shell', 'am', 'force-stop', $packageName)
        if ($stopped.ExitCode -ne 0) {
            throw 'MajMusic could not be stopped before its cold start.'
        }
        $launch = Invoke-StartupAdb -Arguments @('shell', 'am', 'start', '-W', '-a', 'android.intent.action.MAIN',
            '-c', 'android.intent.category.LAUNCHER', '-n', ($packageName + '/' + $activityName))
        $launch.Lines | Set-Content -LiteralPath (Join-Path $attemptDirectory 'launch.txt') -Encoding UTF8
        $initialIds = @(Get-StartupProcessIds)
        Start-Sleep -Seconds $SettleSeconds
        $finalIds = @(Get-StartupProcessIds)
        $result.ProcessStable = $initialIds.Count -eq 1 -and $finalIds.Count -eq 1 -and $initialIds[0] -eq $finalIds[0]
        $result.InitialProcessIds = $initialIds
        $result.FinalProcessIds = $finalIds

        $activity = Invoke-StartupAdb -Arguments @('shell', 'dumpsys', 'activity', 'activities')
        $foreground = @($activity.Lines | Where-Object { $_ -match '\btopResumedActivity\b' })
        if ($foreground.Count -eq 0) {
            $foreground = @($activity.Lines | Where-Object { $_ -match '\bm?ResumedActivity\b' })
        }
        $foreground | Set-Content -LiteralPath (Join-Path $attemptDirectory 'foreground.txt') -Encoding UTF8
        $result.ForegroundMatches = $activity.ExitCode -eq 0 -and $foreground.Count -gt 0 -and
            [bool]($foreground | Where-Object { $_ -match ('\b' + [regex]::Escape($packageName) + '/') })

        $events = Invoke-StartupAdb -Arguments @('logcat', '-d', '-b', 'events', '-v', 'threadtime', '-T', $startTime)
        $packageEvents = @($events.Lines | Where-Object { $_ -match [regex]::Escape($packageName) })
        $packageEvents | Set-Content -LiteralPath (Join-Path $attemptDirectory 'process-events.txt') -Encoding UTF8
        $eventIds = @($packageEvents | ForEach-Object {
            if ($_ -match ('am_proc_start:\s*\[\d+,(\d+),\d+,' + [regex]::Escape($packageName) + '(?:,|\])')) { $Matches[1] }
        })
        $observedIds = @(@($initialIds) + @($finalIds) + @($eventIds) | Select-Object -Unique)
        $logReadsSucceeded = $events.ExitCode -eq 0 -and $observedIds.Count -gt 0
        foreach ($processId in $observedIds) {
            $logs = Invoke-StartupAdb -Arguments @('logcat', '-d', '-b', 'all', '-v', 'threadtime', ('--pid=' + $processId), '-T', $startTime)
            $logs.Lines | Set-Content -LiteralPath (Join-Path $attemptDirectory ('pid-' + $processId + '.txt')) -Encoding UTF8
            $logReadsSucceeded = $logReadsSucceeded -and $logs.ExitCode -eq 0
            $fatal = @($logs.Lines | Where-Object { $_ -match '(?i)\bFATAL EXCEPTION\b|\bUnhandled(?: managed)? exception\b|\bFatal signal\s+\d+\b|\bSIG(?:ABRT|SEGV|BUS|ILL)\b' })
            $result.FatalLogCount += $fatal.Count
        }
        $result.LogReadsSucceeded = $logReadsSucceeded
        $result.Passed = $launch.ExitCode -eq 0 -and $result.ProcessStable -and $result.ForegroundMatches -and
            $logReadsSucceeded -and $result.FatalLogCount -eq 0
    } catch {
        $result.Error = $_.Exception.Message.Replace($Serial, '<device>')
    }
    $results += [PSCustomObject]$result
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $attemptDirectory 'result.json') -Encoding UTF8
    Write-Output ('Cold start {0}: {1}; process={2}, foreground={3}, fatal={4}' -f $attempt,
        $(if ($result.Passed) { 'PASS' } else { 'FAIL' }), $result.ProcessStable, $result.ForegroundMatches, $result.FatalLogCount)
}

$summary = [ordered]@{ Package = $packageName; Attempts = $Attempts; SettleSeconds = $SettleSeconds;
    Passed = @($results | Where-Object { !$_.Passed }).Count -eq 0; Results = $results }
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.json') -Encoding UTF8
Write-Output ('Startup logs: ' + $runDirectory)
if (!$summary.Passed) {
    throw 'Android startup verification failed; inspect the per-attempt process and logcat evidence.'
}
