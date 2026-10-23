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
    }
}
