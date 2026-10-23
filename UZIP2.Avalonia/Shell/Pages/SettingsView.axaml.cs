using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell.Pages
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
            Load();
        }

        private void Load()
        {
            var s = AppHost.Settings.Current;
            SevenZipText.Text = AppHost.Zip.SevenZipPath
                ?? "未找到 7-Zip（7z / 7zz / 7za）。请安装 7zip 包，或在下面指定可执行文件路径。";
            Custom7zBox.Text = s.Customize7z ? s.Customize7zPath : "";
            ParallelExtractBox.Value = s.ParallelExtract;
            ParallelCompressBox.Value = s.ParallelCompress;
            VerifyBox.IsChecked = s.VerifyAfterCompress;
            PreviewBox.IsChecked = s.PreviewBeforeExtract;
            OpenAfterBox.IsChecked = s.AutoOpenAfterExtract;
            RecycleBox.IsChecked = s.DeleteToRecycle;
            WatchEnabledBox.IsChecked = s.WatchEnabled;
            WatchFolderBox.Text = s.WatchFolder;
            VersionText.Text = $"UZIP {UpdateService.CurrentVersion()} · 配置目录 {AppHost.Layout.ConfigDirectory}";
            SavedText.Text = "";
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var custom = (Custom7zBox.Text ?? "").Trim();
            AppHost.Settings.Save(s =>
            {
                s.Customize7z = custom.Length > 0;
                s.Customize7zPath = custom;
                s.ParallelExtract = (int)(ParallelExtractBox.Value ?? 3);
                s.ParallelCompress = (int)(ParallelCompressBox.Value ?? 2);
                s.VerifyAfterCompress = VerifyBox.IsChecked == true;
                s.PreviewBeforeExtract = PreviewBox.IsChecked == true;
                s.AutoOpenAfterExtract = OpenAfterBox.IsChecked == true;
                s.DeleteToRecycle = RecycleBox.IsChecked == true;
                s.WatchEnabled = WatchEnabledBox.IsChecked == true;
                s.WatchFolder = WatchFolderBox.Text ?? "";
            });
            AppHost.Watcher.Apply();
            SavedText.Text = "已保存";
            SevenZipText.Text = AppHost.Zip.SevenZipPath ?? "未找到 7-Zip（7z / 7zz / 7za）。";
        }
    }
}
