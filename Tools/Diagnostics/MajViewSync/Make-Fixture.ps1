#requires -Version 7.3
param([Parameter(Mandatory)][string] $Ffmpeg)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$encoder = (Get-Command -Name $Ffmpeg -CommandType Application -ErrorAction Stop).Source
$fixture = Join-Path $PSScriptRoot '../../../.work/majview-sync-fixture'
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
Push-Location -LiteralPath $fixture
try {
    @(
        '&title=MajView sync check'
        '&artist=Generated beat and video clock'
        '&first=0.25'
        '&lv_7=1'
        '&inote_7=(120){4}8/4,7/3,6/2,5/1,Cf,Ch[4:1],1-3[8:1]*-5[8:1],3q7[8:1],E'
    ) | Set-Content -LiteralPath 'maidata.txt' -Encoding utf8NoBOM
    $base = @('-y', '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i')
    & $encoder @base 'aevalsrc=0.035*sin(2*PI*220*t)+if(between(mod(t\,0.5)\,0.25\,0.275)\,0.3*sin(2*PI*1800*t)\,0):s=48000:d=20' -c:a libmp3lame -b:a 192k 'track.mp3'
    & $encoder @base 'smptebars=size=420x420:rate=1' -frames:v 1 -threads 1 'bg.png'
    $fontOption = ''
    if ($IsWindows) {
        Copy-Item -LiteralPath (Join-Path $env:WINDIR 'Fonts/consola.ttf') -Destination 'sync-font.ttf' -Force
        $fontOption = 'fontfile=sync-font.ttf:'
    }
    $timecode = "drawtext=${fontOption}text=%{pts}:fontsize=52:fontcolor=white:box=1:x=(w-tw)/2:y=(h-th)/2"
    & $encoder @base 'testsrc2=size=420x420:rate=30:d=20' -f lavfi -i 'sine=frequency=1400:sample_rate=48000:duration=20' -vf $timecode -c:v libx264 -pix_fmt yuv420p -g 15 -c:a aac -movflags +faststart -shortest 'pv.mp4'
    Write-Output (Get-Location).Path
}
finally {
    Pop-Location
}
