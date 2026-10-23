using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell.Pages
{
    /// <summary>
    /// 状态 → 是否显示：命中文档里的任一状态即为 true。
    /// 对齐 WPF 的 AnyVisibility / EqualVisibility（Avalonia 11 没有 DataTrigger，也没有多值可见性）。
    /// </summary>
    public sealed class JobStatusVisibilityConverter : IValueConverter
    {
        /// <summary>逗号分隔的状态名单，如 "Failed,Cancelled"。</summary>
        public string Statuses { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is JobStatus status) || string.IsNullOrEmpty(Statuses)) return false;
            foreach (var part in Statuses.Split(','))
            {
                var name = part.Trim();
                if (name.Length > 0 && string.Equals(name, status.ToString(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// 作业类型 → 是否显示：命中任一类型即为 true。
    /// 对齐 WPF 的 EqualVisibility / AnyVisibility 用在 Kind 上的写法（如「预览」只在解压作业上出现）。
    /// </summary>
    public sealed class JobKindVisibilityConverter : IValueConverter
    {
        /// <summary>逗号分隔的类型名单，如 "Extract"。</summary>
        public string Kinds { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var kind = value as string;
            if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(Kinds)) return false;
            foreach (var part in Kinds.Split(','))
            {
                var name = part.Trim();
                if (name.Length > 0 && string.Equals(name, kind, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public partial class HomeView : UserControl
    {
        // 排序用的镜像列表：Worker.Jobs 本身是「按入队顺序追加」的只读视图，
        // 而设置里的 JobSort 要求换个顺序展示。WPF 端为此维护了一份 mirror
        // （HomeViewModel.cs 的 RebuildMirror / RepositionJob），这里对齐同一套语义。
        private readonly System.Collections.ObjectModel.ObservableCollection<JobEntry> _sorted =
            new System.Collections.ObjectModel.ObservableCollection<JobEntry>();
        private int _sortMode;

        public HomeView()
        {
            InitializeComponent();
            JobsList.ItemsSource = _sorted;
            _sortMode = ClampSort(AppHost.Settings.Current.JobSort);
            RebuildSorted();
            ((INotifyCollectionChanged)AppHost.Worker.Jobs).CollectionChanged += OnJobsChanged;
            AppHost.Worker.JobFinished += _ => Dispatcher.UIThread.Post(UpdateSummary);
            AppHost.Settings.Changed += OnSettingsChanged;

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            UpdateSummary();
        }

        static int ClampSort(int sort)
            => sort >= JobOrder.AddOrder && sort <= JobOrder.ByName ? sort : JobOrder.AddOrder;

        /// <summary>设定的排序变了就整体重排（设置页保存 JobSort 后立即生效，不用切换页面）。</summary>
        private void OnSettingsChanged(AppSettings s)
        {
            var mode = ClampSort(s.JobSort);
            if (mode == _sortMode) return;
            _sortMode = mode;
            Dispatcher.UIThread.Post(() => { RebuildSorted(); UpdateSummary(); });
        }

        private void RebuildSorted()
        {
            var list = AppHost.Worker.Jobs.ToList();
            list.Sort((a, b) => JobOrder.Compare(a, b, _sortMode));
            _sorted.Clear();
            foreach (var job in list) { HookJob(job); _sorted.Add(job); }
        }

        private void OnJobsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    foreach (JobEntry job in e.NewItems)
                    {
                        HookJob(job);
                        if (!_sorted.Contains(job))
                            _sorted.Insert(JobOrder.InsertIndex(_sorted, job, _sortMode), job);
                    }
                    break;
                case NotifyCollectionChangedAction.Remove:
                    foreach (JobEntry job in e.OldItems)
                    {
                        job.PropertyChanged -= OnJobPropertyChanged;
                        _sorted.Remove(job);
                    }
                    break;
                default:
                    RebuildSorted();
                    break;
            }
            UpdateSummary();
        }

        /// <summary>「按状态」下卡片状态一变就要挪位置；其他模式顺序不受状态影响。</summary>
        private void Reposition(JobEntry job)
        {
            if (_sortMode != JobOrder.StatusFirst) return;
            int cur = _sorted.IndexOf(job);
            if (cur < 0) return;
            int target = JobOrder.InsertIndex(_sorted, job, _sortMode, cur);
            if (target != cur) _sorted.Move(cur, target);
        }

        // 每张卡都订阅一次 Status 变化：既用于「按状态」重排，也用于刷新顶部统计。
        // 订阅前先减一次，避免重复挂（RebuildSorted 后旧卡片仍在列表里）。
        private void HookJob(JobEntry job)
        {
            job.PropertyChanged -= OnJobPropertyChanged;
            job.PropertyChanged += OnJobPropertyChanged;
        }

        private void OnJobPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(JobEntry.Status)) return;
            if (sender is JobEntry job) Reposition(job);
        }

        private int Mode()
            => ModeExtract.IsChecked == true ? 1 : ModeCompress.IsChecked == true ? 2 : 0;

        private void OnModeChanged(object sender, RoutedEventArgs e) { }

        private void UpdateSummary()
        {
            var jobs = AppHost.Worker.Jobs;
            int active = jobs.Count(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running);
            int failed = jobs.Count(j => j.Status == JobStatus.Failed);
            int done = jobs.Count(j => j.Status == JobStatus.Success);
            SummaryText.Text = jobs.Count == 0
                ? "队列是空的。拖入文件开始，或用上面的按钮添加。"
                : $"活动 {active} · 完成 {done} · 失败 {failed} · 共 {jobs.Count}";
        }

        /// <summary>一次性的操作回执（删除成功/失败、重试已排队），不参与统计文案。</summary>
        private void Hint(string text) => HintText.Text = text ?? "";

        // ---- 拖放 ----

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            var paths = e.Data.GetFiles()?
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray() ?? Array.Empty<string>();
            Enqueue(paths);
            e.Handled = true;
        }

        // ---- 添加 ----

        private async void OnAddFiles(object sender, RoutedEventArgs e)
            => await PickAsync("选择文件", allowMultiple: true);

        private async void OnAddArchives(object sender, RoutedEventArgs e)
            => await PickAsync("选择压缩包", allowMultiple: true);

        private async Task PickAsync(string title, bool allowMultiple)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = allowMultiple
            });
            Enqueue(files.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToArray());
        }

        private void Enqueue(string[] paths)
        {
            var list = (paths ?? Array.Empty<string>())
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .ToList();
            if (list.Count == 0) return;

            var archives = list.Where(p => File.Exists(p) && ArchiveInspector.CanExtractByExtension(p)).ToList();
            var others = list.Except(archives).ToList();

            switch (Mode())
            {
                case 1:
                    if (archives.Count == 1 && AppHost.Settings.Current.PreviewBeforeExtract) ShowPreview(archives[0]);
                    else if (archives.Count > 0) AppHost.Worker.EnqueueExtract(archives);
                    break;
                case 2:
                    AppHost.Worker.EnqueueCompress(list);
                    break;
                default:
                    if (archives.Count == 1 && AppHost.Settings.Current.PreviewBeforeExtract) ShowPreview(archives[0]);
                    else if (archives.Count > 0) AppHost.Worker.EnqueueExtract(archives);
                    if (others.Count > 0) AppHost.Worker.EnqueueCompress(others);
                    break;
            }
            UpdateSummary();
        }

        // ---- 卡片操作 ----

        private static JobEntry Job(object sender) => (sender as Control)?.DataContext as JobEntry;

        private void OnCancelJob(object sender, RoutedEventArgs e)
        {
            var job = Job(sender);
            if (job != null) AppHost.Worker.Cancel(job);
        }

        // 对齐 WPF HomeViewModel.RetryJob：把卡片重新丢回队列。
        // 这张卡上次真解开的那个口令（UsedPassword）作为人工口令带上去，省掉重走整条密码链；
        // 从来没有解开过（失败收尾时 UsedPassword 为 null）就传 null，让引擎重新试。
        private void OnRetry(object sender, RoutedEventArgs e)
        {
            var job = Job(sender);
            if (job == null) return;
            AppHost.Worker.Retry(job, string.IsNullOrEmpty(job.UsedPassword) ? null : job.UsedPassword);
            Hint($"已重新排队：{job.DisplayName}");
            UpdateSummary();
        }

        private void OnRemoveJob(object sender, RoutedEventArgs e)
        {
            var job = Job(sender);
            if (job != null) AppHost.Worker.Remove(job);
            UpdateSummary();
        }

        private void OnPreviewJob(object sender, RoutedEventArgs e)
        {
            var job = Job(sender);
            if (job != null && File.Exists(job.Archive)) ShowPreview(job.Archive);
        }

        private void OnOpenOutput(object sender, RoutedEventArgs e)
        {
            var job = Job(sender);
            if (job == null) return;
            var dir = !string.IsNullOrEmpty(job.OutputDir) ? job.OutputDir : Path.GetDirectoryName(job.Archive);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) OpenPath(dir);
        }

        private void OnConvertJob(object sender, RoutedEventArgs e)
        {
            var job = Job(sender);
            if (job == null || !File.Exists(job.Archive)) return;
            var owner = TopLevel.GetTopLevel(this) as Window;
            var dlg = new ConvertDialog(job.Archive);
            if (owner != null) dlg.ShowDialog(owner); else dlg.Show();
        }

        private void OnChecksumJob(object sender, RoutedEventArgs e)
        {
            var job = Job(sender);
            if (job == null || !File.Exists(job.Archive)) return;
            new ChecksumWindow(job.Archive).Show();
        }

        private async void OnDiffJob(object sender, RoutedEventArgs e)
        {
            var job = Job(sender);
            if (job == null || !File.Exists(job.Archive)) return;
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null) return;
            var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要对比的另一个包",
                AllowMultiple = false
            });
            var other = picked.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(other)) return;
            new DiffWindow(job.Archive, other).Show();
        }

        // 对齐 WPF HomeViewModel.DeleteSource：删的是包本身，卡片留在列表里（历史仍可查）。
        // 回收站走的是 Microsoft.VisualBasic，Linux 上必抛 PlatformNotSupportedException，
        // 所以只有 Windows 才把设置里的"删到回收站"透传下去，其它平台降级为直接删除。
        private void OnDeleteSource(object sender, RoutedEventArgs e)
        {
            var job = Job(sender);
            if (job == null || string.IsNullOrEmpty(job.Archive)) return;
            if (!File.Exists(job.Archive))
            {
                Hint($"源文件已经不在了：{Path.GetFileName(job.Archive)}");
                return;
            }

            var recycle = OperatingSystem.IsWindows() && AppHost.Settings.Current.DeleteToRecycle;
            try
            {
                var vol = ArchiveInspector.AnalyzeVolume(job.Archive);
                if (vol != null && vol.IsVolume) ArchiveInspector.DeleteVolumeSet(vol, recycle);
                else FilterService.Delete(job.Archive, recycle);
                Hint(recycle
                    ? $"已把源文件移到回收站：{Path.GetFileName(job.Archive)}"
                    : $"已删除源文件：{Path.GetFileName(job.Archive)}");
            }
            catch (Exception ex)
            {
                Hint("删除源文件失败：" + ex.Message);
            }
            UpdateSummary();
        }

        private void OnClearDone(object sender, RoutedEventArgs e)
        {
            AppHost.Worker.RemoveTerminal(j => j.Status == JobStatus.Success || j.Status == JobStatus.Cancelled);
            Hint("已清除完成 / 已取消的卡片");
            UpdateSummary();
        }

        // 对齐 WPF HomeViewModel.ClearFailed：只移除失败卡片，历史页仍能查到这些失败记录。
        private void OnClearFailed(object sender, RoutedEventArgs e)
        {
            var removed = AppHost.Worker.RemoveTerminal(j => j.Status == JobStatus.Failed);
            Hint(removed > 0 ? $"已清除 {removed} 个失败项（记录仍可在历史页查到）" : "当前没有失败的卡片");
            UpdateSummary();
        }

        private void OnCancelAll(object sender, RoutedEventArgs e) => AppHost.Worker.CancelAll();

        private void ShowPreview(string archive)
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            var win = new PreviewWindow(archive);
            if (owner != null) win.ShowDialog(owner);
            else win.Show();
        }

        internal static void OpenPath(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch { /* 打开失败不打扰主流程 */ }
        }
    }
}
