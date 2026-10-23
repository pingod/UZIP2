using System;
using System.Collections.Generic;
using System.IO;

namespace UZIP2.Services
{
    /// <summary>
    /// 7z 可执行文件的候选位置。纯函数：平台与 PATH 都由调用方注入，
    /// 这样 Linux 候选表在 Windows 上也能被单测覆盖。
    /// </summary>
    public static class SevenZipLocator
    {
        public static IReadOnlyList<string> Candidates(string basePath, bool isWindows,
            string programFiles, string programFilesX86, string pathEnvironment)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            void Add(string path)
            {
                if (string.IsNullOrEmpty(path)) return;
                if (seen.Add(path)) list.Add(path);
            }

            if (isWindows)
            {
                if (!string.IsNullOrEmpty(basePath))
                    Add(Path.Combine(basePath, "7-Zip", "7z.exe"));
                if (!string.IsNullOrEmpty(programFiles))
                    Add(Path.Combine(programFiles, "7-Zip", "7z.exe"));
                if (!string.IsNullOrEmpty(programFilesX86))
                    Add(Path.Combine(programFilesX86, "7-Zip", "7z.exe"));
                return list;
            }

            // Linux：发行版包名各不相同（7zip 给 7zz/7z，p7zip 给 7z/7za）
            if (!string.IsNullOrEmpty(basePath))
                Add(Path.Combine(basePath, "7-Zip", "7z"));
            Add("/usr/bin/7zz");
            Add("/usr/bin/7z");
            Add("/usr/bin/7za");
            Add("/usr/local/bin/7zz");
            Add("/usr/local/bin/7z");
            Add("/usr/lib/7zip/7z");
            Add("/usr/lib/p7zip/7z");

            if (!string.IsNullOrEmpty(pathEnvironment))
            {
                foreach (var segment in pathEnvironment.Split(Path.PathSeparator))
                {
                    var dir = segment?.Trim();
                    if (string.IsNullOrEmpty(dir)) continue;
                    // PATH 里带尾分隔符很常见（/opt/tools/），去掉以免拼出 /opt/tools//7z
                    dir = Path.TrimEndingDirectorySeparator(dir);
                    Add(Path.Combine(dir, "7zz"));
                    Add(Path.Combine(dir, "7z"));
                    Add(Path.Combine(dir, "7za"));
                }
            }
            return list;
        }
    }
}
