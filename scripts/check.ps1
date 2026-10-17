param([string]$Python, [string]$Artifacts, [string]$Ffprobe, [switch]$Online)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $Python) { $Python = Join-Path $taskRoot 'runtime/python/python.exe' }
if (-not $Artifacts) { $Artifacts = Join-Path $taskRoot 'artifacts/checks' }
$Artifacts = [IO.Path]::GetFullPath($Artifacts)
Push-Location $taskRoot
try {
    cargo fmt --manifest-path native/Cargo.toml --check
    if ($LASTEXITCODE -ne 0) { throw 'Rust formatting failed.' }
    cargo test --manifest-path native/Cargo.toml --target-dir artifacts/native --locked
    if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed.' }
    cargo clippy --manifest-path native/Cargo.toml --target-dir artifacts/native --all-targets --locked -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw 'Rust lint checks failed.' }
    cargo build --manifest-path native/Cargo.toml --target-dir artifacts/native --release --locked
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
    & $Python -m unittest discover -s (Join-Path $taskRoot 'tests') -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) { throw 'Python checks failed.' }
    $taskArguments = @($Python, $taskRoot, (Join-Path $Artifacts 'test-data'))
    if ($Ffprobe) { $taskArguments += $Ffprobe }
    dotnet run --project tests/MediaWorkbench.Checks/MediaWorkbench.Checks.csproj --artifacts-path artifacts/dotnet -- @taskArguments
    if ($LASTEXITCODE -ne 0) { throw 'C# integration checks failed.' }
    if ($Online) {
        & $Python tests/online_catalog_check.py
        if ($LASTEXITCODE -ne 0) { throw 'Live Hub check failed.' }
    }
} finally { Pop-Location }
