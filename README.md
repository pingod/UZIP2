# UZIP2
解压缩小工具，支持自动尝试指定密码。
# 项目由来
最近下载资料多些，解压逐个输入密码过于麻烦，想找个右键菜单一键解压的软件，在网上发现了UZIP2项目，作者是farkaway，应该是放弃维护了，两年没更新了。farkaway在[网站](https://www.yuque.com/farkaway/uzip/gggnsn#l3PpV)上提供了2.22版本和项目源码。我在2.22版本上，根据自己需求做了进一步定制。因该项目在网上无代码托管，并且联系不到作者farkaway，所以修改后的代码无法回流到作者farkaway，故此，我开了本仓库。

# 我的修改

1. 对剪切板导入的密码去重。
2. 程序支持命令行和右键菜单调用，并支持多个文件解压。
3. 优化代码格式，如制表符改成空格，删除多余引用，格式化代码等。
4. 优化临时缓存文件夹相关代码，每个压缩包有单独的缓存文件夹，并且该文件夹是隐藏的。
5. 支持多级解压，压缩包内第一层压缩包再次解压，持续符合条件时会循环解压。

每个版本的详细改动见 [CHANGELOG.md](CHANGELOG.md)，发版说明与产物见
[Releases](https://github.com/pingod/UZIP2/releases)；v3.8 及更早的增强条目归档在
[docs/history.md](docs/history.md)，剩余待办见 [docs/TODO.md](docs/TODO.md)。

## Linux 版（图形界面 + 命令行，同一个二进制）

Windows 界面是 WPF，无法在 Linux 上构建或运行，所以 Linux 版从跨平台核心出发：`Models + 可移植 Services + CLI` 拆进 `UZIP2.Core`（`net8.0`，不引用任何 UI 框架），界面用 Avalonia 重写（`UZIP2.Avalonia`）。**一个二进制同时是 GUI 和 CLI**：带显式命令词（`extract`/`compress`/`diff`…）时在 Avalonia 初始化之前就走无界面路径，所以服务器上没有显示环境也能用。

图形界面的功能与 WPF 版对齐：主页队列（拖放、并发、卡片九种操作含重试与删除源文件、批量清除失败项）、任务排序（加入顺序/最新在前/按状态/按文件名）、包内预览（可勾选只解压选中项）、密码本/密码纸（含从剪贴板粘贴）、历史（含重跑）、设置（40 余项，覆盖主题、热键组合、解压输出模式与覆盖策略、自定义文件夹、更新检查等）、托盘（含失败气泡）、悬浮拖拽方块（含进度环与位置记忆）、Thunar/Nautilus/Dolphin 右键菜单；CLI 全部命令与 Windows 一致：`list / extract / compress / test / convert / diff / checksum / vault / history / config / log / watch once / update --check`。

Wayland 兼容性按能力降级，而不是假装对齐：X11 三项全开；KDE Wayland 托盘/热键/方块定位都可用；GNOME Wayland 没有窗口定位接口、托盘需要 AppIndicator 扩展；sway/Hyprland 这类 wlroots 合成器有托盘宿主，方块定位靠合成器允许客户端置位（本程序没有用 layer-shell）。设置页会直接把当前会话判定结果写出来。**全局热键**：X11 用 25ms 轮询键位图（Ctrl+Alt+U 唤起窗口，在 Xvfb 实机上验证过按下即触发）；Wayland 走 XDG 的 `GlobalShortcuts` portal（经 `gdbus` 调用，目前只有 KDE 实现了它），没有 portal 时安静关闭并在日志里说明。


最新包在 [Releases](https://github.com/pingod/UZIP2/releases/latest) 页面，直接装现成的：

```
sudo pacman -U uzip2-bin-3.13.2-1-x86_64.pkg.tar.zst    # 预编译
```

或者从源码构建（需要 `dotnet-sdk>=10`，见下面的说明）：

```
cd packaging/arch && makepkg -si          # PKGBUILD
# 或用预编译二进制
cd packaging/arch && makepkg -si -p PKGBUILD-bin
```

Debian/Ubuntu 用 `.deb`，Fedora/RHEL/openSUSE 用 `.rpm`：

```
sudo dpkg -i uzip2_3.13.2_amd64.deb                    # Debian/Ubuntu
sudo rpm -i uzip2-3.13.2-1.x86_64.rpm                  # Fedora/RHEL/openSUSE
```

或者直接解包 tar.gz，目录里带安装脚本：

```
tar -xzf UZIP2-3.13.2-linux-x64.tar.gz
cd UZIP2-3.13.2-linux-x64 && sudo ./install.sh      # 默认 /usr/local，装桌面入口与图标
# 卸载：sudo PREFIX=/usr/local ./uninstall.sh（配置目录保留）
# 只要二进制：sudo install -Dm755 UZIP2 /usr/bin/uzip2
```

依赖：7-Zip（Arch: `pacman -S 7zip`；Debian/Ubuntu: `apt install 7zip`），图形界面另需 `fontconfig`、`freetype2`、`libice`、`libsm`（Arch 包的 `depends` 已声明；纯命令行用法在无图形库的机器上也能跑，因为单文件是自包含的）。程序会依次找 `7zz/7z/7za`（`/usr/bin`、`/usr/local/bin`、`/usr/lib/p7zip`、`/usr/lib/7zip`，再扫 `PATH`），也可用 `uzip2 config set customize7zPath <路径>` 指定。配置落 `~/.config/uzip2`，日志落 `~/.local/state/uzip2`（遵循 XDG），程序目录只读也能跑。

**Wayland**：图形界面默认走原生 Wayland 协议，无需 `XWayland`；在纯 Wayland 会话（`DISPLAY` 未设置的 cage/sway/Hyprland）里实测能正常启动与渲染。只有在 Wayland 客户端连接失败时才退回 X11。

密码本的加密在 Linux 上没有 DPAPI，改用 **AES-256-GCM + 本机密钥文件**（`~/.config/uzip2/secret.key`，权限 0600，GUI/CLI 共用）。跨机器或换用户后旧密文解不开，程序按"密码缺失"处理而不是拿垃圾串去试。

自更新（`update --apply`）只在 Windows 可用；Linux 上明确提示用包管理器更新，不做换二进制的动作。

构建与打包：`scripts/publish-linux.ps1`（Windows 与 Linux 都能跑，交叉发布 x64/arm64 并组装 tar.gz + `.sha256` + `SHA256SUMS-linux.txt`）；`scripts/package-linux.sh` 从 tar.gz 生成 `.deb` / `.rpm`（两种格式共用一份暂存树，全程不 strip）；`scripts/aur-sums.sh` 负责发布前核对并回填两个 PKGBUILD 的 `sha256sums`、生成 `.SRCINFO`；版本号只在 `scripts/version.ps1` 读一处。CI 见 `.github/workflows/build-linux.yml`（`archlinux` 容器里跑真正的 `makepkg`、`ubuntu` 上跑无头 Avalonia 测试并打包 deb/rpm）与 `build-windows.yml`（WPF + Avalonia 双侧测试）。

真机验证：在 **Hyprland 0.56.2**（Wayland + XWayland，双显示器）真会话里跑通了托盘（`busctl` 见到 `org.kde.StatusNotifierItem`，图标像素解出来就是 UZIP 的线条画，菜单结构正确）、悬浮方块（4 组坐标重启后精确恢复）、失败气泡（隐藏时 3 个坏包 → 1 条通知，前台 → 0 条）；CLI 解普通包/加密包（含无口令时的正确报错）都正常。

**图形后端**：用 **Avalonia 12.1.4** + `Avalonia.Wayland`，Linux 上走**原生 Wayland**（实测协议流里有 `wl_compositor` / `xdg_wm_base` / `wl_shm` / `wl_seat`），X11 只是兜底 —— `UseWaylandWithFallback()` 仅在 Wayland 客户端连不上时才退回 XWayland。选择 12.x 的原因：`Avalonia.Wayland` 从 12.1.0 才开始发布，Avalonia 11.x **完全没有** Wayland 后端。

**已知限制**（都是环境或上游限制，不是没做完）：原生 Wayland 下客户端既不能声明「我是浮动窗口」也不能设绝对坐标，全归合成器决定。悬浮方块在 Hyprland 上走 `hyprctl` IPC（`hl.dsp.window.float` + `hl.dsp.window.move`）摆位、能精确恢复保存的位置；sway 这类 wlroots 合成器靠窗口的 Min/Max 尺寸自己把它当浮动窗口摆放；**KDE / GNOME Wayland 下方块仍会被当成平铺窗口拉伸**，没有通用解法。Hyprland **不实现 EWMH**，置顶属性设得上但合成器不体现「重启后仍在最上层」；KDE/X11 正常。`rpm` 与 `deb`、两个 Arch 包都是本机真构建并拆包实跑的；AUR 包已就绪（含 `.SRCINFO`）但尚未提交（需要 AUR 账号与 SSH key）；arm64 仍只有交叉构建。

一个打包坑记在这里：makepkg 默认会 `strip`，而 .NET 单文件在 ELF 末尾追加了 bundle，strip 之后运行时报 `Arithmetic overflow while reading bundle`。两个 PKGBUILD 都必须带 `options=(!strip !debug)`。

# [UZIP简介](https://www.yuque.com/farkaway/uzip/gggnsn)
UZIP是一个主要用于解压各种压缩档案的小工具，希望它可以为您提供更便利的解压功能。

有别于市面上其他压缩工具，其有以下主要功能特点：
1. 拖拽压缩/拖拽解压。所有的操作通过更直观的拖拽操作完成。

2. 密码管理功能。提供两种密码管理方式：密码本和密码纸，分别对应永久密码和一次性密码。您可以方便的贴入密码，以便在您需要的时候进行解压。

3. 批量测试解压密码。程序在您的密码本和密码纸中查找密码，并自动执行解压。

4. 解压文件过滤。过滤文件中的某些广告推广文件，避免手动删除的困扰。

5. 压缩日志。记录您压缩过的文件位置和解压密码，方便您随时找回。

6. 随机压缩密码生成。确保您每一个压缩文件都有不一样的密码。

7. 任意格式解压。即使文件后缀不是压缩格式，只需要勾选尝试解压位置格式（仅解压模式），就可以直接解压。

8. 从文件名中提取密码。只需要设定分隔标识符，程序就可以从文件名中自动提取解压密码。

9. 将解压密码写到文件名。您压缩的文件的文件名会包含密码信息，即防止在线解压，又避免遗忘密码。


### 运行环境


10. 程序基于 Microsoft .NET Framework 4.5，界面基于WPF 。 

11. WinXP系统应该无法使用，Win10 亲测可用，Win7，Win8未测试（理论可行）。

12. 程序基于cmd.exe命令行调用7z.exe实现解压/压缩功能。部分限制cmd命令行使用的设备无法使用。

### 使用场景

1. 只在固定的网站活动，只使用几个固定的解压密码。
在密码管理器 - 密码本中，填入这些密码，实现永久免密解压。
2. 出没于各个资源论坛，需要和各种各样的密码打交道。
    ○ 复制密码到剪切板，点击密码纸按钮，一键贴入剪切板内的密码到程序。
    ○ 选中密码，使用热键快速贴入程序。
3. 手机或其他设备无法使用程序。
尝试使用，从文件名提取密码。
4. 常常需要压缩文件，但经常忘记压缩密码。
在设置面板中，点击查看压缩日志按钮。输出位置、解压密码都可以找到。
5. 发布资源需要不同的密码。
在设置面板 - 压缩中，选择随机密码。
6. 下载资源里有很多奇奇怪怪的推广文件，找到需要的东西很费劲。
在设置面板 - 解压中，配置文件过滤选项。

### 使用说明

1. 程序基于7Zip 命令行方式进行解压/压缩。您可以通过设置，使用您自己安装的7zip.exe让程序工作。为了方便压缩包内置了32位的7Zip程序文件，可以自行替换为64位版本。
2. 程序做的事仅仅是替你输入命令行让7z工作，然后做一些7z没有做得事情。综合效率没有直接使用7z快，但使用上更方便了，省去了很多记录、查找解压密码的时间。
3. 理论来讲，7z.exe支持的所有格式都可以解压。但由于命令行限制，某些功能可能无法实现。
4. 解压\压缩时没有进度条，文件较大需要耐心等待。但，如果拖入多个文件可以看到文件进度。
5. 要注意，为了实现某些功能，程序会将压缩档案内的文件先解压到输出目录的UZipTempFolder文件夹内，再进行重命名，因此不要误删。
6. 作者是C#萌新，程序难免出现不可预料状况，如给您带来损失请见谅。
