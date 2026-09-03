# =============================================================================
# DbCodeGen 一键打包脚本
# -----------------------------------------------------------------------------
# 位置：项目根目录
# 用法（任选其一）：
#   PowerShell  :  .\package.ps1
#   Git Bash/CMD:  powershell -ExecutionPolicy Bypass -File package.ps1
# 可选参数：
#   -SelfContained  自包含打包（目标机无需安装 .NET 8 运行时，产物体积更大）
#
# 产物结构（完整可用包，全部输出到 out/DbCodeGen/）：
#   out/DbCodeGen/
#   ├─ DbCodeGen.App.exe         # 桌面宿主：双击即用（WPF）
#   ├─ DbCodeGen.Core.dll        # 领域核心程序集
#   ├─ *.dll / *.json            # 依赖程序集与运行时配置
#   └─ version.txt               # 构建时间与 git 提交号
# =============================================================================

[CmdletBinding()]
param(
    [switch]$SelfContained   # 自包含打包（目标机无需 .NET 8 运行时）
)

$ErrorActionPreference = 'Stop'

# ---- 路径与命名 ----
$RepoRoot     = $PSScriptRoot                          # 脚本所在目录 = 仓库根目录
$SoftwareName = 'DbCodeGen'                            # 软件名（输出目录同名）
$OutputDir    = Join-Path $RepoRoot "out\$SoftwareName"  # 打包输出目录（out 下按软件名建整包）
$AppProject   = Join-Path $RepoRoot 'src\DbCodeGen.App\DbCodeGen.App.csproj'

function Write-Step {
    param([string]$Text)
    Write-Host "`n== $Text ==" -ForegroundColor Cyan
}

# ---- 0/4 工具链检查 ----
Write-Step '0/4 检查工具链'
foreach ($tool in 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "未找到 $tool 命令，请先安装后再运行打包脚本。"
    }
}
Write-Host 'dotnet 就绪。'

# ---- 1/4 清理并重建输出目录 ----
Write-Step '1/4 清理输出目录'
if (Test-Path $OutputDir) { Remove-Item -Path $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null
Write-Host "输出目录：$OutputDir"

# ---- 2/4 发布桌面宿主 ----
Write-Step '2/4 发布桌面宿主 (DbCodeGen.App)'
$ridArg = @()
if ($SelfContained) { $ridArg = @('-r', 'win-x64', '--self-contained', 'true') }

& dotnet publish $AppProject -c Release -o $OutputDir @ridArg
if ($LASTEXITCODE -ne 0) { throw '桌面宿主发布失败。' }
Write-Host '桌面宿主发布完成。'

# ---- 3/4 生成版本信息 ----
Write-Step '3/4 生成版本信息'
$gitCommit = ''
try {
    $gitCommit = (& git -C $RepoRoot rev-parse --short HEAD 2>$null).Trim()
} catch {
    $gitCommit = ''
}
$versionLines = @(
    'DbCodeGen 数据库代码生成工具',
    "构建时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
    "Git 提交：$gitCommit"
)
$versionPath = Join-Path $OutputDir 'version.txt'
Set-Content -Path $versionPath -Value $versionLines -Encoding UTF8
Write-Host "已写入 $versionPath"

# ---- 4/4 校验产物并汇总 ----
Write-Step '4/4 校验产物'
$mustExist = @(
    (Join-Path $OutputDir 'DbCodeGen.App.exe')
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