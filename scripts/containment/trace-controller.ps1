<#
Elevated Process Monitor controller for the drive-containment acceptance run (started by trace.ps1 through one
UAC prompt). Process Monitor needs administrator rights; nothing else here does, so this script only starts,
stops and exports captures. It acts on fixed request file names in the control folder, never on file contents:
  start-<phase>.request -> capture to <procmon>\<phase>.pml, then writes <phase>.started
  stop-<phase>.request  -> stop, export <procmon>\<phase>.csv, then writes <phase>.stopped
  quit.request          -> stop any capture and exit
First it checks which filter file Process Monitor obeys (a small capture of a known write and read), falling back
to an unfiltered capture. It stops by itself when the driver's heartbeat is older than 20 minutes or after 12 hours.

With -Vhd (the drive-failure checks), it also creates an expandable test virtual disk of -VhdSizeMB at that path,
formats it and gives it -VhdLetter, then on request:
  vhd-detach.request -> detaches it (as if the drive were unplugged), then writes vhd-detached
  vhd-attach.request -> attaches it again with the same letter, then writes vhd-attached
When it exits it detaches the disk and deletes the file. Nothing else on the computer is changed.
#>
param(
    [Parameter(Mandatory)] [string]$Procmon,
    [Parameter(Mandatory)] [string]$Control,
    [Parameter(Mandatory)] [string]$Output,
    [string]$Vhd,
    [ValidatePattern('^[D-Z]$')] [string]$VhdLetter,
    [ValidateRange(1024, 65536)] [int]$VhdSizeMB = 24576
)
$ErrorActionPreference = 'Stop'
foreach ($path in @($Procmon, $Control, $Output)) { if (-not [IO.Path]::IsPathFullyQualified($path)) { throw "Not an absolute path: $path" } }
if ($Vhd -and (-not [IO.Path]::IsPathFullyQualified($Vhd) -or -not $Vhd.EndsWith('.vhdx', 'OrdinalIgnoreCase') -or -not $VhdLetter)) { throw 'The test disk needs an absolute .vhdx path and a letter.' }
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

function Mount-TestDisk {
    # No automatic letter, so Windows never offers to format a raw volume; the letter is added explicitly.
    $disk = Mount-DiskImage -ImagePath $Vhd -NoDriveLetter -PassThru | Get-Disk
    if ($disk.PartitionStyle -eq 'RAW') {
        Initialize-Disk -Number $disk.Number -PartitionStyle GPT
        $partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize
        Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel 'MW-TEST' -Confirm:$false | Out-Null
    }
    $partition = Get-Partition -DiskNumber $disk.Number | Where-Object Type -eq 'Basic' | Select-Object -First 1
    if (-not (Test-Path "${VhdLetter}:\")) { Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $partition.PartitionNumber -AccessPath "${VhdLetter}:\" }
    $until = (Get-Date).AddSeconds(30)
    while (-not (Test-Path "${VhdLetter}:\") -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path "${VhdLetter}:\")) { throw "The test disk did not appear as ${VhdLetter}:." }
}

function Dismount-TestDisk {
    if ($Vhd -and (Test-Path -LiteralPath $Vhd) -and (Get-DiskImage -ImagePath $Vhd -ErrorAction SilentlyContinue).Attached) {
        Dismount-DiskImage -ImagePath $Vhd | Out-Null
    }
}

try {
    if ($Vhd) {
        if (Test-Path -LiteralPath $Vhd) { throw "The test disk file already exists: $Vhd" }
        if (Test-Path "${VhdLetter}:\") { throw "Drive ${VhdLetter}: is already in use." }
        $script = Join-Path $Control 'create-vhd.txt'
        Set-Content -LiteralPath $script -Value "create vdisk file=`"$Vhd`" maximum=$VhdSizeMB type=expandable" -Encoding ascii
        & diskpart.exe /s $script | Out-Host
        if (-not (Test-Path -LiteralPath $Vhd)) { throw 'diskpart did not create the test disk.' }
        Mount-TestDisk
        Write-Host "Test disk attached as ${VhdLetter}: ($VhdSizeMB MB, expandable)"
    }
    $mode = 'unfiltered'; $config = $null
    foreach ($candidate in @(@{ Label = 'terminated'; File = 'filter.pmc' }, @{ Label = 'unterminated'; File = 'filter-unterminated.pmc' })) {
        $file = Join-Path $Control $candidate.File
        if ((Test-Path -LiteralPath $file) -and (Test-Filter $file $candidate.Label)) { $mode = "filtered ($($candidate.Label))"; $config = $file; break }
    }
    if (-not $config -and -not (Test-Filter $null 'unfiltered')) { throw 'Process Monitor did not record the calibration write even without a filter.' }
    @{ mode = $mode; ready = $true; vhdLetter = $VhdLetter } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Control 'controller-status.json') -Encoding utf8
    Write-Host "Trace mode: $mode"

    $active = $null
    while ((Get-Date) -lt $deadline) {
        $heartbeat = Join-Path $Control 'heartbeat'
        if ((Test-Path -LiteralPath $heartbeat) -and ((Get-Date) - (Get-Item -LiteralPath $heartbeat).LastWriteTime).TotalMinutes -gt 20) { throw 'The acceptance driver stopped sending its heartbeat.' }
        $request = Get-ChildItem -LiteralPath $Control -Filter '*.request' | Sort-Object CreationTime | Select-Object -First 1
        if (-not $request) { Start-Sleep -Seconds 1; continue }
        Remove-Item -LiteralPath $request.FullName
        if ($request.Name -eq 'quit.request') { break }
        if ($Vhd -and $request.Name -in 'vhd-detach.request', 'vhd-attach.request') {
            if ($request.Name -eq 'vhd-detach.request') { Dismount-TestDisk; $marker = 'vhd-detached' } else { Mount-TestDisk; $marker = 'vhd-attached' }
            Set-Content -LiteralPath (Join-Path $Control $marker) -Value '' -Encoding ascii
            continue
        }
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
    try { Dismount-TestDisk } catch { Write-Host "Could not detach the test disk: $($_.Exception.Message)" }
    if ($Vhd -and (Test-Path -LiteralPath $Vhd)) {
        try { Remove-Item -LiteralPath $Vhd -Force } catch { Write-Host "Could not delete the test disk file: $($_.Exception.Message)" }
    }
    Stop-Transcript | Out-Null
}
