#requires -Version 5.1

[CmdletBinding()]
param([Parameter(Mandatory)][string] $UnityEditor)

$ErrorActionPreference = 'Stop'
$revision = '7b7addc468f41786e2ec8672406238befeb8f86d'
$patch = Join-Path $PSScriptRoot 'MajViewEmbedded.patch'
$patchHash = (Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.work'))
$source = Join-Path $workspace "majview-source-$patchHash"
$output = Join-Path $workspace 'majview-embedded/Build'
$runtime = Join-Path $workspace 'majview-runtime-custom'
$log = Join-Path $workspace 'majview-webgl-build.log'
$UnityEditor = (Resolve-Path -LiteralPath $UnityEditor).Path
$platform = Join-Path (Split-Path -Parent $UnityEditor) 'Data/PlaybackEngines/WebGLSupport'
if (!(Test-Path -LiteralPath $platform -PathType Container)) {
    throw 'Unity 6000.3.17f1 with WebGL Build Support is required.'
}

New-Item -ItemType Directory -Path $workspace -Force | Out-Null

if (!(Test-Path -LiteralPath $source)) {
    git clone --no-checkout --filter=blob:none https://github.com/TeamMajdata/MajdataView_web.git $source
    if ($LASTEXITCODE -ne 0) { throw 'Could not clone MajdataView_web.' }

    git -C $source checkout --detach $revision
    if ($LASTEXITCODE -ne 0) { throw "Could not check out MajdataView_web $revision." }
}

$head = git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -ne $revision) {
    throw "Expected MajdataView_web $revision in $source."
}

if (Test-Path -LiteralPath (Join-Path $source 'Assets/Scripts/UI/EmbeddedViewport.cs')) {
    git -C $source apply --reverse --check $patch
    if ($LASTEXITCODE -ne 0) { throw 'The MajMusic layout patch has changed. Use a fresh source checkout.' }
} else {
    git -C $source apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'Could not apply the MajMusic layout patch.' }
}

$buildArguments = @(
    '-batchmode', '-nographics', '-quit', '-buildTarget', 'WebGL',
    '-projectPath', "`"$source`"", '-executeMethod', 'MajMusicWebBuild.Build',
    '-majViewOutput', "`"$output`"", '-logFile', "`"$log`""
)
$process = Start-Process -FilePath $UnityEditor -ArgumentList $buildArguments -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Unity WebGL build failed. See $log." }

New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$engineFiles = @('Build.loader.js', 'Build.framework.js', 'Build.data', 'Build.wasm')
$files = foreach ($name in $engineFiles) {
    $builtPath = Join-Path $output "Build/$name"
    $info = Get-Item -LiteralPath $builtPath
    $digest = Get-FileHash -LiteralPath $builtPath -Algorithm SHA256
    Copy-Item -LiteralPath $builtPath -Destination (Join-Path $runtime $name) -Force
    @{ name = $name; length = $info.Length; sha256 = $digest.Hash }
}

$manifest = @{
    revision = "$revision-$patchHash"
    embeddedViewport = $true
    files = $files
} | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText((Join-Path $runtime 'runtime.json'), $manifest)

Write-Output $runtime
