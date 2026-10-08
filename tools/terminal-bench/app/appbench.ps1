# In-app flood benchmark on a Release build.
# For each run: launch the test instance on a flood profile, sum CPU of the app process (and
# any WebView2 children, which builds before 2026-10-08 had) until it goes idle, then record
# wall time, private memory and the producer's CPU (conhost + shell for -Prefix Flood, the
# -ServerPid process for -Prefix SSH).
# Profiles come from profiles.py; SSH runs need sshflood.py listening.
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$DataDir,
    [string]$Label = 'app',   # recorded with each result, e.g. a build or commit name
    [string[]]$Workloads = @('baseline', 'logs', 'color', 'unicode', 'tui', 'tiny'),
    [int]$Reps = 2,
    [string]$Out = 'appbench.json',
    [string]$Prefix = 'Flood',
    [int]$ServerPid = 0   # SSH runs: the real server process (a venv python.exe is a launcher)
)
$ErrorActionPreference = 'Stop'

function Tree([int]$root) {
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    [void]$ids.Add($root)
    do {
        $added = $false
        foreach ($p in $all) {
            if ($ids.Contains([int]$p.ParentProcessId) -and -not $ids.Contains([int]$p.ProcessId) -and
                ($p.Name -eq 'msedgewebview2.exe')) {
                [void]$ids.Add([int]$p.ProcessId); $added = $true
            }
        }
    } while ($added)
    $ids
}

function Sample([int]$root) {
    $cpu = 0.0; $mem = 0L
    foreach ($id in (Tree $root)) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p) { $cpu += $p.TotalProcessorTime.TotalMilliseconds; $mem += $p.PrivateMemorySize64 }
    }
    [pscustomobject]@{ Cpu = $cpu; Mem = $mem }
}

# Local runs: conhost and the shell are the producer. Their Process objects are kept (with an
# open handle) so CPU stays readable after they exit.
function TrackProducers([int]$root, [hashtable]$seen) {
    $all = Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name
    $ids = [System.Collections.Generic.HashSet[int]]::new(); [void]$ids.Add($root)
    do {
        $added = $false
        foreach ($p in $all) {
            if ($ids.Contains([int]$p.ParentProcessId) -and -not $ids.Contains([int]$p.ProcessId) -and $p.Name -ne 'msedgewebview2.exe') {
                [void]$ids.Add([int]$p.ProcessId); $added = $true
                if (-not $seen.ContainsKey([int]$p.ProcessId)) {
                    $gp = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
                    if ($gp) { $null = $gp.Handle; $seen[[int]$p.ProcessId] = $gp }
                }
            }
        }
    } while ($added)
}
function ProducerCpu([hashtable]$seen) {
    $sum = 0.0
    foreach ($gp in $seen.Values) { try { $sum += $gp.TotalProcessorTime.TotalMilliseconds } catch { } }
    $sum
}
function ServerCpu { if ($ServerPid) { (Get-Process -Id $ServerPid).TotalProcessorTime.TotalMilliseconds } else { 0 } }

$results = @()
foreach ($surface in @($Label)) {
    foreach ($wl in $Workloads) {
        $profile = if ($wl -eq 'baseline') { if ($Prefix -eq 'SSH') { 'SSH idle' } else { 'Idle baseline' } } else { "$Prefix $wl" }
        for ($r = 0; $r -lt $Reps; $r++) {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $serverStart = ServerCpu
            $seen = @{}
            $proc = Start-Process $Exe -ArgumentList "--data-dir `"$DataDir`" --open `"$profile`"" -PassThru
            for ($k = 0; $k -lt 6; $k++) { Start-Sleep -Milliseconds 250; TrackProducers $proc.Id $seen }
            $last = Sample $proc.Id
            $lowRun = 0; $idleAt = $null
            while ($sw.Elapsed.TotalSeconds -lt 120) {
                Start-Sleep -Milliseconds 250
                TrackProducers $proc.Id $seen
                $now = Sample $proc.Id
                $delta = $now.Cpu - $last.Cpu
                $last = $now
                # idle: under 5% of one core for 1.5 s
                if ($delta -lt 12.5) { $lowRun++ } else { $lowRun = 0; $idleAt = $null }
                if ($lowRun -eq 1) { $idleAt = $sw.Elapsed.TotalSeconds - 0.25 }
                if ($lowRun -ge 6 -and $sw.Elapsed.TotalSeconds -gt 3) { break }
            }
            $final = Sample $proc.Id
            $producer = (ProducerCpu $seen) + ((ServerCpu) - $serverStart)
            $results += [pscustomobject]@{
                Surface = $surface; Workload = $wl; Rep = $r
                CpuMs = [math]::Round($final.Cpu); WallToIdleS = [math]::Round($idleAt, 2)
                PrivateMB = [math]::Round($final.Mem / 1MB, 1); ProducerCpuMs = [math]::Round($producer)
            }
            Write-Host ("{0,-8} {1,-9} rep{2} cpu={3,7:N0} ms  idle@{4,6:N2} s  mem={5,6:N1} MB  producer={6,7:N0} ms" -f $surface, $wl, $r, $final.Cpu, $idleAt, ($final.Mem / 1MB), $producer)
            try { $proc.CloseMainWindow() | Out-Null; if (-not $proc.WaitForExit(5000)) { Stop-Process -Id $proc.Id -Force } } catch { }
            Start-Sleep -Seconds 2
        }
    }
}
$results | ConvertTo-Json | Set-Content $Out
