<#
Drive-containment acceptance run (docs/storage-locations.md#release-acceptance).

    ./scripts/containment/trace.ps1 [-Quick] [-NoTrace] [-Procmon <Procmon64.exe>] [-App <installed app folder>]

Runs every app operation through the installed app's MCP host and CLI against a fresh data folder in
artifacts/containment-trace/<time>/data, traces the app's file changes with Process Monitor (one UAC prompt for
the elevated controller; Process Monitor itself is not shipped or committed, see .tools/procmon), and writes
report.md beside it. The full run downloads about 15 GB into that data folder and uses the GPU for an hour or more.
-Quick runs read-only phases only; -NoTrace skips Process Monitor (no elevation) to check the driver.
-DriveFailure instead puts the data folder on a 24 GB expandable test virtual disk that the elevated controller
creates in the run folder (about 7 GB used on this drive), fills it with filler files to just below setup's and a
picture's space needs, and detaches it before and during a picture; the disk is detached and deleted at the end.
Process Monitor's capture files are removed once the report is written; the data folder is kept for inspection.
#>
param(
    [switch]$Quick,
    [switch]$NoTrace,
    [switch]$DriveFailure,
    [string]$Procmon = (Join-Path $PSScriptRoot '..\..\.tools\procmon\Procmon64.exe'),
    [string]$App = (Join-Path $env:LOCALAPPDATA 'Programs\Media Workbench')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$python = Join-Path $repo 'runtime\python\python.exe'
$App = (Resolve-Path $App).Path
if (-not (Test-Path (Join-Path $App 'MediaWorkbench.Cli.exe'))) { throw "No installed Media Workbench in $App." }
if (Get-Process -Name MediaWorkbench, MediaWorkbench.Cli -ErrorAction SilentlyContinue) { throw 'Close Media Workbench before the acceptance run.' }

$run = Join-Path $repo ('artifacts\containment-trace\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$control = Join-Path $run 'control'; $capture = Join-Path $run 'procmon'
New-Item -ItemType Directory -Force $control, $capture | Out-Null
$volume = [IO.Path]::GetPathRoot($run)
$free = (Get-PSDrive -Name $volume.Substring(0, 1)).Free
if (-not $Quick -and $free -lt 60GB) { throw "The full run needs about 60 GB free on $volume (data folder plus capture files); $([math]::Round($free / 1GB)) GB is free." }

$arguments = @('-I', '-B', (Join-Path $PSScriptRoot 'acceptance.py'), '--run', $run, '--app', $App)
if ($Quick) { $arguments += '--quick' }
$diskArguments = @()
if ($DriveFailure) {
    if ($NoTrace -or $Quick) { throw '-DriveFailure needs the elevated controller; do not combine it with -NoTrace or -Quick.' }
    $used = @((Get-PSDrive -PSProvider FileSystem).Name) + @(Get-CimInstance Win32_LogicalDisk | ForEach-Object { $_.DeviceID.Substring(0, 1) })
    $letter = @('T', 'R', 'S', 'U', 'V', 'W', 'X', 'Y', 'Q', 'P') | Where-Object { $_ -notin $used } | Select-Object -First 1
    if (-not $letter) { throw 'No free drive letter for the test disk.' }
    $diskArguments = @('-Vhd', "`"$(Join-Path $run 'drive-failure.vhdx')`"", '-VhdLetter', $letter)
    $arguments += '--drive-failure'
    Write-Host "The test disk will appear as ${letter}: while the checks run."
}
if (-not $NoTrace) {
    $Procmon = (Resolve-Path $Procmon).Path
    if ((Get-AuthenticodeSignature $Procmon).Status -ne 'Valid') { throw "$Procmon is not validly signed." }
    & $python -I -B (Join-Path $PSScriptRoot 'procmon_config.py') (Join-Path $control 'filter.pmc')
    & $python -I -B (Join-Path $PSScriptRoot 'procmon_config.py') (Join-Path $control 'filter-unterminated.pmc') --no-terminator
    $shell = (Get-Process -Id $PID).Path
    Write-Host 'Starting the elevated trace controller: Windows will ask for permission once.'
    $controllerArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $PSScriptRoot 'trace-controller.ps1')`"",
        '-Procmon', "`"$Procmon`"", '-Control', "`"$control`"", '-Output', "`"$capture`"") + $diskArguments
    Start-Process -FilePath $shell -Verb RunAs -WindowStyle Minimized -ArgumentList $controllerArguments
    $until = (Get-Date).AddMinutes(15)
    while (-not (Test-Path (Join-Path $control 'controller-status.json'))) {
        if (Test-Path (Join-Path $control 'controller-failed')) { throw 'The trace controller failed: ' + (Get-Content (Join-Path $control 'controller-failed') -Raw) }
        if ((Get-Date) -gt $until) { throw 'The trace controller did not start (was the permission prompt declined?).' }
        Start-Sleep -Seconds 2
    }
    Write-Host ('Trace mode: ' + (Get-Content (Join-Path $control 'controller-status.json') -Raw | ConvertFrom-Json).mode)
    $arguments += @('--control', $control)
}

& $python @arguments
$driver = $LASTEXITCODE
if (-not $NoTrace) {
    $until = (Get-Date).AddMinutes(30)
    while ((Get-Process -Name Procmon64, Procmon -ErrorAction SilentlyContinue) -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2 }
}
& $python -I -B (Join-Path $PSScriptRoot 'summarize_writes.py') $run
if ($LASTEXITCODE -eq 0) { Get-ChildItem $capture -Filter '*.pml' -ErrorAction SilentlyContinue | Remove-Item }
Write-Host "Report: $(Join-Path $run 'report.md')"
exit $driver
