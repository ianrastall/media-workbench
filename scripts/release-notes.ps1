# Writes the CHANGELOG.md section for a version to a file; fails if the section is missing or empty.
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$taskLines = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md') -Encoding utf8
$taskStart = [Array]::FindIndex($taskLines, [Predicate[string]]{ param($l) $l -match ('^## ' + [regex]::Escape($Version) + '(\s|$)') })
if ($taskStart -lt 0) { throw "CHANGELOG.md has no '## $Version' section." }
$taskEnd = [Array]::FindIndex($taskLines, $taskStart + 1, [Predicate[string]]{ param($l) $l -match '^## ' })
if ($taskEnd -lt 0) { $taskEnd = $taskLines.Count }
$taskBody = ($taskLines[($taskStart + 1)..($taskEnd - 1)] -join "`n").Trim()
if (-not $taskBody) { throw "CHANGELOG.md section for $Version is empty." }
$taskBody += "`n`nDownload the Windows x64 setup file; verify it with the adjacent SHA-256 file. Built and published by GitHub Actions from this tag."
Set-Content -LiteralPath $OutputPath -Value $taskBody -Encoding utf8
