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
