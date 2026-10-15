param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspace
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
if ($Offline) {
    dotnet restore STWorkbench.slnx --source artifacts/feeds --source artifacts/packages --source "$env:USERPROFILE/.nuget/packages"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build STWorkbench.slnx -c Release --no-restore
} else { dotnet build STWorkbench.slnx -c Release }
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$compiler = Join-Path $workspace '.tools/strucpp-0.7.0/strucpp/strucpp.exe'
if (Test-Path -LiteralPath $compiler) {
    $env:ST_COMPILER_PATH = $compiler
    $env:ST_COMPILER_VERSION = 'STruC++ version 0.7.0'
}
$adapter = Join-Path $workspace 'tools/compiler-adapter/compile.mjs'
if (Test-Path -LiteralPath (Join-Path $workspace 'tools/compiler-adapter/node_modules/strucpp')) {
    $env:ST_COMPILER_ADAPTER = $adapter
    $env:ST_COMPILER_DISTRIBUTION = Split-Path -Parent $compiler
}
foreach ($project in @('Core','Toolchain','OpenPlc','Motion','App','Debug','Deployment')) {
    dotnet run --project "tests/StWorkbench.$project.Tests" -c Release --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
python -m unittest discover -s linux/runtime-install -p 'test*.py'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
python -m unittest discover -s linux/deployment -p 'test*.py'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
python -m unittest discover -s linux/motion -p 'test*.py'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
python -m unittest discover -s linux/axis -p 'test*.py'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host '全部本地验证通过；设备联调结果见 docs/verification.md。'
