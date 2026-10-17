param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [switch]$Publish,
    [switch]$Installer
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    & (Join-Path $PSScriptRoot 'prepare-runtime.ps1') -WithInstallerTools:$Installer
    cargo build --manifest-path native/Cargo.toml --target-dir artifacts/native --release --locked
    if ($LASTEXITCODE -ne 0) { throw 'Native core build failed.' }
    dotnet build MediaWorkbench.slnx --configuration $Configuration --artifacts-path artifacts/dotnet
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    if ($Publish -or $Installer) {
        $taskPublishRoot = Join-Path $taskRoot 'artifacts/publish/win-x64'
        dotnet publish src/MediaWorkbench.App/MediaWorkbench.App.csproj -c Release -r win-x64 --self-contained true --artifacts-path artifacts/dotnet -o $taskPublishRoot
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        & (Join-Path $PSScriptRoot 'test-publish.ps1') -ApplicationDirectory $taskPublishRoot -SkipUI
        Write-Output "Published application: $taskPublishRoot"
        if ($Installer) {
            $taskDist = Join-Path $taskRoot 'dist'
            New-Item -ItemType Directory -Path $taskDist -Force | Out-Null
            $taskVersion = & (Join-Path $PSScriptRoot 'get-version.ps1')
            & (Join-Path $taskRoot '.tools/inno/ISCC.exe') ('/DAppVersion=' + $taskVersion) ('/DSourceDir=' + $taskPublishRoot) ('/DOutputDir=' + $taskDist) (Join-Path $taskRoot 'packaging/media-workbench.iss')
            if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
            Get-ChildItem -LiteralPath $taskDist -Filter '*setup.exe' | ForEach-Object {
                $taskHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "$taskHash  $($_.Name)" | Set-Content -LiteralPath ($_.FullName + '.sha256') -Encoding ascii
            }
        }
    }
} finally { Pop-Location }
