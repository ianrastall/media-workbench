# Refresh the managed release summary while preserving the rest of the repository README.
param([switch]$Check)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseContent.psm1') -Force
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskVersion = & (Join-Path $PSScriptRoot 'get-version.ps1')
$taskReadme = Join-Path $taskRoot 'README.md'
$taskExpected = Get-ReleaseReadme -RepositoryRoot $taskRoot -Version $taskVersion
if ($Check) { $null = Get-ReleaseNotes -RepositoryRoot $taskRoot -Version $taskVersion }
$taskActual = [IO.File]::ReadAllText($taskReadme).Replace("`r`n", "`n")
if ($taskActual -cne $taskExpected) {
    if ($Check) { throw 'README release summary is stale. Run ./scripts/update-release-readme.ps1 and commit README.md with the release.' }
    [IO.File]::WriteAllText($taskReadme, $taskExpected, [Text.UTF8Encoding]::new($false))
    Write-Output "Updated README for $taskVersion."
}
