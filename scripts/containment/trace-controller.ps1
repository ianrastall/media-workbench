<#
Elevated Process Monitor controller for the drive-containment acceptance run (started by trace.ps1 through one
UAC prompt). Process Monitor needs administrator rights; nothing else here does, so this script only starts,
stops and exports captures. It acts on fixed request file names in the control folder, never on file contents:
  start-<phase>.request -> capture to <procmon>\<phase>.pml, then writes <phase>.started
  stop-<phase>.request  -> stop, export <procmon>\<phase>.csv, then writes <phase>.stopped
  quit.request          -> stop any capture and exit
First it checks which filter file Process Monitor obeys (a small capture of a known write and read), falling back
to an unfiltered capture. It stops by itself when the driver's heartbeat is older than 20 minutes or after 12 hours.
#>
param(
    [Parameter(Mandatory)] [string]$Procmon,
    [Parameter(Mandatory)] [string]$Control,
    [Parameter(Mandatory)] [string]$Output
)
$ErrorActionPreference = 'Stop'
foreach ($path in @($Procmon, $Control, $Output)) { if (-not [IO.Path]::IsPathFullyQualified($path)) { throw "Not an absolute path: $path" } }
Start-Transcript -LiteralPath (Join-Path $Control 'controller.log') -Append | Out-Null
$deadline = (Get-Date).AddHours(12)

function Wait-Exit([System.Diagnostics.Process]$process, [int]$seconds) {
    if (-not $process.WaitForExit($seconds * 1000)) { try { $process.Kill($true) } catch {} ; return $false }
    return $true
}

function Stop-Capture {
    if (-not (Get-Process -Name Procmon64, Procmon -ErrorAction SilentlyContinue)) { return }
    [void](Wait-Exit (Start-Process -FilePath $Procmon -ArgumentList '/Terminate' -PassThru) 600)
    $until = (Get-Date).AddMinutes(10)
    while ((Get-Process -Name Procmon64, Procmon -ErrorAction SilentlyContinue) -and (Get-Date) -lt $until) { Start-Sleep -Seconds 1 }
    Get-Process -Name Procmon64, Procmon -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Start-Capture([string]$backing, [string]$config) {
    $filter = if ($config) { @('/LoadConfig', "`"$config`"") } else { @('/NoFilter') }
    [void](Start-Process -FilePath $Procmon -ArgumentList (@('/AcceptEula', '/Quiet', '/Minimized') + $filter + @('/BackingFile', "`"$backing`"")) -PassThru)
    if (-not (Wait-Exit (Start-Process -FilePath $Procmon -ArgumentList '/WaitForIdle' -PassThru) 120)) { Stop-Capture; return $false }
    return $true
}

function Export-Capture([string]$backing, [string]$csv) {
    $process = Start-Process -FilePath $Procmon -ArgumentList @('/AcceptEula', '/Quiet', '/Minimized', '/OpenLog', "`"$backing`"", '/SaveAs', "`"$csv`"") -PassThru
    # Process Monitor normally exits after saving; if it stays open, stop it once the CSV has stopped growing.
    $last = -1; $steady = 0
    while (-not $process.HasExited) {
        Start-Sleep -Seconds 5
        $size = if (Test-Path -LiteralPath $csv) { (Get-Item -LiteralPath $csv).Length } else { -1 }
        if ($size -gt 0 -and $size -eq $last) { $steady++ } else { $steady = 0 }
        $last = $size
        if ($steady -ge 6) { Stop-Capture; break }
    }
    return (Test-Path -LiteralPath $csv)
}

function Test-Filter([string]$config, [string]$label) {
    $backing = Join-Path $Output "calibrate-$label.pml"; $csv = Join-Path $Output "calibrate-$label.csv"
    $marker = Join-Path $Output "calibrate-$label-marker.txt"
    if (-not (Start-Capture $backing $config)) { return $false }
    & cmd.exe /d /c "echo calibrate> `"$marker`" & type `"$marker`" > nul"
    Start-Sleep -Seconds 3
    Stop-Capture
    if (-not (Export-Capture $backing $csv)) { return $false }
    $rows = Import-Csv -LiteralPath $csv
    $wrote = @($rows | Where-Object { $_.Path -eq $marker -and $_.Operation -in 'WriteFile', 'CreateFile' }).Count -gt 0
    $noise = @($rows | Where-Object { $_.Operation -eq 'ReadFile' -or $_.Operation -like 'Reg*' }).Count
    Remove-Item -LiteralPath $backing, $csv, $marker -ErrorAction SilentlyContinue
    Write-Host "Filter $label`: marker write seen $wrote, read/registry events $noise, rows $($rows.Count)"
    # A filter must keep the write and drop reads and registry events; an unfiltered capture only has to see the write.
    return ($wrote -and ($null -eq $config -or $noise -eq 0))
}

try {
    $mode = 'unfiltered'; $config = $null
    foreach ($candidate in @(@{ Label = 'terminated'; File = 'filter.pmc' }, @{ Label = 'unterminated'; File = 'filter-unterminated.pmc' })) {
        $file = Join-Path $Control $candidate.File
        if ((Test-Path -LiteralPath $file) -and (Test-Filter $file $candidate.Label)) { $mode = "filtered ($($candidate.Label))"; $config = $file; break }
    }
    if (-not $config -and -not (Test-Filter $null 'unfiltered')) { throw 'Process Monitor did not record the calibration write even without a filter.' }
    @{ mode = $mode; ready = $true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Control 'controller-status.json') -Encoding utf8
    Write-Host "Trace mode: $mode"

    $active = $null
    while ((Get-Date) -lt $deadline) {
        $heartbeat = Join-Path $Control 'heartbeat'
        if ((Test-Path -LiteralPath $heartbeat) -and ((Get-Date) - (Get-Item -LiteralPath $heartbeat).LastWriteTime).TotalMinutes -gt 20) { throw 'The acceptance driver stopped sending its heartbeat.' }
        $request = Get-ChildItem -LiteralPath $Control -Filter '*.request' | Sort-Object CreationTime | Select-Object -First 1
        if (-not $request) { Start-Sleep -Seconds 1; continue }
        Remove-Item -LiteralPath $request.FullName
        if ($request.Name -eq 'quit.request') { break }
        if ($request.Name -notmatch '^(start|stop)-([a-z0-9-]{1,40})\.request$') { Write-Host "Ignored $($request.Name)"; continue }
        $verb = $Matches[1]; $phase = $Matches[2]
        $backing = Join-Path $Output "$phase.pml"
        if ($verb -eq 'start') {
            if ($active) { throw "Capture $active is still running." }
            if (-not (Start-Capture $backing $config)) { throw "Process Monitor did not start for $phase." }
            $active = $phase
            Set-Content -LiteralPath (Join-Path $Control "$phase.started") -Value '' -Encoding ascii
        }
        else {
            if ($active -ne $phase) { throw "Stop for $phase, but the active capture is $active." }
            Stop-Capture
            $active = $null
            if (-not (Export-Capture $backing (Join-Path $Output "$phase.csv"))) { throw "The $phase capture could not be exported." }
            Set-Content -LiteralPath (Join-Path $Control "$phase.stopped") -Value '' -Encoding ascii
        }
    }
}
catch {
    Set-Content -LiteralPath (Join-Path $Control 'controller-failed') -Value $_.Exception.Message -Encoding utf8
    Write-Host "Failed: $($_.Exception.Message)"
}
finally {
    Stop-Capture
    Stop-Transcript | Out-Null
}
