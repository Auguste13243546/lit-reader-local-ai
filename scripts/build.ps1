# Build LitReader with the .NET Framework MSBuild + WPF targets.
#
# No hardcoded personal paths: everything is derived from this script's location.
# Requires .NET Framework 4.x (ships with Windows 10/11).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -TargetFramework v4.8

param(
    [string]$TargetFramework = "v4.8",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir
$proj      = Join-Path $repoRoot 'src\LitReader.csproj'
$outExe    = Join-Path $repoRoot 'src\bin\LitReaderFloat.exe'

if (-not (Test-Path $proj)) { throw "project not found: $proj" }

# MSBuild from the .NET Framework (64-bit preferred)
$msbuild = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $msbuild) {
    throw ("MSBuild.exe not found at the .NET Framework v4.0.30319 path.`n" +
           "Install the .NET Framework Developer Pack / SDK, or Visual Studio Build Tools,`n" +
           "which provide MSBuild and the WPF build targets.")
}

Write-Output "repo root : $repoRoot"
Write-Output "project   : $proj"
Write-Output "msbuild   : $msbuild"
Write-Output ""

& $msbuild $proj /t:Rebuild /p:Configuration=$Configuration `
    /p:TargetFrameworkVersion=$TargetFramework /v:minimal /nologo |
    Where-Object { $_ -notmatch '^\s*$' }

Write-Output ""
if (Test-Path $outExe) {
    $f = Get-Item $outExe
    Write-Output "BUILD_OK"
    Write-Output ("exe  : {0}" -f $f.FullName)
    Write-Output ("size : {0} KB" -f [math]::Round($f.Length / 1KB, 1))
} else {
    Write-Output "BUILD_FAILED - exe not produced"
    exit 1
}
