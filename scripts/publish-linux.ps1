<#
.SYNOPSIS
  构建 Linux 版 UZIP2（无界面 CLI）并打包成 tar.gz。

.DESCRIPTION
  产出（artifacts/<ver>/ 下）：
    UZIP2-<ver>-linux-x64.tar.gz        自包含单文件（不依赖 .NET 运行时）
    UZIP2-<ver>-linux-arm64.tar.gz
    每个包配 .sha256 侧车 + SHA256SUMS.txt

  包内布局：
    UZIP2-<ver>-linux-<rid>/UZIP2       可执行文件
    UZIP2-<ver>-linux-<rid>/README.md   安装与用法说明

  注意：Windows 上也能跑（需要 dotnet SDK + tar），交叉发布由 SDK 完成，
  产物本身必须在 Linux 上执行 —— 本地可用 WSL 验证（见 README）。
#>
[CmdletBinding()]
param(
  [string]$Dotnet = 'dotnet',
  [string[]]$Rids = @('linux-x64', 'linux-arm64')
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Proj     = Join-Path $RepoRoot 'UZIP2.Avalonia/UZIP2.Avalonia.csproj'
$AiFile   = Join-Path $RepoRoot 'UZIP2/Properties/AssemblyInfo.cs'

if (-not (Get-Command $Dotnet -ErrorAction SilentlyContinue)) {
  throw "dotnet not found ('$Dotnet'). Pass -Dotnet <full path>."
}
if (-not (Get-Command tar -ErrorAction SilentlyContinue)) {
  throw 'tar not found (Windows 10+ ships bsdtar; Linux has it).'
}

# 打包器选择：Windows 的 bsdtar 不支持 --mode，Git 自带的 GNU tar 可以。
# 归档里的权限位必须显式写成 0755，否则解包后 ./UZIP2 不可执行（Windows 上没有 exec 位）。
$TarExe = (Get-Command tar).Source
$TarHasMode = $true
if ($env:OS -eq 'Windows_NT') {
  # PATH 上的 tar 可能是 Git 的 GNU tar（支持 --mode），也可能是 System32 的 bsdtar（不支持）
  $gnu = $TarExe
  if ($gnu -like '*\System32\tar.exe') {
    foreach ($candidate in @((Join-Path $env:ProgramFiles 'Git\usr\bin\tar.exe'),
                            (Join-Path ${env:ProgramFiles(x86)} 'Git\usr\bin\tar.exe'))) {
      if (Test-Path $candidate) { $gnu = $candidate; break }
    }
  }
  $TarExe = $gnu
  $TarHasMode = $TarExe -notlike '*\System32\tar.exe'
}

$m = Select-String -Path $AiFile -Pattern '^\s*\[assembly:\s*AssemblyVersion\("([^"]+)"\)' | Select-Object -First 1
if (-not $m) { throw 'Could not read AssemblyVersion from AssemblyInfo.cs' }
$VerFull = $m.Matches[0].Groups[1].Value
$Ver     = ($VerFull.Split('.') | Select-Object -First 3) -join '.'

$OutVer = Join-Path (Join-Path $RepoRoot 'artifacts') $Ver
New-Item -ItemType Directory -Path $OutVer -Force | Out-Null

$readme = @"
UZIP2 $Ver —— Linux 版（图形界面 + 命令行）

安装：
  tar -xzf UZIP2-$Ver-linux-<rid>.tar.gz
  sudo install -Dm755 UZIP2-$Ver-linux-<rid>/UZIP2 /usr/bin/uzip2

桌面菜单项（可选，装完就能在应用列表里搜到 UZIP2）：
  sudo install -Dm644 UZIP2-$Ver-linux-<rid>/uzip2.desktop /usr/share/applications/uzip2.desktop
  sudo install -Dm644 UZIP2-$Ver-linux-<rid>/uzip2.png /usr/share/icons/hicolor/128x128/apps/uzip2.png

依赖：7-Zip（Arch: pacman -S 7zip；Debian/Ubuntu: apt install 7zip / p7zip-full）
配置与日志：~/.config/uzip2 与 ~/.local/state/uzip2（遵循 XDG）
密码本：AES-256-GCM，密钥在 ~/.config/uzip2/secret.key（0600）

常用命令：
  uzip2 --help
  uzip2 list a.zip
  uzip2 extract a.zip -o out
  uzip2 compress 文件1 文件2 -o . --name out.7z
  uzip2 diff a.zip b.zip
"@

$artifacts = @()
foreach ($rid in $Rids) {
  $publishDir = Join-Path $OutVer "cli-$rid"
  if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

  Write-Host ">> publishing $rid" -ForegroundColor Cyan
  & $Dotnet publish $Proj -c Release -r $rid --self-contained true -p:PublishSingleFile=true `
      -p:IncludeNativeLibrariesForSelfExtract=true `
      -o $publishDir --nologo -v m
  if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid (exit $LASTEXITCODE)" }

  $arch = $rid -replace '^linux-', ''
  $dirLeaf = "UZIP2-$Ver-linux-$arch"
  $stage = Join-Path $OutVer $dirLeaf          # 目录名就是包内顶层目录名
  if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
  New-Item -ItemType Directory -Path $stage -Force | Out-Null
  # 发布出来的可执行名是 UZIP2.Avalonia；装到系统里统一叫 uzip2
  Copy-Item (Join-Path $publishDir 'UZIP2.Avalonia') (Join-Path $stage 'UZIP2') -Force
  # 桌面入口与图标：tarball 里一并带上，安装脚本/PKGBUILD-bin 直接用
  Copy-Item (Join-Path $RepoRoot 'packaging/linux/uzip2.desktop') $stage -Force
  Copy-Item (Join-Path $RepoRoot 'packaging/linux/uzip2.png') $stage -Force
  Copy-Item (Join-Path $RepoRoot 'packaging/linux/uzip2-48.png') $stage -Force
  Set-Content -Path (Join-Path $stage 'README.md') -Value $readme -Encoding UTF8

  $tarName = "UZIP2-$Ver-linux-$arch.tar.gz"
  $tar = Join-Path $OutVer $tarName
  if (Test-Path $tar) { Remove-Item $tar -Force }
  Push-Location $OutVer
  try {
    if ($TarHasMode) { & $TarExe --mode=0755 -czf $tarName $dirLeaf }
    else { & $TarExe -czf $tarName $dirLeaf }
    if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid (exit $LASTEXITCODE)" }
  }
  finally { Pop-Location }

  Remove-Item $stage -Recurse -Force
  Remove-Item $publishDir -Recurse -Force
  $artifacts += $tar
}

$sums = Join-Path $OutVer 'SHA256SUMS-linux.txt'
Set-Content -Path $sums -Value "# UZIP2 v$Ver linux builds" -Encoding ASCII
foreach ($a in $artifacts) {
  $hash = (Get-FileHash $a -Algorithm SHA256).Hash.ToLower()
  $leaf = Split-Path -Leaf $a
  Set-Content -Path "$a.sha256" -Value "$hash  $leaf" -Encoding ASCII
  Add-Content -Path $sums -Value ("$hash  $leaf") -Encoding ASCII
  Write-Host ('  {0,7:N1} MB  {1}' -f ((Get-Item $a).Length / 1MB), $leaf) -ForegroundColor Green
}
Write-Host "Done: $OutVer" -ForegroundColor Cyan
