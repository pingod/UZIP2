using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

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
        private string ThunarPath => Path.Combine(_home, ".config", "Thunar", "uca.xml");

        public const string ThunarExtractId = "uzip2-extract-here";
        public const string ThunarCompressId = "uzip2-compress";

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
            RegisterThunar(exePath);
        }

        public void Unregister()
        {
            RemoveOwned(ExtractScriptPath);
            RemoveOwned(CompressScriptPath);
            RemoveOwned(DolphinPath);
            RemoveOwned(DolphinKde5Path);
            UnregisterThunar();
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

            // Thunar 是可选的：用户装了 Thunar（uca.xml 存在）才检查我们的两条动作
            if (File.Exists(ThunarPath))
            {
                var doc = SafeLoadThunar();
                if (doc == null) return ShellMenuState.Stale;
                var ours = ThunarActions(doc).ToList();
                if (ours.Count != 2) return ShellMenuState.Stale;
                var text = SafeRead(ThunarPath) ?? "";
                if (text.IndexOf(exePath ?? "", StringComparison.Ordinal) < 0) return ShellMenuState.Stale;
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

        // ---- Thunar：合并进用户自己的 uca.xml，绝不覆盖别人的动作 ----

        private void RegisterThunar(string exePath)
        {
            var doc = LoadOrCreateThunar();
            var root = doc.Root;
            foreach (var a in ThunarActions(doc).ToList()) a.Remove();

            root.Add(BuildThunarAction(ThunarExtractId, "UZIP2 解压到当前文件夹",
                "\"" + exePath + "\" extract --here %f"));
            root.Add(BuildThunarAction(ThunarCompressId, "UZIP2 压缩",
                "\"" + exePath + "\" compress %f"));

            Directory.CreateDirectory(Path.GetDirectoryName(ThunarPath));
            // 第一次改用户的文件前留一份原样备份
            if (File.Exists(ThunarPath) && !File.Exists(ThunarPath + ".uzip2.bak"))
            {
                try { File.Copy(ThunarPath, ThunarPath + ".uzip2.bak"); } catch { }
            }
            doc.Save(ThunarPath);
        }

        private void UnregisterThunar()
        {
            var doc = SafeLoadThunar();
            if (doc?.Root == null) return;
            var ours = ThunarActions(doc).ToList();
            if (ours.Count == 0) return;
            foreach (var a in ours) a.Remove();
            try { doc.Save(ThunarPath); } catch { }
        }

        private XDocument LoadOrCreateThunar()
        {
            if (File.Exists(ThunarPath))
            {
                var doc = SafeLoadThunar();
                if (doc?.Root != null) return doc;
                // 用户的 uca.xml 已经坏了：先备份再重建，别让注册流程跟着失败
                try { File.Copy(ThunarPath, ThunarPath + ".uzip2.bak", true); } catch { }
                try { File.Delete(ThunarPath); } catch { }
            }
            return new XDocument(new XElement("actions"));
        }

        private XDocument SafeLoadThunar()
        {
            try { return File.Exists(ThunarPath) ? XDocument.Load(ThunarPath) : null; }
            catch { return null; }
        }

        private static System.Collections.Generic.IEnumerable<XElement> ThunarActions(XDocument doc)
        {
            if (doc?.Root == null) return System.Linq.Enumerable.Empty<XElement>();
            return doc.Root.Elements("action").Where(a =>
            {
                var id = (string)a.Element("unique-id");
                return id == ThunarExtractId || id == ThunarCompressId;
            });
        }

        private static XElement BuildThunarAction(string id, string name, string command)
            => new XElement("action",
                new XElement("icon", "package-x-generic"),
                new XElement("name", name),
                new XElement("unique-id", id),
                new XElement("command", command),
                new XElement("description", "UZIP2"),
                new XElement("patterns", "*"),
                new XElement("other-files"));

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
