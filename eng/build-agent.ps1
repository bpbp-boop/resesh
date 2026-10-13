# Builds the remote session agent: zmx at the commit pinned in eng/agent/agent.json with
# eng/agent/zmx.patch applied, against the Ghostty commit zmx pins with
# eng/agent/ghostty-vt-formatter.patch applied. Output: .artifacts/agent/<arch>/resesh-agent,
# a static musl binary for Linux, stripped. Its version is zmx's with "-resesh.<revision>"
# added; raise the revision in agent.json whenever a patch changes what is built.
#
# -Tests also builds zmx's unit tests as resesh-agent-tests, to copy to a Linux host and run.
#
# Ghostty is checked out with git and used as a local dependency, because Zig cannot
# unpack the Ghostty package on Windows without the right to create symlinks.
#
# Zig comes from the -BuildRoot of build-ghostty-vt.ps1; run that script first.
param(
    [ValidateSet('x86_64', 'aarch64')]
    [string[]]$Architecture = @('x86_64', 'aarch64'),
    [switch]$Tests,
    [string]$BuildRoot = $(Join-Path $PSScriptRoot '..\.artifacts\agent-build'),
    [string]$ZigRoot = $(if ($env:RESESH_GHOSTTY_BUILD_ROOT) { $env:RESESH_GHOSTTY_BUILD_ROOT } else { Join-Path $PSScriptRoot '..\.artifacts\ghostty-build' })
)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$pin = Get-Content (Join-Path $PSScriptRoot 'agent\agent.json') -Raw | ConvertFrom-Json
$version = "$($pin.zmx.version)-resesh.$($pin.revision)"
$zigVersion =(Get-Content (Join-Path $PSScriptRoot 'ghostty-vt.json') -Raw | ConvertFrom-Json).zig.version
$zig = Join-Path (Resolve-Path $ZigRoot) "zig-x86_64-windows-$zigVersion\zig.exe"
if (-not (Test-Path $zig)) { throw "Zig $zigVersion not found under $ZigRoot; run build-ghostty-vt.ps1 first" }

New-Item -ItemType Directory -Force $BuildRoot | Out-Null
$BuildRoot = Resolve-Path $BuildRoot

# Puts a checkout at the pinned commit with our patch as its only change.
function Sync-Source($name, $source, $patch) {
    $dir = Join-Path $BuildRoot $name
    if (-not (Test-Path (Join-Path $dir '.git'))) {
        git clone --filter=blob:none --no-checkout $source.repository $dir
        if ($LASTEXITCODE -ne 0) { throw "clone of $name failed" }
    }
    git -C $dir cat-file -e "$($source.commit)^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { git -C $dir fetch --depth 1 origin $source.commit }
    git -C $dir checkout --detach --force $source.commit
    if ($LASTEXITCODE -ne 0) { throw "checkout of $name $($source.commit) failed" }
    git -C $dir clean -fdq
    git -C $dir apply --whitespace=nowarn (Join-Path $PSScriptRoot "agent\$patch")
    if ($LASTEXITCODE -ne 0) { throw "$patch does not apply to $name $($source.commit)" }
    $dir
}

$ghostty = Sync-Source 'ghostty' $pin.ghostty 'ghostty-vt-formatter.patch'
$zmx = Sync-Source 'zmx' $pin.zmx 'zmx.patch'

$env:ZIG_GLOBAL_CACHE_DIR = Join-Path (Resolve-Path $ZigRoot) 'zig-global-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $BuildRoot 'zig-local-cache'
foreach ($arch in $Architecture) {
    $prefix = Join-Path $BuildRoot "out-$arch"
    Push-Location $zmx
    try {
        & $zig build "-Dtarget=$arch-linux-musl" -Doptimize=ReleaseSmall "-Dversion=$version" --prefix $prefix
        if ($LASTEXITCODE -ne 0) { throw "agent build for $arch failed" }
        if ($Tests) {
            & $zig build test-bin "-Dtarget=$arch-linux-musl" "-Dversion=$version" --prefix $prefix
            if ($LASTEXITCODE -ne 0) { throw "agent test build for $arch failed" }
        }
    } finally { Pop-Location }

    $out = Join-Path $repo ".artifacts\agent\$arch"
    New-Item -ItemType Directory -Force $out | Out-Null
    Copy-Item (Join-Path $prefix 'bin\zmx') (Join-Path $out 'resesh-agent') -Force
    if ($Tests) { Copy-Item (Join-Path $prefix 'bin\test') (Join-Path $out 'resesh-agent-tests') -Force }
    Copy-Item (Join-Path $zmx 'LICENSE') (Join-Path $out 'LICENSE.zmx.txt') -Force
    Copy-Item (Join-Path $ghostty 'LICENSE') (Join-Path $out 'LICENSE.ghostty.txt') -Force
    $binary = Get-Item (Join-Path $out 'resesh-agent')
    $hash = (Get-FileHash $binary -Algorithm SHA256).Hash.ToLower()
    Write-Host "agent $version for $arch -> $out, $($binary.Length) bytes, sha256 $hash"
}
