<#
.SYNOPSIS
  版本号的唯一读取点：两个发布脚本都从这里取，不再各自写一遍正则。

.DESCRIPTION
  UZIP2 的版本号历史上散落在四处，发布前要人工同步：
    UZIP2.Avalonia/UZIP2.Avalonia.csproj   <Version>           Linux 图形版（实际发货产物）
    UZIP2.Cli/UZIP2.Cli.csproj             <Version>           无界面宿主
    UZIP2/Properties/AssemblyInfo.cs       AssemblyVersion      WPF 版
    UZIP2.Core/Properties/AssemblyInfo.cs  AssemblyVersion      共享核心

  以前 publish.ps1 与 publish-linux.ps1 各读一处、各写一份正则，读的地方还不一样
  （Windows 读 WPF 的 AssemblyInfo，Linux 也读 WPF 的 AssemblyInfo），
  一旦其中一处漏改，产物名就会和程序自报的版本不一致。

  这里改成：以 Linux 图形版 csproj 为**唯一权威**（它才是真正发货的产物），
  其余三处必须与它一致，不一致就抛错并列出全部差异 —— 宁可发布中止，也不要发出
  版本号自相矛盾的包。

.EXAMPLE
  . "$PSScriptRoot/version.ps1"
  $Ver = Get-UzipVersion -RepoRoot $RepoRoot
#>

# 权威来源与"必须跟随"的声明位置。顺序即报错时的展示顺序。
$script:UzipVersionSources = @(
  @{ Path = 'UZIP2.Avalonia/UZIP2.Avalonia.csproj';                        Kind = 'csproj'; Canonical = $true  },
  @{ Path = 'UZIP2.Cli/UZIP2.Cli.csproj';                                  Kind = 'csproj'; Canonical = $false },
  @{ Path = 'UZIP2/Properties/AssemblyInfo.cs';                            Kind = 'assembly'; Canonical = $false },
  @{ Path = 'UZIP2.Core/Properties/AssemblyInfo.cs';                       Kind = 'assembly'; Canonical = $false }
)

function Read-UzipVersionFrom([string]$File, [string]$Kind) {
  if (-not (Test-Path $File)) { return $null }
  switch ($Kind) {
    'csproj' {
      $m = Select-String -Path $File -Pattern '<Version>\s*([0-9]+(\.[0-9]+){1,3})\s*</Version>' | Select-Object -First 1
      if (-not $m) { return $null }
      return $m.Matches[0].Groups[1].Value
    }
    'assembly' {
      # 只认真正生效的那一行；注释掉的 [assembly: AssemblyVersion("1.0.*")] 不算
      $m = Select-String -Path $File -Pattern '^\s*\[assembly:\s*AssemblyVersion\("([0-9]+(\.[0-9]+){1,3})"\)' | Select-Object -First 1
      if (-not $m) { return $null }
      return $m.Matches[0].Groups[1].Value
    }
  }
  return $null
}

function Get-UzipVersionSources([string]$RepoRoot) {
  if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
  $rows = @()
  foreach ($src in $script:UzipVersionSources) {
    $full = Join-Path $RepoRoot $src.Path
    $rows += [pscustomobject]@{
      File      = $src.Path
      Canonical = [bool]$src.Canonical
      Version   = Read-UzipVersionFrom $full $src.Kind
    }
  }
  return $rows
}

<#
  返回 x.y.z 形式的版本号（去掉可能存在的第 4 段）。
  四处的声明必须完全一致，否则抛错。
#>
function Get-UzipVersion([string]$RepoRoot) {
  if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

  $rows = Get-UzipVersionSources $RepoRoot
  $canonical = @($rows | Where-Object { $_.Canonical })[0]

  if (-not $canonical.Version) {
    throw "读不到权威版本号：$($canonical.File) 里没有 <Version>x.y.z</Version>"
  }

  # AssemblyInfo 习惯写四段（3.12.0.0），csproj 写三段（3.12.0）——它们是同一个版本，
  # 所以先统一裁到三段再比较，否则每次发布都会被自己的格式差异挡住。
  $norm = { param($v) (($v.Split('.') | Select-Object -First 3) -join '.') }
  $want = & $norm $canonical.Version

  $missing = @($rows | Where-Object { -not $_.Version })
  $drift   = @($rows | Where-Object { $_.Version -and ((& $norm $_.Version) -ne $want) })

  if ($missing.Count -gt 0 -or $drift.Count -gt 0) {
    $detail = ($rows | ForEach-Object {
      $mark = if ($_.Canonical) { ' [权威]' } else { '' }
      $val  = if ($_.Version) { $_.Version } else { '<读不到>' }
      "    $($_.File): $val$mark"
    }) -join [Environment]::NewLine
    throw @"
版本号不一致，发布中止。四处声明必须指向同一个版本：
$detail

以 $($canonical.File) 为准（当前 $want），
其余各处改成同一个版本号后重试。
"@
  }

  # 产物名统一用三段：3.12.0.0 -> 3.12.0
  return $want
}
