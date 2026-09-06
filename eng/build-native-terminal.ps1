param(
    [ValidateSet("x64", "ARM64", "All")]
    [string]$Architecture = "All",
    [string]$ForkPath = "",
    [string]$VcpkgRoot = $env:VCPKG_ROOT,
    [string]$ArtifactRoot = "",
    [switch]$UpdateManifestHashes
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manifestPath = Join-Path $PSScriptRoot "native-terminal.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if (-not $ArtifactRoot) { $ArtifactRoot = Join-Path $repoRoot ".artifacts\native-terminal" }
if (-not $ForkPath) { $ForkPath = Join-Path $repoRoot ".artifacts\terminal-source" }

$invocationRoot = (Get-Location).Path
function Get-AbsolutePath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $invocationRoot $Path))
}
$ArtifactRoot = Get-AbsolutePath $ArtifactRoot
$ForkPath = Get-AbsolutePath $ForkPath

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

# A later patch can change context that an earlier patch needs for its reverse check.
# Build one temporary index with the full expected patch stack, then compare that index
# with the working tree. This accepts an already-applied stack only when every tracked
# source file has the exact expected content.
$patchStackApplied = $false
$temporaryIndex = [System.IO.Path]::GetTempFileName()
$priorIndex = $env:GIT_INDEX_FILE
try {
    [System.IO.File]::Delete($temporaryIndex)
    $env:GIT_INDEX_FILE = $temporaryIndex
    & git -C $ForkPath read-tree HEAD
    $stackValid = $LASTEXITCODE -eq 0
    foreach ($patch in $manifest.fork.patches) {
        if (-not $stackValid) { break }
        $patchPath = Join-Path $PSScriptRoot $patch.file
        & git -C $ForkPath apply --cached --ignore-space-change $patchPath
        $stackValid = $LASTEXITCODE -eq 0
    }
    if ($stackValid) {
        & git -C $ForkPath diff --quiet --ignore-space-at-eol --ignore-submodules --
        $patchStackApplied = $LASTEXITCODE -eq 0
    }
} finally {
    if ($null -eq $priorIndex) {
        Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
    } else {
        $env:GIT_INDEX_FILE = $priorIndex
    }
    if ([System.IO.File]::Exists($temporaryIndex)) {
        [System.IO.File]::Delete($temporaryIndex)
    }
}

foreach ($patch in $manifest.fork.patches) {
    $patchPath = Join-Path $PSScriptRoot $patch.file
    if (-not (Test-Path $patchPath)) {
        throw "Native terminal patch not found: $patchPath"
    }
    # Git can check the patch out with CRLF even though the repository blob uses LF.
    # Hash normalized UTF-8 text so the pin is stable across Git configurations.
    $patchText = [System.IO.File]::ReadAllText($patchPath)
    $normalizedPatchText = $patchText.Replace("`r`n", "`n").Replace("`r", "`n")
    $patchBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($normalizedPatchText)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $actualPatchHash = ([System.BitConverter]::ToString($sha256.ComputeHash($patchBytes))).Replace("-", "").ToLowerInvariant()
    } finally {
        $sha256.Dispose()
    }
    if ($actualPatchHash -ne $patch.sha256) {
        throw "Native terminal patch hash $actualPatchHash does not match $($patch.sha256)."
    }

    if ($patchStackApplied) {
        continue
    }

    $ErrorActionPreference = "Continue"
    & git -C $ForkPath apply --ignore-space-change --check $patchPath 2>$null
    $forwardExitCode = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($forwardExitCode -eq 0) {
        & git -C $ForkPath apply --ignore-space-change $patchPath
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        continue
    }

    $ErrorActionPreference = "Continue"
    & git -C $ForkPath apply --ignore-space-change --reverse --check $patchPath 2>$null
    $reverseExitCode = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($reverseExitCode -ne 0) {
        throw "Native terminal patch does not apply cleanly: $patchPath"
    }
}

if (-not $VcpkgRoot) {
    throw "VCPKG_ROOT must point to vcpkg tool commit $($manifest.toolchain.vcpkgToolCommit)."
}
$VcpkgRoot = (Resolve-Path $VcpkgRoot).Path
$vcpkgCommit = (& git -C $VcpkgRoot rev-parse HEAD).Trim()
if ($vcpkgCommit -ne $manifest.toolchain.vcpkgToolCommit) {
    throw "vcpkg commit $vcpkgCommit does not match $($manifest.toolchain.vcpkgToolCommit)."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$requiredComponents = @("Microsoft.VisualStudio.Component.VC.Tools.x86.x64")
if ($Architecture -in @("ARM64", "All")) {
    $requiredComponents += "Microsoft.VisualStudio.Component.VC.Tools.ARM64"
    $requiredComponents += "Microsoft.VisualStudio.Component.UWP.VC.ARM64"
}
$msbuild = & $vswhere -latest -products * -requires $requiredComponents -find MSBuild\**\Bin\MSBuild.exe |
    Select-Object -First 1
if (-not $msbuild) {
    throw "Visual Studio with the $($manifest.toolchain.platformToolset) C++ toolset is required to reproduce native terminal artifacts."
}

& (Join-Path $ForkPath "dep\nuget\nuget.exe") restore (Join-Path $ForkPath "dep\nuget\packages.config") `
    -PackagesDirectory (Join-Path $ForkPath "packages") -NonInteractive
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Some solution-wide NuGet packages were unavailable. Continuing because the terminal-control target does not consume the internal test and PGO packages."
}

# VS 2026 reports legacy GSL attributes as C4875; the pinned source predates that diagnostic.
$env:_CL_ = "/wd4875 $env:_CL_".Trim()

$architectures = if ($Architecture -eq "All") { @("x64", "ARM64") } else { @($Architecture) }
foreach ($item in $architectures) {
    & $msbuild (Join-Path $ForkPath "OpenConsole.sln") "/t:Terminal\Control\Microsoft_Terminal_Control" `
        /m /nr:false /p:Configuration=Release "/p:Platform=$item" "/p:PlatformToolset=$($manifest.toolchain.platformToolset)" `
        "/p:WindowsTargetPlatformVersion=$($manifest.toolchain.windowsSdk)" `
        "/p:TargetPlatformVersion=$($manifest.toolchain.windowsSdk)" "/p:AppxBundlePlatforms=$item" `
        /p:AppxSymbolPackageEnabled=false `
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
        if ($UpdateManifestHashes) {
            $manifest.artifacts.$architectureName.$fileName = $actual
        } elseif ($expected -and $actual -ne $expected) {
            throw "$item $fileName hash $actual does not match the manifest hash $expected."
        }
        Write-Host "$item $fileName sha256:$actual"
    }
}

if ($UpdateManifestHashes) {
    $manifestJson = $manifest | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText(
        $manifestPath,
        $manifestJson + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
}
