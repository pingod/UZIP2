using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using UZIP2.Services;

namespace UZIP2.Shell
{
    // 压缩日志检索: 找回"这个包当时用的什么密码"，比 Notepad 里 Ctrl+F 快
    public partial class CompressLogWindow : Window
    {
        public sealed class Row
        {
            public string TimeText { get; set; }
            public string FileName { get; set; }
            public string FolderText { get; set; }
            public string PasswordText { get; set; }
            public string Password { get; set; }
            public string FullPath { get; set; }
        }

        private readonly CompressLogService _log;
        private List<CompressLogRecord> _records = new List<CompressLogRecord>();

        public CompressLogWindow(CompressLogService log)
        {
            InitializeComponent();
            _log = log;
            Loaded += (s, e) => Reload();
        }

        void Reload()
        {
            _records = _log.ReadAll();
            ApplyFilter();
        }

        Row ToRow(CompressLogRecord r)
        {
            string folder = "";
            try { folder = Path.GetDirectoryName(r.Path) ?? ""; } catch { }
            var name = r.FileName;
            if (string.IsNullOrEmpty(name))
            {
                try { name = Path.GetFileName(r.Path); } catch { }
            }
            var pw = r.Password;
            string label;
            if (string.IsNullOrEmpty(pw)) label = "—";
            else if (RevealPw != null && RevealPw.IsChecked == true) label = pw;
            else label = pw.Length <= 3 ? "●●●" : "●●●●●●";
            return new Row
            {
                TimeText = r.Time == default ? "" : r.TimeText,
                FileName = name ?? "",
                FolderText = folder,
                Password = pw,
                PasswordText = label,
                FullPath = r.Path ?? "",
            };
        }

        void ApplyFilter()
        {
            var q = (SearchBox?.Text ?? "").Trim();
            var hits = (string.IsNullOrEmpty(q)
                ? _records
                : _records.Where(r =>
                    (r.FileName?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (r.Path?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (r.Password?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)).ToList();

            var rows = hits.Select(ToRow).ToList();
            Items.ItemsSource = rows;
            CopyPwBtn.IsEnabled = false;
            Status.Text = _records.Count == 0
                ? "还没有压缩记录，压缩一个文件后这里就会出现。"
                : $"共 {_records.Count} 条，匹配 {rows.Count} 条";
        }

        void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        void OnRevealChanged(object sender, RoutedEventArgs e) => ApplyFilter();

        void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
            => CopyPwBtn.IsEnabled = Items.SelectedItem is Row r && !string.IsNullOrEmpty(r.Password);

        void OnReload(object sender, RoutedEventArgs e) => Reload();

        void OnCopyPassword(object sender, RoutedEventArgs e)
        {
            if (Items.SelectedItem is Row r && !string.IsNullOrEmpty(r.Password))
                TrySetClipboard(r.Password);
        }

        void OnCopyPath(object sender, RoutedEventArgs e)
        {
            if (Items.SelectedItem is Row r && !string.IsNullOrEmpty(r.FullPath))
                TrySetClipboard(r.FullPath);
        }

        static void TrySetClipboard(string text)
        {
            try { Clipboard.SetText(text); } catch { }
        }

        void OnOpenLocation(object sender, RoutedEventArgs e)
        {
            if (!(Items.SelectedItem is Row r) || string.IsNullOrEmpty(r.FullPath)) return;
            try
            {
                if (File.Exists(r.FullPath))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + r.FullPath + "\"")
                    { UseShellExecute = true });
                else if (Directory.Exists(Path.GetDirectoryName(r.FullPath)))
                    Process.Start(new ProcessStartInfo(Path.GetDirectoryName(r.FullPath)) { UseShellExecute = true });
            }
            catch { }
        }

        void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
