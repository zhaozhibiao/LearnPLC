param([string]$ConfigPath)
$ErrorActionPreference = 'Stop'
$mcpRepository = Split-Path -Parent $PSScriptRoot
if (-not $ConfigPath) { $ConfigPath = Join-Path $mcpRepository 'artifacts/mcp-server.json' }
$mcpDll = Join-Path $mcpRepository 'artifacts/mcp-server/StWorkbench.Mcp.dll'
if (-not (Test-Path -LiteralPath $mcpDll) -or -not (Test-Path -LiteralPath $ConfigPath)) {
    [Console]::Error.WriteLine('请先运行 scripts/setup-mcp.ps1；MCP 客户端启动过程不构建、不自动修改设备权限。')
    exit 2
}
# stdout belongs exclusively to the MCP protocol; do not add Write-Host here.
& dotnet $mcpDll --config ([IO.Path]::GetFullPath($ConfigPath))
exit $LASTEXITCODE
