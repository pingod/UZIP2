using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell.Pages
{
    /// <summary>
    /// 设置页。逐项对齐 WPF 版（UZIP2/Shell/Pages/SettingsView.xaml）：
    /// 主题、窗口置顶、启动清临时目录、解压输出模式/覆盖策略/未知扩展名、自定义输出目录、
    /// 快捷键组合、记录口令、启动检查更新与「立即检查」、任务排序；其余沿用原有的
    /// 7-Zip 路径、并发、行为、监听目录、桌面集成。
    /// 主题与置顶改完立即生效并落盘（WPF 也是改完就存），其余项点「保存」一起写。
    /// </summary>
    public partial class SettingsView : UserControl
    {
        /// <summary>自定义输出目录最多 8 个：ExtractOutMode 5..12 正好对应数组下标 0..7。</summary>
        private const int MaxCustomFolders = 8;

        // 默认输出位置（值与 ArchiveWorker.ResolveExtractOutDir 的分支一致）
        private static readonly (int Value, string Label)[] ExtractModes =
        {
            (0, "上次使用的目录"),
            (1, "压缩包所在目录"),
            (3, "每次询问"),
            (5, "自定义目录 1"),
            (6, "自定义目录 2"),
            (7, "自定义目录 3"),
            (8, "自定义目录 4"),
            (9, "自定义目录 5"),
            (10, "自定义目录 6"),
            (11, "自定义目录 7"),
            (12, "自定义目录 8"),
        };

        // 同名文件覆盖策略（存的是 7z 的 -ao 开关原值）
        private static readonly (string Value, string Label)[] CoverModes =
        {
            ("-aoa", "覆盖已有"),
            ("-aos", "跳过"),
            ("-aou", "重命名已存在"),
            ("-aot", "重命名新文件"),
        };

        private readonly ObservableCollection<FolderRow> _folders = new ObservableCollection<FolderRow>();
        private uint[] _hotkeyKeys = Array.Empty<uint>();
        private uint _hotkeyVk;
        // 程序性赋值（Load / 捕获后同步下拉框）期间不写设置
        private bool _loading;
        private bool _syncingHotkey;

        public SettingsView()
        {
            InitializeComponent();
            BindChoices();
            FolderList.ItemsSource = _folders;
            // 隧道阶段就抓键：TextBox 自己会吃掉空格/方向键（光标移动），
            // 走 XAML 的 KeyDown（冒泡）拿不到这些键，和 WPF 的 PreviewKeyDown 语义对齐。
            HotkeyBox.AddHandler(KeyDownEvent, OnHotkeyCapture, RoutingStrategies.Tunnel, handledEventsToo: true);
            Load();
        }

        /// <summary>切回设置页时重新读一遍（别处可能改过设置）。</summary>
        public void Reload() => Load();

        /// <summary>自定义输出目录的一行；列表里直接编辑，保存时映射回 CustomFolder。</summary>
        public sealed class FolderRow
        {
            public string Name { get; set; } = "";
            // 不叫 Path：和 Avalonia 的 Path 类同名，绑定里容易看岔
            public string FolderPath { get; set; } = "";
        }

        private void BindChoices()
        {
            ThemeBox.ItemsSource = ThemeApplier.Choices.Select(c => c.Label).ToList();
            ExtractModeBox.ItemsSource = ExtractModes.Select(m => m.Label).ToList();
            CoverBox.ItemsSource = CoverModes.Select(m => m.Label).ToList();
            JobSortBox.ItemsSource = JobOrder.Names.ToList();

            var choices = HotkeyKeyNames.Choices;
            _hotkeyKeys = new uint[choices.Count];
            var labels = new List<string>(choices.Count);
            for (int i = 0; i < choices.Count; i++)
            {
                _hotkeyKeys[i] = choices[i].Vk;
                labels.Add(choices[i].Label);
            }
            HotkeyKeyBox.ItemsSource = labels;
        }

        private void Load()
        {
            _loading = true;
            try
            {
                var s = AppHost.Settings.Current;

                ThemeBox.SelectedIndex = IndexOfTheme(s.Theme);
                WindowOnTopBox.IsChecked = s.WindowOnTop;
                CleanTempBox.IsChecked = s.CleanTempOnStartup;

                SevenZipText.Text = AppHost.Zip.SevenZipPath
                    ?? "未找到 7-Zip（7z / 7zz / 7za）。请安装 7zip 包，或在下面指定可执行文件路径。";
                Custom7zBox.Text = s.Customize7z ? s.Customize7zPath : "";
                LogPasswordsBox.IsChecked = s.LogPasswords;

                ExtractModeBox.SelectedIndex = IndexOfExtractMode(s.ExtractOutMode);
                CoverBox.SelectedIndex = IndexOfCoverMode(s.ExtractCoverMode);
                ExtractUnknowBox.IsChecked = s.ExtractUnknow;
                NameToPasswordBox.IsChecked = s.NameToPassword;
                CreateNewFolderBox.IsChecked = s.CreateNewFolder;
                CreateNameFolderBox.IsChecked = s.CreateNameFolder;
                DeleteFinishFileBox.IsChecked = s.DeleteFinishFile;
                ExtractFilterBox.Text = s.ExtractFilter ?? "";

                _folders.Clear();
                var stored = s.CustomizeFolders ?? new List<CustomFolder>();
                foreach (var f in stored.Take(MaxCustomFolders))
                    _folders.Add(new FolderRow { Name = f.Name ?? "", FolderPath = f.Path ?? "" });
                RefreshFolderCount();

                ParallelExtractBox.Value = s.ParallelExtract;
                ParallelCompressBox.Value = s.ParallelCompress;
                VerifyBox.IsChecked = s.VerifyAfterCompress;
                PreviewBox.IsChecked = s.PreviewBeforeExtract;
                OpenAfterBox.IsChecked = s.AutoOpenAfterExtract;
                RecycleBox.IsChecked = s.DeleteToRecycle;
                TrimSpaceBox.IsChecked = s.TrimSpace;
                JobSortBox.SelectedIndex = IndexOfJobSort(s.JobSort);
                MiniPuckBox.IsChecked = s.MiniPuck;
                DebugModeBox.IsChecked = s.DebugMode;

                WatchEnabledBox.IsChecked = s.WatchEnabled;
                WatchFolderBox.Text = s.WatchFolder ?? "";

                UseHotKeyBox.IsChecked = s.UseHotKey;
                _hotkeyVk = s.HotKeyKey;
                _syncingHotkey = true;
                try
                {
                    HotkeyCtrlBox.IsChecked = s.HotKeyCtrl;
                    HotkeyAltBox.IsChecked = s.HotKeyAlt;
                    HotkeyShiftBox.IsChecked = s.HotKeyShift;
                    HotkeyKeyBox.SelectedIndex = Array.IndexOf(_hotkeyKeys, _hotkeyVk);
                }
                finally { _syncingHotkey = false; }
                RefreshHotkeyText();
                HotkeyHintText.Text = "点一下输入框再按组合键；改完点「保存」，立即生效。";

                CheckUpdateOnStartupBox.IsChecked = s.CheckUpdateOnStartup;
                UpdateStatusText.Text = "";

                VersionText.Text = $"UZIP {UpdateService.CurrentVersion()} · 配置目录 {AppHost.Layout.ConfigDirectory}";
                CapsText.Text = AppHost.Capabilities.Summary();
                UpdateMenuState();
                SavedText.Text = "";

                // App.axaml 里写死了 Dark；设置页在 MainWindow 构造期就建好，
                // 所以启动时在这里把用户选过的主题覆盖上去（没有 Application 时静默跳过）。
                ThemeApplier.Apply(s.Theme);
            }
            finally
            {
                _loading = false;
            }
        }

        // ---- 下拉框下标换算 ----

        private static int IndexOfTheme(string theme)
        {
            var key = ThemeApplier.Normalize(theme);
            for (int i = 0; i < ThemeApplier.Choices.Length; i++)
                if (string.Equals(ThemeApplier.Choices[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        private static int IndexOfExtractMode(int mode)
        {
            for (int i = 0; i < ExtractModes.Length; i++)
                if (ExtractModes[i].Value == mode) return i;
            return 0; // 旧配置里的 2/4 之类不认识的值，按「上次使用的目录」显示
        }

        private static int IndexOfCoverMode(string mode)
        {
            for (int i = 0; i < CoverModes.Length; i++)
                if (string.Equals(CoverModes[i].Value, mode, StringComparison.OrdinalIgnoreCase)) return i;
            return 1; // 默认 -aos（跳过），与 AppSettings 的默认值一致
        }

        private static int IndexOfJobSort(int sort)
        {
            if (sort < 0 || sort >= JobOrder.Names.Length) return JobOrder.AddOrder;
            return sort;
        }

        // ---- 主题 / 置顶：改完立即生效并落盘 ----

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            int i = ThemeBox.SelectedIndex;
            if (i < 0 || i >= ThemeApplier.Choices.Length) return;
            var key = ThemeApplier.Choices[i].Key;
            AppHost.Settings.Save(s => s.Theme = key);
            ThemeApplier.Apply(key);
        }

        private void OnWindowOnTopChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool onTop = WindowOnTopBox.IsChecked == true;
            AppHost.Settings.Save(s => s.WindowOnTop = onTop);
            // App 订阅了 Settings.Changed 会调 MainWindow.ApplyWindowOnTop；
            // 这里再直接设一次，保证没有 App 宿主（测试/独立实例）时也即时生效。
            try
            {
                if (TopLevel.GetTopLevel(this) is Window w) w.Topmost = onTop;
            }
            catch (Exception ex)
            {
                AppHost.Logger?.Warn("设置窗口置顶失败: " + ex.Message);
            }
        }

        // ---- 自定义输出目录 ----

        private void OnAddFolder(object sender, RoutedEventArgs e)
        {
            if (_folders.Count >= MaxCustomFolders)
            {
                SavedText.Text = "自定义目录最多 8 个";
                return;
            }
            _folders.Add(new FolderRow());
            RefreshFolderCount();
        }

        private void OnRemoveFolder(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is FolderRow row)
            {
                _folders.Remove(row);
                RefreshFolderCount();
                return;
            }
            if (FolderList.SelectedItem is FolderRow selected)
            {
                _folders.Remove(selected);
                RefreshFolderCount();
            }
        }

        private void RefreshFolderCount()
        {
            FolderCountText.Text = $"已配置 {_folders.Count} 个（最多 {MaxCustomFolders} 个，对应上面的「自定义目录 1」到「自定义目录 8」）";
        }

        // ---- 快捷键 ----

        private void OnHotkeyCapture(object sender, KeyEventArgs e)
        {
            if (_loading) return;
            // Avalonia 没有 WPF 的 SystemKey：Alt 组合下 e.Key 已经是真实主键
            var key = e.Key;
            if (IsModifierKey(key)) return;

            var vk = HotkeyKeyNames.FromKey(key);
            if (vk == 0)
            {
                HotkeyHintText.Text = "这个键不支持，换一个（字母、数字、F1..F12、方向键、空格等）";
                e.Handled = true;
                return;
            }

            _hotkeyVk = vk;
            _syncingHotkey = true;
            try
            {
                HotkeyCtrlBox.IsChecked = (e.KeyModifiers & KeyModifiers.Control) != 0;
                HotkeyAltBox.IsChecked = (e.KeyModifiers & KeyModifiers.Alt) != 0;
                HotkeyShiftBox.IsChecked = (e.KeyModifiers & KeyModifiers.Shift) != 0;
                HotkeyKeyBox.SelectedIndex = Array.IndexOf(_hotkeyKeys, vk);
            }
            finally { _syncingHotkey = false; }

            RefreshHotkeyText();
            HotkeyHintText.Text = "已记下新组合，点「保存」写入设置，立即生效。";
            e.Handled = true;
        }

        private void OnHotkeyKeyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncingHotkey) return;
            int i = HotkeyKeyBox.SelectedIndex;
            if (i < 0 || i >= _hotkeyKeys.Length) return;
            _hotkeyVk = _hotkeyKeys[i];
            RefreshHotkeyText();
        }

        private void OnHotkeyModifierChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _syncingHotkey) return;
            RefreshHotkeyText();
        }

        private void OnClearHotkey(object sender, RoutedEventArgs e)
        {
            _hotkeyVk = 0;
            _syncingHotkey = true;
            try { HotkeyKeyBox.SelectedIndex = -1; }
            finally { _syncingHotkey = false; }
            RefreshHotkeyText();
            HotkeyHintText.Text = "已清除，点「保存」写入设置。";
        }

        private void RefreshHotkeyText()
        {
            HotkeyBox.Text = HotkeyKeyNames.Describe(
                _hotkeyVk,
                HotkeyCtrlBox.IsChecked == true,
                HotkeyAltBox.IsChecked == true,
                HotkeyShiftBox.IsChecked == true);
        }

        private static bool IsModifierKey(Key key)
        {
            switch (key)
            {
                case Key.None:
                case Key.System:
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LWin:
                case Key.RWin:
                    return true;
                default:
                    return false;
            }
        }

        // ---- 浏览（无头测试里 StorageProvider 为空，静默跳过） ----

        private async void OnBrowse7z(object sender, RoutedEventArgs e)
        {
            var path = await PickFileAsync("选择 7z 可执行文件");
            if (!string.IsNullOrEmpty(path)) Custom7zBox.Text = path;
        }

        private async void OnBrowseWatchFolder(object sender, RoutedEventArgs e)
        {
            var path = await PickFolderAsync("选择要监听的文件夹");
            if (!string.IsNullOrEmpty(path)) WatchFolderBox.Text = path;
        }

        private async System.Threading.Tasks.Task<string> PickFileAsync(string title)
        {
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.StorageProvider == null) return null;
                var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false
                });
                return files != null && files.Count > 0 ? files[0].TryGetLocalPath() : null;
            }
            catch (Exception ex)
            {
                SavedText.Text = "选择文件失败：" + ex.Message;
                return null;
            }
        }

        private async System.Threading.Tasks.Task<string> PickFolderAsync(string title)
        {
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top?.StorageProvider == null) return null;
                var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false
                });
                return dirs != null && dirs.Count > 0 ? dirs[0].TryGetLocalPath() : null;
            }
            catch (Exception ex)
            {
                SavedText.Text = "选择文件夹失败：" + ex.Message;
                return null;
            }
        }

        // ---- 立即检查更新 ----

        private async void OnCheckUpdate(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "正在检查…";
            try
            {
                var info = await UpdateService.CheckAsync();
                if (info == null)
                {
                    UpdateStatusText.Text = "没问到版本信息：离线、代理不通或已被限流";
                    return;
                }

                AppHost.Settings.Save(x => x.LastUpdateCheck = DateTime.Now);
                string current = UpdateService.CurrentVersion();
                if (!UpdateService.IsNewer(current, info.Version))
                {
                    AppHost.Settings.Save(x => x.LatestSeenVersion = info.Version);
                    UpdateStatusText.Text = $"已是最新版本（{current}）";
                    return;
                }

                UpdateStatusText.Text = string.Equals(info.Version, AppHost.Settings.Current.LatestSeenVersion,
                    StringComparison.OrdinalIgnoreCase)
                    ? $"新版 {info.Version} 已被你忽略"
                    : $"发现新版本 {info.Version} · {UpdatePolicy.PackageManagerHint}";
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = "检查失败：" + ex.Message;
            }
            finally
            {
                CheckUpdateButton.IsEnabled = true;
            }
        }

        // ---- 桌面集成 ----

        private void UpdateMenuState()
        {
            try
            {
                using (var svc = ShellIntegration.Create())
                {
                    var state = svc.State(ShellIntegration.CurrentExe);
                    var text = state == ShellMenuState.Current ? "已安装（指向当前程序）"
                             : state == ShellMenuState.Stale ? "已安装，但指向别的版本，请重新安装"
                             : "未安装";
                    MenuStateText.Text = "右键菜单：" + text;
                }
            }
            catch (Exception ex)
            {
                MenuStateText.Text = "右键菜单不可用：" + ex.Message;
            }
        }

        private void OnInstallMenu(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var svc = ShellIntegration.Create())
                    svc.Register(ShellIntegration.CurrentExe);
            }
            catch (Exception ex) { MenuStateText.Text = "安装失败：" + ex.Message; return; }
            UpdateMenuState();
        }

        private void OnRemoveMenu(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var svc = ShellIntegration.Create())
                    svc.Unregister();
            }
            catch (Exception ex) { MenuStateText.Text = "移除失败：" + ex.Message; return; }
            UpdateMenuState();
        }

        // ---- 保存 ----

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var custom = (Custom7zBox.Text ?? "").Trim();

            // 空行（名称和路径都没填）不落盘，免得列表里留一堆空占位
            var folders = _folders
                .Select(f => new CustomFolder
                {
                    Name = (f.Name ?? "").Trim(),
                    Path = (f.FolderPath ?? "").Trim()
                })
                .Where(f => f.Name.Length > 0 || f.Path.Length > 0)
                .Take(MaxCustomFolders)
                .ToList();

            var themeKey = ThemeApplier.Choices[Math.Max(0, Math.Min(ThemeBox.SelectedIndex, ThemeApplier.Choices.Length - 1))].Key;
            int extractMode = ExtractModes[Math.Max(0, Math.Min(ExtractModeBox.SelectedIndex, ExtractModes.Length - 1))].Value;
            string coverMode = CoverModes[Math.Max(0, Math.Min(CoverBox.SelectedIndex, CoverModes.Length - 1))].Value;
            int jobSort = Math.Max(0, Math.Min(JobSortBox.SelectedIndex, JobOrder.Names.Length - 1));

            bool onTop = WindowOnTopBox.IsChecked == true;
            uint hotkeyVk = _hotkeyVk;
            bool ctrl = HotkeyCtrlBox.IsChecked == true;
            bool alt = HotkeyAltBox.IsChecked == true;
            bool shift = HotkeyShiftBox.IsChecked == true;

            AppHost.Settings.Save(s =>
            {
                s.Theme = themeKey;
                s.WindowOnTop = onTop;
                s.CleanTempOnStartup = CleanTempBox.IsChecked == true;

                s.Customize7z = custom.Length > 0;
                s.Customize7zPath = custom;
                s.LogPasswords = LogPasswordsBox.IsChecked == true;

                s.ExtractOutMode = extractMode;
                s.ExtractCoverMode = coverMode;
                s.ExtractUnknow = ExtractUnknowBox.IsChecked == true;
                s.NameToPassword = NameToPasswordBox.IsChecked == true;
                s.CreateNewFolder = CreateNewFolderBox.IsChecked == true;
                s.CreateNameFolder = CreateNameFolderBox.IsChecked == true;
                s.DeleteFinishFile = DeleteFinishFileBox.IsChecked == true;
                s.ExtractFilter = (ExtractFilterBox.Text ?? "").Trim();
                s.CustomizeFolders = folders;

                s.ParallelExtract = (int)(ParallelExtractBox.Value ?? 3);
                s.ParallelCompress = (int)(ParallelCompressBox.Value ?? 2);
                s.VerifyAfterCompress = VerifyBox.IsChecked == true;
                s.PreviewBeforeExtract = PreviewBox.IsChecked == true;
                s.AutoOpenAfterExtract = OpenAfterBox.IsChecked == true;
                s.DeleteToRecycle = RecycleBox.IsChecked == true;
                s.TrimSpace = TrimSpaceBox.IsChecked == true;
                s.JobSort = jobSort;
                s.MiniPuck = MiniPuckBox.IsChecked == true;
                s.DebugMode = DebugModeBox.IsChecked == true;

                s.WatchEnabled = WatchEnabledBox.IsChecked == true;
                s.WatchFolder = WatchFolderBox.Text ?? "";

                s.UseHotKey = UseHotKeyBox.IsChecked == true;
                s.HotKeyKey = hotkeyVk;
                s.HotKeyCtrl = ctrl;
                s.HotKeyAlt = alt;
                s.HotKeyShift = shift;

                s.CheckUpdateOnStartup = CheckUpdateOnStartupBox.IsChecked == true;
            });

            ThemeApplier.Apply(themeKey);
            AppHost.Watcher.Apply();
            RefreshFolderCount();

            SavedText.Text = "已保存";
            SevenZipText.Text = AppHost.Zip.SevenZipPath ?? "未找到 7-Zip（7z / 7zz / 7za）。";
        }
    }
}
