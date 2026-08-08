# ============================================================
# DbCodeGen - Clean cache and package script
#
# What it does:
#   1. dotnet clean the solution
#   2. Delete all bin/obj cache dirs under src and test, plus the
#      old dist publish dir, for a truly clean rebuild
#   3. dotnet publish (Release) to ./dist
#
# Usage:
#   .\clean-publish.ps1
#   or bypass execution policy:
#   powershell -ExecutionPolicy Bypass -File .\clean-publish.ps1
# ============================================================

# Stop on any error so a failed step does not cascade
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Repo root = this script's directory, so it works from any CWD
$root = $PSScriptRoot
$solution = Join-Path $root 'DbCodeGen.sln'
$appProject = Join-Path $root 'src\DbCodeGen.App\DbCodeGen.App.csproj'
$distDir = Join-Path $root 'dist'

Write-Host '==> 1/3 dotnet clean' -ForegroundColor Cyan
dotnet clean $solution
if ($LASTEXITCODE -ne 0) {
    throw "dotnet clean failed, exit code $LASTEXITCODE"
}

Write-Host '==> 2/3 delete bin/obj cache dirs and old dist' -ForegroundColor Cyan
# Recursively remove every bin/obj under src and test for a full clean
Get-ChildItem -Path (Join-Path $root 'src'), (Join-Path $root 'test') -Recurse -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('bin', 'obj') } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# Clear the old publish dir so stale files are not carried over
if (Test-Path $distDir) {
    Remove-Item -Path $distDir -Recurse -Force
}

Write-Host '==> 3/3 dotnet publish (Release)' -ForegroundColor Cyan
dotnet publish $appProject -c Release -o $distDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed, exit code $LASTEXITCODE"
}

Write-Host "==> Done. Output: $distDir" -ForegroundColor Green
Write-Host "    Executable: $distDir\DbCodeGen.App.exe" -ForegroundColor Green
