<#
.SYNOPSIS
  Reproducible publish for UZIP2: builds BOTH shipping artifacts into .\artifacts\<version>\
  and prints a size + SHA-256 manifest.

.DESCRIPTION
  Produces (into .\artifacts\<ver>\):
    UZIP-<ver>-win-x64.exe                 framework-dependent single-file (primary, ~8 MB, .NET 8 Desktop Runtime required)
    UZIP-<ver>-win-x64-selfcontained.zip   self-contained single-file   (no runtime needed, larger)
    SHA256SUMS.txt                          checksums of both artifacts

  The csproj trims both artifacts (English-only satellites, no .pdb, and deflate
  compression for the self-contained single-file) automatically.

  Run from anywhere; paths are resolved relative to this script. Requires `dotnet` on PATH
  (or pass -Dotnet <path-to-dotnet>).

.EXAMPLE
  pwsh scripts/publish.ps1
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Dotnet D:\dotnet-sdk-8\dotnet.exe
#>
[CmdletBinding()]
param(
  [string]$Dotnet = 'dotnet',
  [switch]$SkipSelfContained
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Proj     = Join-Path $RepoRoot 'UZIP2\UZIP2.csproj'
$AiFile   = Join-Path $RepoRoot 'UZIP2\Properties\AssemblyInfo.cs'

if (-not (Get-Command $Dotnet -ErrorAction SilentlyContinue)) {
  throw "dotnet not found ('$Dotnet'). Install the .NET 8 SDK or pass -Dotnet <full path>."
}

# Read version from Properties/AssemblyInfo.cs so the artifact name can never drift from the build.
$m = Select-String -Path $AiFile -Pattern '^\s*\[assembly:\s*AssemblyVersion\("([^"]+)"\)' | Select-Object -First 1
if (-not $m) { throw 'Could not read AssemblyVersion from AssemblyInfo.cs' }
$VerFull = $m.Matches[0].Groups[1].Value            # e.g. 3.4.0.0
$Ver     = ($VerFull.Split('.') | Select-Object -First 3) -join '.'  # 3.4.0

$OutRoot   = Join-Path $RepoRoot 'artifacts'
$OutVer    = Join-Path $OutRoot $Ver
$FdOut     = Join-Path $OutVer 'fd'
$ScOut     = Join-Path $OutVer 'sc'

function Publish($extra, $out) {
  if (Test-Path $out) { Remove-Item $out -Recurse -Force }
  $argv = @('publish', $Proj, '-c', 'Release', '-r', 'win-x64',
            '-p:PublishSingleFile=true', '-o', $out, '--nologo', '-v', 'q') + $extra
  Write-Host ('>> ' + $Dotnet + ' ' + ($argv -join ' ')) -ForegroundColor DarkGray
  & $Dotnet @argv
  if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }
}

Write-Host "Publishing UZIP2 v$Ver  (repo: $RepoRoot)" -ForegroundColor Cyan

# 1) Framework-dependent single-file (primary download, in-place updatable by the app itself).
Publish @('--self-contained', 'false') $FdOut
$FdExe = Join-Path $OutVer "UZIP-$Ver-win-x64.exe"
Copy-Item (Join-Path $FdOut 'UZIP2.exe') $FdExe -Force

$Artifacts = @($FdExe)

# 2) Self-contained single-file, zipped (for machines without the .NET 8 Desktop Runtime).
if (-not $SkipSelfContained) {
  Publish @('--self-contained', 'true', '-p:IncludeNativeLibrariesForSelfExtract=true') $ScOut
  $ScZip = Join-Path $OutVer "UZIP-$Ver-win-x64-selfcontained.zip"
  if (Test-Path $ScZip) { Remove-Item $ScZip -Force }
  Compress-Archive -Path (Join-Path $ScOut 'UZIP2.exe'),
                       (Join-Path $ScOut 'App.config'),
                       (Join-Path $ScOut 'UZIP2.dll.config') `
                   -DestinationPath $ScZip -Force
  $Artifacts += $ScZip
}

# 3) Manifest: sizes + SHA-256.
function Mb($p) { '{0,7:N1} MB' -f ((Get-Item $p).Length / 1MB) }
$sums = Join-Path $OutVer 'SHA256SUMS.txt'
Set-Content -Path $sums -Value "# UZIP2 v$Ver build manifest`r`n" -Encoding ASCII
Write-Host ''
Write-Host "Artifacts in $OutVer" -ForegroundColor Green
foreach ($a in $Artifacts) {
  $h = (Get-FileHash $a -Algorithm SHA256).Hash.ToLower()
  Write-Host ('  {0}  {1}' -f (Mb $a), (Split-Path -Leaf $a))
  Add-Content -Path $sums -Value ("{0}  {1}`r" -f $h, (Split-Path -Leaf $a)) -Encoding ASCII
}
Add-Content -Path $sums -Value ('# fd single-file exe (uncompressed on disk): {0}' -f (Mb (Join-Path $FdOut 'UZIP2.exe'))) -Encoding ASCII
Write-Host "  wrote $(Split-Path -Leaf $sums)" -ForegroundColor DarkGray
Write-Host 'Done.' -ForegroundColor Cyan
