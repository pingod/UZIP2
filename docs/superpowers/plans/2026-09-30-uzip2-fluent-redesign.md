# UZIP2 Fluent 重构 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 UZIP2 迁移到 .NET 8 WPF + WPF-UI(Fluent) + MVVM，全功能保留、旧配置自动迁移、界面与交互重做。

**Architecture:** 单 csproj 分四层：Models(纯数据) / Services(构造注入、可单测) / ViewModels(CommunityToolkit.Mvvm) / Views(FluentWindow+NavigationView 三页壳)。旧代码(`UZIP2/*.cs` 原文件)作为行为参考逐服务迁移，每阶段保持可编译可运行。

**Tech Stack:** .NET 8 SDK、WPF-UI 4.x(lepoco)、CommunityToolkit.Mvvm 8.x、System.Text.Json、DPAPI(`ProtectedData`)、xUnit、外部 7z.exe。

**Spec:** `docs/superpowers/specs/2026-09-30-uzip2-fluent-redesign-design.md`

## Global Constraints

- 目标框架 `net8.0-windows`，`OutputType=WinExe`，`UseWPF=true`；发布 `dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true`，产物不含 7-Zip。
- 全部现有功能保留：密码本/密码纸(DPAPI)、智能排序打分(旧键 `Score_*`)、多级解压、文件过滤、文件名提密码、压缩密码写文件名、随机密码、压缩日志、热键贴入、托盘常驻、右键/命令行调用(`e.Args` 纯文件路径→`Setting.FileList`+`IsCmdMode`)、置顶、Debug、独立结果窗口开关、AutoOpenAfterExtract、CleanTempOnStartup。
- 旧配置 `Config/UZIP2.exe.config`(appSettings 键全集见 spec 第 3 节 + `Items` 节 name/icon/contents 元素) 首次启动一次性迁移为 `Config/settings.json`，旧文件改 `*.bak`；DPAPI 密文原样搬运。
- View 代码后置禁止业务逻辑；服务不持全局静态可变状态。
- 每个任务结束：`dotnet build` 0 错 0 警(新代码) + 相关测试绿 + git commit(信息风格 `feat:`/`refactor:`/`fix:`/`chore:`)。
- 提交只在本地；推送/发布需用户另行指示。

---

### Task 1: 环境与 SDK 化工程

**Files:**
- Modify: `UZIP2/UZIP2.csproj`(整体替换为 SDK-style), `UZIP2.sln`(不动)
- Create: `UZIP2/nu.config` 不需要；NuGet 源用默认+nuge 镜像配置(见 Step 2)
- Delete: `UZIP2/App.config` 引用保留读取(迁移用)，文件不删

**Interfaces:**
- Produces: 可在 `net8.0-windows` 下 `dotnet build` 通过的旧程序(旧 UI 原样)。

- [ ] **Step 1: 安装 .NET 8 SDK（国内镜像）**

```bash
# https://dl.microsoft.com 在本机不可靠，走 https://mirrors.tuna.tsinghua.edu.cn/dotnet/ 或 aka.ms 直链
curl -L -o /d/dotnet-sdk.zip https://download.visualstudio.microsoft.com/download/pr/... # 用 dotnet-install.ps1 更稳:
powershell -Command "& {[void](New-Item -Force D:\dotnet); Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile D:\dotnet\install.ps1; & D:\dotnet\install.ps1 -Channel 8.0 -InstallDir D:\dotnet}"
export PATH=/d/dotnet:$PATH && dotnet --version   # 期望 8.0.x
```
若 aka.ms 超时，改用 TUNA 镜像 `https://mirrors.tuna.tsinghua.edu.cn/dotnet/eol/src/sdk/8.0.411/…`(Windows x64 tar.gz zip 包) 手动解压到 `D:\dotnet`。

- [ ] **Step 2: NuGet 国内源**

```bash
mkdir -p /d/githome/UZIP2 && cat > NuGet.Config <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget-cdn" value="https://nuget.cdn.azure.cn/v3/index.json" />
    <add key="api.nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
```
(azure.cn 源失效则只用 api.nuget.org + 系统代理，安装时验证。)

- [ ] **Step 3: csproj 改 SDK-style**

新 `UZIP2/UZIP2.csproj` 全文：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <LangVersion>12</LangVersion>
    <Nullable>disable</Nullable>
    <ApplicationIcon>UZIP.ico</ApplicationIcon>
    <AssemblyName>UZIP2</AssemblyName>
    <RootNamespace>UZIP2</RootNamespace>
    <UseWindowsForms>false</UseWindowsForms>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="WPF-UI" Version="4.0.2" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.3.2" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.1" />
    <PackageReference Include="System.Configuration.ConfigurationManager" Version="8.0.0" />
  </ItemGroup>
  <ItemGroup>
    <Resource Include="**/*.png" Exclude="bin/**;obj/**" />
    <Content Include="App.config" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: 编译并逐个修旧代码不兼容点**

Run: `dotnet build UZIP2/UZIP2.sln -c Release`
已知必改：`HttpWebRequest`(Mypassword.cs:85)→`HttpClient`；`System.Drawing` 若有引用删；`Properties/Settings.Designer.cs` 报缺 `ApplicationSettingsBase` 时补包 `System.Configuration.ConfigurationManager`(已加)；资源 URI(pack application) 不变。
Expected: 旧 UI 在 .NET 8 下编译通过并能启动(7z 路径沿用旧 Config)。

- [ ] **Step 5: 冒烟运行**

`dotnet run --project UZIP2` 启动旧窗口，拖入任一 zip 确认解压链路未断；Ctrl+C 关闭。

- [ ] **Step 6: Commit** `git add -A && git commit -m "build: migrate project to SDK-style .NET 8 (WPF-UI/Mvvm deps)"`

---

### Task 2: Models 与 SettingsService（JSON 配置核心）

**Files:**
- Create: `UZIP2/Models/AppSettings.cs`, `UZIP2/Services/ISettingsService.cs`, `UZIP2/Services/SettingsService.cs`, `UZIP2/Services/FileLogger.cs`
- Test: `UZIP2.Tests/SettingsServiceTests.cs`（新 sln 添加 xUnit 项目 `dotnet new xunit -o UZIP2.Tests` 并 `dotnet sln add`）

**Interfaces:**
- Produces（后续所有任务依赖，签名逐字使用）:

```csharp
namespace UZIP2.Models;
public sealed class AppSettings {
    public int AppMode { get; set; }                    // 0自动 1仅解压 2仅压缩
    public string Theme { get; set; } = "System";       // System|Light|Dark
    public bool WindowOnTop, UseHotKey, TrimSpace = true, ResultWindow, ShowDebug, DebugMode;
    public uint HotKeyKey; public bool HotKeyAlt, HotKeyShift, HotKeyCtrl;
    public bool Customize7z; public string Customize7zPath = "";
    public int ExtractOutMode, ExtractOutModePop;       // 旧编号语义保持
    public string ExtractCoverMode = "ask"; public bool ExtractUnknow, DeleteFinishFile, AutoOpenAfterExtract = true, CleanTempOnStartup = true, CreateNewFolder, CreateNameFolder, NameToPassword, HideZipContent;
    public string ExtractFilter = ""; public List<CustomFolder> CustomizeFolders = new(); // 旧1-8并入
    public int CompressOutMode, CompressOutModePop, PasswordMode, PasswordModePop;
    public string CompressType = "7z", CompressLevel = "-mx5", CompressFilter = "", NameFilter = "", NameFilter2 = "";
    public bool PasswordToName, CompressAlone, DeleteCompressFinish;
    public string LastExtractPath = "", LastCompressPath = "", PWUrl = "";
    public int ReadPasswordMode; public double WindowLeft = -1, WindowTop = -1; // 新增：记录窗口位置(替代旧仅记大小)
}
public sealed class CustomFolder { public string Name { get; set; } public string Path { get; set; } }

namespace UZIP2.Services;
public interface ISettingsService {
    AppSettings Current { get; }                 // 内存单例，加载后只读快照
    void Save(Action<AppSettings> mutate);       // 修改+落盘+发 Changed，原子写(temp+move)
    event Action<AppSettings> Changed;
    string SettingsPath { get; }                 // {BasePath}/Config/settings.json
}
public interface IFileLogger { void Info(string m); void Warn(string m); void Error(string m, Exception ex = null); string TailPath { get; } } // logs/app-yyyyMMdd.log, 保留7天
```

- [ ] **Step 1: 失败测试**：默认值序列化→反序列化等价；`Save` 原子写并发下不损坏(JSON 合法)。

```csharp
[Fact] public void Roundtrip_Preserves_Defaults() {
    var svc = new SettingsService(NewTempDir()); 
    var before = svc.Current.ExtractFilter;
    svc.Save(s => s.ExtractFilter = "广告*.txt");
    var reloaded = new SettingsService(svc.SettingsPath 目录).Current;
    Assert.Equal("广告*.txt", reloaded.ExtractFilter);
}
```

- [ ] **Step 2: 实现** `SettingsService`(System.Text.Json，`WriteIndented`)，`FileLogger` 自研滚动。**Step 3:** `dotnet test` 绿。**Step 4:** Commit `feat: settings.json service + file logger`。

---

### Task 3: LegacyConfigMigrator（旧 XML→settings.json）

**Files:**
- Create: `UZIP2/Services/LegacyConfigMigrator.cs`, `UZIP2/Models/LegacyPasswordStore.cs`(承接旧 Items 节+DPAPI)
- Test: `UZIP2.Tests/MigratorTests.cs`

**Interfaces:**
- Consumes: `ISettingsService`, 旧 `UZIP2/Config.cs`(Config 类可读旧 XML)。
- Produces: `static MigrationResult LegacyConfigMigrator.TryMigrate(string legacyConfigPath, ISettingsService settings, IPasswordStore store)`；`IPasswordStore`(Task 4 定义，这里先建接口: `void ImportBook(IEnumerable<PasswordEntry> items); IEnumerable<PasswordEntry> DumpAll();`)。

- [ ] **Step 1: 失败测试**——fixture 复制真实旧文件 `H:/Sync/PublicShare/software/UZip/Config/*.config` 的最小脱敏版到测试数据：断言 61 个 appSettings 键逐一映射正确(重点: `BackNoteNum` 等旧 UI 遗留键忽略、`CustomizeFolder{Name,Path}1..8`→`CustomizeFolders` 列表、`Score_xyz` 前缀键→PasswordEntry.Score、`ExtractCoverMode` 值原样)。
- [ ] **Step 2: 实现** 映射表(旧键→属性名字符串字典，反射赋值)；`Items` 节 contents 为 DPAPI base64 的原样搬运进新密码文件；成功后旧文件 `File.Move` 加 `.bak`；任何异常→保留原文件、返回 `MigrationResult.Failed(reason)`，调用方(App)仍以默认值启动。
- [ ] **Step 3:** `dotnet test` 绿；**Step 4:** Commit `feat: legacy config migrator with tests`。

---

### Task 4: PasswordService（密码本/纸、DPAPI、打分排序）

**Files:**
- Create: `UZIP2/Services/PasswordService.cs`, `UZIP2/Models/PasswordEntry.cs`
- Move: `UZIP2/DpapiHelper.cs`→`UZIP2/Services/Dpapi.cs`(命名空间改)；`UZIP2/Setting.cs:463 class Password`、`MyPasswords.cs`、`Mypassword.cs` 行为并入
- Test: `UZIP2.Tests/PasswordServiceTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IPasswordStore { /* Task 3 已定 */ }
public sealed class PasswordEntry { public string Name; public string Cipher;   // DPAPI base64
    public int UseCount; public int SuccessCount; public double Score; public bool IsPaper; }
public sealed class PasswordService : IPasswordStore {
    string StorePath { get; }                       // Config/passwords.bin (JSON, 条目含 Cipher)
    IReadOnlyList<PasswordEntry> Book { get; } IReadOnlyList<PasswordEntry> Paper { get; }
    void AddBook(string name, string plain); void RemoveBook(string name); void UpdateBook(string old, string name, string plain);
    void PasteToPaper(string plain);                // 去重(TrimSpace 规则由调用方预处理)
    IReadOnlyList<string> CandidatePasswords(string archivePath); // 顺序: 文件名提取(参数外传入)→Score 降序→Paper→内置(ReadPasswordMode 1-4 旧逻辑, 网络源用 HttpClient)
    void ReportResult(string passwordUsed, bool success);          // 更新 Use/Success/Score 并落盘
}
```

- [ ] **Step 1:** 从 `MainWindow.xaml.cs` 搜索 `LoadScores`/`Score_`/排序打分段(提交 1dc43e0 引入) 摘录行为，写失败测试：去重、打分排序(成功率高→分高、同分新使用优先)、DPAPI 往返、Mypassword 四种读取模式(网络 mock)。
- [ ] **Step 2:** 实现；内置密码数组与旧 `Mypassword.cs:36-43` 一致并允许用户在 `settings.internalPasswords`(新增 `List<string>` 属性,默认含旧 6 条)配置。
- [ ] **Step 3:** 测试绿。旧 `Setting.PWPaper/PWBook` 暂不删(Task 10 统一断线)。**Step 4:** Commit `feat: password service (book/paper, dpapi, smart ordering)`。

---

### Task 5: SevenZipClient（重写 UCmd）

**Files:**
- Create: `UZIP2/Services/SevenZipClient.cs`, `UZIP2/Models/SevenZipProgress.cs`
- Test: `UZIP2.Tests/SevenZipClientTests.cs`（用 `tests/data/` 生成加密样本：`7z a -p"secret123" enc.7z hello.txt`，CI 上装 7-Zip：workflow 加 `choco install 7zip`）

**Interfaces:**
- Consumes: 外部 `7z.exe` 路径解析(移植 `Tools.cs:541-634 UCmdPathHelp`，含注册表/Program Files/自定义)。
- Produces:

```csharp
public sealed class SevenZipClient {
    public string SevenZipPath { get; }              // null => 未找到, UI 出 banner
    public Task<SevenZipResult> TestAsync(string archive, string pwd, CancellationToken ct);
    public Task<SevenZipResult> ExtractAsync(string archive, string dest, string pwd, IProgress<SevenZipProgress> p, CancellationToken ct);
    public Task<SevenZipResult> CompressAsync(IReadOnlyList<string> files, string outArchive, string pwd, string type, string level, bool passwordToName, IProgress<SevenZipProgress> p, CancellationToken ct);
    public string ListContent(string archive, string pwd);   // 供 HideZipContent 预览
}
public record SevenZipProgress(double? Percent, string CurrentFile, int DoneCount, int TotalCount);
public record SevenZipResult(bool Success, string Output, string ArchivePath, SevenZipError Error, string Diagnosis);
public enum SevenZipError { None, WrongPassword, Corrupt, UnsupportedFormat, DiskFull, PathTooLong, Cancelled, NotFound }
```

- [ ] **Step 1:** 失败测试：进度行解析(`55% 4 file.txt`→Percent/CurrentFile)、`Diagnose7zError` 分类(强化 `Tools.cs:177`：加"磁盘满/路径过长"，密码错=`Data Error in encrypted file`/`Wrong Password`)、取消杀进程树。
- [ ] **Step 2:** 实现：参数拼装逐字对照旧 `UCmd.ExtractFile/CompressFile`(Tools.cs:663-860)，保持 `-t -mx -mhe=on -y -bb1 -bsp1` 等；`Process.StartInfo` 用 `ArgumentList` 防注入(修正旧版字符串拼接)；stdin 传密码(`-p!stdin` 7z22+) 若失败回退 `-p<pwd>`(仅内存)。
- [ ] **Step 3:** 集成测试：加密样本 Test 错密码→WrongPassword；对密码→Success；Extract 产物含 hello.txt；Compress→再解 roundtrip。
- [ ] **Step 4:** Commit `feat: sevenzip client with progress, diagnosis, cancellation`。

---

### Task 6: ArchiveInfo/VolumeHelper/RealExtension

**Files:**
- Create: `UZIP2/Services/ArchiveInspector.cs`(合并 `Tools.cs:118-176 RealExtension`, `CanExtract:199`, `VolumesFile:378-540`)
- Test: `UZIP2.Tests/ArchiveInspectorTests.cs`

**Interfaces:** Produces `ArchiveInspector.Inspect(string path) -> ArchiveInfo { bool IsArchive; string RealExt; bool IsVolume; string MainVolumePath; }`。
- [ ] Step 1 失败测试：各魔数(ZIP/RAR/7z/BZ2/GZ/XZ/WIM/TAR/ISO, 逐字移植 Tools.cs:118-176 的签名表)、`.part1.rar/.r00/.001` 分卷、`.zip.001` 式分卷(VolumesFile.GetMainVolumes)。Step 2 实现。Step 3 绿。Step 4 Commit `feat: archive inspector (magic numbers, volumes)`。

---

### Task 7: FilterService + PasswordFromNameService + TempManager + CompressLog

**Files:**
- Create: `UZIP2/Services/FilterService.cs`, `UZIP2/Services/PasswordFromNameService.cs`, `UZIP2/Services/TempManager.cs`, `UZIP2/Services/CompressLogService.cs`
- Test: 同名 Tests 各一文件

**Interfaces:**
- `FilterService.Apply(string extractedDir, string filterRules, bool toRecycle)` — 移植 `Tools.cs:320 FindFile`+`347 Delete*`+隐藏内容(`HideZipContent`)。
- `PasswordFromNameService.Extract(string fileName, string delimiter1, string delimiter2) -> string|null` — 移植 MainWindow 内旧逻辑(搜 `NameToPassword`)。
- `TempManager`: `string CreateSessionTemp(string outputDir)`(旧 `UZipTemp_` 隐藏目录语义, `MoveFolderHelp`)；`void CleanupOnStartup()`(从 App.xaml.cs 移植)。
- `CompressLogService`: `void Log(string archivePath, string password)`；`string LogPath`(旧 `Compress.log` 格式逐字保持, `CompressResultToTxt:974`)。
- [ ] 每服务：失败测试(过滤命中含中文/通配符；`a-b-c.zip` 分隔符 `-` 提 `b`；temp 名冲突；log 追加格式)→实现→绿。分 4 个 commit 或 1 个 `feat: filter/password-from-name/temp/log services`。

---

### Task 8: ArchiveWorker（任务队列）

**Files:**
- Create: `UZIP2/Services/ArchiveWorker.cs`, `UZIP2/Models/JobEntry.cs`
- Test: `UZIP2.Tests/ArchiveWorkerTests.cs`

**Interfaces:**
- Consumes: Task 4-7 全部服务 + `ISettingsService`。
- Produces:

```csharp
public sealed class JobEntry : ObservableObject {   // 直接给 UI 绑定
    public string Archive, Target, Kind;            // Kind: Extract|Compress
    public JobStatus Status;                        // Queued|Running|Success|Failed|Cancelled
    public double? Percent; public string CurrentFile; public int Done, Total;
    public string Diagnosis; public string UsedPassword; public string OutputDir;
}
public sealed class ArchiveWorker {
    public ReadOnlyObservableCollection<JobEntry> Jobs { get; }
    public event Action<JobEntry> JobFinished;      // TrayService/AutoOpen 订阅
    public void EnqueueExtract(IReadOnlyList<string> archives);
    public void EnqueueCompress(IReadOnlyList<string> files, string outDir);
    public void Cancel(JobEntry job);  public void CancelAll();
    public void Retry(JobEntry job, string manualPassword = null);
}
```
串行消费(`System.Threading.Channels`)；解压内部=Test 逐候选密码→Extract；多级解压循环(旧 MainWindow 行为：解出的第一层子包再走同流程，深度上限 5，移植时在代码注释标旧行号)；成功后 Filter→DeleteFinishFile→TempManager 合并移动(`Tools.cs:255 MoveFolder` 移植到内部 `MoveService`)。
- [ ] Step 1 失败测试(注入 fake SevenZipClient)：入队 3 个→串行执行→Jobs 状态推进；Cancel 当前不影响后续；多级解压两层嵌套样本(用 SevenZipClient 真实样本)。Step 2 实现。Step 3 绿。Step 4 Commit `feat: archive worker queue`。

---

### Task 9: TrayService / HotKeyService / ClipboardService

**Files:**
- Create: 对应 `UZIP2/Services/`；Move: `Notifier.cs`→TrayService(改用 `System.Windows.Forms.NotifyIcon`，`UseWindowsForms=true` 或 H.NotifyIcon 二选一——用 WinForms 内置，加 `<UseWindowsForms>true</UseWindowsForms>`)；`UHotKey.cs`, `SimulateInput.cs` 并入 HotKeyService。
- Test: 仅 ClipboardService 可单测(去重/TrimSpace)，其余编译+手工冒烟。

**Interfaces:** `TrayService.Setup(Action restore, Action quit)`, `void ShowBalloon(string title, string text)`；`HotKeyService.Register(uint vk, bool alt, bool shift, bool ctrl, Action fired)`、`Action OnClipboardPaste`(读剪贴板→`PasswordService.PasteToPaper`)。
- [ ] Steps: 移植→编译→冒烟(托盘双击恢复、右键退出、热键贴入)→Commit `feat: tray, hotkey, clipboard services on net8`。

---

### Task 10: App 组装（Host/DI/单实例/迁移接线）

**Files:**
- Modify: `UZIP2/App.xaml`(StartupUri 删除), `UZIP2/App.xaml.cs`(重写)
- Create: `UZIP2/Services/InstanceBus.cs`(单实例：Mutex+命名 Pipe 转发文件路径；若旧版无单实例则新加，行为=第二进程把 `e.Args` 传给主进程并退出；无参数则提示已在托盘)

**Interfaces:** Produces 全局 `IServiceProvider`；启动管线顺序：`FileLogger`→`SettingsService`(含 `LegacyConfigMigrator.TryMigrate`→ 失败记 banner 状态)→`PasswordService`→`TempManager.CleanupOnStartup`→`InstanceBus`→`MainWindow.Show`。
- [ ] Step 1: 重写 App.xaml.cs(Hosting.CreateApplicationBuilder + 单例注册所有服务)。Step 2: `dotnet run` 用**真实旧 Config 副本**验证迁移生成 settings.json + passwords.bin + `.bak`(H 盘目录先整份复制到 `D:\uzip2-migration-test`)。Step 3: Commit `feat: app host, DI, single-instance, migration wiring`。**旧 Setting.cs/静态类此任务后不得再被新代码引用。**

---

### Task 11: Fluent 外壳（MainWindow + NavigationView）

**Files:**
- Create: `UZIP2/View/MainWindow.xaml(.cs)`, `UZIP2/ViewModel/ShellViewModel.cs`；旧 `MainWindow.xaml*` 移入 `UZIP2/Legacy/`(不参与编译：`<Compile Remove="Legacy/**" /> <Page Remove="Legacy/**" />`)
- Test: 手工截图验收

**Interfaces:** Consumes: `ArchiveWorker.Jobs`, `ISettingsService`(Theme/置顶/窗口位置持久化)。
- [ ] Step 1: `ui:FluentWindow`+`ui:NavigationView`(主页/密码本/设置三 MenuItems)、Mica `BackdropType="Mica"`、`ExtendsContentIntoTitleBar`、置顶切换按钮、`WindowCornerPreference`。深浅色：`Wpf.Ui.Appearance.ApplicationThemeManager.Apply(settings.Theme)` 并订阅 Changed。
- [ ] Step 2: 尺寸 860×620、`MinWidth=720`，启动恢复 `WindowLeft/Top`(Task 2 字段)。Step 3: 运行截图(浅/深各一)入 git commit message 或 docs；Commit `feat: fluent shell window`。

---

### Task 12: 主页（拖拽卡 + 模式条 + 任务队列）

**Files:**
- Create: `UZIP2/View/HomePage.xaml`, `UZIP2/ViewModel/HomeViewModel.cs`, `UZIP2/View/JobCardTemplate.xaml`(DataTemplate)
- Test: HomeViewModel 单测(拖放预告文本纯函数化：`string PreviewFor(mode, droppedFiles)` 可测)

**Interfaces:** Consumes: `ArchiveWorker`, `ISettingsService.AppMode`。
- [ ] Step 1: 失败测试预告文案：自动模式混合拖入(2 文件 1 压缩包)→"释放以解压 1 个压缩包、压缩 2 个文件…"；仅压缩模式拖文件夹→合法；仅解压拖文件夹→灰警告文案。
- [ ] Step 2: 拖拽卡实现：`DragOver` 用 `e.Data.GetDataPresent(DataFormats.FileDrop)`+`PreviewFor`；`Drop→Enqueue*`。模式 `ui:Segmented`(不存在则 RadioButton 组包成视觉等价)。队列 `ItemsControl`+`ProgressRing`(绑定 Percent/CurrentFile/Done-Total/取消钮)；成功卡内联"打开目录/删源"，失败卡显示 Diagnosis+重试(调 `ArchiveWorker.Retry`)。底部状态条：`SevenZipPath==null` 时红色 banner(定位按钮→FolderBrowser 写 `Customize7zPath`)。
- [ ] Step 3: 手动验收：拖 5 个密码包看队列/进度/通知/自动打开。Commit `feat: fluent home page with drag preview and job queue`。

---

### Task 13: 密码本页

**Files:** `UZIP2/View/PasswordPage.xaml`, `UZIP2/ViewModel/PasswordBookViewModel.cs`
- [ ] Step 1 失败测试：VM 增删改查走 `PasswordService`；搜索过滤；揭示密码需命令显式调用(默认打码显示，`Password` 列绑 `RevealCommand` 后 3s 自动回掩码——用 DispatcherTimer)。
- [ ] Step 2: 实现上列表(名称/密码/使用/成功/得分/操作行内编辑)+下密码纸(ItemsControl chip，`PasteFromClipboardCommand`、清空；数量徽标显示在主页状态条)。删除旧 `KeyBoard.xaml`(功能由粘贴+热键覆盖；若坚持保留虚拟键盘则留 Legacy 引用——默认不保留，spec 已注明)。
- [ ] Step 3: 验收：迁移来的旧密码本可列出/解出(冒烟一个真实密码包)。Commit `feat: password book/paper page`。

---

### Task 14: 设置页（全量接线）

**Files:** `UZIP2/View/SettingsPage.xaml`, `UZIP2/ViewModel/SettingsViewModel.cs`
**Interfaces:** Consumes: `AppSettings` 每个属性都有对应控件；7z 引导、调试日志尾部查看(`IFileLogger.TailPath` 读尾 200 行进 `ui:TextBox` 只读)。
- [ ] Step 1: 分组 `ui:SettingsExpander`：常规/解压/压缩/密码/快捷键/关于。自定义目录与过滤规则=可增删条目列表(`ObservableCollection<CustomFolder>` 绑 ItemsControl+添加/删除钮)。热键=四修饰键开关+`ui:KeyBox` 录键→`HotKeyKey/Alt/Shift/Ctrl`。
- [ ] Step 2: 任何控件变更→`ISettingsService.Save`(即时保存，无保存钮)；涉及服务即时生效项(TrimSpace/HideZipContent/热键重注册)在 Changed 订阅里重连。
- [ ] Step 3: 对照 spec 第 2 节清单逐项勾选(在计划文档本任务下打钩提交)，回归旧 61 键中有 UI 意义的每一项都有去处。Commit `feat: fluent settings page`。

---

### Task 15: 行为对等回归（命令行/右键/旧链路）

**Files:** Modify: 右键菜单注册脚本或文档说明(旧版如何注册沿用：搜 `UZIP2.reg`/`SendTo`)；Create: `docs/parity-checklist.md`
- [ ] Step 1: 清单逐项验证并记录结果：① `UZIP2.exe a.zip b.zip`(右键发送到/命令行)直接入队解压；② 第二实例转发；③ 全部密码试错→诊断文案；④ 多级解压嵌套样本；⑤ 分卷 rar/zip.001；⑥ 压缩密码写文件名+随机密码；⑦ 过滤广告文件+HideZipContent；⑧ 压缩日志打开；⑨ 置顶/Debug 模式/ShowDebug；⑩ 独立结果窗口开关(ResultViewModel 简化版窗口沿用旧 ResultWindow 数据面，仅换皮)。失败项回对应服务修复。
- [ ] Step 2: `dotnet test` 全绿 + checklist 提交。Commit `test: behavior parity checklist and fixes`。

---

### Task 16: 发布与 CI

**Files:** Modify: `.github/workflows/build-windows.yml`
- [ ] Step 1: workflow 改：`actions/setup-dotnet@v4 dotnet-version: 8.0.x` → `dotnet test` → `dotnet publish UZIP2 -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o release` → upload artifact(含 UZIP2.exe+config，不含 7-Zip)。
- [ ] Step 2: 本地同命令产出，替换 H 盘发布目录属用户后续指示(先问再动)。README"本次增强"追加 v3.0 段落(Fluent 重构说明+迁移说明)。
- [ ] Step 3: 本机 `release/UZIP2.exe` 启动冒烟。Commit `ci: dotnet 8 single-file publish` + `docs: README v3.0`。

---

## Self-Review 记录

- 覆盖检查：spec 五节均有对应任务(架构→1/2/10，UI→11-14，迁移→3/4，错误→5/8/12，测试→各任务+15)。
- 类型一致性：`PasswordEntry/IProgress<SevenZipProgress>/JobEntry/AppSettings` 在定义任务内逐字引用；Task 3 预声明的 `IPasswordStore` 在 Task 4 补全，顺序已注明。
- 无占位符：所有步骤含具体文件、签名、断言或命令；移植类步骤给出旧源码行号范围作为"内容"。
