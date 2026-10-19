# DbCodeGen.McpServer startup smoke test (cmd/PowerShell only, Python is forbidden).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File smoke-mcp.ps1 <exe path> <output directory>
# Package probes are dynamic: prefer package "simple", otherwise use the first available
# package from list_template_packages. When no package exists, package probes are SKIPPED
# (exit code 2, never reported as success).
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

function Send-Messages {
    param(
        [System.Diagnostics.Process]$Process,
        [string[]]$Messages
    )
    foreach ($message in $Messages) {
        $Process.StandardInput.WriteLine($message)
        $Process.StandardInput.Flush()
        Start-Sleep -Milliseconds 150
    }
}

function Test-HasId {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [int]$Id
    )
    $pattern = '"id"\s*:\s*' + $Id + '\s*[,}]'
    foreach ($line in $Lines) {
        if ($line -match $pattern) { return $true }
    }
    return $false
}

function Read-Responses {
    param(
        [System.Diagnostics.Process]$Process,
        [System.Collections.Generic.List[string]]$Lines,
        [int[]]$Ids,
        [int]$TimeoutSeconds = 20
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $pending = $false
        foreach ($id in $Ids) {
            if (-not (Test-HasId -Lines $Lines -Id $id)) { $pending = $true }
        }
        if (-not $pending) { break }

        # Keep at most one pending ReadLineAsync on the stream: issuing a second async read
        # while the previous one is still pending throws InvalidOperationException.
        if ($null -eq $script:PendingReadTask) {
            $script:PendingReadTask = $Process.StandardOutput.ReadLineAsync()
        }
        if ($script:PendingReadTask.Wait(500)) {
            $line = $script:PendingReadTask.Result
            $script:PendingReadTask = $null
            if ($null -eq $line) { break }
            $Lines.Add($line)
        }
    }
}

function Get-ResponseLine {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [int]$Id
    )
    $pattern = '"id"\s*:\s*' + $Id + '\s*[,}]'
    foreach ($line in $Lines) {
        if ($line -match $pattern) { return $line }
    }
    return $null
}

function Get-ToolPayload {
    param([string]$Line)
    if ($null -eq $Line) { return $null }
    $obj = $Line | ConvertFrom-Json
    if ($null -eq $obj.result) { return $null }
    if ($null -ne $obj.result.structuredContent) { return $obj.result.structuredContent }
    if ($null -ne $obj.result.content -and $obj.result.content.Count -gt 0) {
        $text = $obj.result.content[0].text
        if ($null -ne $text) {
            try {
                return ($text | ConvertFrom-Json)
            } catch {
                # A tool payload that is not JSON is a smoke failure signal: report it instead of
                # silently treating the package list as empty (which would misreport the run as SKIP).
                Write-Output ('[warn] tool payload is not JSON: ' + $_.Exception.Message)
                return $null
            }
        }
    }
    return $null
}

function Get-ToolStatus {
    param([string]$Line)
    if ($null -eq $Line) { return 'NO_RESULT' }
    $obj = $Line | ConvertFrom-Json
    if ($null -ne $obj.error) {
        return ('ERROR: ' + [string]$obj.error.message)
    }
    if ($null -eq $obj.result) { return 'NO_RESULT' }

    $text = ''
    if ($null -ne $obj.result.content -and $obj.result.content.Count -gt 0) {
        $text = [string]$obj.result.content[0].text
    }

    # Tool-level failures are carried inside the text payload as {"isError":true,...}
    $inner = $null
    if ($text.Length -gt 0) {
        try { $inner = $text | ConvertFrom-Json } catch { $inner = $null }
    }
    if ($null -ne $inner -and $null -ne $inner.isError) {
        if ($inner.isError -eq $true) {
            return ('ERROR: ' + [string]$inner.message)
        }
        return 'OK'
    }

    if ($obj.result.isError -eq $true) {
        if ($text.Length -gt 200) { $text = $text.Substring(0, 200) + '...' }
        return ('ERROR: ' + $text)
    }
    return 'OK'
}

function Write-ProbeStatus {
    param(
        [int]$Id,
        [string]$Name,
        [string]$Line
    )
    Write-Output ('[probe id=' + $Id + ' ' + $Name + '] ' + (Get-ToolStatus -Line $Line))
}

$baseMessages = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0.0.1"}}}',
    '{"jsonrpc":"2.0","method":"notifications/initialized"}',
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}',
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_template_packages","arguments":{}}}',
    '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"list_datasources","arguments":{}}}',
    '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"list_tables","arguments":{"datasource":"__nonexistent_datasource__"}}}'
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
if (-not $p.HasExited) { Write-Output ('process started: True (pid=' + $p.Id + ')') } else { Write-Output 'process started: False' }

$lines = New-Object System.Collections.Generic.List[string]
Send-Messages -Process $p -Messages $baseMessages
Read-Responses -Process $p -Lines $lines -Ids @(1, 2, 3, 6, 8)
$sw.Stop()
Write-Output ('base responses collected: ' + $lines.Count + ' lines, elapsed ' + [int]$sw.ElapsedMilliseconds + 'ms')

# Tool list evidence
$toolsLine = Get-ResponseLine -Lines $lines -Id 2
if ($null -ne $toolsLine) {
    $toolsObj = $toolsLine | ConvertFrom-Json
    $names = ($toolsObj.result.tools | ForEach-Object { $_.name }) -join ', '
    Write-Output ('[tools/list] count=' + $toolsObj.result.tools.Count + ' names: ' + $names)
}

# Resolve an available package dynamically (prefer "simple", otherwise the first one)
$pkgLine = Get-ResponseLine -Lines $lines -Id 3
$pkgPayload = Get-ToolPayload -Line $pkgLine
$packageNames = New-Object System.Collections.Generic.List[string]
if ($null -ne $pkgPayload -and $null -ne $pkgPayload.packages) {
    foreach ($package in $pkgPayload.packages) {
        if ($null -ne $package.name -and [string]$package.name -ne '') {
            $packageNames.Add([string]$package.name)
        }
    }
}
Write-Output ('[list_template_packages] count=' + $packageNames.Count + ' names: ' + ($packageNames -join ', '))

$pkg = $null
if ($packageNames -contains 'simple') {
    $pkg = 'simple'
} elseif ($packageNames.Count -gt 0) {
    $pkg = $packageNames[0]
}

if ($null -eq $pkg) {
    $p.StandardInput.Close()
    if (-not $p.WaitForExit(5000)) { $p.Kill() }
    $raw = Join-Path $OutDir 'smoke-responses.jsonl'
    [System.IO.File]::WriteAllLines($raw, $lines)
    Write-Output ('raw responses written: ' + $raw)
    Write-Output 'SKIP: no template package available; package-dependent probes (list_templates/read_template/get_type_map/check_type_mapping) were skipped. This is NOT a success.'
    exit 2
}

Write-Output ('[package] selected=' + $pkg + ' (prefer simple, otherwise first available package)')

# Send one request at a time and wait for its own response: this mirrors a real MCP client
# and avoids interleaved buffering on the stdio pipe.
Send-Messages -Process $p -Messages @('{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"list_templates","arguments":{"package":"' + $pkg + '","type":"java"}}}')
Read-Responses -Process $p -Lines $lines -Ids @(20)
Send-Messages -Process $p -Messages @('{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"list_templates","arguments":{"package":"' + $pkg + '"}}}')
Read-Responses -Process $p -Lines $lines -Ids @(21)
Send-Messages -Process $p -Messages @('{"jsonrpc":"2.0","id":22,"method":"tools/call","params":{"name":"get_type_map","arguments":{"package":"' + $pkg + '"}}}')
Read-Responses -Process $p -Lines $lines -Ids @(22)
Send-Messages -Process $p -Messages @('{"jsonrpc":"2.0","id":26,"method":"tools/call","params":{"name":"list_templates","arguments":{"package":"' + $pkg + '","type":"kotlin"}}}')
Read-Responses -Process $p -Lines $lines -Ids @(26)

$line20 = Get-ResponseLine -Lines $lines -Id 20
$line21 = Get-ResponseLine -Lines $lines -Id 21
$line22 = Get-ResponseLine -Lines $lines -Id 22
$line26 = Get-ResponseLine -Lines $lines -Id 26

# Pick a real template path from the package (enabled non-contract first, any path as fallback)
$templatePath = $null
foreach ($candidateLine in @($line20, $line21)) {
    $payload = Get-ToolPayload -Line $candidateLine
    if ($null -eq $payload -or $null -eq $payload.templates) { continue }
    foreach ($template in $payload.templates) {
        if ($null -eq $templatePath -and $template.enabled -eq $true -and $template.isContract -ne $true -and [string]$template.path -ne '') {
            $templatePath = [string]$template.path
        }
    }
}
if ($null -eq $templatePath) {
    foreach ($candidateLine in @($line20, $line21)) {
        $payload = Get-ToolPayload -Line $candidateLine
        if ($null -eq $payload -or $null -eq $payload.templates) { continue }
        foreach ($template in $payload.templates) {
            if ($null -eq $templatePath -and [string]$template.path -ne '') {
                $templatePath = [string]$template.path
            }
        }
    }
}

if ($null -ne $templatePath) {
    $escapedPath = $templatePath.Replace('\', '\\').Replace('"', '\"')
    Send-Messages -Process $p -Messages @('{"jsonrpc":"2.0","id":23,"method":"tools/call","params":{"name":"read_template","arguments":{"package":"' + $pkg + '","path":"' + $escapedPath + '"}}}')
    Read-Responses -Process $p -Lines $lines -Ids @(23)
}
Send-Messages -Process $p -Messages @('{"jsonrpc":"2.0","id":24,"method":"tools/call","params":{"name":"read_template","arguments":{"package":"' + $pkg + '","path":"../../../App.xaml.cs"}}}')
Read-Responses -Process $p -Lines $lines -Ids @(24)
Send-Messages -Process $p -Messages @('{"jsonrpc":"2.0","id":25,"method":"tools/call","params":{"name":"check_type_mapping","arguments":{"datasource":"__nonexistent_datasource__","tables":["t1"],"package":"' + $pkg + '"}}}')
Read-Responses -Process $p -Lines $lines -Ids @(25)

$line23 = Get-ResponseLine -Lines $lines -Id 23
$line24 = Get-ResponseLine -Lines $lines -Id 24
$line25 = Get-ResponseLine -Lines $lines -Id 25

$p.StandardInput.Close()
if (-not $p.WaitForExit(5000)) { $p.Kill() }

# Raw response evidence
$raw = Join-Path $OutDir 'smoke-responses.jsonl'
[System.IO.File]::WriteAllLines($raw, $lines)
Write-Output ('raw responses written: ' + $raw)
Write-Output ('[process] exitCode=' + $p.ExitCode)

# Probe evidence with explicit expectations
Write-ProbeStatus -Id 20 -Name 'list_templates(java)' -Line $line20
Write-ProbeStatus -Id 21 -Name 'list_templates(all)' -Line $line21
Write-ProbeStatus -Id 22 -Name 'get_type_map' -Line $line22
Write-ProbeStatus -Id 26 -Name 'list_templates(invalid language, expected error)' -Line $line26
if ($null -ne $templatePath) {
    Write-Output ('[template path] ' + $templatePath)
    Write-ProbeStatus -Id 23 -Name 'read_template(real)' -Line $line23
} else {
    Write-Output 'SKIP: package exposes no template path; read_template positive probe was skipped.'
}
Write-ProbeStatus -Id 24 -Name 'read_template(path traversal, expected error)' -Line $line24
Write-ProbeStatus -Id 25 -Name 'check_type_mapping(missing datasource, expected error)' -Line $line25

# Expected outcomes: positive probes OK, negative probes ERROR
$failures = New-Object System.Collections.Generic.List[string]
$positiveProbes = @(
    @{ Id = 20; Name = 'list_templates(java)'; Line = $line20 },
    @{ Id = 21; Name = 'list_templates(all)'; Line = $line21 },
    @{ Id = 22; Name = 'get_type_map'; Line = $line22 }
)
if ($null -ne $templatePath) {
    $positiveProbes += @{ Id = 23; Name = 'read_template(real)'; Line = $line23 }
}
foreach ($probe in $positiveProbes) {
    $status = Get-ToolStatus -Line $probe.Line
    if ($status -ne 'OK') { $failures.Add('probe ' + $probe.Id + ' ' + $probe.Name + ' expected OK, got: ' + $status) }
}
$negativeProbes = @(
    @{ Id = 26; Name = 'list_templates(invalid language)'; Line = $line26 },
    @{ Id = 24; Name = 'read_template(path traversal)'; Line = $line24 },
    @{ Id = 25; Name = 'check_type_mapping(missing datasource)'; Line = $line25 }
)
foreach ($probe in $negativeProbes) {
    $status = Get-ToolStatus -Line $probe.Line
    if (-not $status.StartsWith('ERROR')) { $failures.Add('probe ' + $probe.Id + ' ' + $probe.Name + ' expected ERROR, got: ' + $status) }
}

if ($failures.Count -gt 0) {
    Write-Output 'SMOKE FAILED'
    foreach ($failure in $failures) { Write-Output ('  - ' + $failure) }
    exit 1
}

Write-Output 'SMOKE OK'
exit 0
