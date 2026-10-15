param([string]$ConfigPath)
$ErrorActionPreference = 'Stop'
$mcpRepository = Split-Path -Parent $PSScriptRoot
if (-not $ConfigPath) { $ConfigPath = Join-Path $mcpRepository 'artifacts/mcp-server.json' }
$mcpOutput = Join-Path $mcpRepository 'artifacts/mcp-server'
dotnet build (Join-Path $mcpRepository 'src/StWorkbench.Mcp/StWorkbench.Mcp.csproj') -c Release "-p:OutputPath=$mcpOutput/"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path -LiteralPath $ConfigPath)) {
    $mcpNodePath = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not $mcpNodePath) { $mcpNodePath = 'node' }
    $mcpSettings = [ordered]@{
        WorkspaceRoot = $mcpRepository
        RepositoryRoot = $mcpRepository
        NodeExecutable = $mcpNodePath
        CompilerDistribution = (Join-Path $mcpRepository '.tools/strucpp-0.7.0/strucpp')
        Devices = @()
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($ConfigPath))) | Out-Null
    $mcpSettings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ConfigPath -Encoding utf8
}
Write-Host "MCP 已构建。配置文件：$ConfigPath"
Write-Host '在支持 MCP 的客户端中加入 examples/mcp/client-config.example.json 的启动配置。'
Write-Host '新配置默认仅工程操作；设备权限请由操作者在配置文件中启用。'
