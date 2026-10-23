# Arch Linux 打包

两个 PKGBUILD，二选一：

| 文件 | 说明 | 依赖 |
|---|---|---|
| `PKGBUILD` | 源码构建（从 tag 归档拉源码，`dotnet publish`） | `dotnet-sdk`、`7zip` |
| `PKGBUILD-bin` | 直接用 Release 里的自包含 tar.gz | `7zip` |

```bash
cd packaging/arch
makepkg -si                 # 源码包
makepkg -si -p PKGBUILD-bin # 预编译包（首次发布后把 tar.gz 的 sha256 填进 sha256sums）
```

产物装到 `/usr/bin/uzip2`，配置在 `~/.config/uzip2`。

## 必须保留 `options=(!strip !debug)`

.NET 单文件发布的 ELF 末尾追加了 bundle 数据，makepkg 默认的 `strip` 会把它切坏，
运行时报：

```
Failure processing application bundle; possible file corruption.
Arithmetic overflow while reading bundle.
```

两个 PKGBUILD 都已关掉 strip 与 debug 分包（实测验证：关掉后 36 MB 二进制正常输出 `UZIP 3.10.0`）。

## aarch64

`arch=('x86_64' 'aarch64')`，构建时按 `$CARCH` 选 `linux-x64` / `linux-arm64` RID。
在 x86_64 上给 aarch64 打包需要 Arch Linux ARM 环境（CI 用 `archlinux` 容器只构建本机架构）。
