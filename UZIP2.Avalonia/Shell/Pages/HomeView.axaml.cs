using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell.Pages
{
    public partial class HomeView : UserControl
    {
        public HomeView()
        {
            InitializeComponent();
            JobsList.ItemsSource = AppHost.Worker.Jobs;
            ((INotifyCollectionChanged)AppHost.Worker.Jobs).CollectionChanged += (_, __) => UpdateSummary();
            AppHost.Worker.JobFinished += _ => Dispatcher.UIThread.Post(UpdateSummary);

            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            UpdateSummary();
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

        private void OnClearDone(object sender, RoutedEventArgs e)
        {
            AppHost.Worker.RemoveTerminal(j => j.Status == JobStatus.Success || j.Status == JobStatus.Cancelled);
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
