using System;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>两个包的清单对比：只比条目与大小，不读内容。</summary>
    public partial class DiffWindow : Window
    {
        private readonly string _left;
        private readonly string _right;

        public DiffWindow()
        {
            InitializeComponent();
        }

        public DiffWindow(string left, string right) : this()
        {
            _left = left;
            _right = right;
            Title = "包对比 — " + Path.GetFileName(left) + " ↔ " + Path.GetFileName(right);
            HeaderText.Text = Path.GetFileName(left) + "  ↔  " + Path.GetFileName(right);
            _ = RunAsync();
        }

        private async System.Threading.Tasks.Task RunAsync()
        {
            DiffBox.Text = "正在读取两个包的清单…";
            try
            {
                var l = await AppHost.Worker.PreviewAsync(_left).ConfigureAwait(true);
                var r = await AppHost.Worker.PreviewAsync(_right).ConfigureAwait(true);
                if (!l.Success || !r.Success)
                {
                    DiffBox.Text = "读不出清单：" + ((!l.Success ? l.Diagnosis : r.Diagnosis) ?? "包已损坏或需要密码");
                    return;
                }
                DiffBox.Text = Format(ArchiveDiff.Compare(l.Entries, r.Entries));
            }
            catch (Exception ex)
            {
                DiffBox.Text = "对比失败：" + ex.Message;
            }
        }

        private static string Format(ArchiveDiff d)
        {
            var sb = new StringBuilder();
            sb.AppendLine(d.Summarize());
            sb.AppendLine();
            if (d.OnlyInLeft.Count > 0)
            {
                sb.AppendLine("仅左侧有 (" + d.OnlyInLeft.Count + "):");
                foreach (var p in d.OnlyInLeft) sb.AppendLine("  - " + p);
                sb.AppendLine();
            }
            if (d.OnlyInRight.Count > 0)
            {
                sb.AppendLine("仅右侧有 (" + d.OnlyInRight.Count + "):");
                foreach (var p in d.OnlyInRight) sb.AppendLine("  + " + p);
                sb.AppendLine();
            }
            if (d.Changed.Count > 0)
            {
                sb.AppendLine("大小不同 (" + d.Changed.Count + "):");
                foreach (var c in d.Changed) sb.AppendLine($"  ~ {c.Path}  {c.LeftSize} → {c.RightSize}");
            }
            return sb.ToString();
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
