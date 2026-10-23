using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UZIP2.Avalonia.Shell
{
    public partial class PreviewWindow : Window
    {
        private readonly string _archive;

        public PreviewWindow()
        {
            InitializeComponent();
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
            EntryList.ItemsSource = listing.Entries;
            HeaderText.Text = $"{_archive} — {listing.Entries.Count} 项";
        }

        private void OnExtractAll(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_archive)) AppHost.Worker.EnqueueExtract(new[] { _archive });
            Close();
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
