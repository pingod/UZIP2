using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.ViewModel
{
    public sealed record DropPreview(string Text, bool IsWarning);

    public partial class HomeViewModel : ObservableObject
    {
        private readonly ArchiveWorker _worker;
        private readonly ISettingsService _settings;
        private readonly SevenZipClient _zip;
        private readonly PasswordService _passwords;
        private readonly ClipboardService _clipboard;
        private readonly ObservableCollection<JobEntry> _jobs = new ObservableCollection<JobEntry>(); // 排序后的显示镜像
        private readonly Dispatcher _ui = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;

        // 本批已弹过窗的输出目录：一批 20 个档案解到同一目录只该开一个资源管理器
        private readonly HashSet<string> _openedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _openGate = new object();

        /// <summary>打开输出目录的动作，默认为资源管理器；测试或宿主可替换。</summary>
        public Action<string> OpenDirectory { get; set; } = OpenInExplorer;

        // 终态卡片自动回收上限：长会话连跑几百个文件也不让列表无限堆积
        const int TerminalKeepCap = 200;

        public HomeViewModel(ArchiveWorker worker, ISettingsService settings, SevenZipClient zip,
            PasswordService passwords, ClipboardService clipboard)
        {
            _worker = worker;
            _settings = settings;
            _zip = zip;
            _passwords = passwords;
            _clipboard = clipboard;
            _mode = settings.Current.AppMode;
            var sort = settings.Current.JobSort;
            _sortMode = sort >= 0 && sort <= JobOrder.ByName ? sort : JobOrder.AddOrder;
            _sevenZipMissing = zip.SevenZipPath == null;
            _paperCount = passwords.Paper.Count;
            // 异步派发：解压线程会在持有密码本锁的过程中抛 Changed，用 Invoke 就是让 UI 等那把锁
            passwords.Changed += () => Post(() => PaperCount = passwords.Paper.Count);
            _jobs.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasJobs));
            ((INotifyCollectionChanged)_worker.Jobs).CollectionChanged += OnJobsChanged;
            _worker.JobFinished += OnJobFinished;
            foreach (var job in _worker.Jobs) HookJob(job);
            RebuildMirror();
            RefreshSummary();
        }

        // 主页列表是 _worker.Jobs 的排序镜像；绑定名保持不变
        public ObservableCollection<JobEntry> Jobs => _jobs;
        public bool HasJobs => _jobs.Count > 0;

        public string[] SortOptions { get; } = JobOrder.Names;

        // 「转为」下拉：显示顺序与 ArchiveFormat 下标的对应表（7z 放第一位，它最常用）
        static readonly int[] FormatIndices = { 1, 0, 4, 2, 3, 5, 6 };
        public string[] ConvertTypeNames { get; } = { "7z", "zip", "tar", "bz2", "gz", "wim", "xz" };

        [ObservableProperty] private int _mode;
        [ObservableProperty] private string _previewText = "";
        [ObservableProperty] private bool _previewIsWarning;
        [ObservableProperty] private bool _sevenZipMissing;
        [ObservableProperty] private int _paperCount;
        [ObservableProperty] private int _failedCount;
        [ObservableProperty] private int _finishedCount;
        [ObservableProperty] private int _activeCount;
        [ObservableProperty] private int _sortMode;
        [ObservableProperty] private int _convertTypeIndex;

        public bool HasFailures => FailedCount > 0;
        public bool HasFinished => FinishedCount > 0;
        public bool HasActive => ActiveCount > 0;

        partial void OnFailedCountChanged(int value)
        {
            OnPropertyChanged(nameof(HasFailures));
            RetryAllFailedCommand.NotifyCanExecuteChanged();
            ExportFailuresCommand.NotifyCanExecuteChanged();
            ClearFailedCommand.NotifyCanExecuteChanged();
        }

        partial void OnFinishedCountChanged(int value)
        {
            OnPropertyChanged(nameof(HasFinished));
            ClearFinishedCommand.NotifyCanExecuteChanged();
        }

        partial void OnActiveCountChanged(int value)
        {
            OnPropertyChanged(nameof(HasActive));
            CancelAllJobsCommand.NotifyCanExecuteChanged();
        }

        partial void OnSortModeChanged(int value)
        {
            _settings.Save(s => s.JobSort = value);
            RebuildMirror();
        }

        [RelayCommand]
        private void PastePassword() => _clipboard?.PasteFromClipboard();

        partial void OnModeChanged(int value)
        {
            _settings.Save(s => s.AppMode = value);
        }

        // ---- 拖拽预告（纯函数） ----

        public static DropPreview PreviewFor(int mode, IReadOnlyList<string> dropped)
        {
            var list = dropped ?? Array.Empty<string>();
            var archives = list.Count(IsArchivePath);
            var others = list.Count - archives;

            if (list.Count == 0)
                return new DropPreview("无有效文件", true);

            switch (mode)
            {
                case 1: // 仅解压
                    if (archives == 0)
                        return new DropPreview("没有可解压的压缩包", true);
                    if (others == 0)
                        return new DropPreview($"释放以解压 {archives} 个压缩包", false);
                    return new DropPreview($"释放以解压 {archives} 个压缩包，{others} 个文件夹/非压缩包将被忽略", true);
                case 2: // 仅压缩
                    return new DropPreview($"释放以压缩 {list.Count} 个文件/文件夹", false);
                default: // 自动
                    if (archives > 0 && others > 0)
                        return new DropPreview($"释放以解压 {archives} 个压缩包、压缩 {others} 个文件/文件夹", false);
                    if (archives > 0)
                        return new DropPreview($"释放以解压 {archives} 个压缩包", false);
                    return new DropPreview($"释放以压缩 {others} 个文件/文件夹", false);
            }
        }

        static bool IsArchivePath(string path)
        {
            try
            {
                return File.Exists(path) && ArchiveInspector.CanExtractByExtension(path);
            }
            catch
            {
                return false;
            }
        }

        // ---- 拖放 ----

        public void ShowPreview(string[] files)
        {
            var p = PreviewFor(Mode, files ?? Array.Empty<string>());
            PreviewText = p.Text;
            PreviewIsWarning = p.IsWarning;
        }

        public void ClearPreview()
        {
            PreviewText = "";
            PreviewIsWarning = false;
        }

        [RelayCommand]
        public void DropFiles(string[] files)
        {
            ClearPreview();
            var list = (files ?? Array.Empty<string>())
                .Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
            if (list.Count == 0) return;

            var archives = list.Where(IsArchivePath).ToList();
            var others = list.Except(archives).ToList();

            switch (Mode)
            {
                case 1:
                    if (archives.Count == 1 && _settings.Current.PreviewBeforeExtract) OpenPreviewWindow(archives[0]);
                    else if (archives.Count > 0) _worker.EnqueueExtract(archives);
                    break;
                case 2:
                    _worker.EnqueueCompress(list);
                    break;
                default:
                    if (archives.Count == 1 && _settings.Current.PreviewBeforeExtract) OpenPreviewWindow(archives[0]);
                    else if (archives.Count > 0) _worker.EnqueueExtract(archives);
                    if (others.Count > 0) _worker.EnqueueCompress(others);
                    break;
            }
        }

        /// <summary>打开包内清单窗口（勾选解压）。右键「解压并预览」也走这里，避免两套开窗逻辑漂移。</summary>
        public void OpenPreviewWindow(string archive, string password = null)
        {
            var mw = System.Windows.Application.Current?.MainWindow;
            var win = new UZIP2.Shell.PreviewWindow(archive, null, _worker)
            {
                Owner = mw != null && mw.IsVisible ? mw : null
            };
            win.ShowDialog();
        }

        // ---- 校验和 ----

        void OpenChecksum(IEnumerable<string> files)
        {
            var mw = System.Windows.Application.Current?.MainWindow;
            var win = new UZIP2.Shell.ChecksumWindow(files)
            {
                Owner = mw != null && mw.IsVisible ? mw : null
            };
            win.Show();
        }

        [RelayCommand]
        void ChecksumJob(JobEntry job)
        {
            if (job != null && File.Exists(job.Archive)) OpenChecksum(new[] { job.Archive });
        }

        [RelayCommand]
        void BrowseChecksum()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "选择要校验的文件",
                Filter = "所有文件|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            OpenChecksum(dlg.FileNames);
        }

        // ---- 更新检查 / 自更新 ----

        [ObservableProperty] private string _updateVersion;
        [ObservableProperty] private bool _isUpdating;
        [ObservableProperty] private string _updateStatus = "";
        private UpdateInfo _pendingInfo;      // 最近一次发现的可用更新（含直链/体积）

        public bool HasUpdate => !string.IsNullOrEmpty(UpdateVersion);
        public string UpdateMessage => string.IsNullOrEmpty(UpdateVersion) ? ""
            : $"发现新版本 UZIP {UpdateVersion}（当前 {UpdateService.CurrentVersion()}）。";

        // 只有框架依赖单文件、且拿到了直链，才允许一键就地更新；否则回落到"打开发布页"
        public bool CanAutoUpdate => HasUpdate && _pendingInfo != null
            && !string.IsNullOrEmpty(_pendingInfo.DownloadUrl)
            && SelfUpdater.CanApplyInPlace(SelfUpdater.CurrentExePath());

        partial void OnUpdateVersionChanged(string value)
        {
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(UpdateMessage));
            OnPropertyChanged(nameof(CanAutoUpdate));
        }

        // 一天问一次就够；force 用于设置页上的"检查更新"按钮
        public static bool ShouldCheck(bool enabled, DateTime last, DateTime now)
            => enabled && (last == default || (now - last).TotalHours >= 20);

        // 返回一行给人看的结果（设置页用）；null = 没问到，启动路径直接忽略
        public async System.Threading.Tasks.Task<string> CheckForUpdateAsync(bool force = false)
        {
            var s = _settings.Current;
            if (!force && !ShouldCheck(s.CheckUpdateOnStartup, s.LastUpdateCheck, DateTime.Now)) return null;
            _settings.Save(x => x.LastUpdateCheck = DateTime.Now);

            var info = await UpdateService.CheckAsync().ConfigureAwait(false);
            if (info == null) return null;

            string current = UpdateService.CurrentVersion();
            if (!UpdateService.IsNewer(current, info.Version))
            {
                _settings.Save(x => x.LatestSeenVersion = info.Version);
                return $"已是最新版本（{current}）";
            }
            // 用户点过"不再提示"的这个版本不再打扰，等下一个版本
            if (string.Equals(info.Version, s.LatestSeenVersion, StringComparison.OrdinalIgnoreCase))
                return $"新版 {info.Version} 已被你忽略";
            _pendingInfo = info;
            _ui.Invoke(() => UpdateVersion = info.Version);
            return $"发现新版本 {info.Version}，主页顶部已提示";
        }

        [RelayCommand]
        void DismissUpdate()
        {
            var seen = UpdateVersion;
            _pendingInfo = null;
            UpdateVersion = null;
            if (!string.IsNullOrEmpty(seen)) _settings.Save(x => x.LatestSeenVersion = seen);
        }

        [RelayCommand]
        void OpenUpdatePage()
        {
            try
            {
                Process.Start(new ProcessStartInfo(UpdateService.ReleasePageUrl) { UseShellExecute = true });
            }
            catch { }
        }

        // 一键就地更新：下载→校验→写中继脚本→脱离启动，然后本进程退出，
        // 由中继脚本等我们退出后换体并拉起新版。仅框架依赖单文件可用（CanAutoUpdate 已把关）。
        // AsyncRelayCommand 运行期间自动禁用按钮，天然防重复点击。
        [RelayCommand]
        async System.Threading.Tasks.Task ApplyUpdate()
        {
            var info = _pendingInfo;
            if (info == null || string.IsNullOrEmpty(info.DownloadUrl)) { OpenUpdatePage(); return; }

            string exe = SelfUpdater.CurrentExePath();
            if (!SelfUpdater.CanApplyInPlace(exe))
            { UpdateStatus = "当前运行方式不支持就地更新，已打开下载页"; OpenUpdatePage(); return; }

            IsUpdating = true;
            UpdateStatus = "正在下载更新…";
            var progress = new Progress<long>(bytes =>
                UpdateStatus = info.Size > 0
                    ? $"下载中… {Math.Min(100, bytes * 100 / info.Size)}%"
                    : $"下载中… {bytes / 1024} KB");

            var res = await SelfUpdater.StageAndApplyAsync(info, exe, progress, default);
            if (res.Ok)
            {
                UpdateStatus = "更新已就绪，正在重启到新版本…";
                IsUpdating = false;
                await System.Threading.Tasks.Task.Delay(700);   // 让这条提示先渲染
                System.Windows.Application.Current?.Shutdown();  // 进程退出后中继脚本接手换体
            }
            else
            {
                UpdateStatus = res.Error;
                IsUpdating = false;
            }
        }

        // ---- 任务卡命令 ----

        [RelayCommand]
        void CancelJob(JobEntry job)
        {
            if (job != null) _worker.Cancel(job);
        }

        // 解压前先看包内清单，可勾选只解其中几项
        [RelayCommand]
        void PreviewJob(JobEntry job)
        {
            if (job == null || string.IsNullOrEmpty(job.Archive)) return;
            if (!File.Exists(job.Archive)) return;
            OpenPreviewWindow(job.Archive, job.UsedPassword);
        }

        [RelayCommand]
        void RetryJob(JobEntry job)
        {
            if (job != null) _worker.Retry(job);
        }

        // 卡片上的「转格式」：产物就放在原包旁边，原包永远保留
        [RelayCommand]
        void ConvertJob(JobEntry job)
        {
            if (job == null || string.IsNullOrEmpty(job.Archive) || !File.Exists(job.Archive)) return;
            if (ConvertTypeIndex < 0 || ConvertTypeIndex >= FormatIndices.Length) return;
            _worker.EnqueueConvert(new[] { job.Archive }, FormatIndices[ConvertTypeIndex], null, job.UsedPassword);
        }

        // 单卡"移除"：只对终态生效（worker 侧对活动项免疫），历史页不受影响
        [RelayCommand]
        void RemoveJob(JobEntry job) => _worker.Remove(job);

        // 一次卡多个包失败时，逐个点重试太慢；批量重跑一遍（密码链会重新走）
        [RelayCommand(CanExecute = nameof(HasFailures))]
        void RetryAllFailed() => _worker.RetryAllFailed();

        // 清除按钮只动主页列表，不动历史——历史页仍然可查全部记录
        [RelayCommand(CanExecute = nameof(HasFinished))]
        void ClearFinished() =>
            _worker.RemoveTerminal(j => j.Status == JobStatus.Success || j.Status == JobStatus.Cancelled);

        [RelayCommand(CanExecute = nameof(HasFailures))]
        void ClearFailed() => _worker.RemoveTerminal(j => j.Status == JobStatus.Failed);

        [RelayCommand(CanExecute = nameof(HasActive))]
        void CancelAllJobs() => _worker.CancelAll();

        [RelayCommand(CanExecute = nameof(HasFailures))]
        void ExportFailures()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出失败报告",
                Filter = "文本文件|*.txt",
                FileName = "uzip-failures-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                UZIP2.Services.FailureReport.Write(dlg.FileName,
                    UZIP2.Services.FailureReport.Build(Jobs, _zip.SevenZipPath));
            }
            catch { }
        }

        [RelayCommand]
        void OpenOutput(JobEntry job)
        {
            if (job == null) return;
            var dir = job.OutputDir;
            if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(job.Archive))
                dir = Path.GetDirectoryName(job.Archive);
            if (!string.IsNullOrEmpty(dir) && File.Exists(dir))
                dir = Path.GetDirectoryName(dir);
            OpenInExplorer(dir);
        }

        [RelayCommand]
        void DeleteSource(JobEntry job)
        {
            if (job == null || string.IsNullOrEmpty(job.Archive) || !File.Exists(job.Archive)) return;
            var vol = ArchiveInspector.AnalyzeVolume(job.Archive);
            if (vol != null && vol.IsVolume)
                ArchiveInspector.DeleteVolumeSet(vol, _settings.Current.DeleteToRecycle);
            else
                FilterService.Delete(job.Archive, _settings.Current.DeleteToRecycle);
        }

        [RelayCommand]
        void Locate7z()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 7-Zip 安装目录（含 7z.exe）",
                SelectedPath = SafeCurrent7zDir()
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var exe = Path.Combine(dlg.SelectedPath, "7z.exe");
                if (File.Exists(exe))
                {
                    _settings.Save(s =>
                    {
                        s.Customize7z = true;
                        s.Customize7zPath = exe;
                    });
                }
            }
            SevenZipMissing = _zip.SevenZipPath == null;
        }

        string SafeCurrent7zDir()
        {
            try
            {
                var p = _zip.SevenZipPath;
                return p == null ? "" : Path.GetDirectoryName(p);
            }
            catch { return ""; }
        }

        static void OpenInExplorer(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch { /* 打开资源管理器失败不影响任务 */ }
        }

        // ---- 列表镜像：_jobs 始终保持按 SortMode 排序 ----

        // 镜像自身的小锁：真实运行时所有镜像操作都编组在 UI 线程（锁永不争用），
        // 单元测试没有消息泵、动作会内联到后台线程执行，这把锁保证读写不撕裂。
        private readonly object _mirrorGate = new object();

        // UI 线程直接执行；单元测试没有消息泵，也直接执行；只有真实后台线程才编组
        void Post(Action action)
        {
            if (_ui.CheckAccess() || System.Windows.Application.Current == null)
            {
                action();
                return;
            }
            _ui.BeginInvoke(action);
        }

        void RebuildMirror() => Post(RebuildMirrorCore);

        void RebuildMirrorCore()
        {
            var sorted = _worker.Jobs.ToList();
            sorted.Sort((a, b) => JobOrder.Compare(a, b, SortMode));
            lock (_mirrorGate)
            {
                _jobs.Clear();
                foreach (var j in sorted) _jobs.Add(j);
            }
        }

        void RepositionJob(JobEntry job) => Post(() =>
        {
            lock (_mirrorGate)
            {
                int cur = _jobs.IndexOf(job);
                if (cur < 0) return;
                int target = JobOrder.InsertIndex(_jobs, job, SortMode, cur);
                if (target != cur) _jobs.Move(cur, target);
            }
        });

        void OnJobsChanged(object sender, NotifyCollectionChangedEventArgs e) => Post(() =>
        {
            lock (_mirrorGate)
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        foreach (JobEntry job in e.NewItems)
                        {
                            HookJob(job);
                            if (!_jobs.Contains(job))
                                _jobs.Insert(JobOrder.InsertIndex(_jobs, job, SortMode), job);
                        }
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        foreach (JobEntry job in e.OldItems)
                        {
                            job.PropertyChanged -= OnJobPropertyChanged;
                            _jobs.Remove(job);
                        }
                        break;
                    default:
                        foreach (var job in _worker.Jobs) HookJob(job);
                        RebuildMirrorCore();
                        break;
                }
            }
            RefreshSummaryCore();
        });

        void HookJob(JobEntry job)
        {
            job.PropertyChanged -= OnJobPropertyChanged;   // 防重复订阅
            job.PropertyChanged += OnJobPropertyChanged;
        }

        void OnJobPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(JobEntry.Status)) return;
            var job = (JobEntry)sender;

            if (job.IsTerminal)
                Post(() => _worker.TrimTerminal(TerminalKeepCap));   // 终态卡片自动回收
            if (SortMode == JobOrder.StatusFirst)
                RepositionJob(job);                                  // "按状态"下状态一变就要挪位置

            RefreshSummary();

            TryOpenOutput(job);
        }

        // 整批跑完才忘掉开过的目录：批内清登记会让同一目录被后面的档案再弹一次
        void OnJobFinished(JobEntry job)
        {
            if (_worker.IsIdle) lock (_openGate) _openedDirs.Clear();
        }

        void TryOpenOutput(JobEntry job)
        {
            if (job.Status != JobStatus.Success) return;
            if (!_settings.Current.AutoOpenAfterExtract) return;
            var dir = job.OutputDir;
            if (string.IsNullOrEmpty(dir)) return;
            lock (_openGate)
            {
                if (!_openedDirs.Add(NormalizeDir(dir))) return;
            }
            OpenDirectory?.Invoke(dir);
        }

        // 结尾分隔符与大小写不同不等于两个目录
        static string NormalizeDir(string dir)
        {
            try { return Path.GetFullPath(dir).TrimEnd('\\', '/'); }
            catch { return dir.TrimEnd('\\', '/'); }
        }

        // 三个计数是按钮可用性与横幅的唯一数据源，全部在 UI 线程刷新。
        // 一批 200 个作业连着收尾时，每个状态变化都要刷一次会变成 200 次全表扫描 —— 合并成一次排队。
        readonly Coalescer _summaryPending = new Coalescer();

        void RefreshSummary()
        {
            if (!_summaryPending.TryRequest()) return;
            Post(() =>
            {
                _summaryPending.Complete();
                RefreshSummaryCore();
            });
        }

        void RefreshSummaryCore()
        {
            int failed = 0, finished = 0, active = 0;
            lock (_mirrorGate)
            {
                foreach (var j in _jobs)
                {
                    switch (j.Status)
                    {
                        case JobStatus.Failed: failed++; break;
                        case JobStatus.Success:
                        case JobStatus.Cancelled: finished++; break;
                        default: active++; break;
                    }
                }
            }
            FailedCount = failed;
            FinishedCount = finished;
            ActiveCount = active;
        }
    }
}
