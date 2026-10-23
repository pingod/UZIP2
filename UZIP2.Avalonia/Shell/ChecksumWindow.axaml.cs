using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>SHA-256 + 旁挂校验文件（SHA256SUMS / .sfv / .md5…）逐条比对。</summary>
    public partial class ChecksumWindow : Window
    {
        private readonly string _path;

        public ChecksumWindow()
        {
            InitializeComponent();
            _path = null;
        }

        public ChecksumWindow(string path) : this()
        {
            _path = path;
            Title = "校验和 — " + Path.GetFileName(path);
            HeaderText.Text = path;
            _ = RunAsync();
        }

        private async System.Threading.Tasks.Task RunAsync()
        {
            ReportBox.Text = "正在计算…";
            try
            {
                var report = await ChecksumService.VerifyAsync(_path).ConfigureAwait(true);
                ReportBox.Text = Format(report);
            }
            catch (Exception ex)
            {
                ReportBox.Text = "计算失败：" + ex.Message;
            }
        }

        private static string Format(ChecksumReport r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("文件: " + r.Path);
            sb.AppendLine("SHA-256: " + (r.Sha256 ?? "(读取失败)"));
            if (!string.IsNullOrEmpty(r.Error)) sb.AppendLine("错误: " + r.Error);
            sb.AppendLine();
            if (r.Sidecars.Count == 0)
            {
                sb.AppendLine("没有找到旁挂校验文件（SHA256SUMS / *.sha256 / *.sfv / *.md5）。");
                return sb.ToString();
            }
            foreach (var s in r.Sidecars)
            {
                sb.AppendLine("[" + (s.PassedAll ? "通过" : "不一致") + "] " + s.Sidecar);
                sb.AppendLine($"  记录 {s.Total}，匹配 {s.Passed}，不符 {s.Mismatched}，缺失 {s.Missing}");
                if (!string.IsNullOrEmpty(s.Detail)) sb.AppendLine("  " + s.Detail);
            }
            return sb.ToString();
        }

        private async void OnCopy(object sender, RoutedEventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(ReportBox.Text ?? "");
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
