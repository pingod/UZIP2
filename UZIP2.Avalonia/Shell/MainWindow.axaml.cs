using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UZIP2.Avalonia.Shell
{
    public partial class MainWindow : Window
    {
        private readonly Pages.HomeView _home = new Pages.HomeView();
        private readonly Pages.HistoryView _history = new Pages.HistoryView();
        private readonly Pages.SettingsView _settings = new Pages.SettingsView();

        public MainWindow()
        {
            InitializeComponent();
            PageHost.Content = _home;
            Highlight(NavHome);
        }

        private void OnNavHome(object sender, RoutedEventArgs e) => Show(_home, NavHome);
        private void OnNavHistory(object sender, RoutedEventArgs e) => Show(_history, NavHistory);
        private void OnNavSettings(object sender, RoutedEventArgs e) => Show(_settings, NavSettings);

        private void Show(Control page, Button nav)
        {
            PageHost.Content = page;
            _history.Refresh();
            Highlight(nav);
        }

        private void Highlight(Button active)
        {
            foreach (var b in new[] { NavHome, NavHistory, NavSettings })
                b.Background = b == active
                    ? global::Avalonia.Media.Brush.Parse("#2D2D33")
                    : global::Avalonia.Media.Brushes.Transparent;
        }
    }
}
