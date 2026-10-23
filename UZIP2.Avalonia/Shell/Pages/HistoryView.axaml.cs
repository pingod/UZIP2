using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell.Pages
{
    public partial class HistoryView : UserControl
    {
        public HistoryView()
        {
            InitializeComponent();
            Refresh();
        }

        public void Refresh()
        {
            var all = AppHost.History.ReadAll() ?? Array.Empty<HistoryEntry>();
            var q = (SearchBox.Text ?? "").Trim();
            var rows = string.IsNullOrEmpty(q)
                ? all
                : all.Where(h => (h.Source ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                              || (h.Status ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            HistoryList.ItemsSource = rows;
            CountText.Text = rows.Count == 0 ? "没有匹配的记录" : $"共 {rows.Count} 条（上限 {HistoryService.MaxEntries}）";
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

        private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

        private void OnClear(object sender, RoutedEventArgs e)
        {
            AppHost.History.Clear();
            Refresh();
        }
    }
}
