# Regression checks for public release content; no GitHub/network or publishing side effects.
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseContent.psm1') -Force
$taskFixture = Join-Path ([IO.Path]::GetTempPath()) ('MediaWorkbench-release-' + [guid]::NewGuid().ToString('N'))
$taskVersion = '1.2.3-alpha.4'
$taskChangelog = "# Changelog`n`n## Unreleased`n`n## $taskVersion — 2026-10-17`n`n- A shipped feature.`n`n## 1.2.2 — 2026-10-16`n`n- An older feature.`n"
$taskReadme = @'
# Fixture

Handwritten introduction.
<!-- release-summary:start -->
Old release content.
<!-- release-summary:end -->

## Install

Windows x64. [Setup](https://example.com/install) and [details](docs/setup.md#steps).

## What works now

- Local images. [Anchor](#fixture).

## LLM clients

Local MCP. [Permissions](docs/llm-tools.md).

## Development

Developer-only text must not appear in release notes.

## License

[MIT](LICENSE).
'@
function Assert-Release([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Release-content check failed: $Message" }
}
function Assert-ReleaseThrows([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { $null = & $Action } catch { $failed = $true }
    Assert-Release $failed $Message
}
try {
    $null = New-Item -ItemType Directory -Path $taskFixture
    $taskChangelogPath = Join-Path $taskFixture 'CHANGELOG.md'
    $taskReadmePath = Join-Path $taskFixture 'README.md'
    [IO.File]::WriteAllText($taskChangelogPath, $taskChangelog)
    [IO.File]::WriteAllText($taskReadmePath, $taskReadme)
    $taskInfo = Get-ReleaseInfo $taskFixture $taskVersion
    Assert-Release ($taskInfo.Date -eq '2026-10-17' -and $taskInfo.Notes -eq '- A shipped feature.') 'select only the matching dated section'
    $taskGenerated = Get-ReleaseReadme $taskFixture $taskVersion
    Assert-Release ($taskGenerated.Contains('Handwritten introduction.') -and $taskGenerated.EndsWith('[MIT](LICENSE).')) 'preserve surrounding hand-authored content'
    Assert-Release ($taskGenerated.Contains('Pre-release') -and $taskGenerated.Contains("/releases/download/v$taskVersion/MediaWorkbench-$taskVersion-README.md")) 'include matching version, channel, and assets'
    Assert-Release (-not $taskGenerated.Contains('Old release content.') -and -not $taskGenerated.Contains('An older feature.')) 'replace only the managed release block'
    [IO.File]::WriteAllText($taskReadmePath, $taskGenerated.Replace("`n", "`r`n"))
    Assert-Release ((Get-ReleaseReadme $taskFixture $taskVersion) -ceq $taskGenerated) 'idempotent across Windows line endings'
    $taskNotes = Get-ReleaseNotes $taskFixture $taskVersion
    Assert-Release ($taskNotes.Contains("https://github.com/ianrastall/media-workbench/blob/v$taskVersion/docs/setup.md#steps")) 'pin documentation links to the source release'
    Assert-Release ($taskNotes.Contains('[Setup](https://example.com/install)') -and $taskNotes.Contains('[Anchor](#fixture)')) 'preserve absolute links and anchors'
    Assert-Release ($taskNotes.Contains('## Install') -and $taskNotes.Contains('## LLM clients') -and $taskNotes.Contains('Get-FileHash') -and $taskNotes.Contains("./MediaWorkbench-$taskVersion-win-x64-setup.exe.sha256")) 'include installation, permissions, and concrete checksum guidance'
    Assert-Release (-not $taskNotes.Contains('Developer-only text')) 'exclude development instructions'
    Assert-ReleaseThrows { Get-ReleaseInfo $taskFixture '9.9.9' } 'reject missing release'
    Assert-ReleaseThrows { Get-ReleaseInfo $taskFixture '../escape' } 'reject invalid version'
    [IO.File]::WriteAllText($taskChangelogPath, "$taskChangelog`n## $taskVersion — 2026-10-17`nDuplicate")
    Assert-ReleaseThrows { Get-ReleaseInfo $taskFixture $taskVersion } 'reject duplicate release'
    [IO.File]::WriteAllText($taskChangelogPath, "## $taskVersion — 2026-10-17`n`n## 1.0.0 — 2026-10-16`nOld")
    Assert-ReleaseThrows { Get-ReleaseInfo $taskFixture $taskVersion } 'reject empty section instead of using next heading'
    [IO.File]::WriteAllText($taskChangelogPath, $taskChangelog.Replace('2026-10-17', '2026-02-31'))
    Assert-ReleaseThrows { Get-ReleaseInfo $taskFixture $taskVersion } 'reject invalid date'
    [IO.File]::WriteAllText($taskChangelogPath, $taskChangelog)
    [IO.File]::WriteAllText($taskReadmePath, $taskReadme.Replace('<!-- release-summary:start -->', ''))
    Assert-ReleaseThrows { Get-ReleaseReadme $taskFixture $taskVersion } 'reject missing markers'
    [IO.File]::WriteAllText($taskReadmePath, $taskReadme + "`n<!-- release-summary:end -->")
    Assert-ReleaseThrows { Get-ReleaseReadme $taskFixture $taskVersion } 'reject duplicate markers'
    [IO.File]::WriteAllText($taskReadmePath, $taskReadme.Replace('## Install', '## Other'))
    Assert-ReleaseThrows { Get-ReleaseNotes $taskFixture $taskVersion } 'reject missing installation guidance'
    Write-Output 'Release-content regression checks passed.'
} finally {
    $taskTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($taskFixture).StartsWith($taskTemporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped temporary directory.' }
    if (Test-Path -LiteralPath $taskFixture) { Remove-Item -LiteralPath $taskFixture -Recurse -Force }
}
