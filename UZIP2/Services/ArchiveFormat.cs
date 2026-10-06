using System;
using System.IO;

namespace UZIP2.Models
{
    // 旧 CompressTypes 下标的唯一真值表: 0=zip 1=7z 2=bz2 3=gz 4=tar 5=wim 6=xz
    // 下标含义来自旧版 UI，SevenZipClient 的 -t 开关与产出文件的扩展名都必须由这里出，
    // 否则"设置里选 7z、产出叫 .zip"这类错位无法根除。
    public static class ArchiveFormat
    {
        // 产出文件的扩展名
        static readonly string[] ExtNames = { "zip", "7z", "bz2", "gz", "tar", "wim", "xz" };
        // 7z 的 -t 开关取值（实测 26.03: -tbz2 / -tgz 会报错，必须是 bzip2 / gzip）
        static readonly string[] SwitchNames = { "zip", "7z", "bzip2", "gzip", "tar", "wim", "xz" };

        // 单流格式：一个包只能装一个文件，转格式时多条目必须提前拦住
        public static bool IsSingleStream(int compressType) => compressType == 2 || compressType == 3 || compressType == 6;

        public static string TypeName(int compressType)
            => compressType >= 0 && compressType < SwitchNames.Length ? SwitchNames[compressType] : SwitchNames[0];

        public static string Extension(int compressType)
            => "." + (compressType >= 0 && compressType < ExtNames.Length ? ExtNames[compressType] : ExtNames[0]);

        public static bool IsCreatable(int compressType)
            => compressType >= 0 && compressType < ExtNames.Length;

        // 别名照旧版 CLI 的写法收进来，用户敲 --type bzip2 也算数
        public static int Index(string name)
        {
            switch ((name ?? "").Trim().TrimStart('.').ToLowerInvariant())
            {
                case "zip": return 0;
                case "7z": return 1;
                case "bz2": case "bzip2": return 2;
                case "gz": case "gzip": return 3;
                case "tar": return 4;
                case "wim": return 5;
                case "xz": return 6;
                default: return -1;
            }
        }

        // 7z 能读不能写的格式（实测 26.03: 7z a -tiso 报 System ERROR: 未实现）。
        // 与其让用户看到一句英文"not implemented"，不如在入口就讲清楚。
        public static string CreateBlockMessage(string name)
        {
            var n = (name ?? "").Trim().TrimStart('.').ToLowerInvariant();
            switch (n)
            {
                case "iso": case "img":
                    return "ISO 镜像（iso/img）只能读取，7-Zip 不实现创建；要打包同样内容请用 --type 7z 或 zip";
                case "":
                    return "未知格式（可选 zip/7z/bz2/gz/tar/wim/xz）";
                default:
                    return IsCreatable(Index(n)) ? null : "未知格式: " + name + "（可选 zip/7z/bz2/gz/tar/wim/xz）";
            }
        }

        // 互转产物名: 沿用源包主名，剥掉残留的归档后缀（a.tar.gz -> a），撞名时按 -NewN 避让
        public static string TargetPath(string sourceArchive, string outDir, int compressType)
        {
            var dir = string.IsNullOrWhiteSpace(outDir)
                ? Path.GetDirectoryName(sourceArchive) ?? ""
                : outDir;
            if (dir.Length > 0 && !dir.EndsWith("\\") && !dir.EndsWith("/"))
                dir += Path.DirectorySeparatorChar;

            var stem = Path.GetFileNameWithoutExtension(sourceArchive);
            for (;;)
            {
                int dot = stem.LastIndexOf('.');
                if (dot <= 0) break;
                if (Index(stem.Substring(dot + 1)) < 0) break;
                stem = stem.Substring(0, dot);
            }

            string ext = Extension(compressType);
            string path = dir + stem + ext;
            for (int n = 1; File.Exists(path); n++)
                path = dir + stem + "-New" + n + ext;
            return path;
        }
    }
}
