param([switch]$WithInstallerTools)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskManifest = Get-Content -LiteralPath (Join-Path $taskRoot 'packaging/dependencies.json') -Raw | ConvertFrom-Json
$taskCache = Join-Path $taskRoot '.cache'
New-Item -ItemType Directory -Path $taskCache -Force | Out-Null

function Get-VerifiedDependency($Entry, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Destination)) {
        Invoke-WebRequest -Uri $Entry.url -OutFile ($Destination + '.partial')
        $taskDownloadedHash = (Get-FileHash -LiteralPath ($Destination + '.partial') -Algorithm SHA256).Hash
        if ($taskDownloadedHash -ne $Entry.sha256) { throw "Checksum failed for $($Entry.url)" }
        Move-Item -LiteralPath ($Destination + '.partial') -Destination $Destination
    }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $Entry.sha256) {
        throw "Cached dependency checksum failed: $Destination"
    }
}

$taskPythonArchive = Join-Path $taskCache "python-$($taskManifest.python.version)-embed-amd64.zip"
Get-VerifiedDependency $taskManifest.python $taskPythonArchive
$taskPythonRoot = Join-Path $taskRoot 'runtime/python'
$taskMarker = Join-Path $taskPythonRoot 'media-workbench-runtime.json'
if (-not (Test-Path -LiteralPath $taskMarker)) {
    New-Item -ItemType Directory -Path $taskPythonRoot -Force | Out-Null
    Expand-Archive -LiteralPath $taskPythonArchive -DestinationPath $taskPythonRoot -Force
    $taskManifest.python | ConvertTo-Json | Set-Content -LiteralPath $taskMarker -Encoding utf8
}
if (-not (Test-Path -LiteralPath (Join-Path $taskPythonRoot 'python.exe'))) { throw 'Bundled Python is incomplete.' }

if ($WithInstallerTools) {
    $taskInstaller = Join-Path $taskCache "innosetup-$($taskManifest.innoSetup.version)-x64.exe"
    Get-VerifiedDependency $taskManifest.innoSetup $taskInstaller
    $taskCompilerRoot = Join-Path $taskRoot '.tools/inno'
    if (-not (Test-Path -LiteralPath (Join-Path $taskCompilerRoot 'ISCC.exe'))) {
        $taskArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CURRENTUSER','/NOICONS',('/DIR=' + $taskCompilerRoot))
        $taskSetupProcess = Start-Process -FilePath $taskInstaller -ArgumentList $taskArguments -WindowStyle Hidden -Wait -PassThru
        if ($taskSetupProcess.ExitCode -ne 0) { throw "Inno Setup compiler installation failed: $($taskSetupProcess.ExitCode)" }
    }
}
Write-Output "Bundled discovery Python ready: $taskPythonRoot"
