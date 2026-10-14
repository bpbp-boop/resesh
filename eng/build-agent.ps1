# Builds the remote session agent: our fork of zmx at the commit pinned in
# eng/agent/agent.json, against the Ghostty commit pinned in eng/ghostty-vt.json with
# eng/agent/ghostty-vt-formatter.patch applied.
# Output: .artifacts/agent/<arch>/resesh-agent, a static musl binary for Linux, stripped,
# and .artifacts/agent/agent-manifest.json, which the app reads (AgentPackage).
#
# To change the agent, commit to the fork's branch and move the commit in agent.json.
# The agent's version is zmx's with "-resesh.<revision>" added. Raise the revision
# whenever what is built changes: the version is in the path the agent is installed to,
# so hosts keep the old binary until the number moves.
#
# -Tests also builds the unit tests as resesh-agent-tests, to copy to a Linux host and run.
#
# Zig comes from the build root of build-ghostty-vt.ps1; run that script first. The
# Ghostty checkout made here is separate from that script's, which stays unpatched.
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
$ghosttyPin = (Get-Content (Join-Path $PSScriptRoot 'ghostty-vt.json') -Raw | ConvertFrom-Json)
$version = "$($pin.zmx.version)-resesh.$($pin.revision)"
$ghosttyVersion = "ghostty-$($ghosttyPin.ghostty.version)-$($ghosttyPin.ghostty.commit.Substring(0, 8))"
$zig = Join-Path (Resolve-Path $ZigRoot) "zig-x86_64-windows-$($ghosttyPin.zig.version)\zig.exe"
if (-not (Test-Path $zig)) { throw "Zig $($ghosttyPin.zig.version) not found under $ZigRoot; run build-ghostty-vt.ps1 first" }

New-Item -ItemType Directory -Force $BuildRoot | Out-Null
$BuildRoot = Resolve-Path $BuildRoot

# Puts a checkout at the pinned commit, with the patch (if any) as its only change. The
# two sit side by side: the agent's build.zig.zon names Ghostty as ../ghostty.
function Sync-Source($name, $source, $patch) {
    $dir = Join-Path $BuildRoot $name
    if (-not (Test-Path (Join-Path $dir '.git'))) {
        git clone --filter=blob:none --no-checkout $source.repository $dir
        if ($LASTEXITCODE -ne 0) { throw "clone of $name failed" }
    }
    git -C $dir remote set-url origin $source.repository
    git -C $dir cat-file -e "$($source.commit)^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { git -C $dir fetch --depth 1 origin $source.commit }
    git -C $dir checkout --detach --force $source.commit
    if ($LASTEXITCODE -ne 0) { throw "checkout of $name $($source.commit) failed" }
    git -C $dir clean -fdq
    if ($patch) {
        git -C $dir apply --whitespace=nowarn (Join-Path $PSScriptRoot "agent\$patch")
        if ($LASTEXITCODE -ne 0) { throw "$patch does not apply to $name $($source.commit)" }
    }
    $dir
}

$ghostty = Sync-Source 'ghostty' $ghosttyPin.ghostty 'ghostty-vt-formatter.patch'
$zmx = Sync-Source 'zmx' $pin.zmx $null

$env:ZIG_GLOBAL_CACHE_DIR = Join-Path (Resolve-Path $ZigRoot) 'zig-global-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $BuildRoot 'zig-local-cache'
$binaries = [ordered]@{}
foreach ($arch in $Architecture) {
    $prefix = Join-Path $BuildRoot "out-$arch"
    $options = @("-Dtarget=$arch-linux-musl", "-Dversion=$version", "-Dghostty-version=$ghosttyVersion", '--prefix', $prefix)
    Push-Location $zmx
    try {
        & $zig build -Doptimize=ReleaseSmall @options
        if ($LASTEXITCODE -ne 0) { throw "agent build for $arch failed" }
        if ($Tests) {
            & $zig build test-bin @options
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
    $binaries[$arch] = [ordered]@{ file = "$arch/resesh-agent"; sha256 = $hash; size = $binary.Length }
    Write-Host "agent $version for $arch -> $out, $($binary.Length) bytes, sha256 $hash"
}

# It lists the architectures built by this run only, so build them all before packaging
# the app.
$manifest = [ordered]@{ version = $version; binaries = $binaries } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $repo '.artifacts\agent\agent-manifest.json'), $manifest + "`n")
