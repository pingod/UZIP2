using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UZIP2.Models
{
    public enum JobStatus { Queued, Running, Success, Failed, Cancelled }

    // 任务队列条目，直接供 UI 绑定（移植旧版批量解压/压缩的单文件处理单元）
    public partial class JobEntry : ObservableObject
    {
        public long Id { get; set; }
        public string Kind { get; set; }            // Extract | Compress
        public string Archive { get; set; }         // 拖入的档案/源文件
        public string Target { get; set; }          // 输出目录(可选覆盖)
        public List<string> Sources { get; set; }   // 合并压缩时的全部来源
        public List<string> SelectedEntries { get; set; } // 勾选解压时的包内路径，null=全部

        internal string ManualPassword;             // Retry 时人工指定的密码
        internal int Depth;                         // 多级解压深度
        internal bool CancelRequested;
        internal bool Flat;                         // "解压到当前文件夹"：无视建目录设置
        internal System.DateTime StartedUtc;        // 进入 Running 的时刻，用于历史耗时

        [ObservableProperty] private JobStatus status;
        [ObservableProperty] private double? percent;
        [ObservableProperty] private string currentFile;
        [ObservableProperty] private int done;
        [ObservableProperty] private int total = 1;
        [ObservableProperty] private string diagnosis;
        [ObservableProperty] private string usedPassword;
        [ObservableProperty] private string outputDir;

        public string DisplayName => Archive == null ? "" : Path.GetFileName(Archive);
    }
}
