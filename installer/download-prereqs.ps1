# Downloads the redistributable the resesh setup bundle embeds, from Microsoft's
# evergreen "latest" permalink.
param(
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64"
)

$ErrorActionPreference = "Stop"
$prereqDir = Join-Path $PSScriptRoot "prereqs"
New-Item -ItemType Directory -Force -Path $prereqDir | Out-Null

$downloads = @(
    @{
        Name = "vc_redist.$Arch.exe"
        Url  = "https://aka.ms/vs/17/release/vc_redist.$Arch.exe"
    }
)

foreach ($download in $downloads) {
    $target = Join-Path $prereqDir $download.Name
    Write-Host "Downloading $($download.Name) from $($download.Url)"
    Invoke-WebRequest -Uri $download.Url -OutFile $target -UseBasicParsing

    if ((Get-Item $target).Length -lt 100KB) {
        throw "$($download.Name) is suspiciously small; the download likely failed."
    }
}
