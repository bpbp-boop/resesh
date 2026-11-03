# Prints the version a CI build is stamped with: 1.2.3 for a v1.2.3 tag, 0.0.<run number>
# otherwise. Both the build and sign jobs call this, so a release's app, MSI and setup.exe
# agree; the MSI needs a purely numeric version, so anything else fails the build.
#
# Defaults come from the GitHub Actions environment; pass them to try it locally:
#   ./eng/build-version.ps1 -RefType tag -RefName v1.2.3
param(
    [string]$RefType = $env:GITHUB_REF_TYPE,
    [string]$RefName = $env:GITHUB_REF_NAME,
    [string]$RunNumber = $env:GITHUB_RUN_NUMBER
)
$ErrorActionPreference = 'Stop'

$version = if ($RefType -eq 'tag') { $RefName -replace '^v', '' } else { "0.0.$RunNumber" }

if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Release tags must use the form v1.2.3. MSI versions must be numeric."
}

$version
