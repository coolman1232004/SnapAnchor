$ErrorActionPreference = 'Stop'
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resourcePaths = @(
    (Join-Path $workspace 'Resources\Strings.resx'),
    (Join-Path $workspace 'Resources\Strings.zh-Hans.resx'),
    (Join-Path $workspace 'Resources\Strings.zh-Hant.resx')
)

$sets = foreach ($path in $resourcePaths) {
    [xml]$document = Get-Content -Raw -LiteralPath $path -Encoding UTF8
    $values = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($item in $document.root.data) { $values[[string]$item.name] = [string]$item.value }
    [pscustomobject]@{ Path = $path; Values = $values }
}

$base = $sets[0].Values
foreach ($set in $sets | Select-Object -Skip 1) {
    $missing = @($base.Keys | Where-Object { -not $set.Values.ContainsKey($_) -or [string]::IsNullOrWhiteSpace($set.Values[$_]) })
    $extra = @($set.Values.Keys | Where-Object { -not $base.ContainsKey($_) })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        throw "Localization resource mismatch in $($set.Path): missing=$($missing -join '; '); extra=$($extra -join '; ')"
    }
}

$used = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$sourceDirectories = @('Controls', 'Models', 'Resources', 'Services', 'Windows') |
    ForEach-Object { Join-Path $workspace $_ }
$xamlFiles = @(
    Get-ChildItem -LiteralPath $workspace -File -Filter *.xaml
    $sourceDirectories | Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Filter *.xaml }
)
$xamlFiles |
    ForEach-Object {
        $text = Get-Content -Raw -LiteralPath $_.FullName -Encoding UTF8
        foreach ($match in [regex]::Matches($text, '(?:Content|Text|Header|ToolTip|Title|AutomationProperties\.Name)="([^"]+)"')) {
            $value = [System.Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
            if ($value -notmatch '^\{' -and $value -match '[A-Za-z]{2}') { [void]$used.Add($value) }
        }
    }
$codeFiles = @(
    Get-ChildItem -LiteralPath $workspace -File -Filter *.cs
    $sourceDirectories | Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Filter *.cs }
)
$codeFiles |
    ForEach-Object {
        $text = Get-Content -Raw -LiteralPath $_.FullName -Encoding UTF8
        foreach ($match in [regex]::Matches($text, '(?:LocalizationService\.(?:Current|Format|Translate)|\bL)\(\s*"((?:\\.|[^"\\])*)"')) {
            [void]$used.Add([regex]::Unescape($match.Groups[1].Value))
        }
    }

$nonLocalizedTechnicalLabels = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($value in @('0 frames', 'Arial', 'Calibri', 'English', 'JPEG', 'Microsoft YaHei UI', 'Segoe UI', 'SNAPANCHOR', 'SnapAnchor', 'WebP')) {
    [void]$nonLocalizedTechnicalLabels.Add($value)
}
$untracked = @($used | Where-Object { -not $base.ContainsKey($_) -and -not $nonLocalizedTechnicalLabels.Contains($_) } | Sort-Object)
if ($untracked.Count -gt 0) { throw "User-facing strings missing from localization resources: $($untracked -join '; ')" }

Write-Host "LOCALIZATION AUDIT: $($base.Count) keys aligned across English, Simplified Chinese and Traditional Chinese; $($used.Count) UI strings checked"
