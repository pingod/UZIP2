using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
    // 图标外圈有一枚进度环：一圈 = 100%，整批跑完后补满一圈停两秒再收起。
    public partial class MiniPuckWindow : Window
    {
        const double RingSize = 56;
        const double RingThickness = 4;
        static readonly TimeSpan FinishFlash = TimeSpan.FromSeconds(2);

        private readonly ISettingsService _settings;
        private readonly ArchiveWorker _worker;
        private readonly MainWindow _main;
        private HomeViewModel Vm => (HomeViewModel)DataContext;
        private bool _suppressSave;
        private readonly DispatcherTimer _statusTimer;
        private readonly DispatcherTimer _ringTimer;
        private readonly Action<JobEntry> _onFinished;
        private readonly Action<AppSettings> _onSettings;
        private readonly NotifyCollectionChangedEventHandler _onJobsChanged;
        private readonly PropertyChangedEventHandler _onJobChanged;
        private readonly Dictionary<JobEntry, PropertyChangedEventHandler> _hooked =
            new Dictionary<JobEntry, PropertyChangedEventHandler>();

        private volatile bool _ringDirty;
        private bool _hadActive;
        private bool _dragIn;
        private string _lastDragSig;
        private DispatcherTimer _leaveTimer;
        private DateTime _flashUntil;

        public MiniPuckWindow(MainWindow main)
        {
            _main = main;
            InitializeComponent();
            _settings = App.Services.GetRequiredService<ISettingsService>();
            _worker = App.Services.GetRequiredService<ArchiveWorker>();
            DataContext = App.Services.GetRequiredService<HomeViewModel>();

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statusTimer.Tick += (_, __) => { _statusTimer.Stop(); Hint.Text = IdleHint(); };

            // 进度来自解压线程，直接刷 UI 会一个包几百次重绘；这里只置脏，由 200ms 心跳统一重画
            _ringTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _ringTimer.Tick += (_, __) => TickRing();
            _onJobChanged = (_, e) =>
            {
                if (e.PropertyName == nameof(JobEntry.Percent) || e.PropertyName == nameof(JobEntry.Status))
                    _ringDirty = true;
            };
            _onJobsChanged = (s, e) =>
            {
                TrackJobs(e);
                RefreshRing();
            };

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
            ((INotifyCollectionChanged)_worker.Jobs).CollectionChanged += _onJobsChanged;
            foreach (var job in _worker.Jobs) HookJob(job);
            Vm.PropertyChanged += OnVmChanged;

            RestorePosition();
            RefreshMode();
            UpdateBusy();
        }

        // 只跟踪活跃作业：终态卡片不会再动进度，订阅它们只会白唤醒刷新
        void HookJob(JobEntry job)
        {
            if (job == null || _hooked.ContainsKey(job)) return;
            var handler = _onJobChanged;
            job.PropertyChanged += handler;
            _hooked[job] = handler;
        }

        void UnhookJob(JobEntry job)
        {
            if (job == null) return;
            if (_hooked.TryGetValue(job, out var handler))
            {
                job.PropertyChanged -= handler;
                _hooked.Remove(job);
            }
        }

        void TrackJobs(NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (JobEntry job in e.OldItems) UnhookJob(job);
            if (e.NewItems != null)
                foreach (JobEntry job in e.NewItems) HookJob(job);
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var job in _hooked.Keys.ToList()) UnhookJob(job);
                foreach (var job in _worker.Jobs) HookJob(job);
            }
        }

        string IdleHint()
            => PuckProgress.ResolveHint(previewing: false, runningCount: CountActive(), percent: null, finishing: false);

        int CountActive()
            => _worker.Jobs.Count(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running);

        void UpdateBusy()
        {
            Pulse.Visibility = CountActive() > 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshRing();
            if (!_statusTimer.IsEnabled && !IsRingBusy()) Hint.Text = IdleHint();
        }

        bool IsRingBusy() => _hadActive || _flashUntil != default;

        void TickRing()
        {
            if (_flashUntil != default || _ringDirty)
            {
                _ringDirty = false;
                RefreshRing();
            }
        }

        // 环与提示位共用一次判定：有活跃作业就画进度，刚跑完补满一圈停两秒，
        // 闪示结束必须连环带文案一起收回空闲态（否则提示永远卡在"完成"）。
        void RefreshRing()
        {
            var overall = PuckProgress.Overall(_worker.Jobs);
            if (overall.HasValue)
            {
                _hadActive = true;
                Paint(overall, finishing: false);
                if (!_ringTimer.IsEnabled) _ringTimer.Start();
                return;
            }
            if (_hadActive)
            {
                _hadActive = false;
                _flashUntil = DateTime.Now.Add(FinishFlash);
                Paint(100, finishing: true);
                if (!_ringTimer.IsEnabled) _ringTimer.Start();
                return;
            }
            if (_flashUntil != default)
            {
                if (DateTime.Now < _flashUntil)
                {
                    Paint(100, finishing: true);   // 闪示期间保持满环
                    return;
                }
                _flashUntil = default;
            }
            Paint(null, finishing: false);
            _ringTimer.Stop();
        }

        void Paint(double? percent, bool finishing)
        {
            var geometry = PuckProgress.RingGeometry(percent, RingSize, RingThickness);
            if (geometry == null) ClearRing();
            else
            {
                Ring.Data = geometry;
                Ring.Visibility = Visibility.Visible;
            }
            // 拖拽悬停时提示位是包内容预览，别用进度盖掉它
            Hint.Text = PuckProgress.ResolveHint(_dragIn, CountActive(), percent, finishing, Vm.PreviewText);
        }

        void ClearRing()
        {
            Ring.Data = null;
            Ring.Visibility = Visibility.Collapsed;
        }

        void RestorePosition()
        {
            var s = _settings.Current;
            _suppressSave = true;
            // 按整个虚拟桌面夹，不是按主屏工作区——否则拖到副屏的位置一重启就被拽回来。
            // WorkArea / VirtualScreen* 都是设备无关单位，和 Left/Top 同一坐标系。
            var p = PuckPlacement.Resolve(s.PuckLeft, s.PuckTop, Width, Height,
                SystemParameters.WorkArea, new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
            Left = p.X;
            Top = p.Y;
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

        // 指针在方块内子元素（环、图标、提示位）和窗口透明边界之间移动，
        // WPF 会连续抛出成串的假 DragLeave → DragEnter/Over。旧实现每帧重算预览、
        // 每次 leave 立刻把提示从"长(换行)"切回"短(单行)"，居中布局随之反复回流，
        // 图标就上下抖动。三处一起改：
        //  1. 预览只在拖拽内容真正变化时重算（同一批次文件集合恒定，避免每帧 File.Exists）；
        //  2. leave 加宽限 debounce，宽限期内又进入/悬停就取消，真正的离开才收起；
        //  3. 拖放目标统一放到窗口一层，消除 Border/Window 两层 AllowDrop 的进入/离开抖动。
        const int LeaveDebounceMs = 60;

        void OnDragOverFiles(object sender, DragEventArgs e)
        {
            bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
            if (hasFiles) UpdatePreviewIfChanged(e);
            e.Handled = true;
        }

        void OnDragEnterFiles(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) UpdatePreviewIfChanged(e);
        }

        void OnDragLeaveFiles(object sender, DragEventArgs e)
        {
            // 宽限计时已在跑（子元素间抖动产生的连续 leave）就不重复排：
            // 只有宽限期内没有任何新进入/悬停，才真正收起预览。
            if (_leaveTimer != null) return;
            _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LeaveDebounceMs) };
            _leaveTimer.Tick += (_, __) =>
            {
                _leaveTimer.Stop();
                _leaveTimer = null;
                _dragIn = false;
                _lastDragSig = null;
                Vm.ClearPreview();
                if (_flashUntil == default && !_hadActive) Hint.Text = IdleHint();
            };
            _leaveTimer.Start();
        }

        // 取消在途宽限（新的进入/悬停说明指针还没真正离开）
        void CancelPendingLeave()
        {
            if (_leaveTimer != null)
            {
                _leaveTimer.Stop();
                _leaveTimer = null;
            }
        }

        // 只有拖拽的文件集合变化才重算预览；同一批次路径恒定，用签名去重。
        // 签名用字符串拼接（无 IO），重算才走 ShowPreview（会逐文件 File.Exists）。
        void UpdatePreviewIfChanged(DragEventArgs e)
        {
            CancelPendingLeave();
            var files = TryGetFiles(e);
            if (files.Length == 0) return;
            string sig = string.Join("\u0001", files);
            if (sig == _lastDragSig) return;
            _lastDragSig = sig;
            _dragIn = true;
            Vm.ShowPreview(files);
            Hint.Text = Vm.PreviewText;
        }

        void OnDropFiles(object sender, DragEventArgs e)
        {
            CancelPendingLeave();
            var files = TryGetFiles(e);
            _dragIn = false;
            _lastDragSig = null;
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
            _ringTimer.Stop();
            CancelPendingLeave();
            _worker.JobFinished -= _onFinished;
            _settings.Changed -= _onSettings;
            ((INotifyCollectionChanged)_worker.Jobs).CollectionChanged -= _onJobsChanged;
            foreach (var job in _hooked.Keys.ToList()) UnhookJob(job);
            Vm.PropertyChanged -= OnVmChanged;
            base.OnClosing(e);
        }
    }
}
