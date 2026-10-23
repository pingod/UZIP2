using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell.Pages
{
    /// <summary>
    /// 记录类型 → 能否重跑：校验（Test）没有"重跑"语义，
    /// 按解压重跑等于把"校验失败"变成"真的把包解开了"。
    /// 对齐 WPF HistoryRow.CanRetry（那边是绑定 bool，这里复用同一个判断）。
    /// </summary>
    public sealed class HistoryKindRetryableConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var kind = value as string;
            if (string.IsNullOrEmpty(kind)) return false;
            return !string.Equals(kind, "Test", StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

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
            HintText.Text = "已清空历史";
            Refresh();
        }

        // 对齐 WPF HistoryViewModel.Retry：带上记录里的口令重跑 —— 口令不在密码本里时，
        // 裸重跑只会把一个本来成功的作业变成必然失败的作业。目的地不传，跟随当前设置。
        private void OnRetry(object sender, RoutedEventArgs e)
        {
            var entry = (sender as Control)?.DataContext as HistoryEntry;
            if (entry == null || !CanRetry(entry) || string.IsNullOrEmpty(entry.Source)) return;

            var pw = string.IsNullOrEmpty(entry.Password) ? null : entry.Password;
            try
            {
                if (string.Equals(entry.Kind, "Compress", StringComparison.OrdinalIgnoreCase))
                    AppHost.Worker.EnqueueCompress(new[] { entry.Source }, manualPassword: pw);
                else
                    AppHost.Worker.EnqueueExtract(new[] { entry.Source }, manualPassword: pw);
                HintText.Text = "已重新排队，可在主页查看进度";
            }
            catch (Exception ex)
            {
                HintText.Text = "重跑失败：" + ex.Message;
            }
            Refresh();
        }

        private static bool CanRetry(HistoryEntry entry)
            => entry != null && !string.Equals(entry.Kind, "Test", StringComparison.OrdinalIgnoreCase);
    }
}
