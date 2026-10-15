$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspace
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet run --project src/StWorkbench.App -c Release
exit $LASTEXITCODE
