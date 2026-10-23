using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UZIP2.Avalonia.Shell
{
    public partial class MainWindow : Window
    {
        private readonly Pages.HomeView _home = new Pages.HomeView();
        private readonly Pages.PasswordView _password = new Pages.PasswordView();
        private readonly Pages.HistoryView _history = new Pages.HistoryView();
        private readonly Pages.SettingsView _settings = new Pages.SettingsView();
        private PuckWindow _puck;
        private X11HotkeyWatcher _hotkey;

        public MainWindow()
        {
            InitializeComponent();
            PageHost.Content = _home;
            Highlight(NavHome);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (AppHost.Settings.Current.MiniPuck) ShowPuck();
            StartHotkey();
        }

        private void OnNavHome(object sender, RoutedEventArgs e) => Show(_home, NavHome);
        private void OnNavPassword(object sender, RoutedEventArgs e) => Show(_password, NavPassword);
        private void OnNavHistory(object sender, RoutedEventArgs e) => Show(_history, NavHistory);
        private void OnNavSettings(object sender, RoutedEventArgs e) => Show(_settings, NavSettings);

        private void Show(Control page, Button nav)
        {
            PageHost.Content = page;
            if (page == _password) _password.Reload();
            if (page == _history) _history.Refresh();
            if (page == _settings) _settings.Reload();
            Highlight(nav);
        }

        private void Highlight(Button active)
        {
            foreach (var b in new[] { NavHome, NavPassword, NavHistory, NavSettings })
                b.Background = b == active
                    ? global::Avalonia.Media.Brush.Parse("#2D2D33")
                    : global::Avalonia.Media.Brushes.Transparent;
        }

        protected override void OnClosed(EventArgs e)
        {
            _hotkey?.Dispose();
            _hotkey = null;
            base.OnClosed(e);
        }

        public void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>桌面只留方块：开/关都写进设置，重启后保持一致。</summary>
        public void TogglePuck()
        {
            var on = !AppHost.Settings.Current.MiniPuck;
            AppHost.Settings.Save(s => s.MiniPuck = on);
            if (on)
            {
                ShowPuck();
                Hide();
            }
            else
            {
                _puck?.Close();
                _puck = null;
                ShowFromTray();
            }
        }

        private void StartHotkey()
        {
            var s = AppHost.Settings.Current;
            if (!s.UseHotKey) return;
            if (!AppHost.Capabilities.GlobalHotkeys || !AppHost.Capabilities.IsX11) return;

            var key = KeySymName(s.HotKeyKey);
            _hotkey = X11HotkeyWatcher.TryCreate(key, s.HotKeyCtrl, s.HotKeyAlt, s.HotKeyShift,
                () => global::Avalonia.Threading.Dispatcher.UIThread.Post(ShowFromTray));
            AppHost.Logger?.Info(_hotkey != null
                ? $"全局热键已启用: {key}"
                : "全局热键启用失败（没有 DISPLAY 或 keymap 不含该键）");
        }

        // Windows 存的是虚拟键码；X11 要 keysym 名。字母/数字直接映射，其余退回 u。
        private static string KeySymName(uint vk)
        {
            if (vk >= 'A' && vk <= 'Z') return ((char)('a' + (vk - 'A'))).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            return "u";
        }

        private void ShowPuck()
        {
            if (_puck != null) return;
            _puck = new PuckWindow();
            _puck.Closed += (_, __) => _puck = null;
            _puck.Show();
        }
    }
}
