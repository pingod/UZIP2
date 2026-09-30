using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using UZIP2.Models;
using UZIP2.Services;
using UZIP2.ViewModel;

namespace UZIP2.Shell
{
    // 迷你拖拽方块: 桌面上一枚置顶小方块，文件拖上去即走主窗口那套解压/压缩路由。
    public partial class MiniPuckWindow : Window
    {
        private readonly ISettingsService _settings;
        private readonly ArchiveWorker _worker;
        private readonly MainWindow _main;
        private HomeViewModel Vm => (HomeViewModel)DataContext;
        private bool _suppressSave;
        private readonly DispatcherTimer _statusTimer;
        private readonly Action<JobEntry> _onFinished;
        private readonly Action<AppSettings> _onSettings;

        public MiniPuckWindow(MainWindow main)
        {
            _main = main;
            InitializeComponent();
            _settings = App.Services.GetRequiredService<ISettingsService>();
            _worker = App.Services.GetRequiredService<ArchiveWorker>();
            DataContext = App.Services.GetRequiredService<HomeViewModel>();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (_, __) => { _statusTimer.Stop(); Hint.Text = IdleHint(); };

            _onFinished = _ => Dispatcher.Invoke(UpdateBusy);
            // 主窗口的 Changed 处理器可能已经先一步关掉了我们，这里允许重复关闭
            _onSettings = s => Dispatcher.Invoke(() =>
            {
                if (s != null && !s.MiniPuck)
                {
                    try { Close(); } catch { }
                }
            });
            _worker.JobFinished += _onFinished;
            _settings.Changed += _onSettings;
            Vm.PropertyChanged += OnVmChanged;

            RestorePosition();
            RefreshMode();
            UpdateBusy();
        }

        string IdleHint()
        {
            int running = CountActive();
            return running > 0 ? running + " 个任务" : "拖到这里";
        }

        int CountActive()
            => _worker.Jobs.Count(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running);

        void UpdateBusy()
        {
            Pulse.Visibility = CountActive() > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_statusTimer.IsEnabled) Hint.Text = IdleHint();
        }

        void RestorePosition()
        {
            var wa = SystemParameters.WorkArea;
            var s = _settings.Current;
            _suppressSave = true;
            if (s.PuckLeft >= 0 && s.PuckTop >= 0)
            {
                Left = Math.Min(s.PuckLeft, wa.Right - Width);
                Top = Math.Min(s.PuckTop, wa.Bottom - Height);
            }
            else
            {
                Left = wa.Right - Width - 24;
                Top = wa.Bottom - Height - 96;
            }
            _suppressSave = false;
        }

        void SavePosition()
        {
            if (_suppressSave) return;
            _settings.Save(s => { s.PuckLeft = Left; s.PuckTop = Top; });
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ClickCount >= 2) { ShowMain(); return; }
            try { DragMove(); } catch { }
            SavePosition();
        }

        void OnDragOverFiles(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            e.Effects = DragDropEffects.Copy;
            Vm.ShowPreview(TryGetFiles(e));
            Hint.Text = Vm.PreviewText;
            e.Handled = true;
        }

        void OnDragLeaveFiles(object sender, DragEventArgs e)
        {
            Vm.ClearPreview();
            Hint.Text = IdleHint();
        }

        void OnDropFiles(object sender, DragEventArgs e)
        {
            var files = TryGetFiles(e);
            Vm.ClearPreview();
            if (files.Length == 0)
            {
                Hint.Text = "没有可处理的文件";
                _statusTimer.Start();
                return;
            }
            Vm.DropFilesCommand.Execute(files);
            Hint.Text = "已排入 " + files.Length + " 项";
            _statusTimer.Start();
            UpdateBusy();
            e.Handled = true;
        }

        static string[] TryGetFiles(DragEventArgs e)
        {
            try
            {
                return e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        void RefreshMode()
        {
            int m = Vm.Mode;
            ModeAuto.IsChecked = m == 0;
            ModeExtract.IsChecked = m == 1;
            ModeCompress.IsChecked = m == 2;
        }

        void OnModeAuto(object sender, RoutedEventArgs e) { Vm.Mode = 0; RefreshMode(); }
        void OnModeExtract(object sender, RoutedEventArgs e) { Vm.Mode = 1; RefreshMode(); }
        void OnModeCompress(object sender, RoutedEventArgs e) { Vm.Mode = 2; RefreshMode(); }

        void OnShowMain(object sender, RoutedEventArgs e) => ShowMain();

        void OnHideMain(object sender, RoutedEventArgs e) => _main.Hide();

        void OnClosePuck(object sender, RoutedEventArgs e)
        {
            _settings.Save(s => s.MiniPuck = false);
            // 方块是唯一可见界面时，关掉它会把程序整个藏进托盘，这里把主窗口带回来
            if (!_main.IsVisible) _main.ShowFromTray();
        }

        void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HomeViewModel.Mode)) RefreshMode();
        }

        void ShowMain() => _main.ShowFromTray();

        protected override void OnClosing(CancelEventArgs e)
        {
            _statusTimer.Stop();
            _worker.JobFinished -= _onFinished;
            _settings.Changed -= _onSettings;
            Vm.PropertyChanged -= OnVmChanged;
            base.OnClosing(e);
        }
    }
}
