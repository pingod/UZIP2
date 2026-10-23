using System;
using System.IO;
using System.Text;

namespace UZIP2.Services
{
    /// <summary>
    /// Linux 右键菜单：没有注册表，分发的是各文件管理器自己的入口。
    /// 目前覆盖 Nautilus（脚本）与 Dolphin/KDE（service menu，KDE5 与 KDE6 各写一份）。
    /// 每个文件都带标记行，移除时只删我们写过的，绝不碰用户的同名脚本。
    /// </summary>
    public sealed class LinuxShellIntegration : IShellIntegration
    {
        public const string Marker = "# UZIP2 managed entry";
        const string ExePrefix = "# UZIP2-EXE:";

        private readonly string _home;

        public LinuxShellIntegration(string homeDirectory)
        {
            _home = homeDirectory ?? throw new ArgumentNullException(nameof(homeDirectory));
        }

        private string NautilusDir => Path.Combine(_home, ".local", "share", "nautilus", "scripts");
        private string DolphinPath => Path.Combine(_home, ".local", "share", "kio", "servicemenus", "uzip2.desktop");
        private string DolphinKde5Path => Path.Combine(_home, ".local", "share", "kservices5", "ServiceMenus", "uzip2.desktop");

        private string ExtractScriptPath => Path.Combine(NautilusDir, "UZIP2 解压到当前文件夹");
        private string CompressScriptPath => Path.Combine(NautilusDir, "UZIP2 压缩");

        public void Register(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("exePath 不能为空", nameof(exePath));

            Directory.CreateDirectory(NautilusDir);
            Write(ExtractScriptPath, ScriptText(exePath, "extract --here"));
            Write(CompressScriptPath, ScriptText(exePath, "compress"));

            var desktop = DesktopText(exePath);
            Write(DolphinPath, desktop);
            Write(DolphinKde5Path, desktop);
        }

        public void Unregister()
        {
            RemoveOwned(ExtractScriptPath);
            RemoveOwned(CompressScriptPath);
            RemoveOwned(DolphinPath);
            RemoveOwned(DolphinKde5Path);
        }

        public ShellMenuState State(string exePath)
        {
            var files = new[] { ExtractScriptPath, CompressScriptPath, DolphinPath };
            bool any = false;
            foreach (var f in files)
                if (File.Exists(f)) any = true;
            if (!any) return ShellMenuState.Missing;

            foreach (var f in files)
            {
                if (!File.Exists(f)) return ShellMenuState.Missing;
                var text = SafeRead(f);
                if (text == null || text.IndexOf(exePath ?? "", StringComparison.Ordinal) < 0)
                    return ShellMenuState.Stale;
            }
            return ShellMenuState.Current;
        }

        public string ProbeCommand()
        {
            var text = SafeRead(ExtractScriptPath) ?? SafeRead(DolphinPath);
            if (text == null) return null;
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith(ExePrefix, StringComparison.Ordinal))
                    return t.Substring(ExePrefix.Length).Trim();
            }
            return null;
        }

        public void Dispose() { }

        // ---- 文件内容 ----

        static string ScriptText(string exePath, string verb)
            => "#!/bin/sh\n"
             + Marker + "\n"
             + ExePrefix + exePath + "\n"
             + "exec \"" + exePath + "\" " + verb + " \"$@\"\n";

        static string DesktopText(string exePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Desktop Entry]");
            sb.AppendLine(Marker);
            sb.AppendLine(ExePrefix + exePath);
            sb.AppendLine("Type=Service");
            sb.AppendLine("ServiceTypes=KonqPopupMenu/Plugin");
            sb.AppendLine("MimeType=application/zip;application/x-7z-compressed;application/x-tar;application/gzip;application/x-bzip2;application/x-xz;application/x-rar;application/vnd.rar;application/x-compressed-tar;");
            sb.AppendLine("Actions=extractHere;compress;");
            sb.AppendLine("X-KDE-Submenu=UZIP2");
            sb.AppendLine();
            sb.AppendLine("[Desktop Action extractHere]");
            sb.AppendLine("Name=解压到当前文件夹");
            sb.AppendLine("Exec=\"" + exePath + "\" extract --here %F");
            sb.AppendLine();
            sb.AppendLine("[Desktop Action compress]");
            sb.AppendLine("Name=压缩为压缩包");
            sb.AppendLine("Exec=\"" + exePath + "\" compress %F");
            return sb.ToString();
        }

        static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch { }
            }
        }

        static void RemoveOwned(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var text = SafeRead(path);
                if (text != null && text.IndexOf(Marker, StringComparison.Ordinal) >= 0) File.Delete(path);
            }
            catch { }
        }

        static string SafeRead(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }
    }
}
