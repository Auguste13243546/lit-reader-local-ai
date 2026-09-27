# Install LitReader to a stable folder and (optionally) create shortcuts.
#
# No hardcoded personal paths: the install location defaults to
# %LOCALAPPDATA%\LitReader and can be overridden with -InstallDir.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\install-app.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\install-app.ps1 -InstallDir "D:\Apps\LitReader"
#   powershell -ExecutionPolicy Bypass -File scripts\install-app.ps1 -NoShortcuts
#   powershell -ExecutionPolicy Bypass -File scripts\install-app.ps1 -AutoStart

param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'LitReader'),
    [switch]$NoShortcuts,
    [switch]$AutoStart
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir
$srcExe    = Join-Path $repoRoot 'src\bin\LitReaderFloat.exe'
$dstExe    = Join-Path $InstallDir 'LitReaderFloat.exe'

if (-not (Test-Path $srcExe)) {
    throw "built exe not found: $srcExe`nRun scripts\build.ps1 first."
}

# Stop a running instance so the file is not locked
Get-Process -Name 'LitReaderFloat' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item $srcExe $dstExe -Force
Write-Output ("installed -> {0}  ({1} KB)" -f $dstExe, [math]::Round((Get-Item $dstExe).Length / 1KB, 1))

if (-not $NoShortcuts) {
    $sh = New-Object -ComObject WScript.Shell

    function New-Lnk($path, $extraArgs) {
        $lnk = $sh.CreateShortcut($path)
        $lnk.TargetPath = $dstExe
        $lnk.Arguments = $extraArgs
        $lnk.WorkingDirectory = $InstallDir
        $lnk.Description = 'LitReader - local AI reading assistant'
        $lnk.WindowStyle = 1
        $lnk.Save()
        Write-Output ("shortcut -> {0}  args=[{1}]" -f $path, $extraArgs)
    }

    $desktop   = $sh.SpecialFolders('Desktop')
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'

    # Desktop / Start menu: launch and show the window
    New-Lnk (Join-Path $desktop 'LitReader.lnk') ''
    New-Lnk (Join-Path $startMenu 'LitReader.lnk') ''

    if ($AutoStart) {
        # Startup folder: launch hidden (tray icon only); press the hotkey to show it
        New-Lnk (Join-Path (Join-Path $startMenu 'Startup') 'LitReader.lnk') '--hidden'
    }
}

Write-Output ''
Write-Output 'install OK'
Write-Output ("exe     : {0}" -f $dstExe)
Write-Output ("data dir: {0}" -f (Join-Path $InstallDir 'data'))
Write-Output ''
Write-Output 'Next: start it and press the hotkey (Alt+Z by default).'
