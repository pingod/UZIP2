using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using UZIP2.Models;
using UZIP2.Services;

namespace UZIP2.ViewModel
{
    // 设置页 VM：列表型设置(自定义目录/内部密码/自定义密码)的编辑与即时持久化。
    // 标量项由视图直接调 SaveProperty，无中间状态。
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly ISettingsService _settings;

        public SettingsViewModel(ISettingsService settings)
        {
            _settings = settings;
            var s = settings.Current;

            Folders = new ObservableCollection<CustomFolderRow>();
            foreach (var f in s.CustomizeFolders ?? new List<CustomFolder>())
                AddFolderRow(f.Name, f.Path);

            InternalPasswords = new ObservableCollection<TextRow>();
            foreach (var p in s.InternalPasswords ?? new List<string>())
                AddInternalRow(p);

            var custom = Pad3(s.CustomPasswords);
            CustomPassword1 = new TextRow(custom[0]);
            CustomPassword2 = new TextRow(custom[1]);
            CustomPassword3 = new TextRow(custom[2]);
            CustomPassword1.PropertyChanged += OnRowChanged;
            CustomPassword2.PropertyChanged += OnRowChanged;
            CustomPassword3.PropertyChanged += OnRowChanged;
        }

        static List<string> Pad3(List<string> src)
        {
            var list = new List<string>(src ?? new List<string>());
            while (list.Count < 3) list.Add("");
            return list.GetRange(0, 3);
        }

        public AppSettings Current => _settings.Current;

        public ObservableCollection<CustomFolderRow> Folders { get; }
        public ObservableCollection<TextRow> InternalPasswords { get; }
        public TextRow CustomPassword1 { get; }
        public TextRow CustomPassword2 { get; }
        public TextRow CustomPassword3 { get; }

        void OnRowChanged(object sender, PropertyChangedEventArgs e) => PersistLists();

        public void AddFolder() => AddFolderRow($"目录{Folders.Count + 1}", "");
        public void RemoveFolder(CustomFolderRow row)
        {
            if (row == null) return;
            row.PropertyChanged -= OnRowChanged;
            Folders.Remove(row);
            PersistLists();
        }

        void AddFolderRow(string name, string path)
        {
            var row = new CustomFolderRow(name, path);
            row.PropertyChanged += OnRowChanged;
            Folders.Add(row);
        }

        public void AddInternal() => AddInternalRow("");
        public void RemoveInternal(TextRow row)
        {
            if (row == null) return;
            row.PropertyChanged -= OnRowChanged;
            InternalPasswords.Remove(row);
            PersistLists();
        }

        void AddInternalRow(string value)
        {
            var row = new TextRow(value);
            row.PropertyChanged += OnRowChanged;
            InternalPasswords.Add(row);
        }

        public void SaveProperty(string name, object value)
        {
            _settings.Save(s =>
            {
                var prop = s.GetType().GetProperty(name);
                if (prop == null) return;
                if (prop.PropertyType == typeof(List<CustomFolder>) || prop.PropertyType == typeof(List<string>))
                    return;
                prop.SetValue(s, Convert.ChangeType(value, prop.PropertyType));
            });
        }

        public object Read(string name)
        {
            var prop = typeof(AppSettings).GetProperty(name);
            return prop?.GetValue(Current);
        }

        void PersistLists()
        {
            _settings.Save(s =>
            {
                s.CustomizeFolders = Folders.Select(f => new CustomFolder { Name = f.Name, Path = f.Path }).ToList();
                s.InternalPasswords = InternalPasswords.Select(x => x.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
                s.CustomPasswords = new List<string> { CustomPassword1.Value, CustomPassword2.Value, CustomPassword3.Value };
            });
        }

        // ---- 热键文本 ----

        public static string FormatHotkey(uint vk, bool ctrl, bool alt, bool shift)
        {
            if (vk == 0) return "未设置";
            var key = KeyInterop.KeyFromVirtualKey((int)vk);
            var name = key == Key.None ? vk.ToString("X2") : key.ToString();
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            parts.Add(name);
            return string.Join("+", parts);
        }

        public static uint NameToVk(string keyName)
        {
            if (Enum.TryParse<Key>(keyName, out var key))
                return (uint)KeyInterop.VirtualKeyFromKey(key);
            return 0;
        }

        public string HotkeyText => FormatHotkey(Current.HotKeyKey, Current.HotKeyCtrl, Current.HotKeyAlt, Current.HotKeyShift);

        public void SetHotkey(uint vk, bool ctrl, bool alt, bool shift)
        {
            _settings.Save(s =>
            {
                s.HotKeyKey = vk;
                s.HotKeyCtrl = ctrl;
                s.HotKeyAlt = alt;
                s.HotKeyShift = shift;
            });
            OnPropertyChanged(nameof(HotkeyText));
        }

        public sealed partial class CustomFolderRow : ObservableObject
        {
            public CustomFolderRow(string name, string path)
            {
                _name = name ?? "";
                _path = path ?? "";
            }
            [ObservableProperty] private string _name;
            [ObservableProperty] private string _path;
        }

        public sealed partial class TextRow : ObservableObject
        {
            public TextRow(string value) { _value = value ?? ""; }
            [ObservableProperty] private string _value;
        }
    }
}
