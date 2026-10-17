Set-StrictMode -Version Latest

function Get-ReleaseInfo {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$Version)
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Invalid release version: '$Version'" }
    $lines = @(Get-Content -LiteralPath (Join-Path $RepositoryRoot 'CHANGELOG.md') -Encoding utf8)
    $indices = @(for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match ('^## ' + [regex]::Escape($Version) + '(\s|$)')) { $i }
    })
    if ($indices.Count -ne 1) { throw "Expected one CHANGELOG.md section for $Version; found $($indices.Count)." }
    $start = $indices[0]
    if ($lines[$start] -notmatch '^## \S+ — (\d{4}-\d{2}-\d{2})$') { throw 'Release heading must include an ISO date.' }
    $date = $Matches[1]
    $null = [datetime]::ParseExact($date, 'yyyy-MM-dd', [cultureinfo]::InvariantCulture)
    $end = $start + 1
    while ($end -lt $lines.Count -and $lines[$end] -notmatch '^## ') { $end++ }
    $notes = if ($end -gt $start + 1) { ($lines[($start + 1)..($end - 1)] -join "`n").Trim() } else { '' }
    if (-not $notes) { throw "CHANGELOG.md section for $Version is empty." }
    [pscustomobject]@{ Version = $Version; Date = $date; Notes = $notes }
}

function Get-ReleaseReadme {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$Version)
    $info = Get-ReleaseInfo -RepositoryRoot $RepositoryRoot -Version $Version
    $readme = [IO.File]::ReadAllText((Join-Path $RepositoryRoot 'README.md')).Replace("`r`n", "`n")
    $startMarker = '<!-- release-summary:start -->'
    $endMarker = '<!-- release-summary:end -->'
    if ([regex]::Matches($readme, [regex]::Escape($startMarker)).Count -ne 1 -or
        [regex]::Matches($readme, [regex]::Escape($endMarker)).Count -ne 1) {
        throw 'README.md must have exactly one pair of release-summary markers.'
    }
    $start = $readme.IndexOf($startMarker, [StringComparison]::Ordinal)
    $end = $readme.IndexOf($endMarker, [StringComparison]::Ordinal)
    if ($end -lt $start) { throw 'README.md release-summary markers are out of order.' }
    $releaseUrl = "https://github.com/ianrastall/media-workbench/releases/tag/v$Version"
    $downloadUrl = "https://github.com/ianrastall/media-workbench/releases/download/v$Version"
    $asset = "MediaWorkbench-$Version-win-x64-setup.exe"
    $channel = if ($Version.Contains('-')) { 'Pre-release' } else { 'Release' }
    $block = @"
$startMarker
## Current release

**[$Version]($releaseUrl)** · $($info.Date) · $channel · Windows x64

[Installer]($downloadUrl/$asset) · [SHA-256]($downloadUrl/$asset.sha256) · [Release README]($downloadUrl/MediaWorkbench-$Version-README.md)

$($info.Notes)
$endMarker
"@
    $readme.Substring(0, $start) + $block + $readme.Substring($end + $endMarker.Length)
}

function Get-ReleaseNotes {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$Version)
    $info = Get-ReleaseInfo -RepositoryRoot $RepositoryRoot -Version $Version
    $readme = Get-ReleaseReadme -RepositoryRoot $RepositoryRoot -Version $Version
    $sections = foreach ($heading in @('Install', 'What works now', 'LLM clients', 'License')) {
        $match = [regex]::Match($readme, '(?ms)^## ' + [regex]::Escape($heading) + '\n.*?(?=^## |\z)')
        if (-not $match.Success) { throw "README.md has no '$heading' section for the release." }
        $match.Value.Trim()
    }
    $body = "# Media Workbench $Version`n`nReleased $($info.Date). Built and checked by GitHub Actions for Windows x64.`n`n## Changes`n`n$($info.Notes)`n`n" + ($sections -join "`n`n")
    $asset = "MediaWorkbench-$Version-win-x64-setup.exe"
    $downloadUrl = "https://github.com/ianrastall/media-workbench/releases/download/v$Version"
    $downloads = @"
## Downloads and verification

- [Windows x64 installer]($downloadUrl/$asset)
- [Installer SHA-256]($downloadUrl/$asset.sha256)
- [This release README]($downloadUrl/MediaWorkbench-$Version-README.md)

Download the installer and checksum to the same folder. In PowerShell, compare the hash printed by the following command with the first value in the checksum file before running the installer:

``````powershell
Get-FileHash -Algorithm SHA256 -LiteralPath './$asset'
Get-Content -LiteralPath './$asset.sha256'
``````

The checksum verifies the download against this release; it does not replace code signing. This alpha installer is unsigned.
"@
    $body += "`n`n$downloads`n"
    # Downloaded README files and GitHub release bodies need version-pinned absolute documentation links.
    [regex]::Replace($body, '(\]\()([^\s)#]+)(#[^\s)]+)?(\))', [Text.RegularExpressions.MatchEvaluator]{
        param($link)
        $target = $link.Groups[2].Value
        if ($target -match '^[A-Za-z][A-Za-z0-9+.-]*:' -or $target.StartsWith('/')) { return $link.Value }
        '](' + "https://github.com/ianrastall/media-workbench/blob/v$Version/" + $target + $link.Groups[3].Value + ')'
    })
}

Export-ModuleMember -Function Get-ReleaseInfo, Get-ReleaseReadme, Get-ReleaseNotes
