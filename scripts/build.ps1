param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [switch]$Publish,
    [switch]$Installer
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
Push-Location $taskRoot
try {
    & (Join-Path $PSScriptRoot 'update-release-readme.ps1')
    & (Join-Path $PSScriptRoot 'prepare-runtime.ps1') -WithInstallerTools:$Installer
    cargo build --manifest-path native/Cargo.toml --target-dir artifacts/native --release --locked
    if ($LASTEXITCODE -ne 0) { throw 'Native core build failed.' }
    dotnet build MediaWorkbench.slnx --configuration $Configuration --artifacts-path artifacts/dotnet
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
    # Settings generates a console-host path beside the GUI in source builds as well as installs.
    # The GUI has an app-local .NET runtime, so a framework-dependent host beside it
    # cannot resolve the system framework. Build the host for that same bundled runtime.
    dotnet build src/MediaWorkbench.Cli/MediaWorkbench.Cli.csproj --configuration $Configuration -r win-x64 --self-contained true --artifacts-path artifacts/dotnet
    if ($LASTEXITCODE -ne 0) { throw 'Development MCP host build failed.' }
    $taskConfigurationName = $Configuration.ToLowerInvariant()
    $taskAppBuild = Join-Path $taskRoot ("artifacts/dotnet/bin/MediaWorkbench.App/" + $taskConfigurationName + "_win-x64")
    $taskConsoleBuild = Join-Path $taskRoot ("artifacts/dotnet/bin/MediaWorkbench.Cli/" + $taskConfigurationName + "_win-x64")
    foreach ($taskFile in @('MediaWorkbench.Cli.exe', 'MediaWorkbench.Cli.dll', 'MediaWorkbench.Cli.deps.json', 'MediaWorkbench.Cli.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $taskConsoleBuild $taskFile) -Destination $taskAppBuild -Force
    }
    if ($Publish -or $Installer) {
        $taskPublishRoot = Join-Path $taskRoot 'artifacts/publish/win-x64'
        dotnet publish src/MediaWorkbench.App/MediaWorkbench.App.csproj -c Release -r win-x64 --self-contained true --artifacts-path artifacts/dotnet -o $taskPublishRoot
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        # Console MCP host shares the application's self-contained .NET runtime and services.
        # Publish it separately, then merge the payload; this adds no system .NET dependency.
        $taskMcpRoot = Join-Path $taskRoot 'artifacts/publish/mcp-win-x64'
        dotnet publish src/MediaWorkbench.Cli/MediaWorkbench.Cli.csproj -c Release -r win-x64 --self-contained true --artifacts-path artifacts/dotnet -o $taskMcpRoot
        if ($LASTEXITCODE -ne 0) { throw 'MCP host publish failed.' }
        Get-ChildItem -LiteralPath $taskMcpRoot -File | Copy-Item -Destination $taskPublishRoot -Force
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
