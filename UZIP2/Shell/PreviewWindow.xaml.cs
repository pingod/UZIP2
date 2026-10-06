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
        private readonly string _password;
        private readonly ArchiveWorker _worker;
        private readonly List<Row> _rows = new List<Row>();
        private List<Row> _listRows;
        private bool _ready;
        private bool _diff;

        public PreviewWindow(string archive, string password, ArchiveWorker worker)
        {
            InitializeComponent();
            _archive = archive;
            _password = password;
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
            _listRows = new List<Row>(_rows);
            _ready = true;
            ExtractAllBtn.IsEnabled = true;
            DiffBtn.IsEnabled = true;
            RefreshSummary();
        }

        // ---- 勾选 ----

        void OnItemCheck(object sender, RoutedEventArgs e) => RefreshSummary();

        void RefreshSummary()
        {
            if (!_ready || _diff) return;
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
            if (!_ready || _diff) return;
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

        // ---- 与另一个包对比清单 ----

        void OnDiff(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要对比的压缩包",
                Filter = "压缩包|*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.wim;*.iso;*.cab|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };
            if (dlg.ShowDialog(this) != true) return;
            var other = dlg.FileName;
            if (string.Equals(System.IO.Path.GetFullPath(other), System.IO.Path.GetFullPath(_archive),
                              StringComparison.OrdinalIgnoreCase))
            { Status.Text = "不能和自身对比"; return; }
            _ = ShowDiffAsync(other);
        }

        async System.Threading.Tasks.Task ShowDiffAsync(string other)
        {
            Status.Text = "正在读取 " + System.IO.Path.GetFileName(other) + " 的清单…";
            ArchiveListing right;
            try
            {
                // 先拿当前包的口令试（同批分卷/同项目包常见），不行再由 worker 走密码本
                right = await _worker.PreviewAsync(other, _password);
            }
            catch (Exception ex)
            { Status.Text = "对比失败: " + ex.Message; return; }
            if (!right.Success)
            { Status.Text = "对比失败: " + (right.Diagnosis ?? "读取包内清单失败"); return; }

            var d = UZIP2.Models.ArchiveDiff.Compare(
                _listRows.Where(r => !r.IsFolder).Select(ToEntry).ToList(),
                right.Entries.Where(e => !e.IsFolder).ToList());

            _rows.Clear();
            foreach (var p in d.OnlyInLeft)
                _rows.Add(new Row { FullName = p, Name = p, SizeText = "只在当前包", Icon = "Document24" });
            foreach (var c in d.Changed)
                _rows.Add(new Row { FullName = c.Path, Name = c.Path, Icon = "Document24",
                                     SizeText = HumanSize(c.LeftSize) + " → " + HumanSize(c.RightSize) });
            foreach (var p in d.OnlyInRight)
                _rows.Add(new Row { FullName = p, Name = p, SizeText = "只在对比包", Icon = "Document24" });

            _diff = true;
            Items.ItemsSource = null;
            Items.ItemsSource = _rows;
            // 差异视图里的行不是可解压条目，勾选框只会误导
            ExtractBtn.IsEnabled = false;
            ExtractAllBtn.IsEnabled = false;
            DiffBtn.IsEnabled = false;
            BackBtn.Visibility = Visibility.Visible;
            Status.Text = "对比 " + System.IO.Path.GetFileName(other) + "：" + d.Summarize();
        }

        void OnBackToList(object sender, RoutedEventArgs e)
        {
            _diff = false;
            _rows.Clear();
            _rows.AddRange(_listRows);
            Items.ItemsSource = null;
            Items.ItemsSource = _rows;
            DiffBtn.IsEnabled = true;
            BackBtn.Visibility = Visibility.Collapsed;
            ExtractAllBtn.IsEnabled = true;
            RefreshSummary();
        }

        static ArchiveEntry ToEntry(Row r) => new ArchiveEntry(r.FullName, r.Size, false, r.Method ?? "");
    }
}
