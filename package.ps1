# =============================================================================
# DbCodeGen 一键打包脚本
# -----------------------------------------------------------------------------
# 位置：项目根目录
# 用途：一键发布桌面端（DbCodeGen.App）与 MCP 服务端（DbCodeGen.McpServer），
#       产出完整可用包到 out/DbCodeGen/。
# 用法（任选其一）：
#   PowerShell  :  .\package.ps1
#   Git Bash/CMD:  powershell -ExecutionPolicy Bypass -File package.ps1
# 可选参数：
#   -FrameworkDependent  框架依赖打包（体积小，但目标机需已安装 .NET 8 运行时）
#                        缺省为自包含打包（双击即用，目标机免装运行时，产物体积更大）
#
# 产物结构（完整可用包，全部输出到 out/DbCodeGen/）：
#   out/DbCodeGen/
#   ├─ DbCodeGen.App.exe         # 桌面宿主：双击即用（WPF）
#   ├─ DbCodeGen.Core.dll        # 领域核心程序集
#   ├─ *.dll / *.json            # 依赖程序集与运行时配置
#   ├─ McpServer/                # MCP 服务端（本地 stdio，供 AI 客户端注册拉起）
#   │  └─ DbCodeGen.McpServer.exe
#   └─ version.txt               # 构建时间、git 提交号与打包模式
# 模板包说明：当前发布不含内置模板包，模板包全部来自用户模板库；内置包目录机制保留
#             （部署侧自行放入 Templates/Builtin 的包按只读处理），打包不复制任何内置包。
#
# data 保全语义：输出目录已存在时保留其 data\ 子目录（用户数据兜底保全，仅重建
#               程序文件）；正常形态数据目录在 %AppData%\DbCodeGen（或经
#               config-location.json 重定向后的目录），输出目录内通常无 data\。
# 外部运行时依赖：缺省自包含打包，桌面端与 MCP 服务端均内嵌运行时，目标机免装
#               .NET 8；传 -FrameworkDependent 切换为框架依赖（体积小，目标机需
#               已安装 .NET 8 运行时）。
#
# .NET SDK 自举语义：
#   打包需要 .NET 8 SDK（仅有运行时的 dotnet 无法执行 publish）。解析顺序 =
#   PATH 已装 SDK → 仓库 .tools\dotnet-sdk 自举缓存 → .tools\dotnet-sdk-win-x64.zip
#   官方缓存自动解压；三层全缺时报错并给出安装/放置指引。自举缓存与 zip 均不入 Git。
# =============================================================================

[CmdletBinding()]
param(
    [switch]$FrameworkDependent   # 框架依赖打包（体积小，目标机需已安装 .NET 8 运行时）
)

$ErrorActionPreference = 'Stop'

# 缺省自包含（分发口径：双击即用，目标机免装运行时）；-FrameworkDependent 显式退出
$SelfContained = -not $FrameworkDependent.IsPresent

# ---- 路径与命名 ----
$RepoRoot     = $PSScriptRoot                          # 脚本所在目录 = 仓库根目录
$SoftwareName = 'DbCodeGen'                            # 软件名（输出目录同名）
$OutputDir    = Join-Path $RepoRoot "out\$SoftwareName"  # 打包输出目录（out 下按软件名建整包）
$AppProject   = Join-Path $RepoRoot 'src\DbCodeGen.App\DbCodeGen.App.csproj'
$McpProject   = Join-Path $RepoRoot 'src\DbCodeGen.McpServer\DbCodeGen.McpServer.csproj'
$McpOutputDir = Join-Path $OutputDir 'McpServer'       # MCP 服务端产物子目录（与桌面端同包、独立目录）

function Write-Step {
    param([string]$Text)
    Write-Host "`n== $Text ==" -ForegroundColor Cyan
}

# 检测指定 dotnet.exe 是否携带 SDK（--list-sdks 退出码为 0 且有输出）
function Test-DotNetSdk {
    param([string]$DotNetPath)
    if (-not (Test-Path $DotNetPath)) { return $false }
    $sdkList = & $DotNetPath --list-sdks 2>$null
    return ($LASTEXITCODE -eq 0 -and $sdkList)
}

# 解析可用于构建的 dotnet 宿主（发布需要 SDK，仅运行时不够）：
#   ① PATH 已装 SDK → ② 仓库 .tools\dotnet-sdk 自举缓存 →
#   ③ .tools\dotnet-sdk-win-x64.zip 官方缓存自动解压自举（一次性，之后走②）。
# 返回 { Exe, Root }（Root 供 DOTNET_ROOT 使用，PATH 形态为 $null）；三层全缺时抛错并给指引。
function Resolve-DotNetHost {
    $toolsDir     = Join-Path $RepoRoot '.tools'
    $bootstrapDir = Join-Path $toolsDir 'dotnet-sdk'
    $bootstrapZip = Join-Path $toolsDir 'dotnet-sdk-win-x64.zip'

    # 命中即可用的两种形态：PATH 安装、仓库自举缓存
    $candidates = @(
        @{ Exe = 'dotnet'; Root = $null },
        @{ Exe = (Join-Path $bootstrapDir 'dotnet.exe'); Root = $bootstrapDir }
    )
    foreach ($candidate in $candidates) {
        if (Test-DotNetSdk -DotNetPath $candidate.Exe) {
            return [PSCustomObject]@{ Exe = $candidate.Exe; Root = $candidate.Root }
        }
    }

    # 自举：zip 缓存在位 → 解压为仓库本地 SDK 缓存（解压前清残留，保证幂等）
    if (Test-Path $bootstrapZip) {
        Write-Host "未发现已安装 SDK，正在从 zip 缓存自举：$bootstrapZip"
        if (Test-Path $bootstrapDir) { Remove-Item -Path $bootstrapDir -Recurse -Force }
        New-Item -ItemType Directory -Path $bootstrapDir | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($bootstrapZip, $bootstrapDir)
        $bootstrappedExe = Join-Path $bootstrapDir 'dotnet.exe'
        if (Test-DotNetSdk -DotNetPath $bootstrappedExe) {
            Write-Host "SDK 自举完成：$bootstrapDir"
            return [PSCustomObject]@{ Exe = $bootstrappedExe; Root = $bootstrapDir }
        }
        throw "SDK 自举缓存解压后不可用：$bootstrapDir（zip 可能损坏，请重新获取官方 .NET 8 SDK zip 后重试。）"
    }

    throw (@"
未找到 .NET SDK（当前 dotnet 仅有运行时，无法执行 publish）：
  1. 安装 .NET 8 SDK（https://dotnet.microsoft.com/download/dotnet/8.0）后重试；或
  2. 将官方 dotnet-sdk-win-x64.zip 放到 $bootstrapZip，本脚本将自动解压自举。
"@)
}

# ---- 0/5 工具链检查 ----
Write-Step '0/5 检查工具链'
$dotnetHost = Resolve-DotNetHost
# 便携 SDK 形态下为发布子进程固定运行时解析根（.NET 8 已取消多层查找，仅靠 PATH 命中运行时不可靠）
if ($dotnetHost.Root) { $env:DOTNET_ROOT = $dotnetHost.Root }
Write-Host ("dotnet 宿主：{0}（SDK {1}）" -f $dotnetHost.Exe, (& $dotnetHost.Exe --version 2>$null))

# ---- 1/5 清理并重建输出目录（data\ 子目录兜底保全） ----
Write-Step '1/5 清理输出目录（保留 data\ 兜底保全）'
$preservedDataDir = $null
if (Test-Path $OutputDir) {
    # 升级重跑保全：data\ 可能承载经迁移改迁到输出目录内的用户数据，清空前先移出、清空后还原
    $candidateDataDir = Join-Path $OutputDir 'data'
    if (Test-Path $candidateDataDir) {
        $preservedDataDir = Join-Path $RepoRoot ("out\.data-preserve-" + [System.Guid]::NewGuid().ToString('N'))
        Move-Item -Path $candidateDataDir -Destination $preservedDataDir
        Write-Host "已保全输出目录 data\ 子目录 → $preservedDataDir"
    }
    Remove-Item -Path $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null
if ($preservedDataDir) {
    Move-Item -Path $preservedDataDir -Destination $candidateDataDir
    Write-Host "已还原 data\ 子目录 → $candidateDataDir（升级保全完成）"
}
Write-Host "输出目录：$OutputDir"

# ---- 2/5 发布桌面宿主 ----
Write-Step '2/5 发布桌面宿主 (DbCodeGen.App)'
$ridArg = @()
if ($SelfContained) { $ridArg = @('-r', 'win-x64', '--self-contained', 'true') }

& $dotnetHost.Exe publish $AppProject -c Release -o $OutputDir @ridArg
if ($LASTEXITCODE -ne 0) { throw '桌面宿主发布失败。' }
# 对外分发包禁止携带 pdb（调试符号不参与运行，剔除后不影响程序可用）
$pdbs = @(Get-ChildItem -Path $OutputDir -Filter '*.pdb' -File)
if ($pdbs.Count -gt 0) {
    $pdbs | Remove-Item -Force
    Write-Host ("已剔除桌面端产物 pdb：{0}" -f (($pdbs | ForEach-Object { $_.Name }) -join '、'))
}
Write-Host '桌面宿主发布完成。'

# ---- 3/5 发布 MCP 服务端 ----
Write-Step '3/5 发布 MCP 服务端 (DbCodeGen.McpServer)'
& $dotnetHost.Exe publish $McpProject -c Release -o $McpOutputDir @ridArg
if ($LASTEXITCODE -ne 0) { throw 'MCP 服务端发布失败。' }
# 对外分发包禁止携带 pdb（同桌面端整备口径）
$mcpPdbs = @(Get-ChildItem -Path $McpOutputDir -Filter '*.pdb' -File)
if ($mcpPdbs.Count -gt 0) {
    $mcpPdbs | Remove-Item -Force
    Write-Host ("已剔除 MCP 服务端产物 pdb：{0}" -f (($mcpPdbs | ForEach-Object { $_.Name }) -join '、'))
}
Write-Host 'MCP 服务端发布完成。'

# ---- 4/5 生成版本信息 ----
Write-Step '4/5 生成版本信息'
$gitCommit = ''
try {
    $gitCommit = (& git -C $RepoRoot rev-parse --short HEAD 2>$null).Trim()
} catch {
    $gitCommit = ''
}
# 打包模式随参数落进 version.txt，交付包自带运行时依赖说明
$packageMode = if ($SelfContained) { '自包含（目标机免装 .NET 8 运行时）' } else { '框架依赖（目标机需已安装 .NET 8 运行时）' }
$versionLines = @(
    'DbCodeGen 数据库代码生成工具',
    "构建时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "Git 提交：$gitCommit",
    "打包模式：$packageMode"
)
$versionPath = Join-Path $OutputDir 'version.txt'
Set-Content -Path $versionPath -Value $versionLines -Encoding UTF8
Write-Host "已写入 $versionPath"

# ---- 5/5 校验产物并汇总 ----
Write-Step '5/5 校验产物'
$mustExist = @(
    (Join-Path $OutputDir 'DbCodeGen.App.exe'),
    (Join-Path $OutputDir 'McpServer\DbCodeGen.McpServer.exe')
)
$missing = $mustExist | Where-Object { -not (Test-Path $_) }
if ($missing) {
    throw "产物缺失：$($missing -join '；')"
}

Write-Host "`n打包成功 → $OutputDir`n" -ForegroundColor Green
Get-ChildItem -Path $OutputDir -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($OutputDir.Length).TrimStart('\', '/')
    '{0,10:N0} KB  {1}' -f ($_.Length / 1KB), $rel
} | Sort-Object

Write-Host ''
Write-Host '使用方法：'
Write-Host "  双击 $OutputDir\DbCodeGen.App.exe 打开桌面窗口。"
Write-Host "  MCP 服务端：$McpOutputDir\DbCodeGen.McpServer.exe（AI 客户端以 stdio 注册拉起，配置示例见 README）"
