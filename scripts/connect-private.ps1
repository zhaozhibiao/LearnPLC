$ErrorActionPreference = 'Stop'
# The desktop owns its private SSH tunnel; both launchers use the same connection UI.
& (Join-Path $PSScriptRoot 'start.ps1')
exit $LASTEXITCODE