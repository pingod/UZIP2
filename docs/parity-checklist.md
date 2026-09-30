# 行为对等清单（旧版 UZIP 2.22 → 新版 3.0）

每项标注验证方式：**测试** = `UZIP2.Tests/ParityTests.cs` 走真实 7z.exe 端到端；**实测** = 启动 `bin/Release/net8.0-windows/UZIP2.exe` 现场观察。
基线：`dotnet test` 129/129 通过（2026-09-30）。

| # | 行为 | 结果 | 证据 |
|---|------|------|------|
| ① | 命令行 / 右键"发送到"直接入队解压：`UZIP2.exe a.zip b.zip` | ✅ | 实测：`UZIP2.exe D:\tmp-live15\a.zip` 启动后无人工干预，`one.bin` 解到压缩包所在目录（`extractOutMode=1`）；日志见 `7z t` → `7z x -o...\UZipTemp_a -aos` |
| ② | 第二实例把参数转发给主实例后退出 | ✅ | 实测：主实例运行中再启 `UZIP2.exe b.zip`，第二进程 2.3s 内 exit 0 且无新窗口；主实例解出 `two.bin`。链路 = `InstanceBus`（Local 互斥体 + 命名管道 `UZIP2_FileArgs_Pipe`） |
| ③ | 密码试错链：无密码 → 人工指定 → 外部 → 文件名 → 密码本(按成功次数) → 密码纸；全部失败给出诊断 | ✅ | 测试：`WrongPassword_all_sources_tried_then_diagnosis`（诊断文案 `需要密码，但密码本/密码纸中未找到正确密码`，且密码纸未被消耗）、`Paper_password_hit_consumes_entry`（命中即消耗） |
| ④ | 多级解压（包中包递归，深度上限 8） | ✅ | 测试：`Nested_archives_extracted_recursively`，outer.zip→inner.zip→leaf.bin 产生 2 个作业且全成功 |
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
| N7 | Windows 右键菜单注册/移除（HKCU，免管理员），设置页显示当前指向 | 测试：`ShellMenuTests`（写入/移除/状态判定/指向检测）。资源管理器里的实际菜单项待发布部署时确认 |
| N8 | 密码库跨机加密导出/导入（口令派生密钥；导出件不含明文，也不含口令） | 测试：`VaultTransferTests` |
| N9 | 桌面迷你拖拽方块：只留一个小窗，文件拖上去即处理（`MiniPuck`，默认关；位置记忆） | 测试：`HomeViewModelTests` 的模式路由与预告文案复用；方块窗口本身待人工拖放确认 |
| N10 | 分卷压缩 `-v`（`700m` / `1g` / 纯字节；非法值在启动 7z 前挡下） | 测试：`VolumeSizeTests` + `ArchiveWorkerTests`；产出 `name.7z.001/002…` |
| N11 | 高级压缩参数：固实 `-ms`、线程 `-mmt`、文件名加密（并说明只对 7z 生效） | 测试：`CompressOptionsTests` |
| N12 | 按目录压缩预设：最深目录命中、逐字段回落全局、预设刻意不含密码 | 测试：`CompressPresetTests`（含真实 7z 分卷产物、非法分卷值在启动 7z 前挡下、设置往返） |
| N13 | 校验面板：SHA-256 + `.sfv` / `.md5` / `.sha256` 旁挂逐条核对（含分卷缺失、未收录） | 测试：`ChecksumServiceTests`；实测：离屏渲染快照核对四种状态色与图标 |
| N14 | 检查更新：启动至多一天问一次 GitHub Releases，可关可手动，失败静默；代理优先读 `HTTP(S)_PROXY` | 测试：`UpdateServiceTests`；实测：带代理环境变量启动后 `settings.json` 写入 `latestSeenVersion` |
| N15 | 密码本明文按密文缓存，且只在真正改动时写 `passwords.json` | 测试：`PasswordServiceTests`（`Changed` 次数即落盘次数） |
