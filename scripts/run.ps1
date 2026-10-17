param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
Push-Location $taskRoot
try {
    dotnet run --project src/MediaWorkbench.App/MediaWorkbench.App.csproj --artifacts-path artifacts/dotnet --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Media Workbench exited with an error. Inspect its startup log in local application data.' }
} finally { Pop-Location }
