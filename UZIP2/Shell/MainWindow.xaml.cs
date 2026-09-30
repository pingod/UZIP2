using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UZIP2.Models;
using UZIP2.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WpfApplication = System.Windows.Application;

namespace UZIP2.Shell
{
    public partial class MainWindow : FluentWindow
    {
        private readonly ISettingsService _settings;
        private readonly TrayService _tray;
        private readonly HotKeyService _hotkeys;
        private readonly ClipboardService _clipboard;
        private bool _exiting;
        private uint _registeredVk;
        private bool _registeredAlt, _registeredShift, _registeredCtrl;

        public MainWindow()
        {
            InitializeComponent();

            _settings = App.Services.GetRequiredService<ISettingsService>();
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
            });

            _clipboard.Info += msg => Dispatcher.Invoke(() => _tray.ShowBalloon("UZIP", msg));

            SourceInitialized += (s, e) => _hotkeys.Attach(this);
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _tray.Setup(ShowFromTray, Quit, ExtractIcon());
            RegisterHotKey(_settings.Current);
            NavView.Navigate(typeof(Pages.HomeView));
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
