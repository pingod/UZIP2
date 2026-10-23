<#
.SYNOPSIS
  Reproducible publish for UZIP2: builds BOTH shipping artifacts into .\artifacts\<version>\
  and prints a size + SHA-256 manifest.

.DESCRIPTION
  Produces (into .\artifacts\<ver>\):
    UZIP2.exe                                framework-dependent single-file (primary, ~8 MB, .NET 8 Desktop Runtime required)
    UZIP2.exe.sha256                         同名侧车，直接当 Release 附件上传即可
    UZIP-<ver>-win-x64-selfcontained.zip     self-contained single-file   (no runtime needed, larger)
    SHA256SUMS.txt                           checksums of both artifacts

  框架依赖产物刻意叫 UZIP2.exe 而不是带版本号的文件名：应用内自更新
  (UpdateService.PickAsset) 只认这个名字，名字对了发布时就不用手工改名——
  以前手工改过一次就漏改侧车，校验和直接对不上。

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

if (-not (Get-Command $Dotnet -ErrorAction SilentlyContinue)) {
  throw "dotnet not found ('$Dotnet'). Install the .NET 8 SDK or pass -Dotnet <full path>."
}

# 版本号的唯一读取点见 scripts/version.ps1 —— 四处声明（两个 csproj + 两个
# AssemblyInfo.cs）必须指向同一个版本，任何一处漂移都会让发布中止。
. (Join-Path $PSScriptRoot 'version.ps1')
$Ver = Get-UzipVersion -RepoRoot $RepoRoot

$OutRoot   = Join-Path $RepoRoot 'artifacts'
$OutVer    = Join-Path $OutRoot $Ver
$FdOut     = Join-Path $OutVer 'fd'
$ScOut     = Join-Path $OutVer 'sc'

function Publish($extra, $out) {
  if (Test-Path $out) { Remove-Item $out -Recurse -Force }
  # -v m（minimal）而不是 -v q：发布构建的警告必须在日志里看得见，
  # 否则"0 警告"只是本地 test 命令的结论，发布产物可能带着新问题出厂。
  $argv = @('publish', $Proj, '-c', 'Release', '-r', 'win-x64',
            '-p:PublishSingleFile=true', '-o', $out, '--nologo', '-v', 'm') + $extra
  # 非 Windows 上交叉构建 win-x64 需要显式放行（NETSDK1100）：
  # "若要在此操作系统上生成面向 Windows 的项目，请将 EnableWindowsTargeting 属性设置为 true"。
  # 在 Windows 上不加这个开关，避免掩盖真正的目标框架问题。
  # 注意不能用 $IsWindows：它只在 PowerShell 6+ 存在，5.1 里是 $null（会让判断反掉）。
  $onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
  if (-not $onWindows) { $argv += '-p:EnableWindowsTargeting=true' }
  Write-Host ('>> ' + $Dotnet + ' ' + ($argv -join ' ')) -ForegroundColor DarkGray
  & $Dotnet @argv
  if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }
}

Write-Host "Publishing UZIP2 v$Ver  (repo: $RepoRoot)" -ForegroundColor Cyan

# 1) Framework-dependent single-file (primary download, in-place updatable by the app itself).
# 用 -p:SelfContained=false 而不是 --self-contained false：.NET 10 的 CLI 把
# --self-contained 改成了纯开关，"--self-contained false"（空格形式）里的 false 不再
# 被当作取值，实测结果是把整个运行时打进单文件（144 MB，而正确值是 9 MB）。
# -p: 形式的属性不受 CLI 解析规则影响，SDK 8/10 都得到同样的产物。
Publish @('-p:SelfContained=false') $FdOut
# 名字必须与 UpdateService.PickAsset 认的完全一致，Release 附件直接传这两个文件。
$FdExe = Join-Path $OutVer 'UZIP2.exe'
Copy-Item (Join-Path $FdOut 'UZIP2.exe') $FdExe -Force

$Artifacts = @($FdExe)

# 2) Self-contained single-file, zipped (for machines without the .NET 8 Desktop Runtime).
if (-not $SkipSelfContained) {
  Publish @('-p:SelfContained=true', '-p:IncludeNativeLibrariesForSelfExtract=true') $ScOut
  $ScZip = Join-Path $OutVer "UZIP-$Ver-win-x64-selfcontained.zip"
  if (Test-Path $ScZip) { Remove-Item $ScZip -Force }
  # v3.6 起 App.config / *.dll.config 模板残留已从工程清掉，自包含产物目录里只剩 UZIP2.exe；
  # 显式枚举目录内文件打包（PS5.1 的 Compress-Archive 对通配 -Path 会在解析阶段出错）。
  $scFiles = @(Get-ChildItem -Path $ScOut -File | Select-Object -ExpandProperty FullName)
  Compress-Archive -Path $scFiles -DestinationPath $ScZip -Force
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
  $leaf = Split-Path -Leaf $a
  # 应用内自更新会核对同名 .sha256 侧车（sha256sum 格式："hash  文件名"）。
  # 上传改名时侧车要跟着改：UZIP2.exe 对应 UZIP2.exe.sha256。
  Set-Content -Path "$a.sha256" -Value "$h  $leaf" -Encoding ASCII
  Write-Host ('  {0}  {1}' -f (Mb $a), $leaf)
  Write-Host ('              {0}.sha256' -f $leaf) -ForegroundColor DarkGray
  Add-Content -Path $sums -Value ("{0}  {1}`r" -f $h, $leaf) -Encoding ASCII
}
Add-Content -Path $sums -Value ('# fd single-file exe (uncompressed on disk): {0}' -f (Mb (Join-Path $FdOut 'UZIP2.exe'))) -Encoding ASCII
Write-Host "  wrote $(Split-Path -Leaf $sums)" -ForegroundColor DarkGray
Write-Host 'Done.' -ForegroundColor Cyan
