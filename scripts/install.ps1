<#
.SYNOPSIS
  Dependency-free per-user installer for UZIP2 (no NSIS / Inno / admin rights required).

.DESCRIPTION
  UZIP2 is a portable app: every path (Config\, logs\, 7-Zip\) is resolved relative to the
  exe, and the Explorer context menu is written under HKCU\Software\Classes pointing at the
  running exe. So "install" simply means: place UZIP2.exe in a folder, optionally drop a
  7-Zip\ beside it, register the shell menu, and add a Start-Menu shortcut.

  It NEVER touches an existing Config\ folder, so re-running it upgrades in place safely.

.PARAMETER Source
  The UZIP2.exe to install, OR a folder that contains UZIP2.exe.
  Defaults to the newest framework-dependent artifact under .\artifacts.

.PARAMETER Destination
  Install folder. Default: %LOCALAPPDATA%\Programs\UZIP2

.PARAMETER SevenZipDir
  Optional folder containing 7z.exe (e.g. C:\Program Files\7-Zip). It is copied to
  <Destination>\7-Zip\ so the app is fully self-contained. If omitted, the app falls back
  to a system-installed 7-Zip at run time.

.PARAMETER NoShellMenu / -NoShortcut
  Skip context-menu registration / Start-Menu shortcut.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -SevenZipDir 'C:\Program Files\7-Zip'
#>
[CmdletBinding()]
param(
  [string]$Source,
  [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\UZIP2'),
  [string]$SevenZipDir,
  [switch]$NoShellMenu,
  [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

# --- resolve the source exe -------------------------------------------------
if (-not $Source) {
  $cand = Get-ChildItem -Path (Join-Path $RepoRoot 'artifacts') -Recurse -Filter 'UZIP-*-win-x64.exe' -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $cand) { throw "No -Source given and no artifact found under $RepoRoot\artifacts. Run scripts\publish.ps1 first or pass -Source." }
  $Source = $cand.FullName
}
if (Test-Path $Source -PathType Container) { $Source = Join-Path $Source 'UZIP2.exe' }
if (-not (Test-Path $Source -PathType Leaf)) { throw "Source exe not found: $Source" }
$Source = (Resolve-Path $Source).Path

Write-Host "Installing UZIP2" -ForegroundColor Cyan
Write-Host "  from : $Source"
Write-Host "  to   : $Destination"

# stop a running instance so the exe can be overwritten
Get-Process UZIP2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$DstExe = Join-Path $Destination 'UZIP2.exe'
Copy-Item $Source $DstExe -Force
# carry config files if they shipped next to the source (harmless if absent)
foreach ($cfg in 'App.config', 'UZIP2.dll.config') {
  $s = Join-Path (Split-Path $Source) $cfg
  if (Test-Path $s) { Copy-Item $s (Join-Path $Destination $cfg) -Force }
}
Write-Host '  copied UZIP2.exe' -ForegroundColor DarkGray

# --- optional bundled 7-Zip -------------------------------------------------
if ($SevenZipDir) {
  if (-not (Test-Path (Join-Path $SevenZipDir '7z.exe'))) { throw "No 7z.exe in -SevenZipDir: $SevenZipDir" }
  $z = Join-Path $Destination '7-Zip'
  New-Item -ItemType Directory -Path $z -Force | Out-Null
  Copy-Item (Join-Path $SevenZipDir '*') $z -Recurse -Force
  Write-Host "  bundled 7-Zip -> $z" -ForegroundColor DarkGray
}

# --- register the Explorer context menu (HKCU, no admin) --------------------
if (-not $NoShellMenu) {
  & $DstExe shell register | Out-Host
  if ($LASTEXITCODE -ne 0) { Write-Warning "shell register returned $LASTEXITCODE" }
}

# --- Start-Menu shortcut ----------------------------------------------------
if (-not $NoShortcut) {
  $lnkDir  = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
  New-Item -ItemType Directory -Path $lnkDir -Force | Out-Null
  $lnkPath = Join-Path $lnkDir 'UZIP2.lnk'
  $ws  = New-Object -ComObject WScript.Shell
  $sc  = $ws.CreateShortcut($lnkPath)
  $sc.TargetPath       = $DstExe
  $sc.WorkingDirectory = $Destination
  $sc.IconLocation     = "$DstExe,0"
  $sc.Description      = 'UZIP2 archive tool'
  $sc.Save()
  Write-Host "  Start-Menu shortcut -> $lnkPath" -ForegroundColor DarkGray
}

Write-Host ''
Write-Host "Installed. Launch from the Start Menu, or run: `"$DstExe`"" -ForegroundColor Green
