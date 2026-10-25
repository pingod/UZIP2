# Arch Linux 打包

两个 PKGBUILD，二选一：

| 文件 | 说明 | 依赖 |
|---|---|---|
| `PKGBUILD` | 源码构建（从 tag 归档拉源码，`dotnet publish`） | `dotnet-sdk`、`7zip` |
| `PKGBUILD-bin` | 直接用 Release 里的自包含 tar.gz | `7zip` |

```bash
cd packaging/arch
makepkg -si                 # 源码包
makepkg -si -p PKGBUILD-bin # 预编译包（两架构的 sha256 来自 Release 侧车）
```

产物装到 `/usr/bin/uzip2`，配置在 `~/.config/uzip2`。

## 必须保留 `options=(!strip !debug)`

.NET 单文件发布的 ELF 末尾追加了 bundle 数据，makepkg 默认的 `strip` 会把它切坏，
运行时报：

```
Failure processing application bundle; possible file corruption.
Arithmetic overflow while reading bundle.
```

两个 PKGBUILD 都已关掉 strip 与 debug 分包（实测验证：关掉后 82 MB 二进制正常输出 `UZIP 3.12.0`）。

## aarch64

`arch=('x86_64' 'aarch64')`，构建时按 `$CARCH` 选 `linux-x64` / `linux-arm64` RID。
在 x86_64 上给 aarch64 打包需要 Arch Linux ARM 环境（CI 用 `archlinux` 容器只构建本机架构）。

`PKGBUILD-bin` 用 makepkg 的**架构专属数组**（`source_x86_64` / `sha256sums_x86_64` /
`source_aarch64` / `sha256sums_aarch64`），两种架构各校验各自的包。两个 hash 都必须来自
Release 页面附带的 `.sha256` 侧车 —— 不能拿本地构建的 tar.gz 去算，gzip 头带 mtime，
同一份代码两次打包字节流不同。回填/核对用 `scripts/aur-sums.sh`（`--print` 只打印、
`--fill` 回填、`--srcinfo` 生成 AUR 需要的 `.SRCINFO`）。

## 为什么源码 `PKGBUILD` 的 `sha256sums` 是 `SKIP`

不是省事，是**数学上填不对**：`packaging/arch/PKGBUILD` 自己就在它下载的那个
`archive/<tag>.tar.gz` 里。往 `sha256sums` 里填真值 → `PKGBUILD` 变了 → 归档变了 →
hash 又变了，永远追不上。

所以 `scripts/aur-sums.sh --fill` 只对 `PKGBUILD-bin` 有意义；对源码包它会一直报
「不一致」，这是预期行为，不要照着它回填。

填 `SKIP` 之后完整性靠三件事兜住：

1. GitHub 的 tag 归档由 tag 唯一决定，没有篡改空间（服务端重新打包时字节可能变，但内容不会变）；
2. CI 的 `Validate both PKGBUILDs` 会检查源码包**确实是 `SKIP` 且注释里写了理由**，并真跑一遍 `makepkg` 构建；
3. Release 页面里每个产物都逐字给出 `.sha256`，用户装的是产物而不是源码包。

`PKGBUILD-bin` 不受这个限制：它下载的是 Release 上的二进制，不在归档内，所以那两个 hash
必须、也确实填的是真值。

## 其它发行版

`.deb` / `.rpm` 由 `scripts/package-linux.sh` 统一产出（不需要 root，
缺工具的格式会打印提示并跳过）。同一个「绝不 strip」约束在那边同样成立，细节见
脚本头部的注释。

```bash
# 本机最小验证（用已有 tar.gz，不重新 publish）
bash scripts/package-linux.sh --formats deb \
    --from-tarball artifacts/3.12.0/UZIP2-3.12.0-linux-x64.tar.gz
# 全格式、双架构（CI 里的用法）
bash scripts/package-linux.sh --arch both --formats all --tarball-dir artifacts/3.12.0
```
