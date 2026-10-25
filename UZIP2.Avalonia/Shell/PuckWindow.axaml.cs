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
        // 已经成功让 Hyprland 把方块转成浮动窗口了吗。Opened 触发时窗口可能还没进
        // hyprctl 的 clients 列表（映射晚于 Opened），那一次 Float 会落空，所以要重试。
        private bool _floated;

        public PuckWindow()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += (_, __) => Refresh();
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
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
            // 先让它进浮动层，再谈摆位：对平铺窗口发 move 是没意义的（会立刻被布局树拉回去）。
            ApplyHyprlandFloat();
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
        /// 让 Hyprland 把方块改成浮动窗口。
        /// Wayland 的 xdg-shell 里客户端既不能声明「我是浮动窗口」，也不能指定绝对坐标 ——
        /// 这两件事合成器说了算。Hyprland 的 dwindle 布局会把 112×112 的方块当成普通窗口
        /// 塞进布局树，拉伸成整块（实测 1268×688，内容跟着糊）。唯一的办法是走它的 IPC：
        /// 先 float 进位浮动层，再 move 到目标点。实测做完之后窗口立刻变回 112×112。
        /// </summary>
        private void ApplyHyprlandFloat()
        {
            if (_floated || !HyprlandWindowHelper.IsAvailable) return;
            _floated = HyprlandWindowHelper.Float();
            if (!_floated)
            {
                // 找不到窗口（还没被合成器收进 clients 列表）就静默等下一轮追踪再试，
                // 只有追踪跑完还没成功才值得记一条警告。
                AppHost.Logger?.Info("Hyprland 浮动窗口设定暂未生效，下一轮重试");
            }
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
                if (!_floated && HyprlandWindowHelper.IsAvailable)
                {
                    AppHost.Logger?.Warn("Hyprland 浮动窗口设定失败，方块可能仍被平铺拉伸");
                }
                return;
            }
            // 映射晚于 Opened 的窗口第一轮多半找不到，跟着追踪一起补。
            ApplyHyprlandFloat();
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
            // Wayland 下上一行其实没用（合成器不认客户端坐标）。Hyprland 靠 IPC 摆位。
            if (HyprlandWindowHelper.IsAvailable) HyprlandWindowHelper.MoveTo(target.X, target.Y);
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
            if (!TryReadPosition(out int x, out int y)) return;
            var s = AppHost.Settings.Current;
            if (s.PuckLeft == x && s.PuckTop == y) return;
            AppHost.Settings.Save(next => { next.PuckLeft = x; next.PuckTop = y; });
        }

        /// <summary>
        /// 读出方块此刻的真实坐标。
        /// Wayland 下 <c>Window.Position</c> 是假的：xdg-shell 不告诉客户端绝对位置，Avalonia
        /// 只能给 0,0（实测拖动后方块明明在 2198,1462，读回来还是 0,0）。如果照它存盘，
        /// 用户挪过方块后设置里就被写成 0,0，下次启动方块跑到左上角。所以 Wayland 上改问
        /// 合成器要；问不到就不存（宁可保留旧值，也不要写错值）。
        /// </summary>
        private bool TryReadPosition(out int x, out int y)
        {
            x = 0;
            y = 0;
            if (HyprlandWindowHelper.IsAvailable && HyprlandWindowHelper.TryGetPosition(out x, out y)) return true;

            // 非 Wayland（X11）：Position 是可信的。
            var pos = Position;
            if (pos.X == 0 && pos.Y == 0) return false;
            x = pos.X;
            y = pos.Y;
            return true;
        }

        // ---- 交互 ----

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            // 用户开始拖 = 别再来纠正位置了，否则追踪定时器会把窗口从用户手里拽回去。
            StopChase();
            try { BeginMoveDrag(e); } catch { }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            // 用户刚拖完方块。Wayland 下 Position 不会给出新坐标（xdg-shell 不告诉客户端
            // 绝对位置），而且拖动结束时 PositionChanged 也可能压根不触发，所以在这里
            // 主动读一次真实位置存盘。BeginMoveDrag 是异步的交互式移动，松手时窗口已经落位。
            SavePosition();
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            // 指针拖进来又滑出去（没松手）时，把提示还原。少了这一步，提示会永远停在
            // 「松手解压 / 压缩」，即使文件早就被拖走了。
            _dragIn = false;
            e.Handled = true;
            Refresh();
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            _dragIn = e.DataTransfer.Contains(DataFormat.File);
            e.DragEffects = _dragIn ? DragDropEffects.Copy : DragDropEffects.None;
            if (_dragIn) HintText.Text = "松手解压 / 压缩";
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            _dragIn = false;
            var paths = e.DataTransfer.TryGetFiles()?
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
