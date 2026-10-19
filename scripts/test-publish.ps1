param([string]$ApplicationDirectory, [switch]$SkipUI, [string]$ReportsDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApplicationDirectory) { $ApplicationDirectory = Join-Path $taskRoot 'artifacts/publish/win-x64' }
$taskRequired = @('MediaWorkbench.exe','MediaWorkbench.pri','App.xbf','MainWindow.xbf','ShellView.xbf','media_workbench_core.dll','runtime/python/python.exe','runtime/python/_sqlite3.pyd','runtime/python/sqlite3.dll','workers/python/media_worker.py','workers/python/hub_catalog.py','workers/python/inference_setup.py','workers/python/inference_worker.py','workers/python/library_index.py','workers/python/artifact_export.py','workers/python/storage.py','workers/python/upscaler.py','profiles/upscaler-files.json','profiles/sd15-cu126.json','profiles/sd15-cu126.lock','profiles/sd15-files.json','LICENSE.txt')
$taskRequired += 'profiles/storage-layout.json'
$taskRequired += @('workers/python/video.py','profiles/video-files.json','licenses/third-party/AnimateDiff/LICENSE')
$taskRequired += 'workers/python/file_verification.py'
$taskRequired += 'workers/python/model_library.py'
$taskRequired += 'workers/python/hardware.py'
$taskRequired += 'workers/python/hub_models.py'
$taskRequired += 'workers/python/model_setup.py'
foreach ($taskFile in $taskRequired) {
    if (-not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory $taskFile))) { throw "Published application is incomplete: $taskFile" }
}
foreach ($taskFile in @('MediaWorkbench.Cli.exe','MediaWorkbench.Cli.dll','MediaWorkbench.Cli.runtimeconfig.json','MediaWorkbench.Cli.deps.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory $taskFile))) { throw "Published MCP host is incomplete: $taskFile" }
}
# Check actual PE headers: a filename or build configuration does not prove x64 payloads.
foreach ($taskFile in @('MediaWorkbench.exe', 'MediaWorkbench.Cli.exe', 'media_workbench_core.dll', 'coreclr.dll', 'runtime/python/python.exe')) {
    $taskReader = [IO.BinaryReader]::new([IO.File]::OpenRead((Join-Path $ApplicationDirectory $taskFile)))
    try {
        if ($taskReader.ReadUInt16() -ne 0x5A4D) { throw "Invalid executable header: $taskFile" }
        $taskReader.BaseStream.Position = 0x3C
        $taskPeOffset = $taskReader.ReadUInt32()
        if ($taskPeOffset -gt $taskReader.BaseStream.Length - 6) { throw "Invalid PE offset: $taskFile" }
        $taskReader.BaseStream.Position = $taskPeOffset
        if ($taskReader.ReadUInt32() -ne 0x00004550 -or $taskReader.ReadUInt16() -ne 0x8664) {
            throw "Published executable is not Windows x64: $taskFile"
        }
    } finally { $taskReader.Dispose() }
}
Write-Output 'PASS: published app, MCP host, Rust core, .NET runtime, and Python are x64'
$taskReports = if ($ReportsDirectory) { [IO.Path]::GetFullPath($ReportsDirectory) } else { Join-Path $taskRoot 'artifacts/publish-checks' }
New-Item -ItemType Directory -Path $taskReports -Force | Out-Null
$taskModes = @('--self-test')
if (-not $SkipUI) { $taskModes += '--ui-self-test' }
foreach ($taskMode in $taskModes) {
    $taskReport = Join-Path $taskReports ($taskMode.TrimStart('-') + '-' + [guid]::NewGuid().ToString('N') + '.json')
    $taskProcess = Start-Process -FilePath (Join-Path $ApplicationDirectory 'MediaWorkbench.exe') -ArgumentList @($taskMode, ('"' + $taskReport + '"')) -WindowStyle Hidden -PassThru
    try {
        # The app's own operation deadline is 30 seconds. Allow startup/report/exit time too.
        if (-not $taskProcess.WaitForExit(90000) -and -not $taskProcess.HasExited) {
            try { $taskProcess.Kill($true) } catch [InvalidOperationException] { } # May exit between checks.
            throw "Published application timed out: $taskMode"
        }
        if ($taskProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $taskReport)) { throw "Published application failed: $taskMode" }
        $taskResult = Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json
        if (-not $taskResult.success) { throw "Published application reported failure: $taskMode" }
        if ($taskMode -eq '--self-test' -and -not $taskResult.nativeVerification) { throw 'Published native verification was not exercised.' }
        if ($taskMode -eq '--self-test' -and -not $taskResult.hardwareProfile) { throw 'Published hardware profile was not exercised.' }
        if ($taskMode -eq '--self-test' -and -not $taskResult.hubPlanning) { throw 'Published native Hub planning was not exercised.' }
        if ($taskMode -eq '--self-test' -and -not $taskResult.modelCompatibility) { throw 'Published native model compatibility was not exercised.' }
        if ($taskMode -eq '--ui-self-test' -and -not $taskResult.workingFolderRequired) { throw 'First-start UI did not require a working folder.' }
        Write-Output "PASS: published application $taskMode"
    } finally { $taskProcess.Dispose() }
}
# A persistent stdio session verifies the shipped console host, shared runtime, and real worker.
$taskPreferencesCheck = Join-Path $taskReports ('preferences-' + [guid]::NewGuid().ToString('N'))
try {
    $taskProfile = Join-Path $taskPreferencesCheck 'profile'
    $taskWork = Join-Path $taskPreferencesCheck 'work'
    New-Item -ItemType Directory -Path $taskProfile, $taskWork -Force | Out-Null
    @{ schemaVersion = 1; dataRoot = $taskWork; lastSearch = 'smoke'; dataRootChosen = $true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskProfile 'settings.json')
    $taskMigrationMutex = [Threading.Mutex]::new($false, 'Local\ianrastall.MediaWorkbench')
    $taskMigrationOwned = $false
    try {
        try { $taskMigrationOwned = $taskMigrationMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $taskMigrationOwned = $true }
        if (-not $taskMigrationOwned) { throw 'Close Media Workbench before the published preference check.' }
        $taskMigrationInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $ApplicationDirectory 'MediaWorkbench.Cli.exe'))
        $taskMigrationInfo.UseShellExecute = $false
        $taskMigrationInfo.CreateNoWindow = $true
        $taskMigrationInfo.RedirectStandardError = $true
        $taskMigrationInfo.RedirectStandardOutput = $true
        $taskMigrationInfo.ArgumentList.Add('migrate-preferences')
        $taskMigrationInfo.ArgumentList.Add($taskProfile)
        $taskBlockedMigration = [Diagnostics.Process]::Start($taskMigrationInfo)
        try {
            if (-not $taskBlockedMigration.WaitForExit(30000)) { $taskBlockedMigration.Kill($true); throw 'Concurrent preference migration did not stop.' }
            if ($taskBlockedMigration.ExitCode -ne 1 -or $taskBlockedMigration.StandardError.ReadToEnd() -notlike '*Close Media Workbench*' -or
                -not (Test-Path -LiteralPath (Join-Path $taskProfile 'settings.json')) -or
                (Test-Path -LiteralPath (Join-Path $taskProfile 'working-folder.json'))) { throw 'Preference migration did not respect the desktop mutex.' }
        } finally { $taskBlockedMigration.Dispose() }
    } finally {
        if ($taskMigrationOwned) { $taskMigrationMutex.ReleaseMutex() }
        $taskMigrationMutex.Dispose()
    }
    $taskPreferenceResult = & (Join-Path $ApplicationDirectory 'MediaWorkbench.Cli.exe') migrate-preferences $taskProfile
    if ($LASTEXITCODE -ne 0) { throw 'Published preference migration failed.' }
    $taskPreferenceState = $taskPreferenceResult | ConvertFrom-Json
    if ($taskPreferenceState.dataRoot -ne $taskWork -or -not $taskPreferenceState.dataRootChosen -or
        (Test-Path -LiteralPath (Join-Path $taskProfile 'settings.json')) -or
        -not (Test-Path -LiteralPath (Join-Path $taskWork 'preferences/settings.json')) -or
        -not (Test-Path -LiteralPath (Join-Path $taskProfile 'working-folder.json'))) { throw 'Published preference migration was incomplete.' }
    Write-Output 'PASS: published preferences respect the desktop mutex and migrate with only a location pointer in the profile'
    $taskLateSource = Join-Path $taskProfile 'settings.json'
    '{legacy file appeared later' | Set-Content -LiteralPath $taskLateSource
    $taskLateHash = (Get-FileHash -LiteralPath $taskLateSource -Algorithm SHA256).Hash
    $taskPreferenceResult = & (Join-Path $ApplicationDirectory 'MediaWorkbench.Cli.exe') migrate-preferences $taskProfile
    if ($LASTEXITCODE -ne 0) { throw 'Published preferences failed after a legacy leftover appeared.' }
    $taskPreferenceState = $taskPreferenceResult | ConvertFrom-Json
    if ($taskPreferenceState.dataRoot -ne $taskWork -or $taskPreferenceState.note -notlike '*kept*' -or
        (Get-FileHash -LiteralPath $taskLateSource -Algorithm SHA256).Hash -ne $taskLateHash) { throw 'A later legacy source was ignored, loaded or changed.' }
    Write-Output 'PASS: published preferences explain and preserve later legacy leftovers without using them'
} finally {
    $taskPreferencesFull = [IO.Path]::GetFullPath($taskPreferencesCheck)
    $taskReportsPrefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($taskReports)) + [IO.Path]::DirectorySeparatorChar
    if (-not $taskPreferencesFull.StartsWith($taskReportsPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Preference fixture cleanup escaped its reports folder.' }
    if (Test-Path -LiteralPath $taskPreferencesFull) { Remove-Item -LiteralPath $taskPreferencesFull -Recurse -Force }
}
$taskMcpInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $ApplicationDirectory 'MediaWorkbench.Cli.exe'))
$taskMcpInfo.UseShellExecute = $false
$taskMcpInfo.CreateNoWindow = $true
$taskMcpInfo.RedirectStandardInput = $true
$taskMcpInfo.RedirectStandardOutput = $true
$taskMcpInfo.RedirectStandardError = $true
$taskMcpData = Join-Path ([IO.Path]::GetTempPath()) ('MediaWorkbench-mcp-smoke-' + [guid]::NewGuid().ToString('N'))
foreach ($taskArgument in @('mcp','--data-root',$taskMcpData)) { $taskMcpInfo.ArgumentList.Add($taskArgument) }
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
    $taskMcp.StandardInput.WriteLine('{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"workbench_storage","arguments":{}}}')
    $taskStorage = (Read-McpSmokeResponse).result
    if ($taskStorage.isError -or $taskStorage.structuredContent.items.Count -ne 10 -or $taskStorage.structuredContent.freeBytes -le 0) { throw 'Published MCP native storage check failed.' }
    $taskMcp.StandardInput.WriteLine('{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"workbench_models","arguments":{}}}')
    $taskModels = (Read-McpSmokeResponse).result
    if ($taskModels.isError -or $taskModels.structuredContent.models[0].id -ne 'sd15-baseline' -or $taskModels.structuredContent.models.Count -ne 2 -or $taskModels.structuredContent.models[1].id -ne 'sdxl-base') { throw 'Published MCP model listing failed.' }
    $taskMcp.StandardInput.Close()
    if (-not $taskMcp.WaitForExit(30000) -or $taskMcp.ExitCode -ne 0) { throw 'Published MCP host did not exit cleanly.' }
    Write-Output 'PASS: published MCP host, permissions, and worker'
} finally {
    if (-not $taskMcp.HasExited) { $taskMcp.Kill($true); $taskMcp.WaitForExit(5000) | Out-Null }
    $taskMcp.Dispose()
    # The smoke check's data folder is disposable; earlier versions left one per run in %TEMP%.
    if (Test-Path -LiteralPath $taskMcpData) { Remove-Item -LiteralPath $taskMcpData -Recurse -Force -ErrorAction SilentlyContinue }
}
