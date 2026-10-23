using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell
{
    // 桌面迷你方块：置顶无边框，文件拖上去即入队；外圈进度环一圈 = 100%。
    public partial class PuckWindow : Window
    {
        const double RingSize = 56;
        const double RingThickness = 4;
        // 位置恢复要在合成器摆完窗口后再抢几次，50ms × 20 ≈ 1 秒足够，超时就认了。
        const int MaxRestoreTries = 20;
        static readonly TimeSpan FinishFlash = TimeSpan.FromSeconds(2);

        private readonly DispatcherTimer _timer;
        private DispatcherTimer _chase;
        private PixelPoint? _target;
        private int _tries;
        private bool _hadActive;
        private bool _dragIn;
        private DateTime _flashUntil;
        private bool _restoring;

        public PuckWindow()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += (_, __) => Refresh();
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
            PositionChanged += (_, __) => SavePosition();
            Opened += (_, __) => { RestorePosition(); Refresh(); };
            Closed += (_, __) => { _timer.Stop(); _chase?.Stop(); };
            Refresh();
        }

        // ---- 位置 ----

        private void RestorePosition()
        {
            var s = AppHost.Settings.Current;
            var primary = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            _target = ResolveRestorePoint(s.PuckLeft, s.PuckTop, primary);
            if (_target == null)
            {
                // 从没记录过位置：给个主屏右下角的默认落点。
                _target = new PixelPoint(primary.Right - 136, primary.Bottom - 200);
            }

            _tries = 0;
            ApplyTarget();
            // 窗口还没映射时设的坐标会被丢掉，落点改由合成器决定（实测设 800,600 读回 0,0，
            // 最终停在 2240,600）。合成器什么时候摆完窗口没有可靠的信号，所以这里在 Opened
            // 之后的一小段时间里追着目标坐标反复纠正，稳定后放手；用户一动就立刻停止追踪。
            _chase ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _chase.Tick -= OnChaseTick;
            _chase.Tick += OnChaseTick;
            _chase.Start();
        }

        /// <summary>
        /// 把设置里的方块坐标换算成实际该落的像素点。返回 null 表示没有记录过位置，
        /// 交给调用方放到主屏右下角。抽成 static 是为了让无头测试能直接验算这些边界。
        /// </summary>
        internal static PixelPoint? ResolveRestorePoint(double left, double top, PixelRect primary)
        {
            if (left < 0 || top < 0) return null;
            var virtualDesktop = new Rect(0, 0, Math.Max(primary.Right, (int)left + 112),
                                          Math.Max(primary.Bottom, (int)top + 112));
            var p = UZIP2.Services.PuckPlacement.Resolve(left, top, 112, 112,
                new ScreenRect(primary.X, primary.Y, primary.Width, primary.Height),
                new ScreenRect(virtualDesktop.X, virtualDesktop.Y, virtualDesktop.Width, virtualDesktop.Height));
            return new PixelPoint((int)p.X, (int)p.Y);
        }

        void OnChaseTick(object sender, EventArgs e)
        {
            if (_target == null || ++_tries > MaxRestoreTries)
            {
                StopChase();
                return;
            }
            ApplyTarget();
        }

        void StopChase()
        {
            _chase?.Stop();
            _target = null;
        }

        void ApplyTarget()
        {
            if (_target is not { } target) return;
            _restoring = true;
            Position = target;
            _restoring = false;
        }

        // internal 而非 private：让无头测试能直接驱动它，验证「坐标没变就不写盘」。
        internal void SavePosition()
        {
            if (_restoring) return;
            // PositionChanged 不只由用户拖动触发：合成器每次重新摆放窗口、布局重排都会再抛一次，
            // 而且常常给的是同一条坐标。SettingsService.Save 是「无条件写盘 + 全量 Changed 广播」，
            // 所以值没变就不该调它——否则挪一次方块就能让 settings.json 被重写几百遍，
            // 订阅方（App.ApplyWindowSettings）也跟着跑几百遍。
            int x = Position.X, y = Position.Y;
            var s = AppHost.Settings.Current;
            if (s.PuckLeft == x && s.PuckTop == y) return;
            AppHost.Settings.Save(next => { next.PuckLeft = x; next.PuckTop = y; });
        }

        // ---- 交互 ----

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            try { BeginMoveDrag(e); } catch { }
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            _dragIn = e.Data.Contains(DataFormats.Files);
            e.DragEffects = _dragIn ? DragDropEffects.Copy : DragDropEffects.None;
            if (_dragIn) HintText.Text = "松手解压 / 压缩";
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            _dragIn = false;
            var paths = e.Data.GetFiles()?
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray() ?? Array.Empty<string>();
            if (paths.Length > 0) FileRouter.EnqueueAuto(paths);
            e.Handled = true;
            Refresh();
        }

        // ---- 进度环 ----

        private void Refresh()
        {
            var jobs = AppHost.Worker.Jobs;
            var overall = PuckProgress.Overall(jobs);
            if (overall.HasValue)
            {
                _hadActive = true;
                Paint(overall.Value, finishing: false);
                if (!_timer.IsEnabled) _timer.Start();
                return;
            }
            if (_hadActive)
            {
                _hadActive = false;
                _flashUntil = DateTime.Now.Add(FinishFlash);
                Paint(100, finishing: true);
                if (!_timer.IsEnabled) _timer.Start();
                return;
            }
            if (_flashUntil != default)
            {
                if (DateTime.Now < _flashUntil) { Paint(100, finishing: true); return; }
                _flashUntil = default;
            }
            Paint(null, finishing: false);
            _timer.Stop();
        }

        private void Paint(double? percent, bool finishing)
        {
            var geo = PuckRingGeometry.Build(percent, RingSize, RingThickness);
            RingPath.Data = geo;
            RingPath.IsVisible = geo != null;

            int active = AppHost.Worker.Jobs.Count(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running);
            if (!_dragIn) HintText.Text = PuckProgress.ResolveHint(false, active, percent, finishing);
        }
    }
}
