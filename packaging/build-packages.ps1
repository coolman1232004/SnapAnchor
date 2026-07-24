param(
    [string]$DistDirectory = 'dist',
    [string]$Version = '2.1.18',
    [string]$CertificateThumbprint = $env:SNAPANCHOR_CERT_THUMBPRINT
)

$ErrorActionPreference = 'Stop'

$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = [System.IO.Path]::GetFullPath((Join-Path $workspace $DistDirectory))
$staging = [System.IO.Path]::GetFullPath((Join-Path $workspace 'packaging\staging'))
$setupProject = Join-Path $workspace 'packaging\SnapAnchor.Setup\SnapAnchor.Setup.csproj'
$payload = Join-Path $workspace 'packaging\SnapAnchor.Setup\Payload.zip'

function Invoke-CodeSign([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { return }
    $signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signTool) { throw 'SNAPANCHOR_CERT_THUMBPRINT was provided, but signtool.exe was not found.' }
    & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /td SHA256 /tr 'http://timestamp.digicert.com' $Path
    if ($LASTEXITCODE -ne 0) { throw "Code signing failed for $Path" }
}

foreach ($target in @($dist, $staging)) {
    if (-not $target.StartsWith($workspace, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a directory outside the workspace: $target"
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $dist, $staging -Force | Out-Null
$portableDirectory = Join-Path $staging 'SnapAnchor-Portable'
$setupDirectory = Join-Path $staging 'Setup'

dotnet publish (Join-Path $workspace 'SnapAnchor.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:Platform=x64 `
    -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" `
    -p:PublishSingleFile=false `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $portableDirectory
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }

# The x64 product does not load the NuGet packages' x86 native payload. Keep
# only English/Chinese satellite resources used by SnapAnchor's supported UI.
$unusedDirectories = @('x86','ar','cs','da','de','el','es','fi','fr','he','hu','it','ja','ko','nb','nl','pl','pt-BR','pt-PT','ro','ru','sk','sv','th','tr','uk','vi')
foreach ($name in $unusedDirectories) {
    $candidate = Join-Path $portableDirectory $name
    if ((Test-Path -LiteralPath $candidate) -and $candidate.StartsWith($portableDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $candidate -Recurse -Force
    }
}
foreach ($diagnostic in @('createdump.exe','mscordaccore.dll','mscordbi.dll')) {
    $candidate = Join-Path $portableDirectory $diagnostic
    if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Force }
}
Get-ChildItem -LiteralPath $portableDirectory -Filter 'mscordaccore*.dll' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
foreach ($runtime in @('osx','linux-arm','linux-arm64','linux-musl-x64','linux-x64','win-arm64','win-x86')) {
    $candidate = Join-Path (Join-Path $portableDirectory 'runtimes') $runtime
    if ((Test-Path -LiteralPath $candidate) -and $candidate.StartsWith($portableDirectory, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $candidate -Recurse -Force
    }
}

Invoke-CodeSign (Join-Path $portableDirectory 'SnapAnchor.exe')

# Portable updates use this managed-file list to remove files that belonged to
# an older SnapAnchor package without touching documents users placed beside it.
$managedFiles = @(
    Get-ChildItem -LiteralPath $portableDirectory -File -Recurse |
        ForEach-Object { $_.FullName.Substring($portableDirectory.Length).TrimStart('\').Replace('\', '/') }
)
$managedFiles += '.snapanchor-package.json'
[ordered]@{
    version = $Version
    files = @($managedFiles | Sort-Object -Unique)
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $portableDirectory '.snapanchor-package.json') -Encoding UTF8

Compress-Archive -Path (Join-Path $portableDirectory '*') -DestinationPath $payload -CompressionLevel Optimal -Force
Copy-Item -LiteralPath $payload -Destination (Join-Path $dist 'SnapAnchor-Portable-win-x64.zip') -Force

dotnet publish $setupProject `
    -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $setupDirectory
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }

Copy-Item -LiteralPath (Join-Path $setupDirectory 'SnapAnchor.Setup.exe') -Destination (Join-Path $dist 'SnapAnchor-Setup-win-x64.exe') -Force
Invoke-CodeSign (Join-Path $dist 'SnapAnchor-Setup-win-x64.exe')
Copy-Item -LiteralPath $portableDirectory -Destination (Join-Path $dist 'SnapAnchor-Portable') -Recurse -Force

$manifest = [ordered]@{
    version = $Version
    publishedUtc = [DateTime]::UtcNow.ToString('O')
    portableFile = 'SnapAnchor-Portable-win-x64.zip'
    portableSha256 = (Get-FileHash (Join-Path $dist 'SnapAnchor-Portable-win-x64.zip') -Algorithm SHA256).Hash
    installerFile = 'SnapAnchor-Setup-win-x64.exe'
    installerSha256 = (Get-FileHash (Join-Path $dist 'SnapAnchor-Setup-win-x64.exe') -Algorithm SHA256).Hash
    portableSize = (Get-Item (Join-Path $dist 'SnapAnchor-Portable-win-x64.zip')).Length
    installerSize = (Get-Item (Join-Path $dist 'SnapAnchor-Setup-win-x64.exe')).Length
    minimumWindowsBuild = 17763
    signed = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
    downloadUrl = 'https://github.com/coolman1232004/SnapAnchor/releases/latest/download/SnapAnchor-Setup-win-x64.exe'
    portableDownloadUrl = 'https://github.com/coolman1232004/SnapAnchor/releases/latest/download/SnapAnchor-Portable-win-x64.zip'
    releaseNotes = 'SnapAnchor 2.1.18 fixes detection lag: instant window cache on capture open, lazy per-window UI elements, geometry-only mouse move. No more 30s freeze or unusable hover.'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'release.json') -Encoding UTF8

Get-ChildItem -LiteralPath $dist -File -Recurse | Select-Object FullName, Length | Format-Table -AutoSize
