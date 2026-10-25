# 更新日志

本项目按 tag 发布（两段版本号，如 `v3.12`），安装包里的程序版本是三段（`3.12.0`）。
条目按"用户能感知的变化"写，不逐条列重构。

## [未发布] — 分卷（多压缩包分包）修复

分卷解压是 Linux 上最严重的一批缺陷，其中一条让**图形界面的解压功能完全不可用**。

### 修复（严重）

- **Linux 图形界面解压 100% 失败**（阻断级）：`TempManager.Completion` 与 `ArchiveWorker.TempFix`
  把路径分隔符硬编码成反斜杠，Linux 上拼出 `/tmp/voltest\/UZipTemp_x\` 这种路径 ——
  反斜杠被当成普通文件名字符，于是 `/tmp` 下凭空多出一个叫 `voltest\` 的目录，
  紧接着 `Directory.GetFiles(temp)` 找不到 → `DirectoryNotFoundException`。
  所有非 Windows 平台上的每一次 GUI 解压都失败，与分卷无关。
  新增 `PlatformPaths.EnsureTrailing` / `TrimSeparators`（跟随 `Path.DirectorySeparatorChar`，
  TrimEnd 双字符），把 `TempManager`、`ArchiveWorker` 里所有单字符 `TrimEnd('\\')` 一并收口。
- **分卷的中间卷被当成"普通文件"送进压缩队列**：`ArchiveInspector.Extractable` 白名单里只有
  `.001`，于是 `x.7z.003` 被 GUI 自动分流判定为待压缩文件，产出 `x.7z.zip`。
  `CanExtractByExtension` 改为额外认可 `AnalyzeVolume().IsVolume`（覆盖 `.002`…`.999`
  与旧式 `.z01`），仍不把 `log.txt.002` 这类误判成压缩包。
- **预览与转格式在分卷上不可用**：只有解压做了"回退到主卷"，`PreviewAsync` 与
  `RunConvertAsync` 都把中间卷直接交给 7z（`Cannot open the file as archive`）。
  新增 `ArchiveInspector.ExtractTarget`，三处统一走它；转格式的产物名也改按主卷推导
  （`v7.zip`，不再是 `v7.7z.003.zip`）。
- **CLI 完全不认识分卷**（6 项）：
  `extract`/`list`/`test` 遇到中间卷一律报"格式不支持或包内无文件"；
  批量传整组卷时只有 `.001` 成功、其余各报一次错并让退出码变成 1；
  缺中卷、缺主卷都只说"格式不支持"，不告诉用户缺的是哪一卷；
  zip 中间卷被当成独立包解出半截（`CRC` 错误）。
  现在：卷组内归一到主卷并只处理一次（其余打印"已随分卷主文件处理"，退出码 0），
  主卷优先于从属卷排序，缺卷点名（`分卷不完整：缺少 v7.7z.004` /
  `缺少主卷 v7.7z.001（分卷必须从第一卷开始读）`），末卷缺失单独归因，
  非压缩包仍走原来的"格式不支持"（不被分卷逻辑吞掉）。`watch once` 同样去重。
- **`list *.00* --json` 会破坏 NDJSON**：从属卷那行中文插进了输出流，下游 `jq`/CI 直接解析失败。
  改为并入主卷对象的 `volumes` 数组，整组卷只出一个 JSON 对象。

### 测试

- `UZIP2.Avalonia.Tests` 21 → 36 条：路径分隔符（含 Unix 上绝不产生反斜杠）、
  数字卷与旧式 `.z01` 识别、`log.txt.002` 不算压缩包、`ExtractTarget` 回退/主卷缺失时原样返回。
- `UZIP2.Tests/CliVolumeTests.cs` 新增 14 条（真 7z 造 16k 小分卷）：中间卷回落、
  批量去重、乱序输入主卷优先、缺中卷/缺主卷/缺末卷、zip 中间卷、JSON 单对象。
  **注意造多片卷组时填充数据必须是随机的** —— 可压缩的填充会让 7z 把几十 KB 全塞进第一卷。

### 已知限制

- 缺卷检测依赖 7z 的三位补零命名（`.001`…`.999`）；≥1000 卷或四位补零走"不误报"的兜底。
- 7z 本身区分不了"缺中卷"与"缺末卷"，缺口只能在文件名层自己数；
  末卷存在但被截断会归入"末卷缺失或最后一卷未下载完"。

## [3.13.0] — 2026-10-24

Linux 图形界面从 **XWayland** 切到**原生 Wayland**，并补齐了缺位的 Arch 分发产物。

### 图形后端（重要）

- **升级到 Avalonia 12.1.4 + `Avalonia.Wayland`**：Avalonia 11.x 根本没有 Wayland 后端
  （`Avalonia.Wayland` 包从 12.1.0 才发布），所以此前所有窗口都跑在 XWayland 上。
  现在 `Program.BuildAvaloniaApp()` 在 Linux 上叠一层 `UseWaylandWithFallback()`，
  直接用 `wl_compositor` / `xdg_wm_base` / `wl_shm` / `wl_seat` 与合成器通信，
  只有 Wayland 连不上时才退回 X11。
- **实测证据**：在 `DISPLAY` 未设置的纯 Wayland 会话（cage + headless wlroots 后端）里
  启动打包好的单文件二进制，`WAYLAND_DEBUG=1` 抓到 80 次 `wl_shm`、29 次 `wl_compositor`、
  9 次 `wl_seat`、7 次 `xdg_wm_base` 往返，截图渲染完整；进程 fd 里没有任何 X11 socket。
- **toolchain 随之上到 .NET 10 SDK**：Avalonia 12 的 XAML 源生成器引用 Roslyn 4.14，
  SDK 8 自带的 4.11 跑不了（表现为 `error CS0103: 不存在名称 InitializeComponent`）。
  CI 的 `dotnet-version` 与 PKGBUILD 的 `makedepends` 同步改成 10.x。

### 新增（发行版打包）

- **两个 Arch 包都在本机真构建并验证**（`makepkg` 走完整 fakeroot 流程）：
  - `uzip2-bin-3.13.0-1-x86_64.pkg.tar.zst`（34 MB，预编译）
  - `uzip2-3.13.0-1-x86_64.pkg.tar.zst`（34 MB，源码构建，多一份 `usr/share/licenses/uzip2/LICENSE`）

  `bsdtar tf` 确认包含 `usr/bin/uzip2`、桌面入口、48/128 两档图标；解包后 `--version`
  都输出 `UZIP 3.13.0`，在纯 Wayland 会话里能正常开窗。源码包这一路同时验证了
  `sha256sums` 里的源码归档真值（下载 → 校验 → `dotnet publish` → 打包全通）。
- **`PKGBUILD-bin` 改用架构专属数组**（`source_x86_64` / `sha256sums_x86_64` 等），
  x86_64 与 aarch64 各校验各自的包，不再是共用一个 `('SKIP')`。
- 两个 PKGBUILD 的 `pkgver` 提到 3.13.0、`_tag` 提到 v3.13，`.SRCINFO` 重新生成。
- **sha256**：`PKGBUILD-bin` 的两个架构专属数组是真值（`sha256sums_x86_64=fcf1f84b…`、
  `sha256sums_aarch64=71e9706e…`，与 Release 侧车逐字核对）。源码 `PKGBUILD` 的
  `sha256sums` 只能是 `('SKIP')` —— 它自己就在下载的那个 tag 归档里，填真值会改变归档、
  归档一变 hash 又变，是个无解的固定点（详见「修复」里的 CI 条目）。
- `pkgdesc` 更正：两处都还写着「Linux 命令行版」，但包里自 v3.10 起就是图形界面 + 命令行。
- **deb 与 AppImage 也在本机真构建**（`scripts/package-linux.sh`）：`uzip2_3.13.0_amd64.deb`
  与 `UZIP2-3.13.0-linux-x64.AppImage`，后者解包运行 `--version` 输出 `UZIP 3.13.0`，
  在纯 Wayland 沙箱里渲染的截图与 dev build 逐字节相同（md5 一致）。本机没有 `dpkg-deb`，
  deb 走 `ar` 回退路径。`rpm` 本机无 `rpmbuild`，由 CI 构建并在容器里拆包实跑（包内
  `/usr/bin/uzip2 --version` 输出 `UZIP 3.13.0`）。

### 修复（Avalonia 12 迁移）

- **Windows 框架依赖产物被误打成 144 MB**：.NET 10 把 `--self-contained` 从「带值选项」
  改成了纯开关，`--self-contained false` 里的 `false` 不再被当作取值，等于没关自包含。
  实测 `--self-contained false` 与 `--self-contained=false` 都出 144.8 MB，而
  `-p:SelfContained=false` 是正常的 9.2 MB（SDK 8 上旧写法也是 9.2 MB）。
  `UZIP2.exe` 是应用内自更新下载的那个附件，体积错了会让每次更新多下 135 MB。
  三处发布链路（`scripts/publish.ps1`、`scripts/publish-linux.ps1`、Windows CI）
  统一改成 `-p:SelfContained=` 属性形式。
- **Linux 上交叉构建 Windows 产物**报 `NETSDK1100`（需要 `EnableWindowsTargeting`），
  `publish.ps1` 现在按平台自动补。判据用 `RuntimeInformation.IsOSPlatform` 而非
  `$IsWindows`（后者只在 PowerShell 6+ 存在，5.1 里是 `$null`，判断会反掉）。
- 拖放数据改用新的 `IDataTransfer` API（`e.DataTransfer.TryGetFiles()` / `DataFormat.File`），
  旧的 `e.Data` + `DataFormats.Files` 在 12 里已移除。
- 剪贴板改用 `ClipboardExtensions` 的 `TryGetTextAsync` / `SetTextAsync`（`IClipboard` 接口本身只剩
  `TryGetDataAsync` 一类通用方法）。
- `RadioButton` 的选中事件从 `Checked` 改为 `IsCheckedChanged`；`NativeMenuItemToggleType` 改名
  `MenuItemToggleType`；`TextBox.Watermark` → `PlaceholderText`；`Window.SystemDecorations` →
  `WindowDecorations`。
- 测试项目迁到 **xunit v3**（`Avalonia.Headless.XUnit` 12.x 只提供 v3 适配器），
  以 Exe 形态通过 `dotnet <tests>.dll` 运行。
- **`dotnet test` 在 CI 上全红**：VSTest 的 `testhost.deps.json` 按老规矩引 `Newtonsoft.Json 13.0.3`，
  而 xunit.v3 的依赖图里没有它，报 `Testhost process ... exited with error`。加
  `TestingPlatformDotnetTestSupport` 让 `dotnet test` 走 Microsoft.Testing.Platform、
  并移掉 `xunit.runner.visualstudio` 后恢复本地与 CI 一致的运行方式。
- **AppImage 打不出来，是三个坑叠在一起**：桌面文件 `Categories=Utility;Archiver;` 里的
  `Archiver` 不是注册值（改 `Archiving`）；AppStream `<id>` 必须是反域名（改
  `io.github.pingod.UZIP2`）；元数据文件名必须与 id 一致（跟着改名，否则报
  `metainfo-filename-cid-mismatch`）。另外 CI runner 升级到 Ubuntu 26.04 后 `tail` 是
  uutils 版，`tail -40` 报 `unexpected argument '-4'`，把这堆真实错误全盖住了，改成 `tail -n 40`。
- **源码 `PKGBUILD` 的 `sha256sums` 是个无解的固定点**：`packaging/arch/PKGBUILD` 本身就在它
  下载的 `archive/v3.13.tar.gz` 里，往 `sha256sums` 填真值会改变 `PKGBUILD` → 改变归档 →
  hash 又变。之前填的 `0a792aea…` 是对着旧 tag 算的，tag 重指到该提交之后，tag 触发的 CI
  在 `Build package (source PKGBUILD)` 步骤报 `==> ERROR: One or more files did not pass
  the validity check!`（而前一步 `Validate both PKGBUILDs` 是通过的，所以一开始误判为断言写错）。
  改为 `('SKIP')` 并在文件里写清理由；CI 断言同步拆开：源码包必须是 SKIP 且带「固定点」说明，
  `PKGBUILD-bin` 两个架构仍必须是 64 位真值。本地 `makepkg -f --nodeps` 用 SKIP 完整构建通过。

**测试**：21/21 通过，构建 0 警告 0 错误。

**发布**：GitHub Release [`v3.13`](https://github.com/pingod/UZIP2/releases/tag/v3.13)，
九个产物 + 各自的 `.sha256` 侧车（Windows×2、Linux tar.gz×2、Arch 包、deb、rpm、AppImage，
外加汇总清单 `SHA256SUMS-linux-packages.txt`）。

## [3.12.0 的 Linux 收尾] — Linux 桌面收尾与 Avalonia 功能对齐（2026-10-23）

在真实 Hyprland 会话（Wayland + XWayland，双显示器）上跑通了托盘与悬浮方块，
把 Avalonia 版落后 WPF 的 7 处功能差距全部补上，并给发行版打包补了 deb/rpm/AppImage。

### 新增（Linux 图形界面）

- **设置页从 9 项扩到 40 余项**：主题（浅色/深色/跟随系统）、热键组合自由选择、解压输出模式、
  覆盖策略、自定义解压文件夹、启动清理临时目录、窗口置顶、任务排序、记录口令、文件名转密码；
  另补了引擎早已在消费但界面上没暴露的 7 项（新建文件夹、按名建目录、压完删源、解压过滤、
  口令去首尾空格、调试模式、迷你方块）。
- **主题真的跟随设置**：以前 `App.axaml` 里写死了深色，选「浅色」不会有任何变化。现在启动时
  按 `settings.Theme` 应用，切换即时生效。
- **热键改完不用重启**：以前热键只在窗口打开时注册一次，改完必须重开程序。现在保存设置后
  立即重新注册（X11 重抓键位轮询，Wayland 重走一遍 GlobalShortcuts portal）。
- **热键支持字母数字之外的键**：以前 `HotKeyKey` 只映射 A-Z 和 0-9，选 F5 会被悄悄当成 `u`。
  现在支持 F1–F12、方向键、空格、Home/End/PageUp/PageDown、Insert/Delete、小键盘等。
- **主页卡片补「重试」与「删除源文件」**：重试会带上这张卡上次真正解开时用的口令，省掉重走
  整条密码链。删除源文件在 Linux 上是直接删除（回收站走的是 Windows 专有的
  `Microsoft.VisualBasic.FileIO`，Linux 上必抛 `PlatformNotSupportedException`），已在按钮提示里写明。
- **卡片 9 个按钮的可见性逐个对齐 WPF**：以前「预览/转格式/校验/对比/打开目录」统一按"已结束"
  显示，导致解压失败的包也能点「预览」、压缩作业也能点。现在按作业类型与状态精确控制。
- **批量操作补「清除失败项」**。
- **历史页补「重跑」**：可带原口令重跑，校验类记录不给重跑按钮（与 WPF 一致）。
- **密码页补「从剪贴板粘贴」**：多行口令一次贴进密码纸，清洗规则与 WPF 逐字符一致。
- **托盘失败气泡**：任务失败时会弹桌面通知（`notify-send` 优先，其次 D-Bus `Notify`，都没有就只写日志），
  900ms 静默期攒成一条、最长等 5 秒；窗口在前台时不打扰；完整报告另存一份
  `failure-report-*.txt`（刻意不含密码）。
- **设置页补「检查更新」**：手动查 GitHub Releases，Linux 上会提示"请用包管理器升级"而不是假装能自动更新。

### 新增（打包与发布）

- **`.deb` / `.rpm` / `AppImage` 打包**：新增 `scripts/package-linux.sh`，一条命令从 tar.gz 生成三种包。
  三种格式共用同一份"已装好布局"的暂存树，保证内容逐字节一致；全程不 strip —— 这正是
  `Arithmetic overflow while reading bundle` 那条坑的防线，CI 里还会用 `cmp` 逐字节比对兜底。
- **`scripts/aur-sums.sh`**：把 AUR 发布前「下载 → 算 sha256 → 比对 → 回填两个 PKGBUILD → 生成
  `.SRCINFO`」做成一条命令。会用 Release 的 `.sha256` 侧车交叉验证下载完整性。
- **`scripts/version.ps1`**：版本号原本散落在四个文件里、两个发布脚本各抄一遍读取逻辑。现在
  只有一个读取点，四处不一致会直接报错并列出全部来源。
- **Windows CI 也跑 Avalonia 测试**：以前只有 Linux CI 跑，Windows 侧的 UI 回归挡不住。
- **arm64 的 Arch 包**：两个 PKGBUILD 都支持 `aarch64`。

### 修复

- **拖动悬浮方块会把设置文件刷爆**：合成器重新摆放窗口时会反复抛"位置变了"，每次都写一遍
  `settings.json` 并广播一次设置变更，实机 7 秒写了几百次、日志刷了 430 行。现在坐标没变就不写。
- **悬浮方块重启后位置不对**：窗口还没映射时设坐标会被合成器丢弃（设 800,600 实际落在 2240,600）。
  改成按 50ms 间隔最多追 20 次，实机 4 组坐标全部精确恢复。
- **置顶日志刷屏**：只在置顶值真的变化时才记一行。
- **`aur-sums.sh` 误报 checksum 不一致**：GitHub 对大文件偶尔中途断流，`curl` 仍以 0 退出，
  脚本对残缺文件算 sha256 就得出"PKGBUILD 填错了"的错误结论。现在先用侧车核对，能区分
  "缓存坏了"和"hash 填错了"。

### 验证

- **实机（Hyprland 0.56.2，Wayland + XWayland，双显示器）**：
  - 托盘图标与菜单：`busctl` 里出现 `org.kde.StatusNotifierItem`，`IconPixmap` 解出来就是 UZIP 图标，
    dbusmenu 结构为「显示 UZIP / 桌面只留方块 / 分隔符 / 退出」。
  - 悬浮方块：4 组坐标重启后精确恢复。
  - 失败气泡：隐藏窗口 + 3 个坏包 → 1 条通知；前台 → 0 条；连发 3 次仍 1 条。
  - CLI：普通包解出 2 个文件；加密包无口令时正确报错不挂起，带 `--password` 正常解开。
- **无头测试**：Avalonia 测试从 12 条增至 21 条，全绿、0 警告 0 错误。新增的 9 条覆盖
  任务排序、清除失败项、重试/删除源文件、历史重跑、剪贴板清洗、扩展热键名、方块位置恢复、
  设置不被重复写盘。其中「设置不被重复写盘」做了变异验证（把去重条件改成永不去重后测试立刻变红）。
- **发布脚本真跑**：`publish-linux.ps1` 产出 35.6 MB tar.gz，解包后 `--version` → `UZIP 3.12.0`。
- **AUR 包校验**：`makepkg --printsrcinfo` 对两个 PKGBUILD 都通过，`sha256sums` 全部填真值。

### 已知限制

- **Avalonia 只跑 XWayland**（3.13 已解决）：当时项目只引了 `Avalonia`/`Avalonia.Desktop`/`Avalonia.Themes.Fluent`，
  没有 Avalonia.Wayland 包 —— 该包从 12.1.0 才发布，11.x 完全不存在 Wayland 后端。3.13.0 升级到
  Avalonia 12.1.4 并接入原生 Wayland 后端，此限制不再成立。
- **Hyprland 不实现 EWMH 置顶**：`_NET_WM_STATE_ABOVE` 不在 `_NET_SUPPORTED` 里，置顶属性设得上
  但合成器不体现"重启后仍在最上层"。KDE/X11 下正常。
- **Avalonia 11.3.2 的托盘 `Status` 属性写错**：它把 `Id`（"UZIP2"）写进了只允许
  `Passive`/`Active`/`NeedsAttention` 的 `Status` 槽位。quickshell 宿主宽容接受，图标与菜单都正常；
  这是上游 bug，本项目改不到。
- **AppImage 依赖 FUSE**：Ubuntu 22.04+ 默认没 `libfuse2`，用户直接双击会报错。
- **rpm / AppImage 未在本地真构建**：本机无 `rpmbuild`、无 `appimagetool`，也没 sudo 装。脚本已就位，
  由 CI 的 `linux-packages` job 验证。
- **AUR 未真提交**：本机无 SSH 私钥，`git ls-remote ssh://…` 直接 `Host key verification failed`。
  包本身已就绪（含 `.SRCINFO` 与填好的 `sha256sums`）。
- **arm64 仍无设备验证**：只有交叉构建与打包。

## [3.12.0] — 2026-10-23

补齐 Linux 侧最后三块桌面集成，并在真实 X 服务器上验证了热键。

### 新增

- **Thunar 右键菜单**：两个动作合并进用户已有的 `~/.config/Thunar/uca.xml`，不覆盖别人的动作；
  首次修改前留 `uca.xml.uzip2.bak`；重复注册是幂等的；卸载只移除 `uzip2-extract-here` 与 `uzip2-compress` 两个 unique-id。
- **Wayland 全局热键**：通过 `org.freedesktop.portal.GlobalShortcuts` 注册（`CreateSession` →
  `BindShortcuts` → 监听 `Activated`），经 `gdbus` 调用，不内置 D-Bus 栈。只在实现了该 portal 的
  Wayland 桌面（目前是 KDE）尝试；缺 gdbus / 缺 portal 时安静关闭并写日志。
- **tar.gz 安装脚本**：`install.sh` / `uninstall.sh`，`PREFIX` 可调，安装二进制 + 桌面入口 + 48/128
  图标并刷新 desktop/icon 缓存；卸载只删这四项，保留 `~/.config/uzip2`。
- **启动日志记录桌面能力**：会话类型、桌面名，以及托盘/全局热键/方块定位各自是否可用及原因，
  远程排查不用打开设置页。

### 修复

- **X11 全局热键会漏按**：150ms 轮询接不住约 100ms 的按键，实测第一次按键无反应。间隔收紧到
  **25ms**，100ms 按键稳定触发。
- **能力文案误伤非 GNOME 桌面**：Wayland 下没有托盘/定位时，不再一律写成"GNOME 限制"，
  改为按实际桌面描述（GNOME 仍保留扩展提示）。

### 验证

- X11：WSL + Xvfb 真 X 服务器 —— `xwininfo` 看到 `"UZIP2" 1000x700` 窗口，日志记录热键注册成功，
  `xdotool` 按住 Ctrl+Alt+U 后记录"全局热键触发，唤起窗口"。
- Wayland：WSLg（Weston）启动成功，日志走 Wayland 分支并给出降级原因。
- 819 个 WPF 测试 + 12 个 Avalonia 测试，Debug/Release 全绿、0 警告。

### 已知限制

- Wayland 全局热键只在实现了 `GlobalShortcuts` portal 的桌面可用；GNOME Wayland 的托盘需要
  AppIndicator 扩展，窗口定位没有接口。
- 托盘宿主、悬浮方块定位仍缺真实 KDE/GNOME 会话的实机验证；arm64 只有交叉构建。

## [3.11.0] — 2026-10-23

Linux 图形版补齐到日常可用。

### 新增

- **密码页**：密码本增删、逐行显示/隐藏、复制、成功次数；密码纸整段粘贴、清空、200 条上限提示。
- **卡片三个入口**：转格式（zip/7z/tar/wim/bz2/gz/xz，原包保留）、校验和（SHA-256 + 旁挂
  `SHA256SUMS`/`.sfv`/`.md5` 逐条比对）、包对比（选另一个包，列出仅左/仅右/大小不同）。
- **包内预览勾选**：勾选后只解压选中项（走引擎已有的 `onlyEntries`）。
- **单实例**：连续双击多个文件不再开多个窗口，第二个进程把路径转发给已运行实例；启动参数同样入队。
- **桌面入口**：`uzip2.desktop` + 48/128 px 图标，tar.gz 与 Arch 包都会安装。
- **X11 全局热键**：Ctrl+Alt+U 唤起窗口；不抢键，轮询键位图与修饰键掩码、按下沿触发。

## [3.10.0] — 2026-10-23

**Linux 版首次发布**（Avalonia 图形界面 + 同一二进制的命令行）。

### 新增

- 跨平台核心 `UZIP2.Core`：Models、可移植 Services、CLI 从 WPF 工程拆出，三个接缝
  `IUiDispatcher` / `IPasswordCipher` / `IShellIntegration`。
- `UZIP2.Avalonia`：主页队列、包内预览、设置、历史、托盘、悬浮方块（含进度环）。
- Linux 命令行：与 Windows CLI 同源；XDG 配置与日志目录；7-Zip 发行版路径探测；
  密码本用 AES-256-GCM + 0600 密钥文件（无 DPAPI）。
- 桌面集成：Nautilus 脚本、Dolphin service menu、能力探测与降级。
- 打包：linux-x64/arm64 自包含 tar.gz、`PKGBUILD` 与 `PKGBUILD-bin`、CI 在 `archlinux` 容器里
  真跑 `makepkg`。
- 文件管理器右键菜单、`update --apply` 在 Linux 明确拒绝并提示用包管理器。

### 修复

- makepkg 默认 `strip` 会切坏 .NET 单文件 bundle（运行时报 `Arithmetic overflow while reading bundle`），
  两个 PKGBUILD 都加 `options=(!strip !debug)`。
- 发布产物命名统一为 `UZIP2`，与自更新认领的名字一致。

## [3.9.0] — 2026-10-09

- 修复：拖拽文件悬停在迷你方块上方时图标抖动。

## [3.8.0] — 2026-10-06

- 新增：迷你方块外圈进度环（转完一圈 = 100%，收尾补满停两秒）。
- 修复：自更新版本闸门（三段/两段版本号比较错误，自 v3.3 起失效）；更新链路的代理降级；
  `--cover` 参数注入；`update --check` 死参数；方块记不住副屏；闪示后提示位回不来。
- 性能：加密探测可跳过、读到首条结论即收刀；主页计数合并；7z 进度行 `GeneratedRegex`；长列表虚拟化。
- 打包：`publish.ps1` 直接产出 `UZIP2.exe` + 侧车；程序集署名与版本；删除 `release/` 里的
  .NET Framework 残留。

## [3.7.0] — 2026-10-06

- 全量审计修复 14 项（密码本死锁、取消竞态、临时目录泄漏、半截压缩产物、文件过滤硬删、分卷假成功、
  UI 线程崩溃带走程序、监听目录静默停摆等）。
- 新增：格式互转 `convert`、包对比 `diff`、右键「解压并预览」、压缩后自检、更新包 SHA-256 侧车。

## 更早版本

- **3.6**：主页任务列表管理（逐项移除、清除失败/已完成、全部取消、排序持久化）；兼容代码清理。
- **3.5**：任务历史持久化（`Config/history.json`、历史页、CLI `history`）。
- **3.4**：发布瘦身（自包含包 162 MB → 66 MB）与免依赖的安装/卸载脚本。
- **3.3**：一键自更新（下载 → 校验版本与 SHA-256 → 退出后替换并重启）。
- **3.2**：完整命令行（解压/压缩/测试/清单/校验和等）。
- **3.1**：并行队列、包内预览、监听目录、右键菜单、密码转移、拖拽方块、分卷、高级参数、校验和、更新检查。
- **3.0**：WPF Fluent 重写，取代旧版 WinForms 界面。
