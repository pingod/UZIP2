# UZIP2 重构设计（方案 A：.NET 8 WPF + WPF-UI Fluent + MVVM）

日期：2026-09-30
状态：已与用户逐节确认

## 背景与目标

UZIP2 是一个 Windows 拖拽式解压/压缩工具，核心卖点是密码自动尝试（密码本/密码纸智能排序）、多级解压、文件过滤、压缩日志。现有代码经 v2.3 增量修补后仍存在结构性问题：

- `MainWindow.xaml.cs` 2645 行上帝类：UI 事件直接调用 7z 进程、直接读写配置。
- `Tools.cs` 1015 行混合进程调用、分卷识别、路径工具、文件操作。
- `Setting.cs` 836 行静态属性全部即时读写 XML 配置，全局静态状态贯穿所有窗口。
- 界面为 500×250 绝对坐标布局的自绘皮肤小窗，风格陈旧；设置面板 3 个 Tab 密排 40+ 控件。

重构目标（用户确认的优先级）：
1. 界面美观：Fluent / Win11 风格（Mica、圆角、深浅色跟随系统）。
2. 交互重做：拖拽流程、设置界面、进度与结果反馈、密码管理四个环节全部优化。
3. 代码结构：MVVM 分层，服务可单测，View 与业务解耦。

非目标：跨平台（明确不做）；砍功能（全部现有功能保留）；更换解压后端（继续调用外部 7z.exe）。

## 技术选型

| 项 | 选择 | 理由 |
|---|---|---|
| 运行时 | .NET 8 (LTS)，`net8.0-windows` | 用户批准升级；单文件发布、启动更快；放弃 Win7 |
| UI 库 | WPF-UI 4.x（lepoco/wpfui，NuGet） | 活跃的 Fluent 组件库：FluentWindow/Mica、NavigationView、SettingsExpander、ProgressRing、TextBox(Rich)，Win11 原生观感 |
| MVVM | CommunityToolkit.Mvvm 8.x | source generator、ObservableRecipient、RelayCommand |
| DI/Host | Microsoft.Extensions.Hosting（仅 DI + Options + Logging 抽象） | 构造注入，替代全局静态 |
| 配置存储 | `settings.json`（System.Text.Json）+ 旧 XML 一次性迁移 | 替代 `System.Configuration` appSettings |
| 密码存储 | DPAPI（沿用 `ProtectedData`），密文原样迁移 | 安全模型不变 |
| 日志 | 自研滚动文件日志（logs/），不引第三方 | 崩溃诊断够用，包体小 |
| 测试 | xUnit；SevenZipClient 用真实小样本包做集成测试 | Services 层纯逻辑可测 |
| CI | GitHub Actions `dotnet publish` 单文件 win-x64 | 替换现有 MSBuild 流程 |

本机实施前需安装 .NET 8 SDK（走国内镜像下载）。

## 第 1 节 代码架构

单 csproj（UZIP2），按文件夹分层：

```
UZIP2/
├── App.xaml(.cs)              // Host 构建、DI 注册、单实例 Mutex、启动管线、全局异常钩子
├── Models/                    // 纯数据记录，无行为
│   ├── ArchiveInfo.cs         // 文件路径/真实扩展名/是否分卷/建议密码
│   ├── JobEntry.cs            // 队列任务: 状态、进度、当前文件、结果、诊断
│   ├── JobResult.cs           // 成功/失败 + 输出目录 + 使用密码
│   ├── PasswordEntry.cs       // 密码 + 名称 + 使用次数/成功次数(排序打分用)
│   └── CompressionLogEntry.cs
├── Services/
│   ├── SettingsService.cs     // settings.json 读写、变更通知；启动时执行 LegacyConfigMigrator
│   ├── LegacyConfigMigrator.cs// 旧 Config/UZIP2.exe.config(appSettings+Items) → 新 JSON，逐项映射，DPAPI 密文搬运，旧文件改名 .bak
│   ├── PasswordService.cs     // 密码本/密码纸 CRUD、去重、DPAPI 加解密、智能排序打分（从 MainWindow 抽出）
│   ├── ArchiveWorker.cs       // 后台任务队列（Channel），串行消费；IProgress 上报；CancellationToken 取消；完成后触发过滤/删源/自动打开
│   ├── SevenZipClient.cs      // 重写 UCmd：Process + stdout 解析；Test/List/Extract/Compress；进度事件；取消；错误诊断（Diagnose7zError 迁入并扩展）
│   ├── VolumeHelper.cs        // 原 VolumesFile：分卷识别、主卷定位
│   ├── FilterService.cs       // 解压后广告文件过滤（规则来自设置）
│   ├── PasswordFromNameService.cs // 分隔符提取密码
│   ├── TempManager.cs         // UZipTemp_* 隐藏目录创建、启动清理
│   ├── CompressLogService.cs  // 压缩日志读写（保持旧文件格式兼容）
│   ├── TrayService.cs         // 原 Notifier：托盘、气泡、最小化驻留
│   ├── HotKeyService.cs       // 原 UHotKey：RegisterHotKey P/Invoke 封装
│   └── ClipboardService.cs    // 剪贴板读取 + TrimSpace 规则
├── ViewModel/
│   ├── ShellViewModel.cs      // 导航、全局状态条、置顶/主题切换
│   ├── HomeViewModel.cs       // 模式切换、拖放命令、任务队列集合、进度绑定
│   ├── PasswordBookViewModel.cs
│   ├── SettingsViewModel.cs   // 分组设置项，写回即保存 SettingsService
│   └── ResultViewModel.cs     // 独立结果窗口（可选功能保留）
├── View/
│   ├── MainWindow.xaml        // FluentWindow + NavigationView 壳
│   ├── HomePage.xaml          // UserControl：拖拽卡 + Segmented 模式条 + 任务队列
│   ├── PasswordPage.xaml
│   ├── SettingsPage.xaml      // SettingsExpander 分组
│   ├── ResultWindow.xaml      // 独立结果窗口（沿用旧功能的简化版）
│   └── Dialogs/               // YesNo、7z 引导、目录选择（FolderBrowser）
└── Assets/                    // UZIP.ico、插画
```

规则：
- View 代码后置仅允许 `InitializeComponent`、窗口拖拽、DragDrop 事件转发到 ViewModel 命令；禁止在 Click/MouseEnter 中写业务。
- 服务接口（`ISettingsService` 等）+ 构造注入；不再存在 `Setting.cs` 式全局静态可变状态；`StaticData` 仅保留只读常量。
- 原 `SimulateInput.cs`（虚拟键盘贴入）保留为 `HotKeyService` 的贴入实现细节；KeyBoard.xaml 窗口取消，密码输入改为普通 TextBox + 粘贴 + 热键贴入。
- `DebugWindow` 保留入口（设置→关于→调试信息），内容改为读取 logs/ 尾部。
- 删除 ScrollViewDictionary.xaml、手绘标题栏、逐控件 MouseEnter/MouseLeave 变色代码。

## 第 2 节 界面重设计（Fluent）

整体从 500×250 绝对坐标小窗改为约 860×620 可缩放导航窗口；主题默认跟随系统，可在设置锁定浅/深。

**外壳**：`ui:FluentWindow` + Mica 背景；左侧 `ui:NavigationView`（主页/密码本/设置）；系统标题栏（替换手绘 ✖ 与皮肤）。

**主页（拖拽解压/压缩）**——解决"拖了会发生什么"：
- 中央大号拖拽接收卡：虚线圆角边框；`DragEnter` 时整卡高亮，并按当前模式 + 悬停文件类型实时显示预告文案，例如"释放以解压 3 个压缩包 → 同级目录"、"释放以压缩 5 个文件 → 20260930_....zip（随机密码已启用，密码写入文件名）"。非法内容（如拖入文件夹进解压模式）显示灰色警告文案而非静默。
- 顶部模式条：`SegmentedControl` 三态（自动/仅解压/仅压缩）+ 密码收集器按钮。
- 任务队列卡片列表：每个压缩包一张卡；`ProgressRing` + 百分比 + 当前文件名 + 取消按钮；多文件时显示 (2/5)。完成后卡片原地变状态徽标：绿=成功（内联"打开目录"、"删除源文件"动作），红=失败（诊断文案 + "重试"、"手动输密码重试"）。独立 ResultWindow 变为设置项（默认关，走内联卡片）。
- 底部状态条：7z 路径状态点、密码纸计数与"贴入剪贴板密码"按钮、模式相关快捷开关。

**密码本页**——解决"录入/贴入不顺手"：
- 上：密码本列表（名称、密码打码可揭示、使用/成功次数、按得分排序、搜索框、增删改行内完成）。
- 下：密码纸区（多行 chip 样式；剪贴板一键贴入并去重；清空；"选中密码+热键贴入"仍由 HotKeyService 提供）。

**设置页**——解决"密排找不到开关"：
- Win11 设置式分组：常规（置顶/主题/托盘/结果窗口/自动打开/清理临时）、解压（输出模式、8 个自定义目录改为可增删条目列表、覆盖模式、未知格式尝试、删源文件、过滤规则）、压缩（密码策略/随机长度/密码写文件名/日志）、密码（读取模式、TrimSpace、热键录制用 ui:KeyBox）、关于（版本、7z 引导、调试日志入口）。
- 每项副标题说明用途；写回即存 settings.json，无"保存"按钮。

**错误与状态呈现**：7z 缺失 → 主页顶部红色 banner + "定位 7z.exe"按钮，不阻断启动；全部密码试错 → 失败卡片诊断（密码错/文件损坏/格式不支持/磁盘满，迁移并扩展 `Diagnose7zError`）。

## 第 3 节 数据流与配置迁移

运行数据流：
```
Drop(文件集合) → HomeViewModel(模式判定+预告) → ArchiveWorker.Enqueue
  → 逐任务: ArchiveInfo(RealExtension/分卷) → PasswordFromNameService + PasswordService 排序候选
  → SevenZipClient.Test 逐密码 → Extract/Compress（stdout 进度 → IProgress → JobEntry，UI 线程安全绑定）
  → FilterService 删广告 → DeleteFinishFile/TempManager 清理 → AutoOpen/CompressLog/TrayService 通知
```
全程 `CancellationToken`；队列串行保证 7z 不并发锁 temp；取消只作用于当前任务，其余继续。

配置迁移（一次性、幂等）：
- 检测 `Config/UZIP2.exe.config`（旧 appSettings 键全集见 Setting.cs:54-800，含 ExtractOutMode、CustomizeFolder*1-8、HotKey*、PWUrl 等）与 `Items` 节（PWPaper/PWBook 的 DPAPI 密文条目）。
- 逐项映射进 `settings.json`；DPAPI 密文原样搬运（同机同用户可直接解）；明文密码条目自动加密。
- 迁移成功后旧文件改名 `*.bak`；迁移异常则保留旧文件、以默认值启动并在设置页提示。
- 新装用户直接生成默认 settings.json。

## 第 4 节 错误处理

- UI 线程：`DispatcherUnhandledException` 记录日志 + 卡片/横幅呈现，不崩溃退出。
- Worker：任务级 try/catch 全部转 `JobEntry.Error`；进程启动失败（7z 丢失）→ banner。
- 文件冲突：覆盖模式沿用现有逻辑；删除失败不判任务失败，记警告。
- 日志：`logs/app-yyyyMMdd.log` 滚动，保留 7 天；DebugWindow 读尾部 200 行。

## 第 5 节 测试与验收

单测（xUnit）：
- PasswordService 排序打分/去重；PasswordFromNameService 分隔符提取；FilterService 匹配；VolumeHelper 主卷识别；LegacyConfigMigrator 键映射（fixture 旧 config XML）；SevenZipClient 进度行解析；RealExtension 魔数。
集成测试：真实生成 密码 ZIP/7z/RAR 小样本，Test→Extract 全流程、错误密码→诊断文案。
验收（人工 + 截图）：拖拽解压带密码包、批量 5 文件队列、压缩+随机密码写文件名、旧配置迁移（用 H 盘现有 Config 目录做真机验证）、深浅色主题、托盘常驻、热键贴入、取消任务。
CI：build-windows.yml 改 `dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true`，产物不含 7-Zip。

## 实施顺序

1. 安装 .NET 8 SDK（镜像源）→ 工程 SDK-style 化，`net8.0-windows`，编译通过为门槛。
2. Services 抽取 + 单测（先不动 UI）。
3. 新 UI 三页 + MVVM 绑定，删除全部旧事件处理器。
4. 迁移器 + 真机 Config 验证。
5. 全功能验收清单回归 → 发布物对比验证 → 更新 README/CI。

每个阶段独立可运行、可回退；阶段 2-3 期间旧 UI 可用 git 分支隔离。

## 兼容性与风险

- WPF-UI 4.x 对 .NET 8 支持成熟，但 Mica 在 Win10 上自动退化为实色（可接受）。
- 右键菜单/命令行以纯文件路径数组启动（App.xaml.cs 将 `e.Args` 交给 `Setting.FileList`），加上单实例转发，必须保持同等行为，回归清单含命令行入口。
- DPAPI 密文跨机不可解：迁移不改变该性质，UI 需说明。
- 最大风险是 2645 行 MainWindow 中隐含行为遗漏 → 缓解：抽取服务前先为其建立行为清单测试（characterization tests），逐方法对照迁移。
