using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using UZIP2.Services;
using UZIP2.ViewModel;
using ToggleSwitch = Wpf.Ui.Controls.ToggleSwitch;

namespace UZIP2.Shell.Pages
{
    public partial class SettingsView : UserControl
    {
        private SettingsViewModel Vm => (SettingsViewModel)DataContext;
        private bool _loading;

        public SettingsView()
        {
            InitializeComponent();
            DataContext = App.Services.GetRequiredService<SettingsViewModel>();
            Loaded += (_, _) => LoadAll();
        }

        void LoadAll()
        {
            _loading = true;
            try
            {
                foreach (var sw in Logical(this).OfType<ToggleSwitch>())
                    if (sw.Tag is string tag)
                        sw.IsChecked = Vm.Read(tag) as bool? ?? false;

                SelectCombo(ThemeBox, Vm.Read("Theme")?.ToString());
                SelectComboInt(ExtractModeBox, (int)Vm.Read("ExtractOutMode"));
                SelectCombo(CoverBox, Vm.Read("ExtractCoverMode")?.ToString());
                SelectComboInt(CompressModeBox, (int)Vm.Read("CompressOutMode"));
                SelectComboInt(TypeBox, (int)Vm.Read("CompressType"));
                SelectComboInt(LevelBox, (int)Vm.Read("CompressLevel"));
                SelectComboInt(PwModeBox, (int)Vm.Read("PasswordMode"));
                SelectComboInt(ReadModeBox, (int)Vm.Read("ReadPasswordMode"));
                SelectComboInt(ParallelExtractBox, ClampToItem(ParallelExtractBox, (int)Vm.Read("ParallelExtract")));
                SelectComboInt(ParallelCompressBox, ClampToItem(ParallelCompressBox, (int)Vm.Read("ParallelCompress")));

                foreach (var tb in Logical(this).OfType<TextBox>())
                    if (tb.Tag is string tag && !(tb is { IsReadOnly: true }))
                        tb.Text = Vm.Read(tag)?.ToString() ?? "";

                HotkeyBox.Text = Vm.HotkeyText;
                Detected7zPath.Text = App.Services.GetRequiredService<SevenZipClient>().SevenZipPath ?? "未检测到";
                RefreshShellStatus();
            }
            finally { _loading = false; }
        }

        static void SelectCombo(ComboBox box, string tag)
        {
            foreach (ComboBoxItem item in box.Items)
                if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = item;
                    return;
                }
        }

        static void SelectComboInt(ComboBox box, int value)
        {
            foreach (ComboBoxItem item in box.Items)
                if (item.Tag?.ToString() == value.ToString())
                {
                    box.SelectedItem = item;
                    return;
                }
        }

        // 配置里的并行数可能超出下拉可选范围（或来自旧版本），取不大于它的最大项
        static int ClampToItem(ComboBox box, int value)
        {
            int best = -1;
            foreach (ComboBoxItem item in box.Items)
                if (int.TryParse(item.Tag?.ToString(), out var v) && v <= value && v > best)
                    best = v;
            if (best > 0) return best;
            foreach (ComboBoxItem item in box.Items)
                if (int.TryParse(item.Tag?.ToString(), out var v)) return v;
            return value;
        }

        void OnToggleChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var sw = (ToggleSwitch)sender;
            if (sw.Tag is string tag)
                Vm.SaveProperty(tag, sw.IsChecked == true);
        }

        void OnComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var box = (ComboBox)sender;
            if (box.SelectedItem is ComboBoxItem item && box.Tag is string tag)
                Vm.SaveProperty(tag, item.Tag?.ToString() ?? "");
        }

        void OnIntComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var box = (ComboBox)sender;
            if (box.SelectedItem is ComboBoxItem item && box.Tag is string tag
                && int.TryParse(item.Tag?.ToString(), out var v))
                Vm.SaveProperty(tag, v);
        }

        void OnTextChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var tb = (TextBox)sender;
            if (tb.Tag is string tag)
                Vm.SaveProperty(tag, tb.Text);
        }

        void OnVolumeTextChanged(object sender, TextChangedEventArgs e)
        {
            if (VolumeHint == null) return;
            string hint, brush;
            if (string.IsNullOrWhiteSpace(VolumeBox.Text))
            {
                hint = "不分卷";
                brush = "TextFillColorTertiaryBrush";
            }
            else if (VolumeSize.TryParse(VolumeBox.Text, out _, out long bytes))
            {
                hint = "每卷 " + HumanSize(bytes);
                brush = "TextFillColorTertiaryBrush";
            }
            else
            {
                hint = "格式不对: 数字 + 可选 b/k/m/g，例如 700m";
                brush = "SystemFillColorCriticalBrush";
            }
            VolumeHint.Text = hint;
            VolumeHint.Foreground = TryFindResource(brush) as Brush ?? Brushes.Gray;
        }

        static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0") : v.ToString("0.#")) + " " + units[i];
        }

        void OnAddFolder(object sender, RoutedEventArgs e) => Vm.AddFolder();
        void OnRemoveFolder(object sender, RoutedEventArgs e) => Vm.RemoveFolder(((FrameworkElement)sender).DataContext as SettingsViewModel.CustomFolderRow);
        void OnAddInternal(object sender, RoutedEventArgs e) => Vm.AddInternal();
        void OnRemoveInternal(object sender, RoutedEventArgs e) => Vm.RemoveInternal(((FrameworkElement)sender).DataContext as SettingsViewModel.TextRow);

        void OnHotkeyCapture(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
                return;
            var mods = Keyboard.Modifiers;
            Vm.SetHotkey((uint)KeyInterop.VirtualKeyFromKey(key),
                mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Alt), mods.HasFlag(ModifierKeys.Shift));
            HotkeyBox.Text = Vm.HotkeyText;
            e.Handled = true;
        }

        void OnClearHotkey(object sender, RoutedEventArgs e)
        {
            Vm.SetHotkey(0, false, false, false);
            HotkeyBox.Text = Vm.HotkeyText;
        }

        void OnBrowse7z(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 7z.exe",
                Filter = "7z.exe|7z.exe|可执行文件|*.exe",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            Vm.SaveProperty("Customize7zPath", dlg.FileName);
            Vm.SaveProperty("Customize7z", true);
            foreach (var tb in Logical(this).OfType<TextBox>())
                if (tb.Tag is string t && t == "Customize7zPath")
                    tb.Text = dlg.FileName;
            foreach (var sw in Logical(this).OfType<ToggleSwitch>())
                if (sw.Tag is string t && t == "Customize7z")
                    sw.IsChecked = true;
        }

        void OnBrowseWatchFolder(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择要监听的下载文件夹"
            };
            var cur = Vm.Read("WatchFolder") as string;
            if (!string.IsNullOrEmpty(cur) && System.IO.Directory.Exists(cur)) dlg.SelectedPath = cur;
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            Vm.SaveProperty("WatchFolder", dlg.SelectedPath);
            Vm.SaveProperty("WatchEnabled", true);
            foreach (var tb in Logical(this).OfType<TextBox>())
                if (tb.Tag is string tw && tw == "WatchFolder")
                    tb.Text = dlg.SelectedPath;
            foreach (var sw in Logical(this).OfType<ToggleSwitch>())
                if (sw.Tag is string ts && ts == "WatchEnabled")
                    sw.IsChecked = true;
        }

        void OnOpenLog(object sender, RoutedEventArgs e)
        {
            var path = App.Services.GetRequiredService<IFileLogger>().LatestLogPath;
            if (System.IO.File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        void OnOpenCompressLog(object sender, RoutedEventArgs e)
        {
            var path = App.Services.GetRequiredService<CompressLogService>().LogPath;
            if (System.IO.File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        void OnOpenCompressLogSearch(object sender, RoutedEventArgs e)
        {
            var win = new CompressLogWindow(App.Services.GetRequiredService<CompressLogService>())
            { Owner = Window.GetWindow(this) };
            win.Show();
        }

        // ---- Windows 右键菜单 (HKCU) ----

        void OnShellExpanded(object sender, RoutedEventArgs e) => RefreshShellStatus();

        void OnRefreshShellStatus(object sender, RoutedEventArgs e) => RefreshShellStatus();

        void OnRegisterShell(object sender, RoutedEventArgs e) => ChangeShell(register: true);

        void OnUnregisterShell(object sender, RoutedEventArgs e) => ChangeShell(register: false);

        void ChangeShell(bool register)
        {
            var svc = App.Services.GetRequiredService<ShellMenuService>();
            try
            {
                if (register) svc.Register(ShellMenuService.CurrentExePath);
                else svc.Unregister();
            }
            catch (Exception ex)
            {
                MessageBox.Show("写入注册表失败: " + ex.Message, "UZIP 右键菜单",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            RefreshShellStatus();
        }

        void RefreshShellStatus()
        {
            if (ShellStatus == null) return;
            var svc = App.Services.GetRequiredService<ShellMenuService>();
            var state = svc.State(ShellMenuService.CurrentExePath);
            ShellStatus.Text = state switch
            {
                ShellMenuState.Current => "已注册（指向本程序）",
                ShellMenuState.Stale => "已注册但指向: " + (svc.ProbeCommand() ?? "未知"),
                _ => "未注册",
            };
        }

        static System.Collections.Generic.IEnumerable<DependencyObject> Logical(DependencyObject root)
        {
            foreach (var obj in LogicalTreeHelper.GetChildren(root))
                if (obj is DependencyObject d)
                {
                    yield return d;
                    foreach (var sub in Logical(d))
                        yield return sub;
                }
        }
    }
}
