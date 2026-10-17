param([string]$ApplicationDirectory, [switch]$SkipUI)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationDirectory) { $ApplicationDirectory = Join-Path $taskRoot 'artifacts/publish/win-x64' }
$taskRequired = @('MediaWorkbench.exe','MediaWorkbench.pri','App.xbf','MainWindow.xbf','ShellView.xbf','media_workbench_core.dll','runtime/python/python.exe','runtime/python/_sqlite3.pyd','runtime/python/sqlite3.dll','workers/python/media_worker.py','workers/python/hub_catalog.py','workers/python/inference_setup.py','workers/python/inference_worker.py','workers/python/library_index.py','workers/python/artifact_export.py','workers/python/storage.py','workers/python/upscaler.py','profiles/upscaler-files.json','profiles/sd15-cu126.json','profiles/sd15-cu126.lock','profiles/sd15-files.json','LICENSE.txt')
foreach ($taskFile in $taskRequired) {
    if (-not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory $taskFile))) { throw "Published application is incomplete: $taskFile" }
}
foreach ($taskFile in @('MediaWorkbench.Cli.exe','MediaWorkbench.Cli.dll','MediaWorkbench.Cli.runtimeconfig.json','MediaWorkbench.Cli.deps.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory $taskFile))) { throw "Published MCP host is incomplete: $taskFile" }
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
# A persistent stdio session verifies the shipped console host, shared runtime, and real worker.
$taskMcpInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $ApplicationDirectory 'MediaWorkbench.Cli.exe'))
$taskMcpInfo.UseShellExecute = $false
$taskMcpInfo.CreateNoWindow = $true
$taskMcpInfo.RedirectStandardInput = $true
$taskMcpInfo.RedirectStandardOutput = $true
$taskMcpInfo.RedirectStandardError = $true
foreach ($taskArgument in @('mcp','--data-root',(Join-Path ([IO.Path]::GetTempPath()) ('MediaWorkbench-mcp-smoke-' + [guid]::NewGuid().ToString('N'))))) { $taskMcpInfo.ArgumentList.Add($taskArgument) }
$taskMcp = [Diagnostics.Process]::Start($taskMcpInfo)
function Read-McpSmokeResponse {
    $taskRead = $taskMcp.StandardOutput.ReadLineAsync()
    if (-not $taskRead.Wait(30000) -or -not $taskRead.Result) { throw 'Published MCP host timed out or exited.' }
    $taskMessage = $taskRead.Result | ConvertFrom-Json
    if ($taskMessage.error) { throw ('Published MCP protocol failure: ' + $taskMessage.error.message) }
    return $taskMessage
}
try {
    $taskMcp.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"publish-check","version":"1"}}}')
    if ((Read-McpSmokeResponse).result.serverInfo.name -ne 'media-workbench') { throw 'Unexpected MCP server identity.' }
    $taskMcp.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $taskMcp.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
    $taskTools = (Read-McpSmokeResponse).result.tools.name
    if ($taskTools -notcontains 'workbench_model_status' -or $taskTools -contains 'workbench_install_model') { throw 'Published MCP defaults are incorrect.' }
    $taskMcp.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"workbench_model_status","arguments":{}}}')
    $taskState = (Read-McpSmokeResponse).result
    if ($taskState.isError -or $taskState.structuredContent.status -ne 'not_installed') { throw 'Published MCP worker status check failed.' }
    $taskMcp.StandardInput.Close()
    if (-not $taskMcp.WaitForExit(30000) -or $taskMcp.ExitCode -ne 0) { throw 'Published MCP host did not exit cleanly.' }
    Write-Output 'PASS: published MCP host, permissions, and worker'
} finally {
    if (-not $taskMcp.HasExited) { $taskMcp.Kill($true); $taskMcp.WaitForExit(5000) | Out-Null }
    $taskMcp.Dispose()
}
