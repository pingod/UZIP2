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

## v3.8 审计第二轮：更新链路 / CLI 补漏 / 性能 / 进度环

| # | 能力 / 修复 | 证据 |
|---|------|------|
| G1 | **P0：自更新版本闸门按段比较**。`Verify` 原先把四段文件版本（`3.7.0.0`）与 tag 派生的两段期望值（`v3.7`→`3.7`）逐字符比，于是 v3.3 起每次就地更新都被"版本不一致"拒掉。现比前 3 段、忽略尾部 revision，段数不足或非数字按不一致处理 | `SelfUpdaterTests.VersionMatches_compares_segment_wise`（14 组，含线上事故那组 `3.7.0.0`↔`3.7`、`3.7.1`↔`3.7` 仍算不一致）、`Verify_accepts_the_two_part_release_tag_of_the_real_pe`（拿真实发布产物走完整 `Verify`） |
| G2 | **更新链路的代理健壮性**：`HTTPS_PROXY`/`HTTP_PROXY`/`ALL_PROXY` 按协议分域，值写 `direct` 或主机命中 `NO_PROXY` 即明确不走代理；元数据、`.sha256` 侧车、exe 下载三条路共用一套传输，遇传输类失败（`HttpRequestException`/超时/`IOException`/`SocketException`）自动降级直连重试一次。本机实况：GitHub API 走代理 403（限流）、直连 200 | `UpdateTransportTests` 22 个（注入 `Transport` 记录每次尝试的 `useProxy`，断言"成功只调一次""超时→`[true,false]`""本来就直连则不再直连重试""非传输类失败不重试""`HTTP_PROXY=direct` 不影响 https"） |
| G3 | **`--cover` 的参数注入洞**：该值原样拼进 7z 命令行，而 `Config\settings.json` 用户可编辑。CLI 解析层与 `SevenZipClient` 收敛点各校验一次，只放行 7z 实际存在的 `-aoa/-aos/-aou/-aot`，非法值退出码 2 并列出四个模式；非法值经任何路径进来都回落 `-aos` | `CliParserTests` 的 cover theory、`SevenZipCoverModeTests` 11 个（`SanitizeCoverMode`） |
| G4 | **`update --check` 从死参数变为可用**，且与 `--apply` 互斥（只报告 vs 真换身体，同时给退出码 2） | `CliParserTests` 解析用例 + `CliRunnerTests` 2 个（`--check --apply` → rc 2 且不下载） |
| G5 | **一条候选口令都没有时不再做加密探测**（`ShouldProbeEncryption`）：探测结论没有下游用途，纯浪费。64 KB 包实测探测 11.4–16.7 ms vs 解压 11.7–13.1 ms，小包固定开销砍掉近一半；顺带确认跳过之后加密包诊断更准（原空口令那趟把"需要密码"说成"文件已损坏或不是可识别的压缩包"） | `ExtractPasswordChainTests`（`CountingEngine` 统计 `ProbeCalls`；含 `Encrypted_archive_without_passwords_still_says_password_needed`）、`ParallelQueueTests.Unencrypted_Archive_Extracts_Without_Test_Pass` 断言改为"不起 `l` 探测" |
| G6 | **探测读到首条 `Encrypted = ` 就收刀**：以前让 7z 把整包列完、把全部 stdout 读进缓冲（2000 条目实测约 328 KB），现在输出回调里认出该行当场结束进程 | `SevenZipProbeEarlyExitTests` 8 个（`stopWhen` 钩子；含"非加密包仍会正常列完"的反向用例） |
| G7 | **热路径字符串/正则**：7z 进度行解析换成 `[GeneratedRegex]`；`DebugMode` 关闭时不再构造 `Redact(args)` 拼接的命令行字符串 | `SevenZipProgressParsing` 22 个保持全绿；`Redact` 用例仍断言 `-p<口令>` 打码 |
| G8 | **主页计数合并**：`RefreshSummary` 原先每个作业状态一变就整表扫一遍（200 张卡片连着收尾 = 200 次全表扫描），改为 `Coalescer` 单字标志合并成一次排队 | `CoalescerTests` 4 个（含 64 线程并发恰好放行一次）；`HomeViewModelTests`/`QueueManageTests` 对 `FailedCount/FinishedCount/ActiveCount` 的断言不变 |
| G9 | **主页与历史列表虚拟化**：外层 `ScrollViewer` + 裸 `ItemsControl` 的组合天生不虚拟化，改为 `VirtualizingStackPanel` + `Recycling` + 控件自带滚动模板（外面再套 ScrollViewer 会把虚拟化整个废掉） | 无单测（纯 XAML 模板，单测进程没有 WPF 消息泵）；编译 0 警告，滚动表现待真机冒烟 |
| G10 | **悬浮方块进度环**：外圈描边=进度，一圈=100%，12 点起顺时针；多任务画整批平均，收尾补满一圈停 2 秒；纯函数几何 + 200ms 心跳合并重绘 + 只订阅活跃作业 | `PuckProgressTests` 23 个（起点在 12 点、扫掠角、半径、`IsLargeArc`、100% 恰好两段非大弧闭合、0/null 不画、渲染包围盒不出方框；`Overall` 的 null 语义与 Queued 记 0）；真机：隔离实例解 4GB solid 7z，对方块窗口连拍并按像素统计强调色覆盖，读数 `180→180→180→210→246→268→310→350→350→180`（180 = 空闲基线，350 = 满环），中间帧截图可见约 3/4 圈且提示位写 `75%` |
| G11 | **发布件命名与元数据**：`publish.ps1` 的框架依赖主产物改名 `UZIP2.exe`（+ `UZIP2.exe.sha256`），与 `UpdateService.PickAsset` 认的名字一致，发布不再手工改名；verbosity `-v q`→`-v m` 让警告进发布日志；`AssemblyInfo` 的 `HP Inc. / 2021` 换成实际署名与 2021-2026，描述补实，版本 3.8.0.0 | 构建脚本与元数据，无单测；验证走"实发 Release 的资产名 == `UZIP2.exe`/`UZIP2.exe.sha256`"与发布后的 `update --check` 往返 |
| G12 | **`release/` 里的 .NET Framework 残留下线**：`App.config`/`UZIP2.dll.config` 写着 `sku=".NETFramework,Version=v4.8"`，而 CI 把整个 `release/` 当产物上传——一份和 .NET 8 单文件无关的运行时声明跟着发布件走。两个文件删除；`scripts/install.ps1` 本来就"缺了就不拷"、`uninstall.ps1` 本来就"有才删"，无需改动 | 发布物清单核对（CI 产物目录不再出现这两个文件）；脚本容错路径已读码确认 |

| G13 | **方块位置记住副屏**：`RestorePosition` 原先按 `SystemParameters.WorkArea`（只有主屏）夹坐标，重启后把副屏位置拽回主屏右下角（本机实况：存下的 `2624,1421` 被夹成 `2456,1288`）。改为按虚拟桌面夹的纯函数 `PuckPlacement.Resolve`，负值仍表示"从没放过"，越界整块拉回可见范围 | `PuckPlacementTests` 6 个（含"保留右侧副屏位置""越界拉回""跨右边缘时整块保持可见""-1 哨兵落主屏右下角"）；真机：`puckLeft=2700` 重启后方块仍在副屏 |
| G14 | 真机冒烟抓到的一处**观察假象**（记录以免被当成缺陷重复调查）：`EnumWindows` 输出经 grep 时被判定为 binary，中文窗口名整行被吞，看起来像"方块窗口没创建"。实际方块一直在（`GetWindowText` 返回 GBK 字节，grep 需加 `-a`） | 复核：`winrect.ps1 -All` 以文本方式输出后，同一进程列出 `UZIP 拖拽方块\|x=…,y=…,w=104,h=104\|vis=True` |
| G15 | **闪示结束后提示位回不来**：进度环收起（`ClearRing`）时没管图标下方那行小字，于是"完成"永远挂在空闲方块上，直到下一次拖拽才被覆盖。把提示位的取舍抽成纯函数 `PuckProgress.ResolveHint`（预览 > 完成 > 进度 > 空闲），环与文案由同一次判定决定 | `PuckHintTests` 10 个，其中 `Restores_the_idle_prompt_when_the_flash_ends` 就是这个缺陷的回归用例；真机：满环 + "完成"（t=5s）→ 两秒后环收起且文案回到"拖到这里"（t=7s 截图），强调色读数从 350 回到基线 180 |

**本轮刻意不做**：更新检查的 ETag/条件 GET 与 TTL 缓存；watcher 的 `Changed` 去重与启动时先扫现存文件；跨进程历史/设置写回合并；`InstanceBus` 转发的 ack 与日志；历史搜索防抖；重定向 CLI stdout 的 UTF-8 代码页。

## v3.10 Linux 阶段（P1 + P2）

| # | 能力 / 修复 | 证据 |
|---|------|------|
| L1 | **跨平台核心 `UZIP2.Core`**：Models + 可移植 Services + CLI 共 39 个文件迁出 WPF；三个接缝 `IUiDispatcher` / `IPasswordCipher` / `IShellIntegration`；WPF 只留 DPAPI、注册表菜单、托盘、热键、剪贴板、环几何 | 763 个既有测试全绿（Debug/Release）、0 警告；`dotnet build UZIP2.Core -r linux-x64` 成功 |
| L2 | **无界面宿主 `UZIP2.Cli`**：XDG 配置/日志（`~/.config/uzip2`、`~/.local/state/uzip2`），不建窗口、不碰单实例总线 | Arch（WSL）实测：`list`/`extract`/`compress`（含自检）/`diff`/`version --json` 全部正常，配置落在 `~/.config/uzip2` |
| L3 | **Linux 7-Zip 定位**：`7zz/7z/7za` 的发行版路径 + `PATH` 扫描，尾分隔符归一；Windows 候选不变 | `SevenZipLocatorTests` 6 个；实机 7zip 包只提供 `/usr/sbin/7z`，靠 PATH 命中 |
| L4 | **密码存储（无 DPAPI）**：AES-256-GCM，密钥 `secret.key` 0600，密文 `AESGCM1:` 前缀；损坏密文/换密钥/异平台密文一律按"缺失"处理；损坏密钥文件自动重生成 | `FileSecretCipherTests` 10 个（往返、不泄露明文、跨密钥失败、篡改拒绝、0600 创建、损坏自愈） |
| L5 | **临时目录隐藏属性按平台**：Linux 不碰 `FileAttributes.Hidden`（会抛 PlatformNotSupported） | `TempHiddenTests` 3 个 |
| L6 | **自更新平台策略**：`update --apply` 在 Linux 明确拒绝并提示包管理器 | `UpdatePolicyTests` 2 个 |
| L7 | **tar.gz 发布**：x64/arm64 自包含单文件、包内 0755、`.sha256` 侧车、`SHA256SUMS-linux.txt` | `scripts/publish-linux.ps1` 实测产出 28.3 MB / 27.0 MB；解包后 `./UZIP2 --version` → 3.10.0 |
| L8 | **Arch 包**：`PKGBUILD`（源码构建）与 `PKGBUILD-bin`（预编译），`arch=(x86_64 aarch64)`，必须 `options=(!strip !debug)`——strip 会切坏 .NET 单文件 bundle | Arch 真机 `makepkg -f` 产出 `.pkg.tar.zst`，解包后 36 MB 二进制 `uzip2 --version` → 3.10.0，`extract`/`diff` 冒烟通过；首次 strip 版本实测报 `Arithmetic overflow while reading bundle` |
| L9 | **CI**：`.github/workflows/build-linux.yml`（ubuntu 发布双架构 + `archlinux` 容器 makepkg + 安装冒烟） | 尚未触发（首次打 tag 时验证） |

**本阶段刻意不做**：Avalonia 界面（P3）、托盘/悬浮方块/热键/右键菜单等桌面集成（P4）、Linux 上的就地自更新、arm64 包的实机运行（仅有交叉构建）。

## v3.10 Avalonia 壳（P3）

| # | 能力 / 修复 | 证据 |
|---|------|------|
| M1 | **`UZIP2.Avalonia`（net8.0 + Avalonia 11.3）**：左侧导航 + 主页/历史/设置三页，预览独立窗口；服务装配集中在 `AppHost`（配置目录、密码加密、界面线程）；CLI 命令词在 `Program` 里直接转给 `CliHost`，GUI 二进制同时能当 CLI 用 | 无头测试 5 个；`--version` 在 Arch 上输出 `UZIP 3.10.0` |
| M2 | **主页队列**：拖放（`DataFormats.Files`）、添加文件、自动/仅解压/仅压缩三种模式、活动/完成/失败汇总、卡片取消/预览/打开目录/移除、清除已完成、全部取消；复用同一条 `ArchiveWorker.Jobs` | `Home_page_binds_to_the_shared_job_queue`；渲染截图 `artifacts/ui/avalon-shell.png` |
| M3 | **包内预览**：`Worker.PreviewAsync` 读清单，失败时显示诊断而不是空白 | 渲染与构造测试；真机待 P4 |
| M4 | **设置页**：7z 路径显示与自定义、并行度、压缩后自检、预览后解压、完成后打开目录、回收站、监听目录；保存后立即 `Watcher.Apply()` | `Settings_page_reports_the_resolved_7z_path_and_config_dir` |
| M5 | **历史页**：读取 `HistoryService`，按路径/状态过滤，清空 | `History_page_lists_recorded_jobs` |
| M6 | **无头 UI 测试**：`Avalonia.Headless` + Skia（`UseHeadlessDrawing = false`），真实渲染一帧存 PNG，Linux/Windows 都能跑，CI 已接入 | `UZIP2.Avalonia.Tests` 5 个全绿；`Main_window_renders_a_frame` 产出截图 |

**本阶段刻意不做 / 未验证**：托盘、悬浮方块、全局热键、文件管理器右键菜单（P4）；Linux 发布包尚未切换到 Avalonia 二进制（P5）；**没有在真实 Wayland/X11 会话里开过窗口**——渲染验证走的是无头 Skia 截图，真窗口冒烟留到 P4 有桌面集成时一起做。

## v3.10 桌面集成（P4）

| # | 能力 / 修复 | 证据 |
|---|------|------|
| N1 | **能力探测 `DesktopCapabilities`**：按会话（X11/Wayland）与桌面（KDE/GNOME/wlroots 系）推托盘、全局热键、方块定位三项能力；未设 `XDG_SESSION_TYPE` 时按桌面名推断。设置页直接把"为什么不可用"讲出来 | `DesktopCapabilitiesTests` 7 个（KDE Wayland、GNOME Wayland、sway、X11、空会话、Hyprland 兜底、Summary 文案） |
| N2 | **Linux 右键菜单 `LinuxShellIntegration`**：Nautilus 脚本（解压到当前文件夹 / 压缩）+ Dolphin service menu（KDE5 与 KDE6 各一份）；文件带标记行，移除时只删自己写的；`State` 能把"指向别的版本"识别为 Stale | `LinuxShellIntegrationTests` 7 个（临时 HOME：写入内容与引号、状态三态、探针、外来脚本不误删） |
| N3 | **托盘**：Avalonia `TrayIcon` + 原生菜单（显示 / 桌面只留方块 / 退出）；`TraySupported` 为 false 时静默跳过并记日志，绝不阻塞启动 | 构建通过 + 设置页能力文案；真机托盘宿主未验证 |
| N4 | **悬浮方块**：无边框置顶窗口、拖放入队、200ms 心跳重画进度环（核心 `PuckProgress` + Avalonia 版 `PuckRingGeometry`）、收尾满环停 2 秒、位置写回设置并用共享 `PuckPlacement` 夹进可见范围 | 无头渲染截图 `artifacts/ui/avalon-puck.png`（轨道环 + 拖到这里）；`Ring_geometry_is_empty_at_zero_and_closes_at_full` 验证半圈/满圈包围盒 |
| N5 | **设置页集成**：显示能力摘要与右键菜单状态，提供"安装/移除右键菜单" | 构造与文本断言（`Settings_page_reports_the_resolved_7z_path_and_config_dir`） |

**P4 未完成 / 需真机**：全局热键只做了能力判定，没有真正注册（X11 需要 `XGrabKey` 绑定，Wayland 要走 `GlobalShortcuts` portal）；托盘、方块定位、锁屏级置顶都还没在真实的 Wayland/X11 会话里跑过；Thunar 的自定义动作（`uca.xml` 合并）没做。

## v3.10 发版（P5）

| # | 内容 | 证据 |
|---|------|------|
| R1 | Linux 发布物切换到 **Avalonia 二进制**（GUI + CLI 同一文件），`publish-linux.ps1` 增加 `IncludeNativeLibrariesForSelfExtract` 并把产物统一命名为 `UZIP2` | tar.gz 37.3 MB / 33.9 MB；Arch 上解包后 `--version` → 3.10.0、`extract` 正常 |
| R2 | Arch 包依赖补齐 `fontconfig freetype2 libice libsm`（Avalonia 运行时需要），源码包改为构建 `UZIP2.Avalonia` | Arch（WSL）真机 `makepkg` 产出 `.pkg.tar.zst`，payload 86.7 MB、`uzip2 --version` → 3.10.0；缺依赖时 makepkg 会明确报 "Could not resolve all dependencies"（已据此补全） |
| R3 | 文档：README 的 Linux 章节改写为"图形界面 + 命令行、同一个二进制"，说明 Wayland 能力降级与全局热键尚未注册 | 本节 + README |

## v3.11 后续（Linux 桌面体验收尾）

| # | 内容 | 证据 |
|---|------|------|
| O1 | **单实例 + 文件参数**：Linux 图形版接上 `InstanceBus`，第二个进程只转发路径；启动参数（文件管理器"打开方式"）在服务就绪后按自动模式入队；拖放/参数/转发共用 `FileRouter` | 新增跨平台测试 `Single_instance_bus_forwards_files_on_this_platform`（Linux CI 真跑命名管道转发） |
| O2 | **桌面入口**：`packaging/linux/uzip2.desktop` + 48/128 px 图标，随 tar.gz 与 Arch 包安装（旧 tag 缺文件时跳过，不阻断构建） | makepkg 安装路径检查；tarball 内含 desktop/icon |

| O3 | **密码页（Avalonia）**：密码本增删、逐行显示/隐藏、复制、成功次数；密码纸整段粘贴、清空、上限提示 | `Password_page_masks_the_book_and_reveals_on_demand`；渲染截图 `artifacts/ui/avalon-password.png` |
| O4 | **包内预览勾选**：清单逐项勾选 + 全选/清空 + 解压选中（走 `onlyEntries`），解压全部照旧 | 复用既有 onlyEntries 路径；UI 接线 |

| O5 | **主页卡片新增三个入口**：转格式（7 种可创建格式，原包保留）、校验和（SHA-256 + 旁挂校验文件逐条比对）、包对比（选另一个包，清单差异） | `Convert_dialog_offers_every_creatable_formats`；三者复用既有 `EnqueueConvert` / `ChecksumService.VerifyAsync` / `ArchiveDiff` |
| O6 | **X11 全局热键**：不抢键，150ms 轮询键位图与修饰键掩码，边沿触发（Ctrl+Alt+U 唤起窗口）；无 DISPLAY / 无 libX11 时安静降级 | `HotkeyMatcherTests` 8 个（边沿、修饰键精确匹配、别的键不触发）；`X11_hotkey_watcher_degrades_without_a_display` |

| O7 | **Thunar 右键菜单**：动作合并进用户已有的 `~/.config/Thunar/uca.xml`（不覆盖别人的动作），首次修改前留 `.uzip2.bak`，重复注册幂等，卸载只移除 `uzip2-extract-here`/`uzip2-compress` 两条 | `LinuxShellIntegrationTests` 新增 4 个（保留用户动作、幂等、卸载还原、指向别的版本时 Stale） |
| O8 | **Wayland 全局热键（GlobalShortcuts portal）**：经 `gdbus` 建会话、绑定 `toggle`（Ctrl+Alt+U）、监听 `Activated` 信号；仅 Wayland + 有 portal 的桌面（KDE）启用，缺 gdbus/portal 时返回 null | `PortalOutputTests` 4 个（会话路径解析、激活行识别、别的信号不误判）；无 portal 时安静降级（测试） |
| O9 | **X11 实机验证**：WSL + Xvfb 真 X 服务器上启动，`xwininfo` 看到 `"UZIP2" 1000x700` 窗口，日志记录热键注册成功，`xdotool` 按住 Ctrl+Alt+U 后记录"全局热键触发"；轮询从 150ms 收紧到 25ms 以接住 100ms 级按键 | 见 commit；日志两条：`全局热键已启用（X11）: u` / `全局热键触发，唤起窗口` |
| O10 | **tar.gz 安装脚本**：`install.sh` / `uninstall.sh`（`PREFIX` 可调，装二进制 + 桌面入口 + 图标，刷新 desktop/icon 缓存；卸载保留配置） | WSL 实测：`PREFIX=/tmp/prefix ./install.sh` 后二进制 `--version` 正常，`uninstall.sh` 只删自己的 4 个文件 |
| O11 | **能力文案按桌面区分**：非 GNOME 的 Wayland 不再说"GNOME 限制"，改为"当前桌面不提供该接口"；启动时把能力摘要写进日志 | WSLg（Weston）实跑日志：`会话 Wayland / none；托盘没有可用的 SNI 宿主；…` |

## v3.12 后续（Linux 桌面实机验证与 Avalonia 功能对齐）

基线与验证方式：**无头测试** = `dotnet test UZIP2.Avalonia.Tests`（21 条，Release，0 警告 0 错误）；
**实机** = Hyprland 0.56.2（Wayland + XWayland `:0`，双显示器 DP-2/DP-3）真会话；
**CLI** = `artifacts/3.12.0/UZIP2-3.12.0-linux-x64.tar.gz` 解包后的 `UZIP2` 二进制。
GUI 部分刻意不在用户真实会话里开窗（AGENTS.md 铁律），改用无头渲染测试覆盖。

### 功能对齐（Avalonia ← WPF）

| # | 内容 | 证据 |
|---|------|------|
| A1 | **设置页补齐 10 项**：主题、热键组合选择（改完免重启）、解压输出模式、覆盖策略、自定义文件夹、启动清理临时目录、窗口置顶、任务排序、记录口令、文件名转密码；另补 7 项 Core 已在消费但 UI 没暴露的（新建文件夹/按名建目录/压完删源/解压过滤/口令去空格/调试模式/迷你方块） | 无头测试；`SettingsView.axaml` 72→215 行、`.cs` 103→569 行 |
| A2 | **主题跟随设置**：删掉 `App.axaml` 写死的 `RequestedThemeVariant="Dark"`，改由 App 在建窗口前 `ThemeApplier.Apply(settings.Theme)`，避免第一帧跳变 | 构建 0 警告；`ThemeApplier.Normalize` 把 WPF 的 HighContrast 归为「跟随系统」 |
| A3 | **热键免重启生效**：`MainWindow.ReloadHotkey()`（`StopHotkey` + `StartHotkey`），`App.ApplyWindowSettingsOnUi` 用五元组 `_appliedHotkey` 判变化才重注册 | 实机日志 `全局热键已启用（X11）: Ctrl+Alt+F5 → keysym F5`（旧代码只映射 A-Z/0-9，会退化成 `u`） |
| A4 | **任务排序真正作用到主页**：`HomeView` 不再直接绑 `Worker.Jobs`，改为按 `JobOrder` 维护 `_sorted` 镜像（对齐 WPF `HomeViewModel` 的 `RebuildMirror`/`RepositionJob` 语义），`RebuildSorted` + `InsertIndex` + `Move` | `Home_list_follows_the_configured_job_sort`（四种模式的 Compare 与 InsertIndex） |
| A5 | **历史页「重跑」**：`Kind != "Test"` 才可重跑（对齐 `HistoryViewModel.cs:118 CanRetry`），压缩作业走 `EnqueueCompress`、其余 `EnqueueExtract` | `History_page_skips_retry_for_verification_entries`（直测 `HistoryKindRetryableConverter`） |
| A6 | **卡片「重试」「删除源文件」**：重试带 `job.UsedPassword`（失败时为 null → 重走密码链）；删除源文件时 `recycle = OperatingSystem.IsWindows() && DeleteToRecycle`（`FilterService.Delete` 走 `Microsoft.VisualBasic.FileIO.FileSystem`，Linux 必抛 `PlatformNotSupportedException`，故降级直删并换提示语） | `Home_card_shows_retry_and_delete_source_for_terminal_jobs` |
| A7 | **卡片 9 个按钮逐个对齐 WPF**（`UZIP2/Shell/Pages/HomeView.xaml:140-171`）：取消=Queued/Running、预览=Extract、校验与转格式=Success, Failed、打开目录=Success、移除=IsTerminal；「对比」是 Avalonia 独有（WPF 无此控件）保持 IsTerminal | `JobStatusVisibilityConverter` / `JobKindVisibilityConverter`（Avalonia 11 无 DataTrigger，自建 IValueConverter） |
| A8 | **「清除失败项」**：`Worker.RemoveTerminal(j => j.Status == Failed)` | `Home_page_exposes_the_failed_clear_button` |
| A9 | **密码页剪贴板粘贴**：`Clean(raw, trimSpace)` 逐字符对齐 WPF `ClipboardService.cs:25` 的 `Regex.Replace(raw, "[\t\r]", "")`；Avalonia 11.3.2 的 `IClipboard` 没有 `ContainsText`，只能 `await GetTextAsync()` 把 null 当空 | `Clipboard_paste_cleans_control_characters_like_wpf`、`Password_page_has_a_clipboard_paste_entry` |
| A10 | **托盘失败气泡**：`notify-send` → `gdbus org.freedesktop.Notifications.Notify` → 只写日志，三级降级；`FailureNotifier(900, 5000)` 节流；窗口在前台不弹；完整报告写 `failure-report-yyyyMMdd-HHmmss.txt`（UTF-8 BOM，刻意不含密码） | 实机 dbus-monitor：隐藏 + 3 个坏包 → 1 条通知（标题「UZIP 有 3 个任务失败」）；前台 → 0 条；最小化 → 1 条；连发 3 次仍 1 条 |

### 真实桌面实机验证

| # | 内容 | 证据 |
|---|------|------|
| B1 | **托盘图标与菜单（E2）** | `busctl --user list` 出现 `org.kde.StatusNotifierItem-<pid>-0`，pid 与 `_smoke/live/app.pid` 一致；`Properties.GetAll` 返回 `Category=ApplicationStatus`、`Id=UZIP2`、`IconPixmap=[(128,128,16384 bytes)]`，把像素解出来（`magick … rgba:`）就是 UZIP 图标；dbusmenu `GetLayout` = 「显示 UZIP / 桌面只留方块(checkmark) / 分隔符 / 退出」。宿主是 quickshell（`qs -p /run/user/1000/danklinux-shell/…`），不是 waybar |
| B2 | **托盘注册全链路** | dbus-monitor 抓到 `RegisterStatusNotifierItem` → `StatusNotifierItemRegistered` → `NewTitle/NewIcon/NewToolTip/NewStatus` → host `GetAll` |
| B3 | **方块位置恢复（E3 真 bug）** | 窗口未映射时设 `Position` 会被合成器丢弃（实测设 800,600 读回 0,0，最终落 2240,600）。改成 50ms × 最多 20 次的追踪式重设（`_target` + `_restoring` 抑制回写），实机 4 组坐标（800,600 / 1200,300 / 100,1400 / 2000,800）全部精确恢复且 settings 未被污染。回归测试 `Puck_restores_the_saved_position_instead_of_the_compositor_default` |
| B4 | **置顶即时生效** | `Save(WindowOnTop=false)` → `Topmost=False` 立刻体现；但 **Hyprland 不实现 EWMH**（`xprop -root _NET_SUPPORTED` 无 `_NET_WM_STATE_ABOVE`，`hyprctl -j clients` 恒 `pinned:false`），所以「重启后仍在最上层」在此桌面无法验收 |
| B5 | **桌面能力判定与实际一致** | 真机日志：`桌面能力: 会话 Wayland / Hyprland；托盘可用；全局热键不可用（当前 Wayland 桌面没有 GlobalShortcuts portal）；悬浮方块可定位。` |
| B6 | **CLI 日常操作（E5）** | `extract plain.7z -o out` 解出 2 个文件；加密包无口令 → `密码错误或加密头损坏`（不挂起）；`--password Secret123` 解出 2 个文件；`list` → 3 项。注意命令行是 `--password` 不是 `-p`，也没有 `--nologo` |

### 两处实机才暴露的 bug

| # | 内容 | 证据 |
|---|------|------|
| D1 | **拖动方块导致设置被反复写盘 + 日志刷屏**：`PuckWindow.SavePosition` 每次 `PositionChanged` 都 `Settings.Save`，而 `SettingsService.Save` 无条件写盘并全量 `Changed?.Invoke`（无差异比较）；合成器重新摆放窗口会反复抛同坐标，实机 7 秒刷 430 行「设置已变更，窗口置顶 = 开」。修法：坐标没变就不 Save；`ApplyWindowSettingsOnUi` 只在置顶值真变时打日志（文案改「窗口置顶 = X」） | **变异验证**：把去重条件改成 `if (false) return;`，`Puck_window_does_not_rewrite_settings_when_the_position_is_unchanged` 立刻变红（mtime 断言失败）；恢复后 21/21 绿 |
| D2 | **`scripts/aur-sums.sh` 下载截断时误报 hash 不一致**：GitHub 对 35 MB Release 包会中途断流而 `curl -f` 仍以 0 退出，留下残缺文件；脚本对残缺文件算 sha256，得出「PKGBUILD 值与实测不一致」。新增 `fetch_verified()` 用 `<url>.sha256` 侧车交叉验证，区分「缓存坏了」与「hash 填错了」，缓存坏了删了重下 | 实测：缓存里 5,066,752 字节（tar 已损坏）时输出「缓存与官方侧车不符…重下一次」，而不再报 PKGBUILD 错 |

### 工程与发布

| # | 内容 | 证据 |
|---|------|------|
| P1 | **AUR 准备完成**：`.SRCINFO` / `.SRCINFO-bin` 已生成；`PKGBUILD-bin` 的架构专属数组是真值（`sha256sums_x86_64=fcf1f84b…` / `sha256sums_aarch64=71e9706e…`）；源码 `PKGBUILD` 的 `sha256sums=('SKIP')` 是**数学必然**（该文件就在它下载的 tag 归档里，填真值会改变归档，见 P10）；新增 `scripts/aur-sums.sh`（check/`--print`/`--fill`/`--srcinfo`/`--offline`）一条命令完成下载→计算→比对→回填→生成 .SRCINFO | `makepkg --printsrcinfo` 两个 PKGBUILD 都通过；源码包与 bin 包均本机真构建成功。**真提交做不了**：无 SSH key（`Host key verification failed`）、无 sudo。但 v3.13 Release 已发布、九个产物已上传，提交 AUR 所需的文件全部就绪 |
| P2 | **deb / rpm / AppImage**：新增 `scripts/package-linux.sh` + `packaging/{deb,rpm,appimage}/`。三种格式共用一份「已装好布局」的暂存树（`usr/bin/uzip2`、desktop、hicolor 48/128 图标、LICENSE、copyright），保证逐字节一致；全程不 strip（`%global __strip /bin/true` 等），CI 里用 `cmp` 对包内二进制与 tarball 逐字节比对兜底 | **deb 真打包成功 36 MB**（`ar rcD` 回退路径，本机无 dpkg-deb），拆包核对 `Depends`/`Architecture`/`Version`/`Installed-Size: 84864`；rpm/AppImage 本机缺 rpmbuild/appimagetool，明示跳过并退出码 0 不谎报产出 |
| P3 | **arm64**：两个 PKGBUILD 的 `arch` 与 `build()` 支持 aarch64（`case "$CARCH" in aarch64) _rid=linux-arm64`）；`package-linux.sh --arch arm64` 路径可跑；CI 按规格 arm64 只产 tar.gz | 代码路径本地实跑；真 arm64 运行待设备 |
| P4 | **Windows CI 跑 Avalonia 测试**：`build-windows.yml` 加 `Test the Avalonia shell (headless, Windows)`，用项目级 `.csproj`（不是 .sln）所以不会把 WPF 拉进 restore 图，也不需要 `EnableWindowsTargeting` | 本地等价命令通过 |
| P5 | **版本号单一来源**：新增 `scripts/version.ps1`（四行来源声明 + `Get-UzipVersion` 以 Avalonia csproj 为权威、归一化到三段比对、漂移即 throw 并列出四行）；两个 publish 脚本都改成 `. version.ps1; Get-UzipVersion` | `_tools/pwsh/pwsh -NoProfile -Command '. ./scripts/version.ps1; Get-UzipVersion'` → `VERSION=3.12.0`，四行全读出；真跑 `publish-linux.ps1 -Rids linux-x64` 产出 35.6 MB tar.gz，解包 `--version` → `UZIP 3.12.0` |
| P6 | **README 精简** | `README.md` 339→138 行；v3.8 及更早整段移到新增 `docs/history.md`（212 行） |

## v3.13.0（原生 Wayland 后端 + Arch 分发）

基线：**Avalonia 12.1.4** / **.NET 10.0.100 SDK**；无头测试 21 条（xunit v3，`dotnet <tests>.dll`）；
Wayland 验证走 `cage` + `WLR_BACKENDS=headless WLR_RENDERER=pixman` 沙箱，**不开用户真实会话窗口**。

### 图形后端

| # | 内容 | 证据 |
|---|------|------|
| W1 | **根因确认**：`Avalonia.Wayland` NuGet 包只从 **12.1.0** 起发布；`Avalonia.Desktop` 11.3.2/12.1.4 的 `UsePlatformDetect()` 都完全不探测 Wayland（`strings Avalonia.Desktop.dll` 里 grep wayland 零命中，只有 `LoadX11` / `LoadAvaloniaNative`） | `_tools/nuget` 下对两个版本的 nupkg 逐版本 `ls` + `strings` |
| W2 | **接入实现**：`Program.BuildAvaloniaApp()` 在 `OperatingSystem.IsLinux()` 时叠 `UseWaylandWithFallback()`，必须排在 `UsePlatformDetect()` 之后（否则抛 `A fallback windowing backend must be configured before calling UseWaylandWithFallback`）；csproj 增引 `Avalonia.Wayland` / `Avalonia.Skia` / `Avalonia.HarfBuzz` 全 12.1.4 | 纯 Wayland 沙箱（`env -u DISPLAY`）启动无异常 |
| W3 | **协议层实证**：`WAYLAND_DEBUG=1` 统计往返 —— `wl_shm` 80、`wl_compositor` 29、`wl_seat` 9、`xdg_wm_base` 7 | 无任何 X11 协议往返 |
| W4 | **进程层实证**：`ls /proc/<pid>/fd` 无 X11 socket；`/proc/net/unix` 里只有 `/tmp/CoreFxPipe_UZIP2_FileArgs_Pipe`（单实例管道）与 dotnet 诊断 socket | 排除隐式 XWayland |
| W5 | **渲染实证**：headless 沙箱里截图 1280×720，左侧导航/拖放区/模式三选/空队列提示完整渲染；dev build、tar.gz、Arch 包解包产物三份截图 md5 全等 = `0fa4ad02621a3772e9d9c2d27f4ac762` | `grim` 截图 + `md5sum` |
| W6 | **Avalonia 12 API 迁移**：拖放 `e.DataTransfer.TryGetFiles()` + `DataFormat.File`（旧 `e.Data.GetFiles()` / `DataFormats.Files` 已移除）；剪贴板 `ClipboardExtensions.TryGetTextAsync` / `SetTextAsync`（需 `using Avalonia.Input.Platform`）；`RadioButton.Checked` → `IsCheckedChanged`；`NativeMenuItemToggleType` → `MenuItemToggleType`；`TextBox.Watermark` → `PlaceholderText`（9 处）；`Window.SystemDecorations` → `WindowDecorations`；`Bitmap.Save(path,int?)` → `Save(path, PngBitmapEncoderOptions.Default)` | `-t:Rebuild` 0 警告 0 错误（**增量构建会缓存「0 错误」假象，必须 Rebuild**） |
| W7 | **测试框架迁 xunit v3**：`Avalonia.Headless.XUnit` 12.x 只给 v3 适配器，与 xunit 2.5.3 并存报 `CS0433 FactAttribute 同时存在于 xunit.core 与 xunit.v3.core`；项目改 `OutputType=Exe` + `xunit.v3 3.2.2` + `xunit.runner.visualstudio 3.1.5` | `dotnet UZIP2.Avalonia.Tests/bin/Release/net8.0/UZIP2.Avalonia.Tests.dll` → `Total: 21, Errors: 0, Failed: 0` |
| W8 | **工具链上 .NET 10**：Avalonia 12 的 XAML 源生成器引用 Roslyn 4.14，SDK 8 只带 4.11 → `CS0103: 不存在名称 InitializeComponent`（5 个 XAML 文件全中）。装 10.0.100 SDK，CI 的 `dotnet-version` 与 PKGBUILD 的 `makedepends=('dotnet-sdk>=10.0')` 同步 | 全解决方案编译通过；WPF 侧 `-p:EnableWindowsTargeting=true` 在 SDK 10 下仍 0 警告 |
| W9 | **SNI 上游 bug 已由 Avalonia 12 修好**：3.12 时 `Status` 返回 `"UZIP2"`（把 `Id` 写进只允许 `Passive`/`Active`/`NeedsAttention` 的槽位）；升级后在真实 Hyprland 会话里按 PID 定位本进程的 SNI，`Id='UZIP2'`、`Status='Active'` | `gdbus … Properties.Get org.kde.StatusNotifierItem Status` → `(<'Active'>,)` |
| W10 | **无 SNI 宿主时降级正常**：cage 沙箱里没有 `StatusNotifierWatcher`，程序照常启动、窗口正常渲染、日志仅一条桌面能力，不崩不卡 | 沙箱运行 12 秒 `pid alive: YES`，日志无 `Unhandled`/`Exception` |

### Arch 分发

| # | 内容 | 证据 |
|---|------|------|
| P7 | **本机真构建 Arch 包**：`makepkg -f -p PKGBUILD-bin` 走完整 fakeroot 流程 → `uzip2-bin-3.13.0-1-x86_64.pkg.tar.zst`（35,670,578 B） | `bsdtar tf` 列出 `.PKGINFO`/`.MTREE`/`usr/bin/uzip2`/desktop/48-128 图标；解包 `./usr/bin/uzip2 --version` → `UZIP 3.13.0` |
| P8 | **架构专属校验**：`PKGBUILD-bin` 用 `source_x86_64` / `sha256sums_x86_64` / `source_aarch64` / `sha256sums_aarch64`，两架构各校验各自的包（此前是共用 `sha256sums=('SKIP')`） | `CARCH=x86_64 makepkg --printsrcinfo` 与 `CARCH=aarch64 makepkg --printsrcinfo` 各跑一次，两边数组都正确输出 |
| P9 | **版本与 SRCINFO**：两 PKGBUILD `pkgver=3.13.0` / `_tag=v3.13`；`.SRCINFO`（584 B）与 `.SRCINFO-bin`（791 B）重新生成 | `makepkg --printsrcinfo > .SRCINFO` |
| P10 | **源码包 `sha256sums` 必须是 SKIP（数学上的固定点）**：`packaging/arch/PKGBUILD` 本身就在它下载的 `archive/v3.13.tar.gz` 里，往 `sha256sums` 填真值会改变 `PKGBUILD` → 改变归档 → hash 又变，永远填不对。历史上填过 `0a792aea10371bb170063be2524f89fd4f1ac0b5773b987d656ae150de01703b`（对着旧 tag 算的），tag 重指到该提交之后 CI 立刻报 `==> ERROR: One or more files did not pass the validity check!`。现改为 `(SKIP)` 并在文件里写清理由；`PKGBUILD-bin` 不受限（下载 Release 二进制、不在归档内），其两个架构的 hash 仍是真值 | 新归档实测 `14451fc4c959ef5938e64e2cc651b604dcdc28e95403e9d0e3390e7c99a68733`（3,749,801 B）≠ 文件里原先的值，直接印证固定点；本地 `makepkg -f --nodeps` 用 SKIP 完整构建成功（`uzip2-3.13.0-1-x86_64.pkg.tar.zst` 35,683,281 B，解包 `--version` → `UZIP 3.13.0`）；CI `Validate both PKGBUILDs` 改成分开断言：源码包必须是 SKIP 且文件含「固定点」说明，bin 包两个架构必须是 64 位真值 |
| P10b | **源码包真构建**：`makepkg -f -p PKGBUILD --nodeps`（本机无 `dotnet-sdk` 包，SDK 装在 `_tools/`，PATH 指过去即可）→ `uzip2-3.13.0-1-x86_64.pkg.tar.zst`（35,683,348 B），比 bin 包多 `usr/share/licenses/uzip2/LICENSE` | 下载源码归档（`sha256sum -c` 通过）→ `dotnet publish` → fakeroot `package()` → 压缩全通；解包 `./usr/bin/uzip2 --version` → `UZIP 3.13.0` |
| P11 | **Arch 包实跑**：解包产物在纯 Wayland 沙箱里启动并截图 | 截图 md5 与 dev build / tar.gz 完全一致 |

### 发行版包（deb / rpm / AppImage）

| # | 内容 | 证据 |
|---|------|------|
| P12 | **deb 本机真构建**：`scripts/package-linux.sh --arch x64 --formats deb` → `uzip2_3.13.0_amd64.deb`（38,848,292 B）。本机没有 `dpkg-deb`，脚本自动走 `ar + tar` 回退路径 | `file` → `Debian binary package (format 2.0), with control.tar.zst, data compression zst`；随附 `.sha256` 与汇总清单 |
| P13 | **AppImage 本机真构建**：`--formats appimage` → `UZIP2-3.13.0-linux-x64.AppImage`（36,563,448 B）；`--appimage-extract` 后 `./squashfs-root/usr/bin/uzip2 --version` → `UZIP 3.13.0` | 纯 Wayland 沙箱 `--appimage-extract-and-run` 渲染截图 md5 = `0fa4ad02621a3772e9d9c2d27f4ac762`，与 dev build / tar.gz / Arch 包完全一致 |
| P14 | **AppImage 三个真坑**：① `.desktop` 的 `Categories=Utility;Archiver;` 里 `Archiver` 不是注册值（appimagetool 报 `contains an unregistered value "Archiver"`）→ 改 `Archiving`；② AppStream `<id>` 必须是反域名（`appstreamcli` 报 `cid-desktopapp-is-not-rdns`，新版 appimagetool 把警告当失败）→ `io.github.pingod.UZIP2`；③ 元数据文件名必须与 id 一致（否则 `metainfo-filename-cid-mismatch`）→ 文件改名。**XML 注释里不能出现连续两个连字符**，原注释写了 `--no-appstream` 导致 `parser error : Comment must not contain '--'` | `appstreamcli validate --no-net` → `✔ 验证成功`；`desktop-file-validate packaging/linux/uzip2.desktop` 无输出 |
| P15 | **CI runner 的 `tail` 换成 uutils 版**：Ubuntu 26.04 上 `tail -40` 报 `error: unexpected argument '-4' found`，这行错误把 appimagetool 的真实失败完全盖住（现象是日志里只有 tail 的用法说明）。改成 `tail -n 40` | `scripts/package-linux.sh` 两处（rpm.log / appimage.log）；CI run 40049383452 的 `linux-packages` 通过 |
| P16 | **rpm 由 CI 真构建 + 实跑**：`linux-packages` job 装 `rpm`/`cpio`，`scripts/package-linux.sh --formats deb,rpm,appimage` → `uzip2-3.13.0-1.x86_64.rpm`（31,745,020 B）；CI 用 `rpm2cpio` 拆包校验文件清单并跑 `--version` → `UZIP 3.13.0` | CI run 40049383452 日志：`生成 uzip2-3.13.0-1.x86_64.rpm（31M）`、`/usr/bin/uzip2` / `copyright` / `LICENSE` / 两档图标齐全、`UZIP 3.13.0`、`/tmp/rpmx/p.zip -> /tmp/rpmx/out/`（真解压一个包） |
| P17 | **`appimagetool` 联网下载 runtime**：最后一步从 `AppImage/type2-runtime` Releases 拉 `runtime-x86_64`，网络受限时报 `Failed to download runtime`。脚本加 `--runtime-file PATH` / `APPIMAGE_RUNTIME_FILE` / 同级目录自动发现 `runtime-<arch>`，失败日志里额外提示。**从 appimagetool 自身切出来的 runtime 不可用**（产物 `--appimage-extract` 直接 core dump，`REAL_EXIT=139`），只能用可信来源 | `--runtime-file` 传入手工切的 runtime → 产物段错误；不传该参数（让 appimagetool 自己下）→ 产物正常，截图 md5 一致 |
| P18 | **CI 全绿**：`linux-binary` / `arch-package` / `linux-packages` 三个 job 与 Windows 侧 `build-windows` 全部 success（run 40049383452 / 40049470683） | `gh run view --json jobs` 逐 job 结论 |
| P19 | **Release 资产齐备**：v3.13 共 15 个资产（9 个产物 + 5 个 `.sha256` 侧车 + 汇总清单 `SHA256SUMS-linux-packages.txt`），其中 rpm 用的是 CI 构建的那份（本机无 `rpmbuild`） | `gh release view v3.13 --json assets` 列出全部名称与大小；本地 `sha256sum -c SHA256SUMS-linux-packages.txt` 三项全通过 |
