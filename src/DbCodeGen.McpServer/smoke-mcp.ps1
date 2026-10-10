# DbCodeGen.McpServer 启动冒烟取证脚本（cmd/PowerShell 承载，禁 Python 纪律下执行）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File smoke-mcp.ps1 <exe路径> <输出目录>
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$msgs = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0.0.1"}}}',
    '{"jsonrpc":"2.0","method":"notifications/initialized"}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_template_packages","arguments":{}}}',
    '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_templates","arguments":{"package":"java-mybatis-plus","type":"java"}}}',
    '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"read_template","arguments":{"package":"java-mybatis-plus","path":"entity.java.scriban"}}}',
    '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"list_datasources","arguments":{}}}',
    '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"get_type_map","arguments":{"package":"java-mybatis-plus"}}}',
    '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"list_tables","arguments":{"datasource":"__不存在的连接__"}}}',
    '{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"read_template","arguments":{"package":"java-mybatis-plus","path":"../../../App.xaml.cs"}}}',
    '{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"list_templates","arguments":{"package":"java-mybatis-plus","type":"kotlin"}}}',
    '{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"check_type_mapping","arguments":{"datasource":"__不存在的连接__","tables":["t1"],"package":"java-mybatis-plus"}}}'
)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ExePath
$psi.WorkingDirectory = Split-Path -Parent $ExePath
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$p = [System.Diagnostics.Process]::Start($psi)
if (-not $p.HasExited) { Write-Output ('进程可启动: True (pid=' + $p.Id + ')') } else { Write-Output ('进程可启动: False') }

foreach ($m in $msgs) {
    $p.StandardInput.WriteLine($m)
    $p.StandardInput.Flush()
    Start-Sleep -Milliseconds 200
}

# 保持 stdin 打开逐行收应答，收齐 9 条带 id 的响应（initialized 为通知无响应）
$lines = New-Object System.Collections.Generic.List[string]
$deadline = [DateTime]::UtcNow.AddSeconds(15)
while ($lines.Count -lt 11 -and [DateTime]::UtcNow -lt $deadline) {
    $task = $p.StandardOutput.ReadLineAsync()
    if ($task.Wait(1500)) {
        if ($null -ne $task.Result) { $lines.Add($task.Result) } else { break }
    }
}
$sw.Stop()
$p.StandardInput.Close()
if (-not $p.WaitForExit(5000)) { $p.Kill() }
Write-Output ('应答收集: ' + $lines.Count + '/11 条，总耗时 ' + [int]$sw.ElapsedMilliseconds + 'ms，进程退出码: ' + $p.ExitCode)

# 原始应答逐行落盘取证
$raw = Join-Path $OutDir 'smoke-responses.jsonl'
[System.IO.File]::WriteAllLines($raw, $lines)

# 摘录输出：initialize 应答 + tools/list 工具名清单
foreach ($line in $lines) {
    if ($line -match '"id":1,') { Write-Output ('[initialize] ' + $line) }
    if ($line -match '"id":2,') {
        $obj = $line | ConvertFrom-Json
        $names = ($obj.result.tools | ForEach-Object { $_.name }) -join ', '
        Write-Output ('[tools/list] 工具数=' + $obj.result.tools.Count + ' 清单: ' + $names)
    }
}
Write-Output ('原始应答已落盘: ' + $raw)
