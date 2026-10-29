# Renderer frame timings in the real app: for each window size and profile, set the window
# placement in the data dir's settings, launch with RESESH_FRAME_STATS pointing at a fresh
# folder, wait until the frame log stops growing, kill the app, then summarize with
# framestats.py. Profiles come from profiles.py (sshflood.py must be listening for SSH ones).
#   pwsh framebench.ps1 -Exe <publish>/Resesh.App.exe -DataDir <data-dir> -Out <dir> `
#       -Profiles 'SSH logs-x4','SSH tui-x4' -Sizes 'small=1280x800','max'
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string[]]$Profiles,
    [string[]]$Sizes = @('small=1280x800', 'max'),
    [int]$Reps = 1,
    [int]$QuietSeconds = 3,
    [int]$TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $DataDir 'settings.json'

function Set-Placement([string]$size) {
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $placement = if ($size -eq 'max') {
        [pscustomobject]@{ x = 100; y = 100; width = 1280; height = 800; isMaximized = $true }
    } else {
        $w, $h = ($size -split '=')[-1] -split 'x'
        [pscustomobject]@{ x = 100; y = 100; width = [int]$w; height = [int]$h; isMaximized = $false }
    }
    $settings | Add-Member -NotePropertyName windowPlacement -NotePropertyValue $placement -Force
    $settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath -Encoding utf8
}

$runs = @()
foreach ($size in $Sizes) {
    $sizeName = ($size -split '=')[0]
    foreach ($profile in $Profiles) {
        for ($r = 0; $r -lt $Reps; $r++) {
            $name = "$sizeName-$($profile -replace '[^\w-]+', '_')-$r"
            $dir = Join-Path $Out $name
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
            Set-Placement $size
            $env:RESESH_FRAME_STATS = $dir
            $proc = Start-Process $Exe -ArgumentList "--data-dir `"$DataDir`" --open `"$profile`"" -PassThru
            $env:RESESH_FRAME_STATS = $null
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $lastSize = -1; $quietSince = $null
            while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
                Start-Sleep -Milliseconds 250
                $file = Get-ChildItem $dir -Filter 'frames-*.csv' -ErrorAction SilentlyContinue | Select-Object -First 1
                $len = if ($file) { $file.Length } else { 0 }
                if ($len -ne $lastSize) { $lastSize = $len; $quietSince = $sw.Elapsed.TotalSeconds; continue }
                if ($len -gt 300 -and $sw.Elapsed.TotalSeconds - $quietSince -ge $QuietSeconds) { break }
            }
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 800
            Write-Host "$name done in $([int]$sw.Elapsed.TotalSeconds) s"
            $runs += $dir
        }
    }
}
python (Join-Path $PSScriptRoot '..\framestats.py') @runs
