# 行为对等清单（旧版 UZIP 2.22 → 新版 3.0）

每项标注验证方式：**测试** = `UZIP2.Tests/ParityTests.cs` 走真实 7z.exe 端到端；**实测** = 启动 `bin/Release/net8.0-windows/UZIP2.exe` 现场观察。
基线：`dotnet test` 129/129 通过（2026-09-30）。

| # | 行为 | 结果 | 证据 |
|---|------|------|------|
| ① | 命令行 / 右键"发送到"直接入队解压：`UZIP2.exe a.zip b.zip` | ✅ | 实测：`UZIP2.exe D:\tmp-live15\a.zip` 启动后无人工干预，`one.bin` 解到压缩包所在目录（`extractOutMode=1`）；日志见 `7z t` → `7z x -o...\UZipTemp_a -aos` |
| ② | 第二实例把参数转发给主实例后退出 | ✅ | 实测：主实例运行中再启 `UZIP2.exe b.zip`，第二进程 2.3s 内 exit 0 且无新窗口；主实例解出 `two.bin`。链路 = `InstanceBus`（Local 互斥体 + 命名管道 `UZIP2_FileArgs_Pipe`） |
| ③ | 密码试错链：无密码 → 人工指定 → 外部 → 文件名 → 密码本(按成功次数) → 密码纸；全部失败给出诊断 | ✅ | 测试：`WrongPassword_all_sources_tried_then_diagnosis`（诊断文案 `需要密码，但密码本/密码纸中未找到正确密码`，且密码纸未被消耗）、`Paper_password_hit_consumes_entry`（命中即消耗） |
| ④ | 多级解压（包中包递归，深度上限 8） | ✅ | 测试：`Nested_archives_extracted_recursively`（outer.zip→inner.zip→leaf.bin 产生 2 个作业且全成功）；v3.7 又补了三层链与深度上限截断两个用例，见 F23 |
| ⑤ | 分卷（`.zip.001` / `.part1.rar` / `.z01`）只解一次，副卷静默完成 | ✅ | 测试：`Split_volumes_extract_once_from_main`，4 卷 `7z a -v128k` 产物入队 → 4 作业全成功，副卷诊断为 `已随分卷主文件处理`，`big.bin` 落盘 |
| ⑥ | 压缩密码写进文件名 + 随机密码（8/16/32 位） | ✅ | 测试：`Random_password_written_into_archive_name`，`passwordMode=6`+`passwordToName` → 文件名以 `" " + 密码 + ".zip"` 结尾，且该密码确实可解 |
| ⑦ | 解压过滤命中项删除；`HideZipContent` → `-mhe=on` 头加密后包内文件名不可见 | ✅ | 测试：`Extract_filter_removes_ad_files_and_header_encryption_hides_names`（`广告.txt` 被删；无密码 `7z l` 输出不含 `inner-name.dat`） |
| ⑧ | 压缩日志 `Config/Compress.log`（旧逐字格式，含密码，供事后找回）+ 打开入口 | ✅ | 测试：`Compress_log_records_archive_and_password`；实测：设置 → "7-Zip 与日志" → 新增 `打开压缩日志` 按钮（旧版"查看压缩日志"的等价入口） |
| ⑨ | 窗口置顶 / 调试模式 | ✅ | 实测：`windowOnTop=true` 后主窗口 `WS_EX_TOPMOST` 置位（exstyle 0x40108）；`debugMode=true` 后 `logs/app-yyyyMMdd.log` 出现 `[7z] ...` 命令行，且 `-p<密码>` 参数写日志前脱敏为 `-p***`（测试 `DebugMode_logs_redacted_command_line`） |
| ⑩ | 独立结果窗口开关（旧 ResultWindow 数据面，换 Fluent 皮） | ✅ | 实测：`resultWindow=true` 时批次完成后弹出"处理结果"窗口，含 `共 1 项，成功 1 项，失败/取消 0 项` 与 名称/操作/结果/说明 表格；关闭设置内该开关后不再弹出 |

## 本轮为达成对等而修复的缺陷

1. **7z 交互式密码提示会挂起进程**（`SevenZipClient.RunAsync`）：无密码试探时 7z 打印 `Enter password (will not be echoed):` 并等待控制台输入。从终端启动程序时整批作业会卡死。现改为重定向并立即关闭 stdin，7z 快速失败退出。
2. **加密包诊断文案错误**（`SevenZipClient.Classify`）：上述输出未被识别，导致"需要密码"的包被归为 `Unknown`，用户看到的是"密码本/密码纸中未找到正确密码"而非"需要密码，但…"。新增 `enter password` 关键字 → `WrongPassword`。
3. **settings.json 大小写不兼容**（`SettingsService`）：写入用 camelCase，读取却区分大小写，任何手写/工具生成的 PascalCase 配置被静默忽略（全部回落到默认值）。开启 `PropertyNameCaseInsensitive`。
4. **结果窗口异常被静默吞掉**（`MainWindow`）：`BatchFinished` 回调现在捕获并写日志，避免窗口创建失败时用户毫无察觉。
5. **压缩日志无 UI 入口**：旧版有"查看压缩日志"按钮，新版缺失，已补 `打开压缩日志`。

## 未改动项说明

- `ShowDebug`（旧版独立调试窗口开关）在 3.0 中由 `DebugMode` + `logs/app-*.log` + "打开最新日志"覆盖，不再单开窗口。
- 右键菜单注册沿用旧版机制（向资源管理器注册表写入调用 `UZIP2.exe` 的命令），程序名与可执行文件名未变（`UZIP2.exe`），旧注册项无需重写；升级后如需调整路径，指向新的 `UZIP2.exe` 即可。

## v3.1 增量能力（旧版没有的新功能）

上表是"旧版有的功能一个都不能丢"；下面是重构后新增的处理逻辑与入口，同样标注验证方式。基线：`dotnet test` 329/329 通过（2026-09-30）。**渲染核对** = 用一次性离屏快照（`RenderTargetBitmap`，窗口摆在屏幕外且不激活）检查布局、图标与配色，同时收集 WPF 数据绑定告警。

| # | 能力 | 证据 |
|---|------|------|
| N1 | 并发作业队列（解压默认 3 路、压缩默认 2 路，设置里 1–8 实时可调） | 测试：`ParallelQueueTests`；实测：一次拖入多个包，进度条同时推进。8×20 MB 从 712 ms 降到 122 ms（8 路） |
| N2 | 未加密包不再跑整包 `7z t`：只读归档头（`7z l -slt`）判定加密状态 | 测试：`SevenZipClientTests.Probe_Detects_Encryption_State` / `Probe_Non_Archive_Is_Unknown`（实测 100 MB 包 `l` 0.041 s vs `t` 1.548 s） |
| N3 | 解压前包内清单预览，可勾选只解其中几项（`PreviewBeforeExtract`，默认关） | 测试：`ArchiveInspectorTests` + `ArchiveWorkerTests` 的选中项解压 |
| N4 | 监听下载目录自动解压（`WatchEnabled` / `WatchFolder`，去抖 + 稳定性判定） | 测试：`WatchFolderServiceTests` |
| N5 | 压缩日志检索窗口（按包名 / 密码 / 时间过滤 `Compress.log`） | 测试：`CompressLogServiceTests`；实测：设置 → "7-Zip 与日志" → 检索压缩日志 |
| N6 | 失败条：一键批量重试全部失败项 + 导出失败报告（报告绝不含密码） | 测试：`FailureReportTests`（含"导出件不含密码"用例） |
| N7 | Windows 右键菜单注册/移除（HKCU，免管理员），设置页显示当前指向；"解压到当前文件夹"动词带 `Flat`，无视 `CreateNewFolder/CreateNameFolder` 建目录设置 | 测试：`ShellMenuTests`（写入/移除/状态判定/指向检测）+ `ArchiveWorkerTests.Flat_extract_overrides_create_name_folder`。实测：从 H: 部署副本跑 `--register-shell` 后，注册表里 5 项（文件解压到当前/解压/压缩 + 目录压缩 + 目录背景压缩）全部指向（v3.7 起为 6 项，见 F20） `H:\Sync\PublicShare\software\UZip\UZIP2.exe`；资源管理器里的菜单外观未截图核对 |
| N8 | 密码库跨机加密导出/导入（口令派生密钥；导出件不含明文，也不含口令） | 测试：`VaultTransferTests` |
| N9 | 桌面迷你拖拽方块：只留一个小窗，文件拖上去即处理（`MiniPuck`，默认关；位置记忆） | 测试：`HomeViewModelTests` 的模式路由与预告文案复用；方块窗口本身待人工拖放确认 |
| N10 | 分卷压缩 `-v`（`700m` / `1g` / 纯字节；非法值在启动 7z 前挡下） | 测试：`VolumeSizeTests` + `ArchiveWorkerTests`；产出 `name.7z.001/002…` |
| N11 | 高级压缩参数：固实 `-ms`、线程 `-mmt`、文件名加密（并说明只对 7z 生效） | 测试：`CompressOptionsTests` |
| N12 | 按目录压缩预设：最深目录命中、逐字段回落全局、预设刻意不含密码 | 测试：`CompressPresetTests`（含真实 7z 分卷产物、非法分卷值在启动 7z 前挡下、设置往返） |
| N13 | 校验面板：SHA-256 + `.sfv` / `.md5` / `.sha256` 旁挂逐条核对（含分卷缺失、未收录） | 测试：`ChecksumServiceTests`；实测：离屏渲染快照核对四种状态色与图标 |
| N14 | 检查更新：启动至多一天问一次 GitHub Releases，可关可手动，失败静默；代理优先读 `HTTP(S)_PROXY` | 测试：`UpdateServiceTests`；实测：带代理环境变量启动后 `settings.json` 写入 `latestSeenVersion` |
| N15 | 密码本明文按密文缓存，且只在真正改动时写 `passwords.json` | 测试：`PasswordServiceTests`（`Changed` 次数即落盘次数） |

## v3.2 命令行 CLI（全新入口）

基线：`dotnet test` 385/385 通过（2026-10-01）。CLI 复用 GUI 同一套服务，headless 运行。验证方式：**测试** = `UZIP2.Tests/CliParserTests` + `CliRunnerTests`（真实 7z.exe）；**实测** = 从 Git Bash 直接跑 `bin/Release/net8.0-windows/UZIP2.exe <命令>` 核对退出码与输出。

| # | 能力 | 证据 |
|---|------|------|
| C1 | 显式命令词才进 CLI；`--extract`/`--compress`/`--register-shell`/裸文件路径仍走 GUI/单实例 | 测试：`CliParserTests.IsCli_*`（命令词 true、菜单开关 false） |
| C2 | `list`/`test`/`extract`：`-o`/`--here`/`--password`/`--auto`/`--entries`/`--cover` | 测试：`CliRunnerTests` 的 compress→list→extract 往返、`--here`、加密需密码、`--entries` 只解选中项 |
| C3 | `compress`：`-o`/`--name`/`--type`/`--level`/`--password`/`--volume`/`--solid`/`--threads`/`--headers`/`--exclude`/`--delete-source`，成功写压缩日志 | 测试：`CliRunnerTests` + `Usage`；实测：`compress --name pack.zip` 产出可 `list` 的包 |
| C4 | `checksum`：SHA-256 展示 / `--verify` / `--write` | 测试：`CliRunnerTests.Checksum_*`（写入→核对→篡改退出 1）；实测：`--write` 出 `.sha256`，`--verify` OK |
| C5 | `vault` 子命令 + `config show/get/set` + `log compress/app` + `shell register/unregister/status` + `watch once` + `update` | 测试：`CliRunnerTests` 各子命令；实测：`config get/set Theme`、`shell status`、`log compress` |
| C6 | 退出码 `0/1/2`（操作失败/用法错误），`--json` 结构化输出 | 测试：`CliRunnerTests`（缺文件→2、错误密码→1、未知键→1）；实测：`list`（无包）退出 2 |
| C7 | 安全：`vault list`/`log compress` 默认脱敏，`--show-passwords` 才还原；错误报告不含密码 | 测试：`CliRunnerTests`（vault 掩码 + 导出件不含明文/口令 + 日志默认脱敏）；实测：`vault list` 无值、`--show-passwords` 见值、`log compress` 显示 `***` |
| C8 | WinExe 控制台接管 + 进程退出：`AttachConsole` 输出、跑完 `Environment.Exit(code)`、headless 作业放线程池避开 UI 线程死锁 | 实测：Git Bash 下 `version`/`compress`/`checksum` 等即时返回并给出正确退出码（修复前 await 类命令挂死、`Shutdown()` 不终止进程） |

## v3.3 一键自更新（就地换体）

基线：`dotnet test` 417/417 通过（2026-10-01），主工程 0 警告。仅框架依赖单文件可就地更新；自包含/缓存路径回落"打开发布页"。验证方式：**测试** = `UpdateServiceTests`（资产直链解析）+ `SelfUpdaterTests`（判定/校验/中继，含一个真实 `_rel/fd/UZIP2.exe` 端到端换体）+ `CliRunnerTests`（`update`/`--apply` 注入 seam 分支）。

| # | 能力 | 证据 |
|---|------|------|
| S1 | 从最新 Release 的 assets 里挑 `UZIP2.exe` 直链，填 `DownloadUrl`/`Size`；命中不到留 null 回落 | 测试：`UpdateServiceTests.ParseRelease_picks_the_framework_dependent_asset`、`Asset_matching_is_case_insensitive`、`No_matching_asset_leaves_download_url_null` |
| S2 | `CanApplyInPlace`：小体积真实 exe 放行；>40MB（自包含）、`\.net\UZIP2` 缓存、不存在/空路径一律拒绝 | 测试：`SelfUpdaterTests.CanApply_*`（4 例） |
| S3 | `Verify`：`MZ` 头 + `FileVersionInfo` 逐段版本比对，过小/非 PE/有 MZ 但读不到版本/版本不符分别拒绝 | 测试：`SelfUpdaterTests.Verify_*`（6 例，含用真实程序集自身版本放行、`0.0.1` 触发不符） |
| S4 | 中继 `cmd`：等 PID 退出→重试覆盖 `new`→`exe`→拉起新版→删临时体与脚本自身；路径带空格仍正确加引号 | 测试：`SelfUpdaterTests.Relay_*`（字符串断言 tasklist/copy/start/`del %~f0`）+ **实测端到端**：`Relay_end_to_end_swaps_real_exe_and_cleans_up` 用真 exe 死 PID 完成换体、`.new` 与脚本均消失、`--version` 无窗口退出 |
| S5 | CLI `update`（默认 `--check`，`--json` 含 `downloadUrl/size/canApply`）与 `update --apply`（下载→校验→就位退出） | 测试：`CliRunnerTests.Update_*`（8 例：有更新提示 apply、已是最新、JSON canApply、`--apply` 各分支与失败上报，全部离线注入） |
| S6 | GUI 主页"可更新"横幅新增**一键更新**按钮（`CanAutoUpdate` 把关）+ 下载进度文案 + 就绪自动重启 | 测试：`UpdateServiceTests.Banner_bindings_resolve_against_the_view_model`（校验 `ApplyUpdateCommand`/`CanAutoUpdate`/`UpdateStatus`/`IsUpdating` 绑定解析）；`ApplyUpdate` 走 S2 的就地判定与安全回落 |
| S7 | 安全边界：64MB 体积上限、版本精确匹配、只覆盖自身 exe 不碰 `Config`/`7-Zip`/`Bandizip`、失败/取消清理 `.update.new`、复用同一环境代理 | 代码：`SelfUpdater.StageAndApplyAsync` 失败路径 `TryDelete(temp)`；`UpdateService.MaxDownloadBytes`；实测换体仅在临时目录进行，绝不触碰部署目录 |

## v3.4 发布瘦身 + 免依赖安装

基线：`dotnet test` 418/418 通过（2026-10-01），主工程 0 警告。本轮改的是发布配置与脚本，不新增运行期逻辑，故验证以**实测体积**与**脚本跑通**为准。

| # | 能力 | 证据 |
|---|------|------|
| P1 | 单文件发布瘦身开关（英文附属资源裁剪、无 `.pdb`、关 EnC 元数据、自包含开 deflate 压缩），条件属性组隔离使普通 build/test 不受影响 | 实测同码对比：自包含 exe 162.0 MB→66.1 MB（−59%）、zip 65.6→60.6 MB、框架依赖 exe 8.1 MB 不变；`dotnet test` 418 全绿证明普通构建未被裁剪开关波及 |
| P2 | `scripts/publish.ps1` 一条命令产出 fd+sc 两地产物到 `artifacts/<版本>\` + SHA256SUMS 清单，版本号取自 `AssemblyInfo.cs` | 实测跑通：`Publishing UZIP2 v3.4.0` → `UZIP-3.4.0-win-x64.exe`(8.1MB)+`...-selfcontained.zip`(60.6MB)+`SHA256SUMS.txt`；修复了误匹配注释行 `// [assembly: AssemblyVersion("1.0.*")]` 的正则 |
| P3 | `scripts/install.ps1` 免依赖 per-user 安装：自动选/显式指定源、可选打包 `7-Zip\`、复用 `shell register`、建开始菜单快捷方式；**绝不触碰已存在的 `Config\`** | 实测一次性目录跑通安装；就地重装后 `Config\settings.json` 哨兵内容原样存活；装出的 exe `version` 输出 3.4.0 |
| P4 | `scripts/uninstall.ps1` 默认注销右键+删程序文件+保留 `Config\`+`logs\`；`-Purge` 删数据前需输 `YES`/`-Force` 二次确认 | 实测：安全卸载后 exe 消失、`Config\` 保留；`-Purge -Force` 清空整个目录 |
| P5 | 可选 NSIS 配方 `scripts/installer.nsi`（本机未装 NSIS，不静默装系统依赖，作为想要 setup.exe 的人的现成配方；per-user 免 UAC，右键仍复用 app 自身命令） | 交付脚本文件；未在本机编译（无 makensis），文档注明构建方式 |

## v3.5 持久化历史库

基线：`dotnet test` 464/464 通过（2026-10-01），主工程 0 警告。本轮新增运行期逻辑（服务/建议/落库钩子/CLI 记录/相对路径修复）与一个 GUI 页，验证以单元测试 + 真机冒烟（CLI 写→GUI 读闭环）为准。

| # | 能力 | 证据 |
|---|------|------|
| H1 | `HistoryService`：`Config\history.json` 最新在前、上限 500 丢最旧、原子写、损坏隔离 `history.json.bad-<ts>` 后从空开始；`Record` 只落 Success/Failed | `HistoryServiceTests` 13 个（读写往返、容量裁剪、成功/失败门控、损坏隔离、口令脱敏门控）；真机跑通落盘 |
| H2 | `HistoryAdvice.For(kind,error)`：关键字→中文下一步建议（密码/分卷/磁盘/占用/路径过长/损坏/格式/回落） | `HistoryAdviceTests` 理论用例逐关键字命中；GUI 历史页与 CLI `history` 均渲染失败建议 |
| H3 | GUI 收尾钩子落库（`ArchiveWorker.JobFinished`→`_history.Record`）+ DI 注册 `IHistoryService`/`HistoryViewModel` | 主工程 0 警告编译；历史页导航项渲染、绑定 `Rows` 正常 |
| H4 | CLI `extract`/`compress`/`test`/`watch` 完成后复用 `BuildEntry` 落库，与 GUI 共享同一份历史 | `CliRunnerTests` 端到端 2 个：CLI 压缩+解压写入历史且 `Source` 为绝对路径；错误密码解压记 Failed 且 Advice 非空；真机 `history` 列出四类条目 |
| H5 | CLI `history` 子命令：列表 / `--grep` / `--limit` / `--json` / `--show-passwords` / `--clear`；默认（含 `--json`）口令脱敏为 `***` | `CliRunnerTests` 历史 7 个；真机验证 masked vs `--show-passwords`、`--json` 打码、`--clear` 删文件、`LogPasswords=false` 时落库不含口令字段（grep 明文 0 命中） |
| H6 | 历史页（导航"历史"）：搜索、只看失败、显示口令、刷新、清空（确认）、状态胶囊、原因+建议、口令打码、每行"重跑" | `HistoryViewModelTests` 7 个；真机截图确认渲染、默认打码、勾选"显示口令"还原明文 `letmein`、失败行无口令 |
| H7 | 修复 CLI 相对路径缺陷：文件类命令位置参数与 `-o` 执行前 `Path.GetFullPath` 规范化 | 真机：`compress work/in -o work` 由"文件被占用或无法打开"→ rc=0；H4 端到端断言 `Source` 绝对 |

## v3.6 任务列表管理

基线：`dotnet test` 475/475 通过（2026-10-03），主工程 0 警告。

| # | 能力 | 证据 |
|---|------|------|
| M1 | 终态卡片逐张移除 / 清除失败项 / 清除已完成 / 全部取消；移除只动主页，历史保留 | `QueueManageTests`；真机 UIA 冒烟（失败到达后按钮即时可用） |
| M2 | 列表排序 `settings.JobSort`：加入顺序/最新在前/按状态/按文件名，`JobOrder` 全序比较器 + 精准插入 | `QueueManageTests` 排序全序与插入位置用例 |
| M3 | 窗口不在前台时一次托盘气泡汇总本批失败条数；列表超 200 张自动回收最旧 | `MainWindow` 防抖逻辑（v3.7 抽成 `FailureNotifier` 后单测覆盖） |

## v3.7 全量审计：缺陷清账 + 性能 + 格式互转/包对比

基线：`dotnet test` **642/642** 通过（2026-10-07），**主工程与测试工程均 0 警告、0 错误**。验证方式同上：**测试** = 真实 7z.exe 端到端；**实测** = 驱动 `bin/Release` 的 exe 或部署副本现场观察。

| # | 能力 / 修复 | 证据 |
|---|------|------|
| F1 | 密码本 `Changed` 改为锁外派发（旧实现在持 `_sync` 时回调读 `Book`/`Paper` → 必死锁） | `PasswordServiceTests`（订阅回调里读密码本不再卡死）；接口注释写明"调用方必须持有 `_sync`" |
| F2 | 取消/重试的"状态判定 + cts 查找"原子化，堵住"刚翻成 Running 的一瞬漏掉 `CancelRequested`"；影子作业不再重复入队 | `ParallelQueueTests`、`ArchiveWorkerTests` 取消用例 |
| F3 | session temp 记账回收：创建时在 `Config\` 落一行，启动按账清理输出盘上的 `UZipTemp_*` 残留，失败/取消/异常路径都在 finally 删 | `TempManagerTests`（账本逐条回收、失效行丢弃） |
| F4 | 取消或失败的压缩产物当场删除（分卷按 `.001/.002…` 删到第一个缺口）；压缩后自检先于删源 | `ArchiveWorkerTests`、`CompressVerifyTests`（自检不过→源保留 + Failed） |
| F5 | 解压过滤命中项跟随"删除到回收站"设置 | `FilterServiceTests` |
| F6 | 分卷影子作业等主卷真实终态，主卷失败影子也失败，不再凭空报绿 | `ArchiveWorkerTests` 分卷用例 + `VolumeCompressTests` |
| F7 | `CrashGuard`：UI 线程非致命异常记日志+提示后继续跑；后台线程崩前留现场；未 await 的 Task 不再静默吞；致命异常放行 | `CrashGuardTests` 6 个 |
| F8 | `WatchFolderService.Error` 接住并按当前设置重建 watcher（缓冲溢出/目录被删不再崩进程、不再假"监听中"） | `WatchFolderServiceTests` |
| F9 | `AtomicFile` 唯一临时名，GUI/CLI 双进程不再互搬对方临时件 | `AtomicFileTests` |
| F10 | 历史页"重跑"带上记录里当时成功的口令；校验类记录不再按解压语义重跑 | `HistoryViewModelTests` |
| F11 | 一批解到同一目录只开一个资源管理器（大小写/尾分隔符不同不算两个目录）；`OpenDirectory` 可注入，测试不再弹 explorer | `HomeViewModelTests` |
| F12 | 加密包解压改为一遍盘：候选口令直接解进私有 temp，错口令的半截产物不落入用户目录；清单已确认加密就不试空口令；硬伤（磁盘满/路径过长）不再被说成"密码不对" | `ExtractPasswordChainTests` 15 个（经 `IArchiveEngine` 数清一个作业起了几遍全盘读） |
| F13 | `SlotPool` 取代 20 ms 轮询名额：等待者挂 TCS、并行度调大当场唤醒、不再超发并发 | `SlotPoolTests` 5 个 |
| F14 | 历史写盘节流 + 退出/CLI 收尾 `Flush`；外链密码整批复用（独立锁）；失败气泡 `FailureNotifier` 加最长等待期兜底 | `HistoryServiceTests`、`PasswordServiceTests`、`FailureNotifierTests` |
| F15 | CLI 正确性：`--json` 默认脱敏（`--show-passwords` 还原）、未知命令词/选项一律退出码 2、`-o ""` 显式空值不被吞、命令词可写在已知选项之后 | `CliParserTests` 50 个、`CliRunnerTests` 41 个 |
| F16 | 7z 契约修正：以退出码为准并按官方码兜底分类、`-sccUTF-8` 统一两条流（中文包名不再乱码）、等进程真断开再返回（句柄未放就删会静默失败） | `SevenZipClientTests`、`SevenZipProgressParsing`、`SevenZipStreamTests` |
| F17 | **格式互转** GUI「转格式」+「转为」下拉 / CLI `convert`，原包一律保留，`bz2/gz/xz` 单流限制由程序说清，复合后缀剥净 + 撞名 `-New1` | `ConvertTests` 42 个（`ArchiveFormatTests` 31 + `ConvertWorkerTests` 6 + `ConvertCliTests` 5） |
| F18 | 格式表收敛到 `ArchiveFormat`：`-t` 开关名与文件后缀是两张表（实测 `-tbz2`/`-tgz` 报错、`-tbzip2`/`-tgzip` 正常）；ISO/UDF 只读，认不出的 `--type`/`--name` 不再静默降级成 zip | `ArchiveFormatTests`；实测 `-tiso` 返回"System ERROR: 未实现" |
| F19 | **包对比** `ArchiveDiff`（键归一 `\`→`/`、去尾斜杠、跳过目录条目）+ CLI `diff`（GNU 退出码 0/1）+ 预览窗口「对比另一个包」 | `ArchiveDiffTests` 7 个、`CliRunnerTests` diff 4 个；按钮渲染待真机冒烟 |
| F20 | 右键新增第 6 项「用 UZIP 解压并预览」（旧 5 项注册会被判为过期并提示重注册）；`--preview` 走主页同一 `OpenPreviewWindow`，两套开窗逻辑合并 | `ShellMenuTests`、`ShellArgsTests` |
| F21 | 压缩后校验 `VerifyAfterCompress`（默认开，设置页开关 + CLI `--test/--no-test`） | `CompressVerifyTests` 5 个 |
| F22 | 自更新核对 `<产物>.sha256` 侧车（兼容 sha256sum 与 PowerShell 两种文本风格；无侧车则跳过，不挡更新）；`publish.ps1` 逐产物出侧车 | `SelfUpdaterTests` 30 个、`UpdateServiceTests` 57 个 |
| F23 | 多级解压深度上限实测：三层链每层都 Success；第 9 层（`Depth == 8`）不再派生作业，截断处的包留在原地 | `ArchiveWorkerTests.MultiLevel_chains_three_levels_to_the_innermost_file` / `MultiLevel_stops_at_the_depth_limit`（替换 ④ 里"产生 2 个作业"的弱断言） |
| F24 | 单实例命名管道转发可测：互斥体/管道名可注入，测试不与用户正在运行的实例抢名字；空参数=仅唤起窗口仍会触发事件 | `InstanceBusTests` 4 个 |

| F25 | 真机冒烟抓到并修掉：**`CLI convert` 转换成功却毫无输出**（既不报产物路径也不出 JSON，rc=0）。原因是 `DoConvert` 读绑定用的 `Jobs` 集合，而它只往 UI 调度线程里塞，CLI 正阻塞在那个线程等结果 → 读到的永远是空集合。现 `EnqueueConvert` 直接返回作业对象，CLI 不再绕道 UI 集合 | 测试：`ConvertWorkerTests.EnqueueConvert_reports_its_own_jobs_even_when_the_dispatcher_is_blocked`（注入一个"建好但从不泵消息"的 Dispatcher，精确复现该处境并断言 `Jobs` 为空、返回的作业为 Success）；实测：`convert _smoke/A.zip --type 7z -o out --json` 修前 stdout 全空，修后输出 `A.zip -> out\A.7z` + JSON、rc=0 |

**未覆盖项（如实记录）**：`HotKeyService` 的注册/`WM_HOTKEY` 派发、`TrayService` 气泡与菜单、`PreviewWindow` 的对比按钮、`HomeView` 的「转为/转格式」、`App.ShowPreview` 路由都依赖 WPF 消息泵或真实窗口句柄，单元测试盖不住（见 [[unit-tests-are-dispatcher-blind]]），需按 N7/M1 的老办法驱动真实 exe 冒烟。另：设置与历史跨进程仍是后写者覆盖（合并未做），写回节流下硬崩最多丢约 1.2 s 的记录。
