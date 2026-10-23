using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UZIP2.Models;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>转格式对话框：只选目标格式，输出目录沿用原包所在目录。</summary>
    public partial class ConvertDialog : Window
    {
        private readonly string _archive;

        public ConvertDialog()
        {
            InitializeComponent();
            _archive = null;
        }

        public ConvertDialog(string archive) : this()
        {
            _archive = archive;
            SourceText.Text = Path.GetFileName(archive);
            FormatBox.ItemsSource = Enumerable.Range(0, 7)
                .Select(i => i + " — " + ArchiveFormat.TypeName(i) + " (" + ArchiveFormat.Extension(i) + ")")
                .ToList();
            FormatBox.SelectedIndex = 1;   // 默认 7z
        }

        private void OnConvert(object sender, RoutedEventArgs e)
        {
            var type = FormatBox.SelectedIndex;
            if (!string.IsNullOrEmpty(_archive) && type >= 0)
                AppHost.Worker.EnqueueConvert(new[] { _archive }, type, Path.GetDirectoryName(_archive));
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e) => Close();
    }
}
