param(
    [ValidateSet("webview", "native")]
    [string]$Surface = "webview",
    [ValidateRange(1, 20)]
    [int]$Samples = 5,
    [string]$Configuration = "Release",
    [string]$Architecture = "x64"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\App\Resesh.App.csproj"
$results = @()
$previousSurface = $env:RESESH_TERMINAL_SURFACE

try {
    if ($Surface -eq "native") {
        $env:RESESH_TERMINAL_SURFACE = "native"
    } else {
        Remove-Item Env:RESESH_TERMINAL_SURFACE -ErrorAction SilentlyContinue
    }

    for ($sample = 1; $sample -le $Samples; $sample++) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $launchText = & winapp run $project -c $Configuration --arch $Architecture --no-build --detach --json --args "--demo" | Out-String
        if ($LASTEXITCODE -ne 0) { throw "winapp run failed for sample $sample." }
        $launch = $launchText | ConvertFrom-Json
        $process = Get-Process -Id $launch.ProcessId
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $status = & winapp ui status -a $process.Id --json 2>$null
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Milliseconds 25
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($LASTEXITCODE -ne 0) { throw "The app did not expose a window for sample $sample." }

        & winapp ui wait-for "QuickConnectBox" -a $process.Id -t 10000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "The app chrome did not become ready for sample $sample." }
        & winapp ui focus "QuickConnectBox" -a $process.Id | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not focus the app for sample $sample." }
        & winapp ui send-keys "ctrl+shift+t" -a $process.Id --via send-input | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not open the local terminal for sample $sample." }
        & winapp ui wait-for "RecordButton" -a $process.Id -t 10000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "The local terminal did not become ready for sample $sample." }
        Start-Sleep -Milliseconds 500

        $watch.Stop()
        $process.Refresh()
        $results += [pscustomobject]@{
            startupMilliseconds = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
            privateMemoryMiB = [Math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        }

        $process.Kill()
        if (-not $process.WaitForExit(5000)) {
            throw "The baseline app did not stop after sample $sample."
        }
    }
} finally {
    if ($null -eq $previousSurface) {
        Remove-Item Env:RESESH_TERMINAL_SURFACE -ErrorAction SilentlyContinue
    } else {
        $env:RESESH_TERMINAL_SURFACE = $previousSurface
    }
}

$startup = @($results.startupMilliseconds | Sort-Object)
$memory = @($results.privateMemoryMiB | Sort-Object)
$p95Index = [Math]::Min($Samples - 1, [Math]::Ceiling($Samples * 0.95) - 1)
[pscustomobject]@{
    schemaVersion = 1
    surface = $Surface
    architecture = $Architecture
    configuration = $Configuration
    measuredAtUtc = [DateTime]::UtcNow.ToString("o")
    samples = $results
    summary = [pscustomobject]@{
        startupMedianMilliseconds = $startup[[Math]::Floor(($Samples - 1) / 2)]
        startupP95Milliseconds = $startup[$p95Index]
        privateMemoryMedianMiB = $memory[[Math]::Floor(($Samples - 1) / 2)]
        privateMemoryP95MiB = $memory[$p95Index]
    }
} | ConvertTo-Json -Depth 5
