using System;
using System.Collections.Generic;
using System.IO;

namespace UZIP2.Services
{
    // 隐藏临时解压目录管理（移植 UCmdPathHelp.UExtractPath 的 UZipTemp_ 语义 + App.CleanupTempFolders）
    public static class TempManager
    {
        public const string Prefix = "UZipTemp_";

        // temp 建在解压输出目录下（往往是用户下载目录），单扫程序目录回收不到，
        // 所以创建时在 Config 记一笔，启动时按这本账回收崩溃/强杀残留。
        static string JournalPath(string journalDir) => Path.Combine(journalDir, "tempdirs.txt");

        // 为某个压缩包在输出目录下创建(或复用)专属隐藏临时目录，返回带尾反斜杠的路径。
        // token 用于让同名档案(不同目录的同名包)在并发作业时拿到互不相同的临时目录。
        public static string CreateSessionTemp(string outputDir, string archivePath, string token = null,
            string journalDir = null)
        {
            string name = Prefix + Path.GetFileNameWithoutExtension(archivePath);
            if (!string.IsNullOrEmpty(token)) name += "_" + token;
            string dir = Path.Combine(Completion(outputDir), name);
            if (!Directory.Exists(dir))
            {
                var created = Directory.CreateDirectory(dir);
                created.Attributes |= FileAttributes.Hidden;
            }
            JournalAdd(journalDir, dir);
            return Completion(dir);
        }

        static void JournalAdd(string journalDir, string dir)
        {
            if (string.IsNullOrEmpty(journalDir)) return;
            try
            {
                Directory.CreateDirectory(journalDir);
                File.AppendAllText(JournalPath(journalDir), dir + Environment.NewLine);
            }
            catch { /* 记账失败只影响残留回收，不影响作业 */ }
        }

        // 启动时清理程序目录下残留的 UZipTemp_*，返回清理数量；失败不抛异常
        public static int CleanupOnStartup(string baseDir, string journalDir = null)
        {
            int cleaned = 0;
            try
            {
                if (Directory.Exists(baseDir))
                    foreach (string dir in Directory.GetDirectories(baseDir, Prefix + "*", SearchOption.TopDirectoryOnly))
                        if (TryRemove(dir)) cleaned++;
                cleaned += CleanFromJournal(journalDir);
            }
            catch { }
            return cleaned;
        }

        // 账本里逐条回收，顺带丢掉已经消失/已不属于本程序命名的一行
        static int CleanFromJournal(string journalDir)
        {
            if (string.IsNullOrEmpty(journalDir)) return 0;
            string journal = JournalPath(journalDir);
            if (!File.Exists(journal)) return 0;
            string[] lines;
            try { lines = File.ReadAllLines(journal); } catch { return 0; }

            int cleaned = 0;
            var keep = new List<string>();
            foreach (var raw in lines)
            {
                string dir = (raw ?? "").Trim();
                if (dir.Length == 0) continue;
                if (!Path.GetFileName(dir.TrimEnd('\\')).StartsWith(Prefix, StringComparison.Ordinal)) continue;
                if (!Directory.Exists(dir)) continue;
                if (TryRemove(dir)) cleaned++;
                else keep.Add(dir);        // 正被占用（另一实例在跑），下轮再试
            }
            try
            {
                if (keep.Count == 0) File.Delete(journal);
                else File.WriteAllText(journal, string.Join(Environment.NewLine, keep) + Environment.NewLine);
            }
            catch { }
            return cleaned;
        }

        static bool TryRemove(string dir)
        {
            try { Directory.Delete(dir.TrimEnd('\\'), true); return true; }
            catch { return false; }
        }

        static string Completion(string path)
        {
            return path.EndsWith("\\") ? path : path + "\\";
        }
    }
}
