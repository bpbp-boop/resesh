param(
    [ValidateSet("x64", "ARM64", "All")]
    [string]$Architecture = "All",
    [string]$ForkPath = "",
    [string]$VcpkgRoot = $env:VCPKG_ROOT,
    [string]$ArtifactRoot = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manifestPath = Join-Path $PSScriptRoot "native-terminal.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if (-not $ArtifactRoot) { $ArtifactRoot = Join-Path $repoRoot ".artifacts\native-terminal" }
if (-not $ForkPath) { $ForkPath = Join-Path $repoRoot ".artifacts\terminal-source" }

if (-not (Test-Path (Join-Path $ForkPath ".git"))) {
    New-Item -ItemType Directory -Force -Path (Split-Path $ForkPath) | Out-Null
    & git clone --filter=blob:none $manifest.fork.repository $ForkPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& git -C $ForkPath fetch origin $manifest.fork.commit --no-tags
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& git -C $ForkPath checkout --detach $manifest.fork.commit
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$actualCommit = (& git -C $ForkPath rev-parse HEAD).Trim()
if ($actualCommit -ne $manifest.fork.commit) {
    throw "Terminal fork commit $actualCommit does not match $($manifest.fork.commit)."
}

if (-not $VcpkgRoot) {
    throw "VCPKG_ROOT must point to vcpkg tool commit $($manifest.toolchain.vcpkgToolCommit)."
}
$vcpkgCommit = (& git -C $VcpkgRoot rev-parse HEAD).Trim()
if ($vcpkgCommit -ne $manifest.toolchain.vcpkgToolCommit) {
    throw "vcpkg commit $vcpkgCommit does not match $($manifest.toolchain.vcpkgToolCommit)."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -version "[17.0,18.0)" -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe |
    Select-Object -First 1
if (-not $msbuild) {
    throw "Visual Studio 2022 with the v143 C++ toolset is required to reproduce native terminal artifacts."
}

& (Join-Path $ForkPath "dep\nuget\nuget.exe") restore (Join-Path $ForkPath "dep\nuget\packages.config") `
    -PackagesDirectory (Join-Path $ForkPath "packages") -NonInteractive
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$architectures = if ($Architecture -eq "All") { @("x64", "ARM64") } else { @($Architecture) }
foreach ($item in $architectures) {
    & $msbuild (Join-Path $ForkPath "OpenConsole.sln") "/t:Terminal\Control\Microsoft_Terminal_Control" `
        /m /p:Configuration=Release "/p:Platform=$item" /p:AppxSymbolPackageEnabled=false `
        "/p:OpenConsoleDir=$ForkPath\" "/p:SolutionDir=$ForkPath\" "/p:VcpkgRoot=$VcpkgRoot"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $architectureName = $item.ToLowerInvariant()
    $source = Join-Path $ForkPath "bin\$item\Release\Microsoft.Terminal.Control"
    $destination = Join-Path $ArtifactRoot $architectureName
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item (Join-Path $source "Microsoft.Terminal.Control.dll") $destination -Force
    Copy-Item (Join-Path $source "Microsoft.Terminal.Control.pdb") $destination -Force
    Copy-Item (Join-Path $ForkPath "LICENSE") $destination -Force

    foreach ($fileName in @("Microsoft.Terminal.Control.dll", "Microsoft.Terminal.Control.pdb")) {
        $expected = $manifest.artifacts.$architectureName.$fileName
        $actual = (Get-FileHash (Join-Path $destination $fileName) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expected -and $actual -ne $expected) {
            throw "$item $fileName hash $actual does not match the manifest hash $expected."
        }
        Write-Host "$item $fileName sha256:$actual"
    }
}
