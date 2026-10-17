# Generate the release body and optional downloadable README from the changelog and public README.
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$OutputPath, [string]$ReadmeOutputPath)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseContent.psm1') -Force
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskNotes = Get-ReleaseNotes -RepositoryRoot $taskRoot -Version $Version
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $taskNotes, [Text.UTF8Encoding]::new($false))
if ($ReadmeOutputPath) {
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReadmeOutputPath), $taskNotes, [Text.UTF8Encoding]::new($false))
}
