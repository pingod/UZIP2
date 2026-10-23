using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using UZIP2.Avalonia.Shell;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Avalonia
{
    public sealed class App : Application
    {
        // 失败气泡的节流：900ms 静默期攒成一条，最长等 5s 就强制报（与 WPF 端同一组参数）。
        private readonly FailureNotifier _failures = new FailureNotifier(quietMs: 900, maxWaitMs: 5000);

        private TrayIcon _tray;
        private MainWindow _main;
        private DispatcherTimer _failTimer;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppHost.Initialize();
                // 主题要在建窗口前定下来，否则第一帧会先用默认变体再跳一下（App.axaml 已不再写死）。
                ThemeApplier.Apply(AppHost.Settings.Current.Theme);
                var window = new MainWindow();
                _main = window;
                desktop.MainWindow = window;
                SetupTray(window, desktop);
                SetupFailureBalloon();

                // 设置保存后立即作用到已打开的窗口（置顶）。SettingsService.Save 在调用线程上
                // 同步触发 Changed，设置页又在 UI 线程保存，所以这里通常已经是 UI 线程；
                // 万一将来从后台线程保存，ApplyWindowSettings 里会自己切回 UI 线程。
                AppHost.Settings.Changed += ApplyWindowSettings;

                // 启动参数里带的文件（文件管理器"打开方式"）在服务就绪后入队
                if (Program.StartupFiles.Length > 0)
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() => FileRouter.EnqueueAuto(Program.StartupFiles));

                // 第二实例转发的路径
                if (Program.Bus != null)
                {
                    Program.Bus.FilesReceived += files => global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        window.ShowFromTray();
                        FileRouter.EnqueueAuto(files);
                    });
                }

                desktop.ShutdownRequested += (_, __) => AppHost.Shutdown();
                desktop.Exit += (_, __) => Teardown();
            }
            base.OnFrameworkInitializationCompleted();
        }

        // 托盘走 StatusNotifierItem：KDE/XFCE/wlroots 桌面可用，GNOME 需要 AppIndicator 扩展。
        // 不支持时静默跳过（设置页会说明原因），绝不让启动失败。
        private void SetupTray(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!AppHost.Capabilities.TraySupported) return;
            try
            {
                var show = new NativeMenuItem("显示 UZIP");
                show.Click += (_, __) => window.ShowFromTray();

                var puck = new NativeMenuItem("桌面只留方块")
                {
                    ToggleType = NativeMenuItemToggleType.CheckBox,
                    IsChecked = AppHost.Settings.Current.MiniPuck
                };
                puck.Click += (_, __) => { window.TogglePuck(); puck.IsChecked = AppHost.Settings.Current.MiniPuck; };

                var quit = new NativeMenuItem("退出");
                quit.Click += (_, __) => desktop.Shutdown();

                var menu = new NativeMenu();
                menu.Items.Add(show);
                menu.Items.Add(puck);
                menu.Items.Add(new NativeMenuItemSeparator());
                menu.Items.Add(quit);

                _tray = new TrayIcon
                {
                    Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://UZIP2.Avalonia/Assets/UZIP.ico"))),
                    ToolTipText = "UZIP2",
                    Menu = menu,
                    IsVisible = true
                };
                _tray.Clicked += (_, __) => window.ShowFromTray();
                TrayIcon.SetIcons(this, new TrayIcons { _tray });
            }
            catch (Exception ex)
            {
                AppHost.Logger?.Warn("托盘不可用: " + ex.Message);
            }
        }

        /// <summary>设置变更：把窗口级设置（目前是置顶）应用到已经打开的窗口。</summary>
        private void ApplyWindowSettings(AppSettings s)
        {
            if (s == null) return;
            try
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    ApplyWindowSettingsOnUi(s);
                }
                else
                {
                    Dispatcher.UIThread.Post(() => ApplyWindowSettingsOnUi(s));
                }
            }
            catch (Exception ex)
            {
                AppHost.Logger?.Warn("应用窗口设置失败: " + ex.Message);
            }
        }

        // 上一次真正应用过的置顶值。Settings.Changed 是「任何一次 Save 都广播、不做差异比较」
        // 的语义（SettingsService 里就是无条件 Invoke），而 Save 在拖动方块、调并行度这类
        // 高频路径上都会被调用。只记变化、不记每次回调，日志才不会被无关设置刷爆。
        private bool? _appliedOnTop;

        // 热键只有在「组合真的变了」时才需要重注册：X11 要重建键位轮询，
        // Wayland 要重走一遍 portal，都不便宜，也不该被无关设置牵动。
        private (bool On, uint Key, bool Ctrl, bool Alt, bool Shift)? _appliedHotkey;

        private void ApplyWindowSettingsOnUi(AppSettings s)
        {
            try
            {
                _main?.ApplyWindowOnTop(s.WindowOnTop);
                if (_appliedOnTop != s.WindowOnTop)
                {
                    _appliedOnTop = s.WindowOnTop;
                    AppHost.Logger?.Info("窗口置顶 = " + (s.WindowOnTop ? "开" : "关"));
                }

                // 热键：改完立即生效，不用重启（旧版只能在启动时注册一次）。
                var hotkey = (s.UseHotKey, s.HotKeyKey, s.HotKeyCtrl, s.HotKeyAlt, s.HotKeyShift);
                if (_appliedHotkey != hotkey)
                {
                    _appliedHotkey = hotkey;
                    _main?.ReloadHotkey();
                }
            }
            catch (Exception ex)
            {
                AppHost.Logger?.Warn("应用窗口置顶失败: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // F7 失败气泡
        //
        // Avalonia 的 TrayIcon 没有 ShowNotification（WPF 的 NotifyIcon 有 ShowBalloonTip），
        // 所以这里走桌面通知服务：notify-send（libnotify）→ gdbus 调
        // org.freedesktop.Notifications.Notify → 两条都不行就只写日志。
        // 整条链不抛异常、不阻塞 UI 线程超过几秒；跑不通一律静默降级。
        // ------------------------------------------------------------------

        private void SetupFailureBalloon()
        {
            try
            {
                _failTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                _failTimer.Tick += (_, __) => OnFailTimerTick();
                if (AppHost.Worker != null) AppHost.Worker.JobFinished += OnJobFinished;
            }
            catch (Exception ex)
            {
                AppHost.Logger?.Warn("失败气泡初始化失败: " + ex.Message);
            }
        }

        private void Teardown()
        {
            try
            {
                _failTimer?.Stop();
                _failTimer = null;
                if (AppHost.Worker != null) AppHost.Worker.JobFinished -= OnJobFinished;
                AppHost.Settings.Changed -= ApplyWindowSettings;
                _tray?.Dispose();
            }
            catch (Exception ex)
            {
                AppHost.Logger?.Warn("退出清理失败: " + ex.Message);
            }
        }

        // JobFinished 在后台线程触发：这里只记一笔并让 UI 线程重启计时器（对齐 WPF 的 OnJobFinished）。
        private void OnJobFinished(JobEntry job)
        {
            if (job == null || job.Status != JobStatus.Failed) return;
            _failures.NoteFailure(Environment.TickCount64);
            try
            {
                Dispatcher.UIThread.Post(RestartFailTimer);
            }
            catch
            {
                // 应用正在退出时 Dispatcher 已停止，丢掉这一次通知即可
            }
        }

        private void RestartFailTimer()
        {
            if (_failTimer == null) return;
            _failTimer.Stop();
            _failTimer.Start();
        }

        private void OnFailTimerTick()
        {
            try
            {
                _failTimer?.Stop();

                int n = _failures.FlushIfDue(
                    AppHost.Worker != null && !AppHost.Worker.IsIdle, Environment.TickCount64);
                if (n == 0)
                {
                    // 还没到安静期：继续等，别把这一批拆成好几条气泡
                    if (_failures.Pending > 0) RestartFailTimer();
                    return;
                }
                ReportFailures(n);
            }
            catch (Exception ex)
            {
                AppHost.Logger?.Warn("失败气泡处理异常: " + ex.Message);
            }
        }

        private void ReportFailures(int n)
        {
            var failed = new List<JobEntry>();
            string reportPath = null;
            try
            {
                var jobs = AppHost.Worker?.Jobs;
                if (jobs != null)
                {
                    failed = FailureReport.Failed(jobs);
                    reportPath = WriteFailureReport(jobs);
                }
            }
            catch (Exception ex)
            {
                AppHost.Logger?.Warn("失败报告生成失败: " + ex.Message);
            }

            // 气泡只说这一批：failed 里可能还有更早、已经报过的失败，只取最后 n 条并倒序
            // （列表按入队顺序，最新的在末尾），免得标题写「1 个」正文却在讲旧任务。
            if (failed.Count > n)
                failed = failed.GetRange(failed.Count - n, n);
            failed.Reverse();

            // 窗口在前台时不弹：主页横幅已经写着失败（与 WPF 端、docs/history.md 的行为一致）
            bool windowVisible = _main != null && _main.IsVisible && _main.WindowState != WindowState.Minimized;
            if (windowVisible)
            {
                AppHost.Logger?.Info($"失败汇总: {n} 个任务失败（窗口在前台，不弹气泡）"
                    + (reportPath != null ? "，报告已写入 " + reportPath : ""));
                return;
            }

            string title = $"UZIP 有 {n} 个任务失败";
            string body = BuildBalloonBody(failed, reportPath);
            string path = reportPath;

            // 弹通知要起外部进程（notify-send / gdbus），最坏会等满 4s 超时：
            // 绝不能在 UI 线程上等，否则窗口整个卡住。结果只写日志。
            Task.Run(() =>
            {
                if (FailureBalloon.Show(title, body))
                {
                    AppHost.Logger?.Info($"已弹出失败气泡: {n} 个任务失败（notify-send/gdbus）"
                        + (path != null ? "，报告: " + path : ""));
                }
                else
                {
                    AppHost.Logger?.Warn("系统通知不可用（notify-send 与 gdbus 都失败），失败提示只写进日志"
                        + (path != null ? "，报告: " + path : ""));
                }
            });
        }

        /// <summary>完整报告落到配置目录（与 settings.json 同处），文件名带时间戳。失败只记日志。</summary>
        private static string WriteFailureReport(IEnumerable<JobEntry> jobs)
        {
            try
            {
                var dir = AppHost.Layout.ConfigDirectory;
                if (string.IsNullOrEmpty(dir)) return null;
                Directory.CreateDirectory(dir);
                var name = "failure-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                var path = Path.Combine(dir, name);
                FailureReport.Write(path, FailureReport.Build(jobs, AppHost.Zip?.SevenZipPath));
                return path;
            }
            catch (Exception ex)
            {
                AppHost.Logger?.Warn("失败报告写盘失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>气泡正文只放摘要：前两条原因 + 剩下几条 + 去哪儿看详情。整篇报告留给文件。</summary>
        private static string BuildBalloonBody(List<JobEntry> failed, string reportPath)
        {
            const int maxReasons = 2;
            const int maxReasonChars = 42;
            var sb = new StringBuilder();

            int shown = 0;
            foreach (var j in failed)
            {
                if (shown >= maxReasons) break;
                var name = j.DisplayName;
                if (string.IsNullOrEmpty(name)) name = j.Archive ?? "未知任务";
                var why = (j.Diagnosis ?? "").Trim();
                if (why.Length == 0) why = "未记录原因";
                if (why.Length > maxReasonChars) why = why.Substring(0, maxReasonChars) + "…";
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(name).Append('：').Append(why);
                shown++;
            }

            int rest = failed.Count - shown;
            if (rest > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("另有 ").Append(rest).Append(" 个失败");
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("详情见主页列表与历史页");
            if (!string.IsNullOrEmpty(reportPath))
                sb.Append("；完整报告 ").Append(Path.GetFileName(reportPath)).Append("（配置目录）");

            var body = sb.ToString();
            return body.Length > 260 ? body.Substring(0, 260) + "…" : body;
        }

        /// <summary>
        /// 系统通知的降级链。返回 false 表示两条路都不通（调用方只写日志）。
        /// </summary>
        private static class FailureBalloon
        {
            private const int TimeoutMs = 4000;

            public static bool Show(string title, string body)
            {
                // 1) notify-send：libnotify 的命令行，绝大多数桌面装了
                var notifySend = FindOnPath("notify-send");
                if (notifySend != null &&
                    Run(notifySend, TimeoutMs,
                        "--app-name=UZIP2",
                        "--icon=dialog-error",
                        "--urgency=normal",
                        "--expire-time=6000",
                        title, body))
                {
                    return true;
                }

                // 2) gdbus 直接调 Notifications 服务（D-Bus 会话总线）
                var gdbus = FindOnPath("gdbus");
                if (gdbus != null &&
                    Run(gdbus, TimeoutMs,
                        "call",
                        "--session",
                        "--dest", "org.freedesktop.Notifications",
                        "--object-path", "/org/freedesktop/Notifications",
                        "--method", "org.freedesktop.Notifications.Notify",
                        "\"UZIP2\"", "0", "\"\"",
                        Quote(title), Quote(body),
                        "[]", "{}", "6000"))
                {
                    return true;
                }

                return false;
            }

            /// <summary>GVariant 文本格式的字符串字面量：gdbus 不吃裸字符串里的换行/引号。</summary>
            private static string Quote(string s)
            {
                var sb = new StringBuilder("\"", (s ?? "").Length + 8);
                foreach (var ch in s ?? "")
                {
                    switch (ch)
                    {
                        case '\\': sb.Append("\\\\"); break;
                        case '"': sb.Append("\\\""); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': break;
                        case '\t': sb.Append("\\t"); break;
                        default: sb.Append(ch); break;
                    }
                }
                sb.Append('"');
                return sb.ToString();
            }

            private static bool Run(string exe, int timeoutMs, params string[] args)
            {
                Process p = null;
                try
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    foreach (var a in args) psi.ArgumentList.Add(a);

                    p = Process.Start(psi);
                    if (p == null) return false;
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(true); } catch { }
                        AppHost.Logger?.Warn("通知命令超时: " + Path.GetFileName(exe));
                        return false;
                    }
                    if (p.ExitCode == 0) return true;

                    var err = ReadAll(p.StandardError);
                    AppHost.Logger?.Warn($"通知命令失败({p.ExitCode}): {Path.GetFileName(exe)}"
                        + (string.IsNullOrWhiteSpace(err) ? "" : " — " + err.Trim()));
                    return false;
                }
                catch (Exception ex)
                {
                    AppHost.Logger?.Warn("通知命令调用失败: " + ex.Message);
                    return false;
                }
                finally
                {
                    p?.Dispose();
                }
            }

            private static string ReadAll(StreamReader reader)
            {
                try
                {
                    var text = reader.ReadToEnd();
                    return text.Length > 300 ? text.Substring(0, 300) : text;
                }
                catch { return ""; }
            }

            private static string FindOnPath(string exe)
            {
                try
                {
                    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                    foreach (var dir in path.Split(Path.PathSeparator))
                    {
                        if (string.IsNullOrWhiteSpace(dir)) continue;
                        var full = Path.Combine(dir.Trim(), exe);
                        if (File.Exists(full)) return full;
                    }
                }
                catch { }
                return null;
            }
        }
    }
}
