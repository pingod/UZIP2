using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UZIP2.Models;
using UZIP2.Services;
using UZIP2.ViewModel;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WpfApplication = System.Windows.Application;

namespace UZIP2.Shell
{
    public partial class MainWindow : FluentWindow
    {
        private readonly ISettingsService _settings;
        private readonly IFileLogger _logger;
        private readonly TrayService _tray;
        private readonly HotKeyService _hotkeys;
        private readonly ClipboardService _clipboard;
        private bool _exiting;
        private uint _registeredVk;
        private bool _registeredAlt, _registeredShift, _registeredCtrl;
        private MiniPuckWindow _puck;

        public MainWindow()
        {
            InitializeComponent();

            _settings = App.Services.GetRequiredService<ISettingsService>();
            _logger = App.Services.GetRequiredService<IFileLogger>();
            _tray = App.Services.GetRequiredService<TrayService>();
            _hotkeys = App.Services.GetRequiredService<HotKeyService>();
            _clipboard = App.Services.GetRequiredService<ClipboardService>();

            ApplyTheme(_settings.Current);
            RestoreGeometry(_settings.Current);
            Topmost = _settings.Current.WindowOnTop;

            _settings.Changed += s => Dispatcher.Invoke(() =>
            {
                ApplyTheme(s);
                Topmost = s.WindowOnTop;
                RegisterHotKey(s);
                SyncPuck();
            });

            _clipboard.Info += msg => Dispatcher.Invoke(() => _tray.ShowBalloon("UZIP", msg));

            App.Services.GetRequiredService<HomeViewModel>().BatchFinished += batch =>
            {
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!_settings.Current.ResultWindow) return;
                        new ResultWindow(batch) { Owner = this }.Show();
                    });
                }
                catch (Exception ex) { _logger.Error("结果窗口显示失败", ex); }
            };

            SourceInitialized += (s, e) => _hotkeys.Attach(this);
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _tray.Setup(ShowFromTray, Quit, ExtractIcon(), ShowPuckOnly);
            RegisterHotKey(_settings.Current);
            NavView.Navigate(typeof(Pages.HomeView));
            SyncPuck();
            _ = UpdateNotice();
        }

        // 更新检查在后台跑，失败静默，不影响启动
        async System.Threading.Tasks.Task UpdateNotice()
        {
            try { await App.Services.GetRequiredService<HomeViewModel>().CheckForUpdateAsync(); }
            catch (Exception ex) { _logger.Error("更新检查失败", ex); }
        }

        // 迷你方块: 设置开着就存在，关掉设置即消失
        void SyncPuck()
        {
            if (_settings.Current.MiniPuck) EnsurePuck();
            else ClosePuck();
        }

        void EnsurePuck()
        {
            if (_puck != null) return;
            try
            {
                _puck = new MiniPuckWindow(this);
                _puck.Closed += (_, __) => _puck = null;
                _puck.Show();
            }
            catch (Exception ex)
            {
                _logger.Error("迷你方块创建失败", ex);
                _puck = null;
            }
        }

        void ClosePuck()
        {
            var p = _puck;
            _puck = null;
            try { p?.Close(); } catch { }
        }

        // 托盘菜单: 桌面只留方块。持久写 MiniPuck，避免下次保存设置时被 SyncPuck 关掉
        void ShowPuckOnly()
        {
            _settings.Save(s => s.MiniPuck = true);
            if (_puck != null) Hide();
        }

        // 关闭 = 隐藏到托盘（旧行为）；只有托盘"退出"才真正退出
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (_exiting) return;
            e.Cancel = true;
            SaveGeometry();
            Hide();
        }

        public void NavigateToSettings() => NavView.Navigate(typeof(Pages.SettingsView));

        public void ShowFromTray()        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private void Quit()
        {
            _exiting = true;
            SaveGeometry();
            WpfApplication.Current.Shutdown();
        }

        private void RegisterHotKey(AppSettings s)
        {
            if (_registeredVk != 0)
            {
                _hotkeys.Unregister(_registeredVk, _registeredAlt, _registeredShift, _registeredCtrl);
                _registeredVk = 0;
            }
            if (!s.UseHotKey || s.HotKeyKey == 0) return;
            _hotkeys.Register(s.HotKeyKey, s.HotKeyAlt, s.HotKeyShift, s.HotKeyCtrl,
                () => _clipboard.PasteFromClipboard());
            _registeredVk = s.HotKeyKey;
            _registeredAlt = s.HotKeyAlt;
            _registeredShift = s.HotKeyShift;
            _registeredCtrl = s.HotKeyCtrl;
        }

        private static void ApplyTheme(AppSettings s)
        {
            ApplicationTheme theme;
            switch (s.Theme)
            {
                case "Light": theme = ApplicationTheme.Light; break;
                case "Dark": theme = ApplicationTheme.Dark; break;
                case "HighContrast": theme = ApplicationTheme.HighContrast; break;
                default:
                    theme = ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
                        ? ApplicationTheme.Dark : ApplicationTheme.Light;
                    break;
            }
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: true);
        }

        private void RestoreGeometry(AppSettings s)
        {
            if (s.WindowWidth < 0) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }

        private void SaveGeometry()
        {
            if (WindowState != WindowState.Normal) return;
            _settings.Save(s =>
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            });
        }

        private static System.Drawing.Icon ExtractIcon()
        {
            try
            {
                var path = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                return System.Drawing.Icon.ExtractAssociatedIcon(path);
            }
            catch
            {
                return System.Drawing.SystemIcons.Application;
            }
        }
    }
}
