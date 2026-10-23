# 更新日志

本项目按 tag 发布（两段版本号，如 `v3.12`），安装包里的程序版本是三段（`3.12.0`）。
条目按"用户能感知的变化"写，不逐条列重构。

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
