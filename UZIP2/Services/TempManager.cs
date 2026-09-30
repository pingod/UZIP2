using System;
using System.IO;

namespace UZIP2.Services
{
    // 隐藏临时解压目录管理（移植 UCmdPathHelp.UExtractPath 的 UZipTemp_ 语义 + App.CleanupTempFolders）
    public static class TempManager
    {
        public const string Prefix = "UZipTemp_";

        // 为某个压缩包在输出目录下创建(或复用)专属隐藏临时目录，返回带尾反斜杠的路径
        public static string CreateSessionTemp(string outputDir, string archivePath)
        {
            string dir = Path.Combine(Completion(outputDir), Prefix + Path.GetFileNameWithoutExtension(archivePath));
            if (!Directory.Exists(dir))
            {
                var created = Directory.CreateDirectory(dir);
                created.Attributes |= FileAttributes.Hidden;
            }
            return Completion(dir);
        }

        // 启动时清理程序目录下残留的 UZipTemp_*，返回清理数量；失败不抛异常
        public static int CleanupOnStartup(string baseDir)
        {
            int cleaned = 0;
            try
            {
                if (!Directory.Exists(baseDir)) return 0;
                foreach (string dir in Directory.GetDirectories(baseDir, Prefix + "*", SearchOption.TopDirectoryOnly))
                {
                    try { Directory.Delete(dir, true); cleaned++; }
                    catch { }
                }
            }
            catch { }
            return cleaned;
        }

        static string Completion(string path)
        {
            return path.EndsWith("\\") ? path : path + "\\";
        }
    }
}
