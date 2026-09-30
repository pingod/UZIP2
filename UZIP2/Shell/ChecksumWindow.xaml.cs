using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using UZIP2.Services;

namespace UZIP2.Shell
{
    // 校验和面板：SHA-256 + 旁挂 .sfv/.md5/.sha256 逐条核对。
    // 大文件流式读取，边算边把行填进列表，界面不会卡住。
    public partial class ChecksumWindow : Window
    {
        public sealed partial class Row : ObservableObject
        {
            public string FullPath { get; set; }
            public string Name { get; set; }
            public string SizeText { get; set; }
            public ObservableCollection<Line> Sidecars { get; } = new ObservableCollection<Line>();
            [ObservableProperty] private string _sha256;
            [ObservableProperty] private string _error;

            public bool HasHash => !string.IsNullOrEmpty(Sha256);
            public bool HasError => !string.IsNullOrEmpty(Error);

            partial void OnSha256Changed(string value) => OnPropertyChanged(nameof(HasHash));
            partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
        }

        public sealed class Line
        {
            public string Text { get; set; }
            public string Icon { get; set; }
            public Brush Brush { get; set; }
        }

        readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();
        readonly List<string> _files;

        public ChecksumWindow(IEnumerable<string> files)
        {
            InitializeComponent();
            _files = (files ?? Enumerable.Empty<string>()).Where(File.Exists).ToList();
            Rows.ItemsSource = _rows;
            Loaded += async (_, _) => await RunAsync();
        }

        async Task RunAsync()
        {
            int bad = 0;
            for (int i = 0; i < _files.Count; i++)
            {
                Status.Text = $"正在校验 {i + 1}/{_files.Count}…";
                var row = new Row
                {
                    FullPath = _files[i],
                    Name = Path.GetFileName(_files[i]),
                    SizeText = HumanSize(new FileInfo(_files[i]).Length),
                };
                _rows.Add(row);
                try
                {
                    var report = await ChecksumService.VerifyAsync(_files[i]);
                    row.Sha256 = report.Sha256;
                    row.Error = report.Error;
                    foreach (var c in report.Sidecars)
                    {
                        row.Sidecars.Add(Describe(c));
                        if (!c.PassedAll && !c.SelectedOk) bad++;
                    }
                }
                catch (Exception ex)
                {
                    row.Error = ex.Message;
                }
            }
            Status.Text = _files.Count == 0
                ? "没有可校验的文件"
                : bad == 0
                    ? $"已校验 {_files.Count} 个文件"
                    : $"已校验 {_files.Count} 个文件，{bad} 项校验未通过";
        }

        static Line Describe(SidecarCheck c)
        {
            if (!c.SelectedCovered)
                return Make($"{c.Sidecar}：未收录该文件（共 {c.Total} 条记录）", "Info24", Tertiary);
            if (c.SelectedOk && c.Mismatched == 0 && c.Missing == 0)
                return Make($"{c.Sidecar}：全部 {c.Total} 项通过", "CheckmarkCircle24", Success);
            if (c.SelectedOk)
                return Make($"{c.Sidecar}：本文件通过；{c.Detail}", "Warning24", Warning);
            return Make($"{c.Sidecar}：本文件不符；{c.Detail}", "DismissCircle24", Critical);
        }

        static Line Make(string text, string icon, Brush brush)
            => new Line { Text = text, Icon = icon, Brush = brush };

        static Brush Find(string key, Brush fallback)
            => Application.Current.TryFindResource(key) as Brush ?? fallback;

        static Brush Tertiary => Find("TextFillColorTertiaryBrush", Brushes.Gray);
        static Brush Success => Find("SystemFillColorSuccessBrush", Brushes.Green);
        static Brush Warning => Find("SystemFillColorCautionBrush", Brushes.DarkOrange);
        static Brush Critical => Find("SystemFillColorCriticalBrush", Brushes.Red);

        void OnCopy(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not Row row || string.IsNullOrEmpty(row.Sha256)) return;
            try
            {
                Clipboard.SetText(row.Sha256);
                if (sender is Wpf.Ui.Controls.Button b) b.Content = "已复制";
            }
            catch { }
        }

        void OnClose(object sender, RoutedEventArgs e) => Close();

        static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[u];
        }
    }
}
