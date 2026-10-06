using System;
using Microsoft.Win32;

namespace UZIP2.Services
{
    public enum ShellMenuState { Missing, Current, Stale }

    public sealed class ShellMenuEntry
    {
        public ShellMenuEntry(string key, string label, string verb, string placeholder)
        {
            Key = key; Label = label; Verb = verb; Placeholder = placeholder;
        }
        public string Key { get; }          // 相对 HKCU\Software\Classes
        public string Label { get; }
        public string Verb { get; }         // 传给 --verb 的那个开关
        public string Placeholder { get; }  // %1 选中项 / %V 背景目录
    }

    // HKCU 右键菜单: 不需要管理员权限，也不需要 COM shell 扩展。
    // 代价是 Windows 11 下这些项落在"显示更多选项"(经典菜单)里，和 7-Zip/Bandizip 一致。
    public sealed class ShellMenuService : IDisposable
    {
        public static readonly ShellMenuEntry[] Menu =
        {
            new ShellMenuEntry(@"*\shell\UZIP.ExtractHere", "用 UZIP 解压到当前文件夹", ShellArgs.ExtractHereFlag, "%1"),
            new ShellMenuEntry(@"*\shell\UZIP.Extract", "用 UZIP 解压", ShellArgs.ExtractFlag, "%1"),
            new ShellMenuEntry(@"*\shell\UZIP.ExtractPreview", "用 UZIP 解压并预览", ShellArgs.ExtractFlag + " " + ShellArgs.PreviewFlag, "%1"),
            new ShellMenuEntry(@"*\shell\UZIP.Compress", "用 UZIP 压缩", ShellArgs.CompressFlag, "%1"),
            new ShellMenuEntry(@"Directory\shell\UZIP.Compress", "用 UZIP 压缩", ShellArgs.CompressFlag, "%1"),
            new ShellMenuEntry(@"Directory\Background\shell\UZIP.Compress", "用 UZIP 压缩当前文件夹", ShellArgs.CompressFlag, "%V"),
        };

        private readonly RegistryKey _root;
        private readonly bool _ownsRoot;

        public ShellMenuService() : this(OpenClassesRoot(), true) { }

        public ShellMenuService(RegistryKey root, bool ownsRoot = false)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _ownsRoot = ownsRoot;
        }

        static RegistryKey OpenClassesRoot()
            => Registry.CurrentUser.CreateSubKey(@"Software\Classes");

        public static string CurrentExePath => Environment.ProcessPath;

        public ShellMenuState State(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return ShellMenuState.Missing;
            bool any = false, allCurrent = true;
            foreach (var e in Menu)
            {
                var cmd = ReadCommand(e);
                if (cmd == null) { allCurrent = false; continue; }
                any = true;
                if (!string.Equals(cmd, CommandOf(e, exePath), StringComparison.OrdinalIgnoreCase))
                    allCurrent = false;
            }
            if (!any) return ShellMenuState.Missing;
            return allCurrent ? ShellMenuState.Current : ShellMenuState.Stale;
        }

        public void Register(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) throw new ArgumentException("exe 路径为空", nameof(exePath));
            foreach (var e in Menu)
            {
                using (var key = _root.CreateSubKey(e.Key, true))
                {
                    key.SetValue("", e.Label);
                    key.SetValue("Icon", Quoted(exePath) + ",0");
                    using (var cmd = key.CreateSubKey("command"))
                        cmd.SetValue("", CommandOf(e, exePath));
                }
            }
            if (_ownsRoot) NotifyShell();
        }

        public void Unregister()
        {
            foreach (var e in Menu)
            {
                var leaf = e.Key.LastIndexOf('\\');
                var parent = _root.OpenSubKey(e.Key.Substring(0, leaf), true);
                try { parent?.DeleteSubKeyTree(e.Key.Substring(leaf + 1), false); }
                finally { parent?.Dispose(); }
            }
            if (_ownsRoot) NotifyShell();
        }

        string ReadCommand(ShellMenuEntry e)
        {
            using (var key = _root.OpenSubKey(e.Key + @"\command"))
                return key?.GetValue("") as string;
        }

        // 状态提示里要告诉用户菜单现在指向哪个 exe（升级/搬家后最常见）
        public string ProbeCommand() => ReadCommand(Menu[0]);

        static string CommandOf(ShellMenuEntry e, string exePath)
            => Quoted(exePath) + " " + e.Verb + " \"" + e.Placeholder + "\"";

        static string Quoted(string path) => "\"" + path + "\"";

        // 资源管理器缓存注册表项，不广播的话要等到下次登录才生效
        static void NotifyShell()
        {
            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        const uint SHCNE_ASSOCCHANGED = 0x08000000;
        const uint SHCNF_IDLIST = 0x0000;

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public void Dispose()
        {
            if (_ownsRoot) _root.Dispose();
        }
    }
}
