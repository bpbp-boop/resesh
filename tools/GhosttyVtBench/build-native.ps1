# Builds libghostty-vt (ghostty-vt.dll) and the rvt.dll flattening shim for GhosttyVtBench.
# Everything heavy (Zig, the Ghostty checkout, Zig caches, NuGet, outputs) lives under -Root,
# which should be on a fast drive with a few GB free.
param(
    [string]$Root = 'F:\resesh-spike'
)
$ErrorActionPreference = 'Stop'

$ZigVersion = '0.16.0'
$ZigSha256 = '68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e'
$GhosttyCommit = 'b699ea79f4b881421b4b3055abc16a0957d76beb'

New-Item -ItemType Directory -Force $Root | Out-Null
$zigDir = Join-Path $Root "zig-x86_64-windows-$ZigVersion"
$zig = Join-Path $zigDir 'zig.exe'
if (-not (Test-Path $zig)) {
    $zip = Join-Path $Root 'zig.zip'
    Invoke-WebRequest "https://ziglang.org/download/$ZigVersion/zig-x86_64-windows-$ZigVersion.zip" -OutFile $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower() -ne $ZigSha256) { throw 'Zig archive hash mismatch' }
    Expand-Archive $zip -DestinationPath $Root
    Remove-Item $zip
}

$src = Join-Path $Root 'ghostty'
if (-not (Test-Path $src)) {
    git clone --filter=blob:none https://github.com/ghostty-org/ghostty $src
}
git -C $src fetch --depth 1 origin $GhosttyCommit
git -C $src checkout --detach $GhosttyCommit

$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $Root 'zig-global-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $Root 'zig-local-cache'
$vtOut = Join-Path $Root 'vt-out'
Push-Location $src
try {
    & $zig build -Demit-lib-vt -Doptimize=ReleaseFast -Dtarget=x86_64-windows-msvc --prefix $vtOut
    if ($LASTEXITCODE -ne 0) { throw 'libghostty-vt build failed' }
} finally { Pop-Location }

$nativeOut = Join-Path $Root 'native-out'
New-Item -ItemType Directory -Force $nativeOut | Out-Null
& $zig cc -shared -O2 -target x86_64-windows-msvc -I (Join-Path $vtOut 'include') `
    (Join-Path $PSScriptRoot 'native\rvt.c') (Join-Path $vtOut 'lib\ghostty-vt.lib') -o (Join-Path $nativeOut 'rvt.dll')
if ($LASTEXITCODE -ne 0) { throw 'rvt shim build failed' }
Copy-Item (Join-Path $vtOut 'bin\ghostty-vt.dll') $nativeOut -Force
Write-Host "Native DLLs in $nativeOut"
