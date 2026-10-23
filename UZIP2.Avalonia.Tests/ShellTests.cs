using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
            Assert.Same(AppHost.Worker.Jobs, list.ItemsSource);
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
                frame.Save(Path.Combine(outDir, "avalon-shell.png"));
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
                frame.Save(Path.Combine(outDir, "avalon-puck.png"));
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
                win.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "avalon-password.png"));
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
            // Windows 上没 libX11；Linux CI 上没有 DISPLAY —— 两种情况都必须安静地返回 null
            Assert.Null(X11HotkeyWatcher.TryCreate("u", true, true, false, () => { }));
        }

        [Fact]
        public void Wayland_portal_returns_null_when_no_portal_is_available()
        {
            // Windows：不是 Linux；Linux CI：没有 WAYLAND_DISPLAY/portal —— 都必须安静返回 null
            Assert.Null(WaylandShortcutPortal.TryCreate("toggle", "显示 UZIP2", "CTRL+ALT+u", () => { }));
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

            var files = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "/tmp/a.zip", "/tmp/b.7z" }, files);
        }
    }
}
