<#
.SYNOPSIS
  Remove a UZIP2 per-user install created by scripts\install.ps1.

.DESCRIPTION
  Default (safe): unregister the Explorer context menu, delete the Start-Menu shortcut and
  UZIP2.exe, and leave Config\ + logs\ untouched (they are your archives passwords and history).

  -Purge additionally deletes the whole install folder. Because Config\ holds your live data,
  Purge shows the exact Config path and asks to type YES (or pass -Force) before deleting it.

.PARAMETER Destination
  Must match the install folder used at install time. Default: %LOCALAPPDATA%\Programs\UZIP2
#>
[CmdletBinding()]
param(
  [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\UZIP2'),
  [switch]$Purge,
  [switch]$Force
)
$ErrorActionPreference = 'Stop'

$DstExe = Join-Path $Destination 'UZIP2.exe'
$Cfg    = Join-Path $Destination 'Config'

# unregister while the exe still exists (it removes HKCU\Software\Classes\...\UZIP.* keys)
if (Test-Path $DstExe) {
  & $DstExe shell unregister | Out-Host
} else {
  Write-Warning "UZIP2.exe not found at $Destination; skipping shell unregister."
}

$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\UZIP2.lnk'
if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "  removed shortcut $lnk" -ForegroundColor DarkGray }

Get-Process UZIP2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

if ($Purge) {
  if (Test-Path $Cfg) {
    Write-Host ''
    Write-Host "PURGE will permanently delete your Config folder (passwords / history):" -ForegroundColor Yellow
    Write-Host "    $Cfg" -ForegroundColor Yellow
    if (-not $Force) {
      $ans = Read-Host "Type YES to delete it, anything else to keep Config\ and just remove the program"
      if ($ans -ne 'YES') {
        Write-Host 'Keeping Config\; removing program files only.' -ForegroundColor Cyan
        $Purge = $false
      }
    }
  }
}

if ($Purge) {
  if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force; Write-Host "  deleted $Destination" -ForegroundColor DarkGray }
} else {
  foreach ($f in 'UZIP2.exe', 'App.config', 'UZIP2.dll.config') {
    $p = Join-Path $Destination $f; if (Test-Path $p) { Remove-Item $p -Force }
  }
  $z = Join-Path $Destination '7-Zip'; if (Test-Path $z) { Remove-Item $z -Recurse -Force }
  Write-Host '  removed program files (Config\ + logs\ kept)' -ForegroundColor DarkGray
}

Write-Host 'Uninstalled.' -ForegroundColor Green
