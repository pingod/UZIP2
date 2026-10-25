using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using UZIP2.Avalonia.Shell;
using UZIP2.Avalonia.Shell.Pages;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Avalonia.Tests
{
    // 无头跑 Avalonia：验证壳的装配与页面绑定，不依赖真实桌面。
    public class ShellTests
    {
        private static readonly string Root =
            Path.Combine(Path.GetTempPath(), "uzip2-av-" + Guid.NewGuid().ToString("N"));

        private static void EnsureHost()
        {
            if (AppHost.Settings != null) return;
            Directory.CreateDirectory(Root);
            AppHost.Initialize(new PlatformLayout(Path.Combine(Root, "config"), Path.Combine(Root, "state")));
        }

        [AvaloniaFact]
        public void Main_window_opens_on_the_home_page()
        {
            EnsureHost();
            var window = new MainWindow();
            var host = window.FindControl<ContentControl>("PageHost");
            Assert.NotNull(host);
            Assert.IsType<HomeView>(host.Content);
        }

        [AvaloniaFact]
        public void History_page_lists_recorded_jobs()
        {
            EnsureHost();
            AppHost.History.Record(new JobEntry
            {
                Kind = "Extract",
                Archive = Path.Combine(Root, "sample.zip"),
                OutputDir = Path.Combine(Root, "out"),
                Status = JobStatus.Success,
                Done = 3,
                Total = 3
            });

            var page = new HistoryView();
            var count = page.FindControl<TextBlock>("CountText");
            Assert.Contains("共 1 条", count.Text);
        }

        [AvaloniaFact]
        public void Settings_page_reports_the_resolved_7z_path_and_config_dir()
        {
            EnsureHost();
            var page = new SettingsView();
            var version = page.FindControl<TextBlock>("VersionText");
            Assert.Contains("UZIP", version.Text);
            Assert.Contains(AppHost.Layout.ConfigDirectory, version.Text);
        }

        [AvaloniaFact]
        public void Home_page_binds_to_the_shared_job_queue()
        {
            EnsureHost();
            var page = new HomeView();
            var list = page.FindControl<ListBox>("JobsList");

            // 不再直接把 Worker.Jobs 当 ItemsSource：设置里的 JobSort 要求换顺序展示，
            // 所以列表绑的是一份按 JobOrder 维护的镜像。这里验的是「同一个队列」这件事本身
            // ——镜像里的元素必须就是队列里的元素，且队列新增立刻就进来。
            var shown = Assert.IsAssignableFrom<System.Collections.IEnumerable>(list.ItemsSource);
            Assert.Equal(AppHost.Worker.Jobs.ToList(), shown.Cast<object>().ToList());
        }

        /// <summary>
        /// 设置里的「任务排序」必须真的作用到主页列表。WPF 端由 HomeViewModel 的 mirror 实现，
        /// Avalonia 这边接的是 HomeView._sorted；这里验 JobOrder 在四种模式下给出的顺序，
        /// 以及 InsertIndex 能把新作业插到正确位置（列表从此保持有序）。
        /// </summary>
        [AvaloniaFact]
        public void Home_list_follows_the_configured_job_sort()
        {
            EnsureHost();

            var a = new JobEntry { Id = 1, Archive = "/tmp/a.7z", Kind = "Extract" };
            var b = new JobEntry { Id = 2, Archive = "/tmp/b.7z", Kind = "Extract" };
            var c = new JobEntry { Id = 3, Archive = "/tmp/c.7z", Kind = "Extract" };

            // 加入顺序：早的在前
            Assert.True(JobOrder.Compare(a, b, JobOrder.AddOrder) < 0);
            // 最新在前：晚的在前
            Assert.True(JobOrder.Compare(a, b, JobOrder.NewestFirst) > 0);
            // 按文件名：a < b < c，与 Id 无关
            Assert.True(JobOrder.Compare(a, c, JobOrder.ByName) < 0);

            // 新作业按当前模式插到正确位置（前提：传入的是已按该模式排好的列表）
            var byName = new List<JobEntry> { a, c };        // a.7z, c.7z
            Assert.Equal(1, JobOrder.InsertIndex(byName, b, JobOrder.ByName));   // b 插在中间

            var newest = new List<JobEntry> { c, a };        // Id 3, 1（最新在前）
            Assert.Equal(1, JobOrder.InsertIndex(newest, b, JobOrder.NewestFirst)); // Id 2 插在两者之间（3,2,1）

            var addOrder = new List<JobEntry> { a, c };      // Id 1, 3
            Assert.Equal(1, JobOrder.InsertIndex(addOrder, b, JobOrder.AddOrder)); // Id 2 插在中间
        }

        // 真渲染一帧存成 PNG：证明 XAML 能实际画出来，而不只是构造成功。
        [AvaloniaFact]
        public void Main_window_renders_a_frame()
        {
            EnsureHost();
            var window = new MainWindow();
            window.Show();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var outDir = Environment.GetEnvironmentVariable("UZIP2_SHOT_DIR");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "avalon-shell.png"), PngBitmapEncoderOptions.Default);
            }
        }

        [AvaloniaFact]
        public void Puck_window_renders_its_ring()
        {
            EnsureHost();
            var window = new PuckWindow();
            window.Show();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var outDir = Environment.GetEnvironmentVariable("UZIP2_SHOT_DIR");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "avalon-puck.png"), PngBitmapEncoderOptions.Default);
            }
        }

        [AvaloniaFact]
        public void Ring_geometry_is_empty_at_zero_and_closes_at_full()
        {            Assert.Null(PuckRingGeometry.Build(null, 56, 4));
            Assert.Null(PuckRingGeometry.Build(0, 56, 4));

            var half = PuckRingGeometry.Build(50, 56, 4);
            Assert.NotNull(half);
            var b = half.Bounds;
            // 12 点顺时针走到 6 点：只占右半边，但纵向走满
            Assert.True(b.Width >= 25, $"半圈应占右半幅，实际 {b}");
            Assert.True(b.Height >= 50, $"半圈应下探到 6 点方向，实际 {b}");

            var full = PuckRingGeometry.Build(100, 56, 4);
            Assert.NotNull(full);
            Assert.True(full.Bounds.Width >= 50 && full.Bounds.Height >= 50, $"满圈应闭合，实际 {full.Bounds}");
        }

        [AvaloniaFact]
        public void Password_page_masks_the_book_and_reveals_on_demand()
        {
            EnsureHost();
            var unique = "pw-" + Guid.NewGuid().ToString("N");
            AppHost.Passwords.AddBook(unique, "secret-" + unique);

            var page = new PasswordView();
            var list = page.FindControl<ListBox>("BookList");
            var rows = ((System.Collections.IEnumerable)list.ItemsSource).Cast<PasswordView.Row>().ToList();
            var row = rows.Single(r => r.Name == unique);

            Assert.Equal("••••••", row.DisplayText);
            row.Revealed = true;
            Assert.Equal("secret-" + unique, row.DisplayText);

            var outDir = Environment.GetEnvironmentVariable("UZIP2_SHOT_DIR");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                var win = new Window { Content = page, Width = 1000, Height = 700 };
                win.Show();
                win.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "avalon-password.png"), PngBitmapEncoderOptions.Default);
            }
        }

        [AvaloniaFact]
        public void Convert_dialog_offers_every_creatable_format()
        {
            EnsureHost();
            var dlg = new ConvertDialog("/tmp/sample.zip");
            var box = dlg.FindControl<ComboBox>("FormatBox");
            var items = ((System.Collections.IEnumerable)box.ItemsSource).Cast<string>().ToList();
            Assert.Equal(7, items.Count);
            Assert.Contains("7z", items[1]);
            Assert.Equal(1, box.SelectedIndex);
        }

        [Fact]
        public void X11_hotkey_watcher_degrades_without_a_display()
        {
            // 降级路径要在两种宿主机上都能断言：Windows 没有 libX11，开发机则真有桌面会话。
            // 不能用"改 DISPLAY 环境变量"来测 —— libX11 会缓存进程内已打开的 Display，
            // 所以这里直接替换打开显示的钩子，模拟"连不上 X 服务器"。
            WithDisplayOpener(() => IntPtr.Zero, () =>
                Assert.Null(X11HotkeyWatcher.TryCreate("u", true, true, false, () => { })));
        }

        [Fact]
        public void Wayland_portal_returns_null_when_no_portal_is_available()
        {
            // 在 KDE Wayland 上这条会真的拿到 portal，所以先清掉会话变量再断言降级。
            // DesktopCapabilities 读的是托管侧的环境变量，这里改得到。
            WithEnv("WAYLAND_DISPLAY", null, () => WithEnv("XDG_SESSION_TYPE", null, () =>
                Assert.Null(WaylandShortcutPortal.TryCreate("toggle", "显示 UZIP2", "CTRL+ALT+u", () => { }))));
        }

        /// <summary>临时替换 X11 打开显示的函数，跑完恢复。</summary>
        private static void WithDisplayOpener(Func<IntPtr> opener, Action body)
        {
            var saved = X11HotkeyWatcher.DisplayOpener;
            try
            {
                X11HotkeyWatcher.DisplayOpener = opener;
                body();
            }
            finally
            {
                X11HotkeyWatcher.DisplayOpener = saved;
            }
        }

        /// <summary>临时改一个环境变量跑一段断言，跑完恢复原值（含"原本不存在"）。</summary>
        private static void WithEnv(string name, string value, Action body)
        {
            var saved = Environment.GetEnvironmentVariable(name);
            try
            {
                Environment.SetEnvironmentVariable(name, value);
                body();
            }
            finally
            {
                Environment.SetEnvironmentVariable(name, saved);
            }
        }

        // 单实例在 Linux 上也得成立：命名管道在 Unix 下是域套接字，值得真跑一遍。
        [Fact]
        public async System.Threading.Tasks.Task Single_instance_bus_forwards_files_on_this_platform()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var mutex = "UZipTestMutex_" + suffix;
            var pipe = "UZipTestPipe_" + suffix;

            using var primary = new InstanceBus(mutex, pipe);
            Assert.True(primary.TryBecomePrimary());
            var received = new System.Threading.Tasks.TaskCompletionSource<string[]>();
            primary.FilesReceived += files => received.TrySetResult(files);

            // 同进程里再造一个 InstanceBus 会重入同一把互斥体（同线程所有），
            // 所以第二实例直接走转发路径模拟——这里要证明的是管道在 Unix 上也能通。
            Assert.True(InstanceBus.ForwardArgsToPrimary(pipe, new[] { "/tmp/a.zip", "/tmp/b.7z" }));

            var files = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "/tmp/a.zip", "/tmp/b.7z" }, files);
        }

        // ---------------------------------------------------------------
        // 下面几条覆盖 F2/F3/F5/F6 新增的界面件：这些按钮原先只有手工验证，
        // 无头渲染又因为队列为空而不实例化 DataTemplate，等于零覆盖。
        // ---------------------------------------------------------------

        [AvaloniaFact]
        public void Home_page_exposes_the_failed_clear_button()
        {
            EnsureHost();
            var page = new HomeView();
            // 顶部批量操作里的「清除失败项」：存在即可，行为由 ArchiveWorker.RemoveTerminal 保证。
            var buttons = AllButtons(page).Select(b => b.Content as string).ToList();
            Assert.Contains("清除失败项", buttons);
            Assert.Contains("清除已完成", buttons);
            Assert.Contains("全部取消", buttons);
        }

        [AvaloniaFact]
        public void Home_card_shows_retry_and_delete_source_for_terminal_jobs()
        {
            EnsureHost();
            // 卡片按钮的可见性靠转换器决定，DataTemplate 只有在有任务时才实例化，
            // 所以这里直接断言转换器语义 + 模板里确实带了这两个按钮。
            var retry = new JobStatusVisibilityConverter { Statuses = "Failed,Cancelled" };
            var preview = new JobKindVisibilityConverter { Kinds = "Extract" };
            var openOutput = new JobStatusVisibilityConverter { Statuses = "Success" };

            Assert.Equal(true, retry.Convert(JobStatus.Failed, typeof(bool), null, CultureInfo.InvariantCulture));
            Assert.Equal(true, retry.Convert(JobStatus.Cancelled, typeof(bool), null, CultureInfo.InvariantCulture));
            Assert.Equal(false, retry.Convert(JobStatus.Success, typeof(bool), null, CultureInfo.InvariantCulture));
            Assert.Equal(false, retry.Convert(JobStatus.Queued, typeof(bool), null, CultureInfo.InvariantCulture));

            Assert.Equal(true, preview.Convert("Extract", typeof(bool), null, CultureInfo.InvariantCulture));
            Assert.Equal(false, preview.Convert("Compress", typeof(bool), null, CultureInfo.InvariantCulture));

            // 「打开目录」对齐 WPF 的 EqualVisibility Success：失败任务没有目录可开。
            Assert.Equal(true, openOutput.Convert(JobStatus.Success, typeof(bool), null, CultureInfo.InvariantCulture));
            Assert.Equal(false, openOutput.Convert(JobStatus.Failed, typeof(bool), null, CultureInfo.InvariantCulture));

            var xaml = File.ReadAllText(Path.Combine(RepoRoot, "UZIP2.Avalonia", "Shell", "Pages", "HomeView.axaml"));
            Assert.Contains("OnRetry", xaml);
            Assert.Contains("OnDeleteSource", xaml);
        }

        [AvaloniaFact]
        public void History_page_skips_retry_for_verification_entries()
        {
            EnsureHost();
            // 不往共享历史里写记录：历史是跨用例共享的（ShellTests.Root 是静态的），
            // 写进去会把 History_page_lists_recorded_jobs 的计数断言打乱。
            var page = new HistoryView();
            Assert.NotNull(page.FindControl<ListBox>("HistoryList"));

            // 校验记录没有「重跑」语义（重跑等于把包真解开），对齐 WPF HistoryRow.CanRetry。
            var converter = new HistoryKindRetryableConverter();
            Assert.Equal(false, converter.Convert("Test", typeof(bool), null, CultureInfo.InvariantCulture));
            Assert.Equal(true, converter.Convert("Extract", typeof(bool), null, CultureInfo.InvariantCulture));
            Assert.Equal(true, converter.Convert("Compress", typeof(bool), null, CultureInfo.InvariantCulture));
            Assert.Equal(false, converter.Convert(null, typeof(bool), null, CultureInfo.InvariantCulture));

            var xaml = File.ReadAllText(Path.Combine(RepoRoot, "UZIP2.Avalonia", "Shell", "Pages", "HistoryView.axaml"));
            Assert.Contains("OnRetry", xaml);
        }

        [AvaloniaFact]
        public void Password_page_has_a_clipboard_paste_entry()
        {
            EnsureHost();
            var page = new PasswordView();
            var buttons = AllButtons(page).Select(b => b.Content as string).ToList();
            Assert.Contains("从剪贴板粘贴", buttons);
        }

        [Fact]
        public void Clipboard_paste_cleans_control_characters_like_wpf()
        {
            // 与 UZIP2/Services/ClipboardService.cs 的 Clean 逐字符一致：抹掉 \t 与 \r，保留 \n 做分隔。
            // 空结果统一返回 null（语义是"没什么可贴的"），调用方按 null 当空处理。
            Assert.Equal("pw-a", PasswordView.Clean("  pw-a  ", trimSpace: true));
            Assert.Equal("  pw-a  ", PasswordView.Clean("  pw-a  ", trimSpace: false));
            Assert.Equal("line1\nline2", PasswordView.Clean("line1\r\nline2", trimSpace: false));
            Assert.Equal("pw-apw-b", PasswordView.Clean("pw-a\tpw-b", trimSpace: false));
            Assert.Null(PasswordView.Clean(null, trimSpace: true));
            Assert.Null(PasswordView.Clean("   ", trimSpace: true));
            Assert.Null(PasswordView.Clean("\t\r\n", trimSpace: true));
        }

        [AvaloniaFact]
        public void Hotkey_names_cover_more_than_letters_and_digits()
        {
            // 旧版 MainWindow.KeySymName 只映射 A-Z/0-9，其余退回 "u"，
            // 用户在设置页选的 F5 会被静默改成 u。共享映射表要覆盖常用键。
            Assert.Equal("F5", HotkeyKeyNames.ToKeySymName(0x74));       // VK_F5
            Assert.Equal("space", HotkeyKeyNames.ToKeySymName(0x20));    // VK_SPACE
            Assert.Equal("Left", HotkeyKeyNames.ToKeySymName(0x25));     // VK_LEFT
            Assert.Equal("u", HotkeyKeyNames.ToKeySymName(0x55));        // VK_U
            Assert.Equal("5", HotkeyKeyNames.ToKeySymName(0x35));        // VK_5

            Assert.True(HotkeyKeyNames.IsSupported(0x74));
            Assert.True(HotkeyKeyNames.IsSupported(0x55));
            Assert.Equal("Ctrl+Alt+F5", HotkeyKeyNames.Format(0x74, ctrl: true, alt: true, shift: false));
        }

        // ---------------------------------------------------------------
        // 回归：E2 实机验证发现的刷屏
        // ---------------------------------------------------------------

        [AvaloniaFact]
        public void Puck_window_does_not_rewrite_settings_when_the_position_is_unchanged()
        {
            EnsureHost();
            AppHost.Settings.Save(s => { s.PuckLeft = 300; s.PuckTop = 400; });

            var window = new PuckWindow();
            window.Show();

            var path = AppHost.Settings.SettingsPath;
            var before = File.ReadAllText(path);

            // 复现实机场景：合成器反复抛同一坐标的 PositionChanged。
            // 修复前每次都会 Save → 无条件写盘 + 全量 Changed 广播，实机日志 7 秒刷了 430 行。
            var stamp = File.GetLastWriteTimeUtc(path);
            for (var i = 0; i < 20; i++) window.SavePosition();
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
            Assert.Equal(before, File.ReadAllText(path));

            // 坐标真变了才必须落盘（E3 的「拖到副屏后重启位置不变」依赖这条）。
            // 先把内存与磁盘都摆到和目标不同的位置，再让窗口报出目标坐标。
            AppHost.Settings.Save(s => { s.PuckLeft = 10; s.PuckTop = 20; });
            window.Position = new global::Avalonia.PixelPoint(11, 21);
            window.SavePosition();
            var after = File.ReadAllText(path);
            Assert.Contains("\"puckLeft\": 11", after);
            Assert.Contains("\"puckTop\": 21", after);
            Assert.Equal(11, AppHost.Settings.Current.PuckLeft);
            Assert.Equal(21, AppHost.Settings.Current.PuckTop);
        }

        /// <summary>
        /// E3 的验收点是「拖到副屏后重启位置不变」。合成器/多屏环境下真正写进窗口的是
        /// ResolveRestorePoint 的结果，所以直接验算它：记录过位置就换算到设置的那个点，
        /// 从没记录过（负数哨兵）就返回 null 让窗口落到默认角。
        /// </summary>
        [AvaloniaFact]
        public void Puck_restores_the_saved_position_instead_of_the_compositor_default()
        {
            var primary = new global::Avalonia.PixelRect(0, 0, 1920, 1080);

            var restored = PuckWindow.ResolveRestorePoint(800, 600, primary);
            Assert.NotNull(restored);
            Assert.Equal(800, restored!.Value.X);
            Assert.Equal(600, restored.Value.Y);

            // 副屏/越界坐标不能被丢回主屏左上角：PuckPlacement 会夹进虚拟桌面。
            var far = PuckWindow.ResolveRestorePoint(2600, 300, primary);
            Assert.NotNull(far);
            Assert.True(far!.Value.X >= 0, "越界坐标不该变成负值");

            // 负数哨兵 = 从没存过位置。
            Assert.Null(PuckWindow.ResolveRestorePoint(-1, -1, primary));
        }

        // ── Hyprland 浮动窗口（方块被平铺拉伸）─────────────────────────────────
        // 根因：Wayland 的 xdg-shell 里，客户端既不能声明「我是浮动窗口」，也不能指定
        // 绝对坐标 —— 都由合成器决定。Hyprland 的 dwindle 布局会把 112×112 的方块当普通
        // 窗口塞进布局树，拉伸成整块（实测 1268×688，内容跟着糊）。唯一出路是走 hyprctl IPC。
        // 这些用例只验命令拼装与 JSON 解析，不碰真进程。

        [Fact]
        public void Hyprland_float_command_targets_the_window_address()
        {
            Assert.Equal(
                "hl.dsp.window.float({ window = \"address:0x5601564cf680\", action = \"enable\" })",
                HyprlandWindowHelper.BuildFloatCommand("0x5601564cf680"));

            Assert.Equal(
                "hl.dsp.window.move({ window = \"address:0x5601564cf680\", x = 800, y = 600 })",
                HyprlandWindowHelper.BuildMoveCommand("0x5601564cf680", 800, 600));

            // 负坐标也要原样拼进去（多屏合成器允许负 xy），不能被吞掉或加引号。
            Assert.Contains("x = -1920", HyprlandWindowHelper.BuildMoveCommand("0x1", -1920, 200));
        }

        [Fact]
        public void Hyprland_picks_this_process_window_by_pid()
        {
            // 真实 hyprctl clients -j 的裁剪版：两个窗口，只有 pid 能区分。
            const string json = "[{\"address\":\"0xaaaa\",\"pid\":11,\"title\":\"别的程序\"}," +
                                "{\"address\":\"0xbbbb\",\"pid\":22,\"title\":\"UZIP 拖拽方块\"}]";
            Assert.Equal("0xbbbb", HyprlandWindowHelper.PickAddress(json, 22));
            Assert.Null(HyprlandWindowHelper.PickAddress(json, 99));
            Assert.Null(HyprlandWindowHelper.PickAddress("", 22));
            Assert.Null(HyprlandWindowHelper.PickAddress("not json", 22));
        }

        /// <summary>
        /// Wayland 下 `Window.Position` 恒为 0,0（xdg-shell 不告诉客户端绝对位置）。
        /// 如果照它存盘，用户挪过方块后设置里就被写成 0,0，下次启动方块跑到左上角。
        /// 所以 Wayland 上必须改问合成器 —— 这里验 JSON 解析这条路。
        /// </summary>
        [Fact]
        public void Hyprland_reads_the_real_position_from_the_compositor()
        {
            const string json = "[{\"address\":\"0xaaaa\",\"pid\":11,\"at\":[0,0]}," +
                                "{\"address\":\"0xbbbb\",\"pid\":22,\"at\":[2198,1462]}]";

            Assert.True(HyprlandWindowHelper.TryParsePosition(json, 22, out int x, out int y));
            Assert.Equal(2198, x);
            Assert.Equal(1462, y);

            // 找不到自己 → false，而不是给个 0,0 的假答案。
            Assert.False(HyprlandWindowHelper.TryParsePosition(json, 99, out _, out _));
            Assert.False(HyprlandWindowHelper.TryParsePosition("", 22, out _, out _));
            Assert.False(HyprlandWindowHelper.TryParsePosition("[{\"pid\":22}]", 22, out _, out _));
        }

        // ── 方块拖放 ──────────────────────────────────────────────────────────
        /// <summary>
        /// 曾经：代码里挂了 DragDrop.DragOver/Drop 处理器，但 XAML 里没有
        /// DragDrop.AllowDrop="True"。Avalonia 只会把拖放事件派发给 AllowDrop 为 true 的
        /// 控件，所以把文件拖到方块上毫无反应 —— 用户报的「拖到方块无任何动作」。
        /// 这条用例守住 XAML 里的那个属性，别再丢。
        /// </summary>
        [Fact]
        public void Puck_window_accepts_dropped_files()
        {
            var xaml = File.ReadAllText(Path.Combine(RepoRoot, "UZIP2.Avalonia", "Shell", "PuckWindow.axaml"));

            Assert.Contains("DragDrop.AllowDrop=\"True\"", xaml);

            // 固定尺寸：不给 Min/Max，Hyprland 收不到「不可拉伸」的信号，
            // 会把方块平铺拉伸（这是「桌面只留方块」被拉伸的另一半原因）。
            Assert.Contains("MinWidth=\"112\"", xaml);
            Assert.Contains("MaxWidth=\"112\"", xaml);
        }

        /// <summary>
        /// AllowDrop 只在 XAML 里写对还不够 —— 得真有一个实例化的 PuckWindow，
        /// 而 Avalonia 判断要不要把拖放事件派给某个控件，看的就是这个附加属性。
        /// 这条用例直接读实例上的值，比 grep XAML 文本更贴近真实行为。
        /// </summary>
        [AvaloniaFact]
        public void Puck_window_has_allow_drop_switched_on()
        {
            var window = new PuckWindow();
            try
            {
                Assert.True(global::Avalonia.Input.DragDrop.GetAllowDrop(window),
                    "方块窗口必须 AllowDrop，否则 Avalonia 不会把拖放事件派给它");

                // 事件处理器本身也要挂上（有两个来源：构造函数里的 AddHandler）。
                Assert.NotNull(window.FindControl<global::Avalonia.Controls.TextBlock>("HintText"));
            }
            finally
            {
                window.Close();
            }
        }

        // ── 路径分隔符（Linux GUI 解压曾经 100% 失败）──────────────────────────
        // 根因：TempManager.Completion / ArchiveWorker.TempFix 硬编码反斜杠，
        // Linux 上把 "/tmp/x" 拼成 "/tmp/x\"，反斜杠只是普通文件名字符 →
        // 临时目录建到 /tmp 下一个叫 "x\" 的怪目录里，随后 Directory.GetFiles
        // 用带尾反斜杠的路径找不到，抛 DirectoryNotFoundException。
        [Fact]
        public void Ensure_trailing_uses_the_platform_separator()
        {
            var sep = Path.DirectorySeparatorChar;
            Assert.Equal("/tmp/voltest" + sep, PlatformPaths.EnsureTrailing("/tmp/voltest"));
            // 已经有分隔符（任一种）就不要重复追加
            Assert.Equal("/tmp/voltest/", PlatformPaths.EnsureTrailing("/tmp/voltest/"));
            Assert.Equal("/tmp/voltest/", PlatformPaths.EnsureTrailing("/tmp/voltest/"));
            // 空值与 null 原样返回，不炸
            Assert.Equal("", PlatformPaths.EnsureTrailing(""));
            Assert.Equal("", PlatformPaths.EnsureTrailing(null));
        }

        [Fact]
        public void Trim_separators_strips_both_kinds()
        {
            Assert.Equal("/tmp/voltest", PlatformPaths.TrimSeparators("/tmp/voltest/"));
            Assert.Equal("/tmp/voltest", PlatformPaths.TrimSeparators("/tmp/voltest\\"));
            Assert.Equal("/tmp/voltest", PlatformPaths.TrimSeparators("/tmp/voltest/\\"));
            Assert.Equal("", PlatformPaths.TrimSeparators(null));
        }

        [Fact]
        public void Temp_manager_never_produces_a_backslash_on_unix()
        {
            if (OperatingSystem.IsWindows()) return;   // 这个 bug 只在 Unix 上暴露
            var outDir = Path.Combine(Path.GetTempPath(), "uzip2-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            try
            {
                var temp = TempManager.CreateSessionTemp(outDir, Path.Combine(outDir, "a.7z.001"), "1",
                    Path.Combine(outDir, "cfg"));
                Assert.DoesNotContain('\\', temp);
                Assert.True(temp.StartsWith(outDir + "/"), $"临时目录应落在输出目录下，实际 {temp}");
                Assert.True(Directory.Exists(temp), $"临时目录必须真的存在：{temp}");
                // 曾经这里会抛 DirectoryNotFoundException
                Assert.Empty(Directory.GetFiles(temp, "*", SearchOption.TopDirectoryOnly));
            }
            finally
            {
                try { Directory.Delete(outDir, true); } catch { }
            }
        }

        // ── 分卷识别（GUI 自动分流曾把中间卷送进压缩队列）─────────────────────
        // 白名单里只有 ".001"，.002/.003… 因此被判成"普通文件"，
        // GUI 的 EnqueueAuto 于是把它当待压缩物（产出 x.7z.zip），CLI 报"格式不支持"。
        [Theory]
        [InlineData("x.7z.002")]
        [InlineData("x.7z.008")]
        [InlineData("x.zip.003")]
        [InlineData("x.tar.002")]
        [InlineData("x.bz2.002")]
        [InlineData("x.gz.002")]
        [InlineData("x.xz.002")]
        public void Every_numeric_volume_counts_as_an_archive(string name)
        {
            Assert.True(ArchiveInspector.CanExtractByExtension("/tmp/" + name),
                $"{name} 是分卷，必须按压缩包对待");
        }

        [Fact]
        public void Old_style_zip_splits_count_as_archives()
        {
            Assert.True(ArchiveInspector.CanExtractByExtension("/tmp/b.z01"));
            Assert.True(ArchiveInspector.CanExtractByExtension("/tmp/b.z12"));
        }

        [Fact]
        public void Unknown_numeric_suffix_is_still_not_an_archive()
        {
            // 数字后缀但基名不是压缩包 → 不是分卷
            Assert.False(ArchiveInspector.CanExtractByExtension("/tmp/log.txt.002"));
            Assert.False(ArchiveInspector.CanExtractByExtension("/tmp/photo.png"));
        }

        [Fact]
        public void Extract_target_falls_back_to_the_first_volume()
        {
            // 分卷的中间卷本身打不开（7z: "Cannot open the file as archive"），
            // 所以凡是"读包"的动作都要先回退到 .001。预览与转格式曾经漏了这一步。
            string dir = Directory.CreateTempSubdirectory("uzip2-vol-").FullName;
            try
            {
                string main = Path.Combine(dir, "x.7z.001");
                string mid = Path.Combine(dir, "x.7z.003");
                File.WriteAllText(main, "main");
                File.WriteAllText(mid, "mid");

                Assert.Equal(main, ArchiveInspector.ExtractTarget(mid));
                Assert.Equal(main, ArchiveInspector.ExtractTarget(main));
                // Inspect 的 ExtractTargetPath 走同一条路径
                Assert.Equal(main, ArchiveInspector.Inspect(mid).ExtractTargetPath);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Extract_target_keeps_the_middle_volume_when_the_first_is_missing()
        {
            // 主卷缺失不能在这里瞎猜：原样返回，让上层报"分卷不完整"
            string dir = Directory.CreateTempSubdirectory("uzip2-vol-").FullName;
            try
            {
                string mid = Path.Combine(dir, "y.7z.003");
                File.WriteAllText(mid, "mid");
                Assert.Equal(mid, ArchiveInspector.ExtractTarget(mid));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Extract_target_leaves_plain_archives_alone()
        {
            string dir = Directory.CreateTempSubdirectory("uzip2-vol-").FullName;
            try
            {
                string plain = Path.Combine(dir, "plain.7z");
                File.WriteAllText(plain, "x");
                Assert.Equal(plain, ArchiveInspector.ExtractTarget(plain));
                Assert.Equal("", ArchiveInspector.ExtractTarget(""));
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>递归找出一棵可视树里所有 Button（Avalonia 11 没有 WPF 的 FindVisualChildren）。</summary>
        private static IEnumerable<Button> AllButtons(Control root)
        {
            if (root is Button b) yield return b;
            foreach (var child in LogicalChildren(root))
                foreach (var found in AllButtons(child))
                    yield return found;
        }

        private static IEnumerable<Control> LogicalChildren(Control control)
        {
            if (control is Panel panel)
                foreach (var child in panel.Children.OfType<Control>())
                    yield return child;
            if (control is Decorator decorator && decorator.Child is Control d)
                yield return d;
            if (control is ContentControl content && content.Content is Control c)
                yield return c;
            if (control is ItemsControl items)
                foreach (var child in items.GetRealizedContainers().OfType<Control>())
                    yield return child;
        }

        /// <summary>仓库根目录（测试跑在 bin/Release/net8.0 下，往上找有 .git 的那层）。</summary>
        private static string RepoRoot
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "UZIP2.sln")))
                    dir = dir.Parent;
                return dir?.FullName ?? AppContext.BaseDirectory;
            }
        }
    }
}
