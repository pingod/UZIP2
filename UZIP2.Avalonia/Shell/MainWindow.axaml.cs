using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UZIP2.Avalonia.Shell
{
    public partial class MainWindow : Window
    {
        private readonly Pages.HomeView _home = new Pages.HomeView();
        private readonly Pages.HistoryView _history = new Pages.HistoryView();
        private readonly Pages.SettingsView _settings = new Pages.SettingsView();
        private PuckWindow _puck;

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
        }

        private void OnNavHome(object sender, RoutedEventArgs e) => Show(_home, NavHome);
        private void OnNavHistory(object sender, RoutedEventArgs e) => Show(_history, NavHistory);
        private void OnNavSettings(object sender, RoutedEventArgs e) => Show(_settings, NavSettings);

        private void Show(Control page, Button nav)
        {
            PageHost.Content = page;
            if (page == _history) _history.Refresh();
            if (page == _settings) _settings.Reload();
            Highlight(nav);
        }

        private void Highlight(Button active)
        {
            foreach (var b in new[] { NavHome, NavHistory, NavSettings })
                b.Background = b == active
                    ? global::Avalonia.Media.Brush.Parse("#2D2D33")
                    : global::Avalonia.Media.Brushes.Transparent;
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

        private void ShowPuck()
        {
            if (_puck != null) return;
            _puck = new PuckWindow();
            _puck.Closed += (_, __) => _puck = null;
            _puck.Show();
        }
    }
}
