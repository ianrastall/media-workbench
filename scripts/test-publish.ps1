param([string]$ApplicationDirectory, [switch]$SkipUI)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationDirectory) { $ApplicationDirectory = Join-Path $taskRoot 'artifacts/publish/win-x64' }
$taskRequired = @('MediaWorkbench.exe','MediaWorkbench.pri','App.xbf','MainWindow.xbf','ShellView.xbf','media_workbench_core.dll','runtime/python/python.exe','workers/python/media_worker.py','workers/python/hub_catalog.py','workers/python/inference_setup.py','workers/python/inference_worker.py','profiles/sd15-cu126.json','profiles/sd15-cu126.lock','profiles/sd15-files.json')
foreach ($taskFile in $taskRequired) {
    if (-not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory $taskFile))) { throw "Published application is incomplete: $taskFile" }
}
$taskReports = Join-Path $taskRoot 'artifacts/publish-checks'
New-Item -ItemType Directory -Path $taskReports -Force | Out-Null
$taskModes = @('--self-test')
if (-not $SkipUI) { $taskModes += '--ui-self-test' }
foreach ($taskMode in $taskModes) {
    $taskReport = Join-Path $taskReports ($taskMode.TrimStart('-') + '-' + [guid]::NewGuid().ToString('N') + '.json')
    $taskProcess = Start-Process -FilePath (Join-Path $ApplicationDirectory 'MediaWorkbench.exe') -ArgumentList @($taskMode, ('"' + $taskReport + '"')) -WindowStyle Hidden -PassThru
    if (-not $taskProcess.WaitForExit(30000)) { Stop-Process -Id $taskProcess.Id; throw "Published application timed out: $taskMode" }
    if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskReport)) { throw "Published application failed: $taskMode" }
    $taskResult = Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json
    if (-not $taskResult.success) { throw "Published application reported failure: $taskMode" }
    Write-Output "PASS: published application $taskMode"
}
