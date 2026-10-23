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
        static readonly TimeSpan FinishFlash = TimeSpan.FromSeconds(2);

        private readonly DispatcherTimer _timer;
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
            Closed += (_, __) => _timer.Stop();
            Refresh();
        }

        // ---- 位置 ----

        private void RestorePosition()
        {
            var s = AppHost.Settings.Current;
            if (s.PuckLeft < 0 || s.PuckTop < 0)
            {
                var area = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
                Position = new PixelPoint(area.Right - 136, area.Bottom - 200);
                return;
            }
            var primary = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            var virtualDesktop = new Rect(0, 0, Math.Max(primary.Right, (int)s.PuckLeft + 112),
                                          Math.Max(primary.Bottom, (int)s.PuckTop + 112));
            var p = UZIP2.Services.PuckPlacement.Resolve(s.PuckLeft, s.PuckTop, 112, 112,
                new ScreenRect(primary.X, primary.Y, primary.Width, primary.Height),
                new ScreenRect(virtualDesktop.X, virtualDesktop.Y, virtualDesktop.Width, virtualDesktop.Height));
            _restoring = true;
            Position = new PixelPoint((int)p.X, (int)p.Y);
            _restoring = false;
        }

        private void SavePosition()
        {
            if (_restoring) return;
            AppHost.Settings.Save(s => { s.PuckLeft = Position.X; s.PuckTop = Position.Y; });
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
