param(
    [string]$ManifestPath = ''
)

$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$propsPath = Join-Path $workspace 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath
$version = [string]$props.Project.PropertyGroup.SnapAnchorVersion
$minimumBuild = [int]$props.Project.PropertyGroup.SnapAnchorMinimumWindowsBuild

if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props contains an invalid SnapAnchorVersion: '$version'."
}
if ($minimumBuild -lt 17763) {
    throw "SnapAnchorMinimumWindowsBuild must be Windows 10 version 1809 or later."
}

foreach ($project in @(
    (Join-Path $workspace 'SnapAnchor.csproj'),
    (Join-Path $workspace 'packaging\SnapAnchor.Setup\SnapAnchor.Setup.csproj')
)) {
    $projectText = Get-Content -LiteralPath $project -Raw
    if ($projectText -match '<(?:Version|AssemblyVersion|FileVersion)>') {
        throw "$project hard-codes a version instead of inheriting Directory.Build.props."
    }
    $reported = (& dotnet msbuild $project -nologo -getProperty:Version | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $reported -ne $version) {
        throw "$project reports version '$reported'; expected '$version'."
    }
}

$changeLog = Get-Content -LiteralPath (Join-Path $workspace 'CHANGELOG.md') -Raw
if ($changeLog -notmatch "(?m)^## \[$([regex]::Escape($version))\]") {
    throw "CHANGELOG.md has no section for $version."
}

$readme = Get-Content -LiteralPath (Join-Path $workspace 'README.md') -Raw
if ($readme -notmatch 'shields\.io/github/v/release/coolman1232004/SnapAnchor') {
    throw 'README.md must use the live GitHub release badge instead of a hard-coded version.'
}

if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    $resolvedManifestPath = [System.IO.Path]::GetFullPath((Join-Path $workspace $ManifestPath))
    if (-not (Test-Path -LiteralPath $resolvedManifestPath)) {
        throw "Release manifest not found: $resolvedManifestPath"
    }
    $manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -ne $version) {
        throw "release.json version '$($manifest.version)' does not match '$version'."
    }
    if ($manifest.minimumWindowsBuild -ne $minimumBuild) {
        throw 'release.json minimumWindowsBuild does not match Directory.Build.props.'
    }
    foreach ($hash in @($manifest.portableSha256, $manifest.installerSha256)) {
        if ($hash -notmatch '^[0-9A-Fa-f]{64}$') {
            throw 'release.json contains an invalid SHA-256 checksum.'
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.releaseNotes) -or
        [string]$manifest.releaseNotes -match '\b2\.1\.21\b') {
        throw 'release.json contains missing or stale release notes.'
    }
}

Write-Host "RELEASE METADATA: version $version and Windows build $minimumBuild are centralized and aligned"
