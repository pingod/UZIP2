using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell
{
    public partial class PreviewWindow : Window
    {
        private readonly string _archive;
        private readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();

        public PreviewWindow()
        {
            InitializeComponent();
            EntryList.ItemsSource = _rows;
            _archive = null;
        }

        public PreviewWindow(string archive) : this()
        {
            _archive = archive;
            Title = "包内预览 — " + System.IO.Path.GetFileName(archive);
            _ = LoadAsync();
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            HeaderText.Text = "正在读取清单…";
            var listing = await AppHost.Worker.PreviewAsync(_archive).ConfigureAwait(true);
            if (!listing.Success)
            {
                HeaderText.Text = "读不出清单：" + (listing.Diagnosis ?? "包已损坏或需要密码");
                return;
            }
            _rows.Clear();
            foreach (var e in listing.Entries) _rows.Add(new Row(e));
            HeaderText.Text = $"{_archive} — {listing.Entries.Count} 项（勾选后可只解压选中的）";
        }

        private void OnSelectAll(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows) r.IsSelected = true;
        }

        private void OnSelectNone(object sender, RoutedEventArgs e)
        {
            foreach (var r in _rows) r.IsSelected = false;
        }

        private void OnExtractSelected(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_archive)) return;
            var selected = _rows.Where(r => r.IsSelected).Select(r => r.Entry.Path).ToList();
            if (selected.Count == 0) return;
            AppHost.Worker.EnqueueExtract(new[] { _archive }, null, selected);
            Close();
        }

        private void OnExtractAll(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_archive)) AppHost.Worker.EnqueueExtract(new[] { _archive });
            Close();
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        /// <summary>预览行：勾选状态只存在界面里，不参与清单解析。</summary>
        public sealed class Row : INotifyPropertyChanged
        {
            private bool _selected;
            public Row(ArchiveEntry entry) { Entry = entry; }

            public ArchiveEntry Entry { get; }
            public string Path => Entry.Path;
            public string SizeText => Entry.IsFolder ? "<目录>" : Human(Entry.Size);

            public bool IsSelected
            {
                get => _selected;
                set
                {
                    if (_selected == value) return;
                    _selected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            static string Human(long bytes)
            {
                if (bytes < 1024) return bytes + " B";
                double kb = bytes / 1024.0;
                if (kb < 1024) return kb.ToString("0.#") + " KB";
                return (kb / 1024.0).ToString("0.#") + " MB";
            }
        }
    }
}
