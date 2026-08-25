param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" })
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\App\Resesh.App.csproj"

& dotnet build $project "-p:Platform=$Platform" --nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exe = Join-Path $PSScriptRoot "src\App\bin\$Platform\Debug\net8.0-windows10.0.19041.0\Resesh.App.exe"
$process = Start-Process -FilePath $exe -ArgumentList "--demo" -PassThru

while (-not $process.HasExited) {
    $process.Refresh()
    if ($process.MainWindowHandle -ne 0) {
        Write-Host "Resesh demo is ready (PID $($process.Id))."
        break
    }
    Start-Sleep -Milliseconds 100
}

if (-not $process.HasExited) {
    $process.WaitForExit()
}
exit $process.ExitCode
