# 待办事项

截至 **v3.13.0**（2026-10-24）。v3.13 把 Linux 图形界面从 XWayland 换到原生 Wayland，
并补齐发行版打包（Arch / deb / rpm / AppImage）；v3.12 后那一轮补完了 E2/E3/E5 实机验证与 F1–F7 功能对齐。
下面只留「仍然做不了」和「新发现、尚未做」的部分，末尾记录已做完的与验证方式。

> **v3.13 已关闭的旧条目**：`E3b`（原生 Wayland 下的方块定位）与 `N1`（Avalonia 11.3.2
> 的 SNI `Status` 写错）都已解决 —— 升级到 Avalonia 12.1.4 后接入 `Avalonia.Wayland`，
> 实测走原生 Wayland 协议（`WAYLAND_DEBUG` 抓到 `wl_compositor` / `xdg_wm_base` /
> `wl_shm` / `wl_seat` 往返，进程 fd 里没有 X11 socket）；SNI 的 `Status` 现在返回规范的
> `Active`（不再是 `"UZIP2"`）。

---

## 一、仍然需要真实环境（本机不具备）

| # | 事项 | 为什么做不了 | 验收方式 |
|---|------|-------------|---------|
| E1 | **KDE Wayland 的 GlobalShortcuts portal 实跑** | 本机是 Hyprland（wlroots），`DesktopCapabilities.GlobalHotkeys` 为 false，代码走不到 portal 分支；只有 X11 热键路径被实机验证过 | 在 KDE Wayland 下启动，`gdbus monitor` 看到 `BindShortcuts` 的 Response，按组合键能唤起窗口 |
| E4 | **arm64 运行验证** | 只有交叉构建与打包，没有 arm64 设备，也没有 qemu | 在 aarch64 机器上跑 `uzip2 --version` + 解压一个包 |
| P1b | **AUR 真提交** | 无 SSH 私钥、无 `ssh-askpass`，`git ls-remote ssh://aur@aur.archlinux.org` 直接 `Host key verification failed`；且 `sudo` 不可用 | 有 AUR 账号与 SSH key 的机器上 `git push` |

---

## 二、这一轮新发现、尚未做的

| # | 事项 | 现状 | 建议 |
|---|------|------|------|
| N2 | **AppImage 依赖 FUSE** | Ubuntu 22.04+ 默认没装 `libfuse2`，用户直接双击会报 `dlopen(): error loading libfuse2` | 文档提示，或改用 `--appimage-extract-and-run` 包装启动 |
| N2b | **`appimagetool` 打包要联网下载 runtime** | 最后一步从 `AppImage/type2-runtime` 的 Releases 拉 `runtime-x86_64`，网络受限时失败并报 `Failed to download runtime`。已加 `--runtime-file PATH` / `APPIMAGE_RUNTIME_FILE` / 同级目录自动发现 `runtime-<arch>`。**注意**：从 `appimagetool` 自身切出来的 runtime 会让产物段错误（`--appimage-extract` 都 core dump），只能用可信来源 | 网络受限环境传入合法 runtime |
| N3 | **arm64 只有 tar.gz** | deb/rpm/AppImage 的 arm64 分支脚本已支持（`--arch arm64`）但没有任何流程产出，交叉打包无法验证 | 有 arm64 runner 后再接入 CI |
| N4 | **Hyprland 不实现 EWMH 置顶** | `xprop -root _NET_SUPPORTED` 里没有 `_NET_WM_STATE_ABOVE`，`hyprctl -j clients` 显示 `pinned:false`。置顶开关本身能设（`Topmost` 属性即时变化），但"重启后仍在最上层"在这个桌面上无法体现 | 桌面限制，非代码缺陷。KDE/X11 下正常 |

---

## 三、这一轮做完了什么（附验证方式）

### 功能对齐（F1–F7 全部完成）

| # | 事项 | 落地位置 | 验证 |
|---|------|---------|------|
| F1 | 设置页补齐 10 项缺失 + 7 项 Core 已在消费但 UI 没暴露的项 | `UZIP2.Avalonia/Shell/Pages/SettingsView.axaml(.cs)`（72→215 / 103→569 行）、新增 `Shell/ThemeApplier.cs` | 无头测试 + 真机日志 |
| F1a | **任务排序** | `Shell/Pages/HomeView.axaml.cs` 里按 `JobOrder` 维护 `_sorted` 镜像（对齐 WPF `HomeViewModel` 的 mirror 语义），新增 `Home_list_follows_the_configured_job_sort` 用例 | 测试 21/21 |
| F1b | **主题跟随设置** | 删掉 `App.axaml` 里写死的 `RequestedThemeVariant="Dark"`，改由 `App.OnFrameworkInitializationCompleted` 在建窗口前 `ThemeApplier.Apply(...)` | 构建 + 测试 |
| F1c | **热键改完免重启** | `MainWindow.ReloadHotkey()`（`StopHotkey` + `StartHotkey`）；`App.ApplyWindowSettingsOnUi` 用 `_appliedHotkey` 五元组判变化才重注册 | 真机日志「全局热键已启用（X11）: Ctrl+Alt+F5 → keysym F5」 |
| F2 | 历史页「重跑」 | `HistoryView` 行内按钮 + `HistoryKindRetryableConverter`（`Kind != "Test"`） | 单测 |
| F3 | 卡片「重试」「删除源文件」 | `HomeView` 的 `OnRetry` / `OnDeleteSource`，按钮可见性换成 `JobStatusVisibilityConverter` / `JobKindVisibilityConverter`，9 个按钮逐个对齐 WPF `HomeView.xaml:140-171` | 单测 |
| F4 | 应用内更新检查 | 设置页「检查更新」按钮 → `UpdateService.CheckAsync()` + `IsNewer` + `UpdatePolicy.PackageManagerHint` | 构建 + 测试 |
| F5 | 密码页剪贴板粘贴 | `PasswordView.OnPasteClipboard` + `Clean(raw, trimSpace)`（逐字符对齐 WPF `ClipboardService.cs:25`） | `Clipboard_paste_cleans_control_characters_like_wpf` |
| F6 | 「清除失败项」 | `HomeView.OnClearFailed` → `Worker.RemoveTerminal(j => j.Status == Failed)` | 单测 |
| F7 | 托盘失败气泡 | `App.axaml.cs`：`notify-send` → `gdbus org.freedesktop.Notifications.Notify` → 只写日志三级降级；`FailureNotifier(900, 5000)` 节流；前台不弹；完整报告写 `failure-report-yyyyMMdd-HHmmss.txt` | 实机 dbus-monitor：隐藏+3 坏包 → 1 条通知；前台 → 0 条；连发 3 次仍 1 条 |
### 真实桌面验证（E2 / E3 / E5）

| # | 事项 | 结论 |
|---|------|------|
| E2 | 托盘图标与菜单 | **已实证**。`busctl --user list` 出现 `org.kde.StatusNotifierItem-<pid>-0`；`GetAll` 返回 `IconPixmap=[(128,128,16384 bytes)]`，解出来就是 UZIP 图标；dbusmenu `GetLayout` 结构为「显示 UZIP / 桌面只留方块(checkmark) / 分隔符 / 退出」。宿主是 quickshell（`qs -p /run/user/1000/danklinux-shell/...`），不是 waybar |
| E3 | 方块位置恢复 | **修掉一个真 bug**：窗口未映射时设 `Position` 会被丢弃（实测设 800,600 读回 0,0，最终落 2240,600）。改成 50ms × 最多 20 次的追踪式重设（`_target` + `_restore` 抑制回写），实机 4 组坐标全部精确恢复。回归测试 `Puck_restores_the_saved_position_instead_of_the_compositor_default` |
| E3 | 置顶 | 设置变化即时作用到 `Topmost`；但 Hyprland 无 EWMH 置顶语义（见 N4） |
| E5 | 日常使用 | CLI 侧真机验证：`extract plain.7z -o out` 解出 2 个文件；加密包无口令时正确报「密码错误或加密头损坏」，带 `--password` 解出。GUI 侧按 AGENTS.md 铁律**不在用户真实会话里开窗**，改由无头渲染测试 + 21 条单测覆盖 |
### 顺手修掉的两个真 bug（都是实机才暴露的）

1. **拖动方块导致 settings.json 被反复写盘 + 日志刷屏**：`PuckWindow.SavePosition` 每收到一次 `PositionChanged` 就 `Settings.Save`，而 `SettingsService.Save` 是「无条件写盘 + 全量 Changed 广播」。合成器重新摆放窗口会反复抛同坐标，实机 7 秒刷了 430 行日志。修法：坐标没变就不 Save；`App.ApplyWindowSettingsOnUi` 只在置顶值真变时打日志。**变异验证**：把去重条件改成 `if (false) return;` 后回归测试立刻变红。
2. **`scripts/aur-sums.sh` 在下载被截断时误报 hash 不一致**：GitHub 对 35 MB 的 Release 包会中途断流，`curl -f` 仍以 0 退出，留下残缺文件；脚本对残缺文件算 sha256，得出「PKGBUILD 里的值和实测不一致」的吓人结论。修法：新增 `fetch_verified()`，用 `<url>.sha256` 侧车交叉验证，能区分「缓存坏了」和「hash 填错了」，缓存坏了就删了重下。

### 工程与发布（P1–P6 全部完成）

| # | 事项 | 结果 |
|---|------|------|
| P1 | AUR 准备 | `packaging/arch/.SRCINFO` 与 `.SRCINFO-bin` 已生成；`PKGBUILD-bin` 的 `sha256sums_x86_64`=`fcf1f84b…` / `sha256sums_aarch64`=`71e9706e…` 是真值（源码 `PKGBUILD` 因「文件在归档内」的固定点只能 SKIP，见下表）；新增 `scripts/aur-sums.sh` 做「下载→计算→比对→回填→生成 .SRCINFO」一条命令。**真提交做不了**（见 P1b），但 v3.13 Release 已发布、产物已上传，AUR 提交所需的 hash 与文件都已就绪 |
| P2 | deb / rpm / AppImage | 新增 `scripts/package-linux.sh`（统一入口）+ `packaging/deb/`、`packaging/rpm/`、`packaging/appimage/`。三种格式共用一份 payload 暂存树，保证逐字节一致，全程不 strip（守住 `Arithmetic overflow while reading bundle` 那条坑）。**deb 已真打包（36 MB）并拆包核对元数据**；rpm/AppImage 本机缺工具，留给 CI |
| P3 | arm64 | 两个 PKGBUILD 的 `arch` 与 `build()` 已支持 aarch64；`package-linux.sh --arch arm64` 路径可跑；CI 按规格只产 arm64 tar.gz |
| P4 | Windows CI 跑 Avalonia 测试 | `build-windows.yml` 新增 `Test the Avalonia shell (headless, Windows)`；不需要 `EnableWindowsTargeting`（Avalonia 测试工程本身是 net8.0），且 `dotnet test` 给的是 .csproj 而非 .sln，WPF 项目不会被拉进 restore 图 |
| P5 | 版本号单一来源 | 新增 `scripts/version.ps1`：声明四处来源（Avalonia csproj 为权威、Cli csproj、两个 AssemblyInfo），归一化到三段后比对，漂移即 throw 并列出四行。`publish.ps1` 与 `publish-linux.ps1` 都改成 `. version.ps1; Get-UzipVersion` |
| P6 | README 精简 | `README.md` 339→138 行，v3.8 及更早整段移到新增的 `docs/history.md`（212 行） |

---

## 三·补、v3.13.0 做了什么（原生 Wayland + Arch 分发）

### 图形后端：XWayland → 原生 Wayland

根因是 **`Avalonia.Wayland` 这个包从 12.1.0 才开始发布**，项目原先锁在 11.3.2，而 Avalonia 11.x
原生不带 Wayland 后端（`strings Avalonia.Desktop.dll | grep -i wayland` 为空，只有
`LoadX11` / `LoadAvaloniaNative`），所以 `UsePlatformDetect()` 在 Linux 上必然退回 XWayland。

| 事项 | 做法 | 验证 |
|------|------|------|
| 升级 Avalonia | 11.3.2 → **12.1.4**，新增 `Avalonia.Wayland`、`Avalonia.Skia`、`Avalonia.HarfBuzz` 引用 | 构建 0 警告 0 错误 |
| 接入 Wayland | `UZIP2.Avalonia/Program.cs` 的 `BuildAvaloniaApp()` 在 `OperatingSystem.IsLinux()` 时叠 `UseWaylandWithFallback()`（必须排在 `UsePlatformDetect()` 之后，否则抛 `A fallback windowing backend must be configured…`） | 纯 Wayland 会话（`env -u DISPLAY`）里启动成功 |
| 协议层实证 | `WAYLAND_DEBUG=1` 统计：`wl_shm` 80 次、`wl_compositor` 29 次、`wl_seat` 9 次、`xdg_wm_base` 7 次 | 无任何 X11 往返 |
| 进程层实证 | `ls /proc/<pid>/fd` 无 X11 socket；`/proc/net/unix` 里只有单实例管道与 dotnet 诊断 socket | 确认不是隐式 XWayland |
| 渲染实证 | cage + headless wlroots（`WLR_BACKENDS=headless WLR_RENDERER=pixman`）截图 1280×720，主界面完整 | 三份产物（dev build / tar.gz / Arch 包）截图 md5 相同：`0fa4ad02621a3772e9d9c2d27f4ac762` |
| 12.x API 迁移 | 拖放改 `e.DataTransfer.TryGetFiles()` + `DataFormat.File`；剪贴板改 `ClipboardExtensions.TryGetTextAsync/SetTextAsync`；`RadioButton.Checked`→`IsCheckedChanged`；`NativeMenuItemToggleType`→`MenuItemToggleType`；`Watermark`→`PlaceholderText`；`SystemDecorations`→`WindowDecorations`；`Bitmap.Save`→`PngBitmapEncoderOptions.Default` | Rebuild 0 警告（增量构建会缓存「0 错误」假象，必须 `-t:Rebuild`） |
| 测试框架 | `Avalonia.Headless.XUnit` 12.x 只给 xunit v3 适配器，与 xunit 2.5.3 并存会 `CS0433 FactAttribute 同时存在于两个程序集` → 迁到 **xunit.v3 3.2.2**，项目改 `OutputType=Exe`，用 `dotnet <tests>.dll` 跑 | 21/21 通过 |
| 工具链 | Avalonia 12 的 XAML 源生成器引用 Roslyn **4.14**，SDK 8 只带 4.11 → 报 `CS0103: 不存在名称 InitializeComponent`。装 **.NET 10.0.100 SDK**；CI 的 `dotnet-version` 与 PKGBUILD 的 `makedepends`（`dotnet-sdk>=10.0`）同步 | 全解决方案编译通过 |

### Arch 分发

| 事项 | 结果 |
|------|------|
| 本机真构建（bin） | `makepkg -f -p PKGBUILD-bin` 走完整 fakeroot 流程 → `uzip2-bin-3.13.0-1-x86_64.pkg.tar.zst`（34 MB）；`bsdtar tf` 确认 `usr/bin/uzip2` + 桌面入口 + 48/128 两档图标；解包后 `--version` → `UZIP 3.13.0` |
| 本机真构建（源码） | `makepkg -f -p PKGBUILD --nodeps`（本机无 `dotnet-sdk` 包，SDK 在 `_tools/` 里）→ `uzip2-3.13.0-1-x86_64.pkg.tar.zst`（34 MB）；比 bin 包多 `usr/share/licenses/uzip2/LICENSE`；解包后 `--version` → `UZIP 3.13.0` |
| 架构专属校验 | `PKGBUILD-bin` 用 `source_x86_64` / `sha256sums_x86_64` / `source_aarch64` / `sha256sums_aarch64`，两架构各校验各自的包 |
| 版本 | 两个 PKGBUILD 的 `pkgver=3.13.0`、`_tag=v3.13`；`.SRCINFO` / `.SRCINFO-bin` 重新生成 |
| **源码包 `sha256sums` 只能是 SKIP**（数学上的固定点） | `PKGBUILD` 本身就在它下载的 tag 归档里：往里填真值会改变 `PKGBUILD` → 改变归档 → hash 又变，永远填不对。历史上填过 `0a792aea…`（对着旧 tag 算的），tag 重指后 CI 立刻报 `==> ERROR: One or more files did not pass the validity check!`。改为 `('SKIP')` 并在文件里写清理由；完整性靠 GitHub 归档无篡改空间 + CI 真跑构建 + Release 逐字给出产物 sha256 兜住 |
| bin 包 sha256 是真值 | `sha256sums_x86_64` = `fcf1f84b…`、`sha256sums_aarch64` = `71e9706e…`（均与 Release 侧车逐字核对一致）。它下载的是 Release 二进制、不在归档内，不受上面那个固定点限制；CI 会断言两个都是 64 位真值 |

### 另外确认的两件事

1. **Avalonia 12 已修掉 SNI `Status` 上游 bug**：3.12 时 `org.kde.StatusNotifierItem.Status` 返回
   `"UZIP2"`（把 `Id` 写进了只允许 `Passive`/`Active`/`NeedsAttention` 的槽位）。升级后在真实
   Hyprland 会话里按 PID 定位到本进程的 SNI，`Id='UZIP2'`、`Status='Active'` —— 规范了，`N1` 关闭。
2. **无 SNI 宿主时托盘降级正常**：cage 沙箱里没有 `StatusNotifierWatcher`，程序照常启动、
   窗口正常渲染、日志只记一条桌面能力，不崩不卡。

### 发行版包（deb / rpm / AppImage）

| 事项 | 结果 |
|------|------|
| deb 本机真构建 | `scripts/package-linux.sh --arch x64 --formats deb` → `uzip2_3.13.0_amd64.deb`（38.8 MB）。本机没有 `dpkg-deb`，走 `ar + tar` 回退路径 |
| AppImage 本机真构建 | 同上 `--formats appimage` → `UZIP2-3.13.0-linux-x64.AppImage`（36.5 MB）；`--appimage-extract` 后 `--version` → `UZIP 3.13.0`，cage 无头沙箱渲染截图与 dev build **md5 完全相同** |
| rpm 由 CI 构建 | 本机无 `rpmbuild`，CI 的 `linux-packages` job 产出 `uzip2-3.13.0-1.x86_64.rpm`（31.7 MB），并在容器里 `rpm2cpio` 拆包、跑 `--version` 得 `UZIP 3.13.0` |
| AppImage 三个坑 | ① `.desktop` 的 `Categories=Utility;Archiver;` 里 `Archiver` 不是注册值 → 改 `Archiving`；② AppStream `<id>` 必须是反域名 → 改 `io.github.pingod.UZIP2`；③ 元数据文件名必须与 id 一致 → 文件改名（否则报 `metainfo-filename-cid-mismatch`）。另外 CI runner 升到 Ubuntu 26.04 后 `tail` 是 uutils 版，`tail -40` 报 `unexpected argument '-4'`，把上面这些真实错误全盖住了，改成 `tail -n 40` |
| 发布 | 九个产物 + 各自 `.sha256` 已上传 Release `v3.13`；rpm 是 CI 构建的那个 |

## 三·补二、v3.13 之后：分卷（多压缩包分包）

用户点测分卷解压时挖出四个 bug，其中一个让 **Linux 图形界面的解压功能完全不可用**。

| 缺陷 | 根因 | 修法 |
|------|------|------|
| **Linux GUI 解压 100% 失败**（阻断级） | `TempManager.Completion` / `ArchiveWorker.TempFix` 把分隔符硬编码成 `\\`，Linux 上拼出 `/tmp/voltest\/UZipTemp_x\` —— 反斜杠成了普通文件名字符，`/tmp` 下凭空多出目录 `voltest\`，随后 `Directory.GetFiles` 抛 `DirectoryNotFoundException`。与分卷无关，所有非 Windows GUI 解压都失败 | 新增 `PlatformPaths.EnsureTrailing` / `TrimSeparators`（跟 `Path.DirectorySeparatorChar`、TrimEnd 双字符），收口 `TempManager` 与 `ArchiveWorker` 里全部单字符 `TrimEnd('\\')` |
| 中间卷被送进**压缩**队列 | `ArchiveInspector.Extractable` 白名单只有 `.001`，`x.7z.003` 被判为普通文件，GUI 自动分流产出 `x.7z.zip` | `CanExtractByExtension` 额外认可 `AnalyzeVolume().IsVolume`（含 `.z01`），仍不误判 `log.txt.002` |
| 预览 / 转格式在分卷上不可用 | 只有解压做了主卷回退，`PreviewAsync`、`RunConvertAsync` 直接把中间卷给 7z（`Cannot open the file as archive`） | 新增 `ArchiveInspector.ExtractTarget`，三处统一走它；转格式产物名按主卷推导（`v7.zip` 而非 `v7.7z.003.zip`） |
| CLI 完全不认识分卷（6 项） | `DoExtract`/`DoList`/`DoTest`/`DoWatch` 从不调 `ArchiveInspector.Inspect`，逐文件硬跑 7z | 新 `VolumeResolution`/`ResolveVolume`：归一到主卷、按文件名数卷找缺口、主卷优先排序、卷组内去重（`PathComparer` 跟平台）、`Refine` 改写诊断为"分卷不完整：缺少 X" |

**trap 记录**：7z 根本区分不了"缺中卷"和"缺末卷"（两者都只回 `Unexpected end of archive` / `Cannot open as archive`，exit=2），缺口只能在文件名层自己数；且**只有在缺的那卷后面还有卷时才报缺口**，否则 `.001~.007` 恰好是全集会被误判成"缺 .008"。

**测试**：`UZIP2.Avalonia.Tests` 21→36（路径分隔符、卷识别、`ExtractTarget`）；`UZIP2.Tests/CliVolumeTests.cs` 新增 14 条（真 7z 造 16k 分卷）。造多片卷组时**填充必须随机** —— 可压缩填充会让 7z 把几十 KB 全塞进第一卷。

**验证**：CLI 六场景全部 exit code 正确；GUI 在 wstream 沙箱里 `v7.7z.003` 卡片显示"Extract 完成"、汇总"活动 0 · 完成 1 · 失败 0 · 共 1"；预览/转格式用 net8.0 探针实测 `entries=22` 与产物 `v7.zip`。

**残留**：① 缺卷检测依赖三位补零命名，≥1000 卷走不误报兜底；② 末卷"存在但被截断"归入"末卷缺失或最后一卷未下载完"；③ `UZIP2.Tests` 在 Linux 跑不了（testhost 需 `Microsoft.WindowsDesktop.App`），新 CLI 测试只在临时 net8.0 探针里跑过，正式工程需 Windows CI 复跑。

---


## 四、明确不做的（评估过，留记录避免重复讨论）

- **更新检查的 ETag / 条件 GET / TTL 缓存**：每天一次的固定成本，不值得引入缓存正确性风险。
- **监听目录启动时先扫一遍现存文件**、`Changed` 去重：与"只处理新出现的完整文件"语义有冲突，收益不明确。
- **跨进程历史/设置写回合并**：GUI 与 CLI 同时写时后写者覆盖，已在已知限制里说明。
- **InstanceBus 转发的 ack 与日志**：转发失败已经有降级路径（第二实例继续独立启动）。
- **历史搜索防抖**：条目上限 500，全量过滤没有性能问题。
- **重定向 CLI stdout 的 UTF-8 代码页**：Windows 控制台代码页由用户环境决定，改成强制 UTF-8 会破坏已有脚本。
- **WinRAR 恢复记录 / ISO 写入**：7-Zip 26.03 不支持（实测），不做假开关。
- **macOS 版**：没有需求，且引擎与桌面集成都要重做。
- **给 SNI 的 `Status` 属性打补丁（N1）**：Avalonia 内部实现，本项目改不到。**v3.13 升级到
  Avalonia 12 后已自行修好，无需打补丁**。
