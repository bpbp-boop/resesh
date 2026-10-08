param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "ARM64" } else { "x64" })
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\App\Resesh.App.csproj"

# The terminal's native libraries are a build input; build them once per architecture.
$arch = $Platform.ToLowerInvariant()
if (-not (Test-Path (Join-Path $PSScriptRoot ".artifacts\ghostty-vt\$arch\reseshvt.dll"))) {
    & (Join-Path $PSScriptRoot "eng\build-ghostty-vt.ps1") -Architecture $arch
}

& winapp run $project --arch $Platform.ToLowerInvariant() -p "Platform=$Platform" -- --demo
exit $LASTEXITCODE
