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
        private readonly HashSet<long> _autoOpened = new HashSet<long>();
        private readonly Dispatcher _ui = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;

        public HomeViewModel(ArchiveWorker worker, ISettingsService settings, SevenZipClient zip,
            PasswordService passwords, ClipboardService clipboard)
        {
            _worker = worker;
            _settings = settings;
            _zip = zip;
            _passwords = passwords;
            _clipboard = clipboard;
            _mode = settings.Current.AppMode;
            _sevenZipMissing = zip.SevenZipPath == null;
            _paperCount = passwords.Paper.Count;
            passwords.Changed += () => _ui.Invoke(() => PaperCount = passwords.Paper.Count);
            ((INotifyCollectionChanged)_worker.Jobs).CollectionChanged += OnJobsChanged;
            foreach (var job in _worker.Jobs) HookJob(job);
        }

        public ReadOnlyObservableCollection<JobEntry> Jobs => _worker.Jobs;

        [ObservableProperty] private int _mode;
        [ObservableProperty] private string _previewText = "";
        [ObservableProperty] private bool _previewIsWarning;
        [ObservableProperty] private bool _isDragging;
        [ObservableProperty] private bool _sevenZipMissing;
        [ObservableProperty] private int _paperCount;
        [ObservableProperty] private int _failedCount;

        public bool HasFailures => FailedCount > 0;

        partial void OnFailedCountChanged(int value)
        {
            OnPropertyChanged(nameof(HasFailures));
            RetryAllFailedCommand.NotifyCanExecuteChanged();
            ExportFailuresCommand.NotifyCanExecuteChanged();
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
            IsDragging = true;
            var p = PreviewFor(Mode, files ?? Array.Empty<string>());
            PreviewText = p.Text;
            PreviewIsWarning = p.IsWarning;
        }

        public void ClearPreview()
        {
            IsDragging = false;
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
                    if (archives.Count == 1 && _settings.Current.PreviewBeforeExtract) OpenPreview(archives[0]);
                    else if (archives.Count > 0) _worker.EnqueueExtract(archives);
                    break;
                case 2:
                    _worker.EnqueueCompress(list);
                    break;
                default:
                    if (archives.Count == 1 && _settings.Current.PreviewBeforeExtract) OpenPreview(archives[0]);
                    else if (archives.Count > 0) _worker.EnqueueExtract(archives);
                    if (others.Count > 0) _worker.EnqueueCompress(others);
                    break;
            }
        }

        void OpenPreview(string archive)
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
            var mw = System.Windows.Application.Current?.MainWindow;
            var win = new UZIP2.Shell.PreviewWindow(job.Archive, job.UsedPassword, _worker)
            {
                Owner = mw != null && mw.IsVisible ? mw : null
            };
            win.ShowDialog();
        }

        [RelayCommand]
        void RetryJob(JobEntry job)
        {
            if (job != null) _worker.Retry(job);
        }

        // 一次卡多个包失败时，逐个点重试太慢；批量重跑一遍（密码链会重新走）
        [RelayCommand(CanExecute = nameof(HasFailures))]
        void RetryAllFailed() => _worker.RetryAllFailed();

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

        // ---- 批次完成（旧独立结果窗口的数据面） ----

        readonly List<JobEntry> _finished = new List<JobEntry>();

        public event Action<IReadOnlyList<JobEntry>> BatchFinished;

        void TrackCompletion(JobEntry job)
        {
            switch (job.Status)
            {
                case JobStatus.Success:
                case JobStatus.Failed:
                case JobStatus.Cancelled:
                    if (!_finished.Contains(job)) _finished.Add(job);
                    break;
            }
            if (_finished.Count == 0) return;
            foreach (var j in _worker.Jobs)
                if (j.Status == JobStatus.Queued || j.Status == JobStatus.Running) return;
            var batch = _finished.ToArray();
            _finished.Clear();
            BatchFinished?.Invoke(batch);
        }

        // ---- 成功后自动打开输出目录 ----

        void OnJobsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (JobEntry job in e.NewItems) HookJob(job);
            if (e.OldItems != null)
                foreach (JobEntry job in e.OldItems) job.PropertyChanged -= OnJobPropertyChanged;
            RefreshFailedCount();
        }

        void HookJob(JobEntry job)
        {
            job.PropertyChanged += OnJobPropertyChanged;
        }

        void OnJobPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(JobEntry.Status)) return;
            var job = (JobEntry)sender;
            TrackCompletion(job);
            if (job.Status == JobStatus.Failed || job.Status == JobStatus.Success
                || job.Status == JobStatus.Cancelled)
                RefreshFailedCount();
            if (job.Status != JobStatus.Success) return;
            if (!_settings.Current.AutoOpenAfterExtract) return;
            if (string.IsNullOrEmpty(job.OutputDir) || !_autoOpened.Add(job.Id)) return;
            OpenInExplorer(job.OutputDir);
        }

        void RefreshFailedCount()
        {
            // 状态变更来自后台作业线程：计数和命令可用性都只能在 UI 线程动
            if (!_ui.CheckAccess())
            {
                _ui.BeginInvoke(new Action(RefreshFailedCount));
                return;
            }
            int n = 0;
            foreach (var j in Jobs)
                if (j.Status == JobStatus.Failed) n++;
            FailedCount = n;
        }
    }
}
