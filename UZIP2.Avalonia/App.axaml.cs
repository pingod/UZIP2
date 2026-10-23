using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using UZIP2.Avalonia.Shell;

namespace UZIP2.Avalonia
{
    public sealed class App : Application
    {
        private TrayIcon _tray;

        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppHost.Initialize();
                var window = new MainWindow();
                desktop.MainWindow = window;
                SetupTray(window, desktop);

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
    }
}
