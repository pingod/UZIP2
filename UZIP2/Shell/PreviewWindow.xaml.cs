using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Shell
{
    // 包内清单预览 + 勾选解压。清单来自 7z l -slt，只读头部，百 MB 包也是几十毫秒级。
    public partial class PreviewWindow : Window
    {
        public sealed partial class Row : ObservableObject
        {
            public string FullName { get; set; }   // 包内原始路径，-i@ 清单用它
            public string Name { get; set; }       // 显示用
            public string SizeText { get; set; }
            public string Method { get; set; }
            public string Icon { get; set; }
            public bool IsFolder { get; set; }
            public long Size { get; set; }
            [ObservableProperty] private bool _selected;
        }

        private readonly string _archive;
        private readonly ArchiveWorker _worker;
        private readonly List<Row> _rows = new List<Row>();
        private bool _ready;

        public PreviewWindow(string archive, string password, ArchiveWorker worker)
        {
            InitializeComponent();
            _archive = archive;
            _worker = worker;
            Title_.Text = System.IO.Path.GetFileName(archive);
            Status.Text = "正在读取包内清单…";
            Loaded += async (s, e) => await LoadAsync(password);
        }

        async System.Threading.Tasks.Task LoadAsync(string password)
        {
            ArchiveListing listing;
            try
            {
                listing = await _worker.PreviewAsync(_archive, password);
            }
            catch (Exception ex)
            {
                Status.Text = "读取失败: " + ex.Message;
                return;
            }

            if (!listing.Success)
            {
                Status.Text = listing.Diagnosis ?? "读取包内清单失败";
                return;
            }
            if (listing.Entries.Count == 0)
            {
                Status.Text = "包内没有文件";
                return;
            }

            foreach (var en in listing.Entries)
            {
                _rows.Add(new Row
                {
                    FullName = en.Path,
                    Name = en.Path,
                    IsFolder = en.IsFolder,
                    Size = en.Size,
                    SizeText = en.IsFolder ? "" : HumanSize(en.Size),
                    Method = en.Method ?? "",
                    Icon = en.IsFolder ? "Folder24" : "Document24",
                    Selected = true,
                });
            }

            Items.ItemsSource = _rows;
            _ready = true;
            ExtractAllBtn.IsEnabled = true;
            RefreshSummary();
        }

        // ---- 勾选 ----

        void OnItemCheck(object sender, RoutedEventArgs e) => RefreshSummary();

        void RefreshSummary()
        {
            if (!_ready) return;
            var sel = _rows.Where(r => r.Selected && !r.IsFolder).ToList();
            long bytes = _rows.Where(r => r.Selected).Sum(r => r.Size);
            ExtractBtn.IsEnabled = sel.Count > 0 || _rows.Any(r => r.Selected && r.IsFolder);
            Status.Text = $"共 {_rows.Count(r => !r.IsFolder)} 个文件，已选 {sel.Count} 个，约 {HumanSize(bytes)}";
        }

        static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return v.ToString(i == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[i];
        }

        // ---- 选择操作 ----

        void SetAll(bool value)
        {
            if (!_ready) return;
            foreach (var r in _rows) r.Selected = value;
            RefreshSummary();
        }

        void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);
        void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(false);

        void OnInvert(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            foreach (var r in _rows) r.Selected = !r.Selected;
            RefreshSummary();
        }

        // ---- 解压 ----

        void OnExtractSelected(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            var picked = _rows.Where(r => r.Selected).ToList();
            if (picked.Count == 0) return;

            var paths = new List<string>();
            foreach (var r in picked)
            {
                paths.Add(r.FullName);
                if (!r.IsFolder) continue;
                var prefix = r.FullName + "\\";
                paths.AddRange(_rows.Where(x => !x.IsFolder && x.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                   .Select(x => x.FullName));
            }
            var unique = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            _worker.EnqueueExtract(new[] { _archive }, null, unique);
            Close();
        }

        void OnExtractAll(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            _worker.EnqueueExtract(new[] { _archive });
            Close();
        }

        void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
