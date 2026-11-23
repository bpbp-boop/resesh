param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" })
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\App\Resesh.App.csproj"

& winapp run $project --arch $Platform.ToLowerInvariant() -p "Platform=$Platform" -- --demo
exit $LASTEXITCODE
