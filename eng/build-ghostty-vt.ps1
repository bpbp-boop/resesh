# Builds the libghostty-vt terminal surface's native DLLs from the commit pinned in
# eng/ghostty-vt.json: ghostty-vt.dll (upstream, unpatched) and reseshvt.dll (our shim in
# src/Terminal/Native/Shim). Output: .artifacts/ghostty-vt/<arch>/, which the Terminal
# project links into the app output when present.
#
# -BuildRoot holds Zig, the Ghostty checkout and Zig caches (~2 GB); point it at a fast drive.
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',
    [string]$BuildRoot = $(if ($env:RESESH_GHOSTTY_BUILD_ROOT) { $env:RESESH_GHOSTTY_BUILD_ROOT } else { Join-Path $PSScriptRoot '..\.artifacts\ghostty-build' })
)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$pin = Get-Content (Join-Path $PSScriptRoot 'ghostty-vt.json') -Raw | ConvertFrom-Json
$zigVersion = $pin.zig.version
$target = if ($Architecture -eq 'x64') { 'x86_64-windows-msvc' } else { 'aarch64-windows-msvc' }

New-Item -ItemType Directory -Force $BuildRoot | Out-Null
$BuildRoot = Resolve-Path $BuildRoot

# Zig runs on the build machine, which is x64 here regardless of the target architecture.
$zigDir = Join-Path $BuildRoot "zig-x86_64-windows-$zigVersion"
$zig = Join-Path $zigDir 'zig.exe'
if (-not (Test-Path $zig)) {
    $zip = Join-Path $BuildRoot 'zig.zip'
    Invoke-WebRequest "https://ziglang.org/download/$zigVersion/zig-x86_64-windows-$zigVersion.zip" -OutFile $zip
    $expected = $pin.zig.sha256.'x86_64-windows'
    if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower() -ne $expected) { throw 'Zig archive hash mismatch' }
    Expand-Archive $zip -DestinationPath $BuildRoot
    Remove-Item $zip
}

$src = Join-Path $BuildRoot 'ghostty'
if (-not (Test-Path (Join-Path $src '.git'))) {
    git clone --filter=blob:none $pin.ghostty.repository $src
    if ($LASTEXITCODE -ne 0) { throw 'clone failed' }
}
git -C $src fetch --depth 1 origin $pin.ghostty.commit
git -C $src checkout --detach --force $pin.ghostty.commit
if ($LASTEXITCODE -ne 0) { throw "checkout of $($pin.ghostty.commit) failed" }

$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $BuildRoot 'zig-global-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $BuildRoot 'zig-local-cache'
$prefix = Join-Path $BuildRoot "vt-$Architecture"
Push-Location $src
try {
    & $zig build -Demit-lib-vt -Doptimize=ReleaseFast "-Dtarget=$target" --prefix $prefix
    if ($LASTEXITCODE -ne 0) { throw 'libghostty-vt build failed' }
} finally { Pop-Location }

$out = Join-Path $repo ".artifacts\ghostty-vt\$Architecture"
New-Item -ItemType Directory -Force $out | Out-Null
& $zig cc -shared -O2 -Wall -Werror -target $target -I (Join-Path $prefix 'include') `
    (Join-Path $repo 'src\Terminal\Native\Shim\reseshvt.c') (Join-Path $prefix 'lib\ghostty-vt.lib') -lkernel32 `
    -o (Join-Path $out 'reseshvt.dll')
if ($LASTEXITCODE -ne 0) { throw 'reseshvt build failed' }
Copy-Item (Join-Path $prefix 'bin\ghostty-vt.dll') $out -Force
Copy-Item (Join-Path $src 'LICENSE') (Join-Path $out 'LICENSE.ghostty.txt') -Force
Remove-Item (Join-Path $out '*.pdb'), (Join-Path $out '*.lib') -ErrorAction SilentlyContinue
Write-Host "libghostty-vt $($pin.ghostty.commit.Substring(0, 8)) for $Architecture -> $out"
