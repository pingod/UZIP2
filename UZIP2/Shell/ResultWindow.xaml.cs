using System.Collections.Generic;
using System.Linq;
using System.Windows;
using UZIP2.Models;

namespace UZIP2.Shell
{
    // 旧版独立结果窗口：一批任务跑完后汇总成败。默认关闭（内联卡片已覆盖），由设置项 ResultWindow 开启。
    public partial class ResultWindow : Window
    {
        public sealed class Row
        {
            public string Name { get; set; }
            public string Kind { get; set; }
            public string Outcome { get; set; }
            public string Detail { get; set; }
        }

        public ResultWindow(IReadOnlyList<JobEntry> jobs)
        {
            InitializeComponent();
            var rows = jobs.Select(j => new Row
            {
                Name = j.DisplayName,
                Kind = j.Kind == "Extract" ? "解压" : "压缩",
                Outcome = OutcomeText(j.Status),
                Detail = j.Status == JobStatus.Success ? j.OutputDir ?? "" : j.Diagnosis ?? "",
            }).ToList();

            int ok = rows.Count(r => r.Outcome == "成功");
            Summary.Text = $"共 {rows.Count} 项，成功 {ok} 项，失败/取消 {rows.Count - ok} 项";
            Items.ItemsSource = rows;
        }

        static string OutcomeText(JobStatus status)
        {
            switch (status)
            {
                case JobStatus.Success: return "成功";
                case JobStatus.Failed: return "失败";
                case JobStatus.Cancelled: return "已取消";
                default: return status.ToString();
            }
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
