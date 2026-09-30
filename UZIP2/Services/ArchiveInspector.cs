using System;
using System.Collections.Generic;
using System.IO;

namespace UZIP2.Services
{
    public sealed class VolumeInfo
    {
        public bool IsVolume;
        public string ZipType;          // rar / zip / zip-z / zip-zip / 7z / bz2 / gz / tar / wim / xz
        public string BaseName;         // 不含分卷后缀的主名
        public string MainVolumePath;   // null=无需纠正 / 主卷完整路径
        public string Folder;           // 所在目录(带尾部'\')
        public string SecondExt;        // 数字分卷的二级后缀, 如 a.zip.001 中的 ".zip"
    }

    public sealed class ArchiveInfo
    {
        public string Path;
        public bool IsArchive;          // 魔数是压缩包
        public string RealExt;          // .zip/.rar/... null=未知
        public bool CanExtract;         // 旧 UTool.CanExtract 扩展名白名单语义
        public VolumeInfo Volume;
        public string ExtractTargetPath; // 实际交给 7z 的路径(分卷时为主卷)
        public string BaseName;          // 用于建目录/命名的基础名
    }

    // 魔数识别 + 扩展名白名单 + 分卷识别（移植 Tools.cs:118-176/199-238/378-540）
    public static class ArchiveInspector
    {
        public static string RealExtension(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    var head = new byte[8];
                    int read = fs.Read(head, 0, head.Length);
                    if (read < 4) return null;

                    // RAR: "Rar!"
                    if (read >= 7 && head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21)
                        return ".rar";
                    // ZIP: PK\x03\x04 / \x05\x06 / \x07\x08
                    if (head[0] == 0x50 && head[1] == 0x4B && (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07))
                        return ".zip";
                    // 7z
                    if (read >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
                        return ".7z";
                    // BZIP2 "BZh"
                    if (head[0] == 0x42 && head[1] == 0x5A && head[2] == 0x68)
                        return ".bz2";
                    // GZIP
                    if (head[0] == 0x1F && head[1] == 0x8B)
                        return ".gz";
                    // XZ
                    if (read >= 6 && head[0] == 0xFD && head[1] == 0x37 && head[2] == 0x7A && head[3] == 0x58 && head[4] == 0x5A && head[5] == 0x00)
                        return ".xz";
                    // WIM "MSWIM"
                    if (head[0] == 0x4D && head[1] == 0x53 && head[2] == 0x57 && head[3] == 0x49 && head[4] == 0x4D)
                        return ".wim";
                    // TAR: 0x101 "ustar"
                    if (fs.Length >= 0x106)
                    {
                        fs.Position = 0x101;
                        var tar = new byte[5];
                        if (fs.Read(tar, 0, 5) == 5 && tar[0] == 0x75 && tar[1] == 0x73 && tar[2] == 0x74 && tar[3] == 0x61 && tar[4] == 0x72)
                            return ".tar";
                    }
                    // ISO: 0x8001 "CD001"
                    if (fs.Length >= 0x8006)
                    {
                        fs.Position = 0x8001;
                        var iso = new byte[5];
                        if (fs.Read(iso, 0, 5) == 5 && iso[0] == 0x43 && iso[1] == 0x44 && iso[2] == 0x30 && iso[3] == 0x30 && iso[4] == 0x31)
                            return ".iso";
                    }
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        // 旧 CanExtract 白名单
        static readonly HashSet<string> Extractable = new HashSet<string>
        {
            ".zip", ".bz2", ".gz", ".tar", ".wim", ".7z", ".xz",
            ".rar", ".arj", ".cab", ".chm", ".cpio", ".deb", ".dmg", ".fat", ".hfs",
            ".iso", ".lzh", ".lzma", ".mbr", ".msi", ".nsis", ".ntfs", ".rpm", ".001"
        };

        public static bool CanExtractByExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext.Length > 0 && Extractable.Contains(ext);
        }

        public static VolumeInfo AnalyzeVolume(string filePath)
        {
            var v = new VolumeInfo
            {
                BaseName = Path.GetFileNameWithoutExtension(filePath),
                Folder = Path.GetDirectoryName(filePath) + Path.DirectorySeparatorChar
            };
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var folder = v.Folder;
            var fileName = Path.GetFileNameWithoutExtension(filePath);

            if (extension.Length <= 3) return v;

            if (extension == ".rar")
            {
                v.ZipType = "rar";
                var ext2 = Path.GetExtension(fileName).ToLowerInvariant();
                if (ext2.Length > 5 && ext2.Substring(0, 5) == ".part")
                {
                    v.IsVolume = true;
                    fileName = Path.GetFileNameWithoutExtension(fileName);
                }
                v.BaseName = fileName;
                if (v.IsVolume) v.MainVolumePath = folder + fileName + ".part1.rar";
                return v;
            }
            if (extension == ".zip") { v.ZipType = "zip-zip"; v.BaseName = fileName; return v; }
            if (extension.StartsWith(".z") && int.TryParse(extension.Substring(2), out _))
            {
                v.ZipType = "zip-z";
                v.IsVolume = true;
                v.MainVolumePath = folder + fileName + ".zip";
                return v;
            }
            // .001/.002... 之类数字分卷: 前一级须是压缩扩展名
            if (int.TryParse(extension.Substring(1), out _))
            {
                var ext2 = Path.GetExtension(fileName).ToLowerInvariant();
                string mapped = null;
                switch (ext2)
                {
                    case ".zip": mapped = "zip"; break;
                    case ".bz2": mapped = "bz2"; break;
                    case ".gz": mapped = "gz"; break;
                    case ".tar": mapped = "tar"; break;
                    case ".wim": mapped = "wim"; break;
                    case ".7z": mapped = "7z"; break;
                    case ".xz": mapped = "xz"; break;
                }
                if (mapped != null)
                {
                    v.ZipType = mapped;
                    v.IsVolume = true;
                    v.SecondExt = ext2;
                    fileName = Path.GetFileNameWithoutExtension(fileName);
                    v.BaseName = fileName;
                    v.MainVolumePath = folder + fileName + ext2 + ".001";
                }
            }
            return v;
        }

        // 删除整组分卷文件（移植 VolumesFile.DeleteVolumesFile）
        public static void DeleteVolumeSet(VolumeInfo v, bool toRecycle)
        {
            if (v == null || v.ZipType == null) return;
            switch (v.ZipType)
            {
                case "zip-zip":
                case "zip-z":
                    Del(v.Folder + v.BaseName + ".zip", toRecycle);
                    int n = 1;
                    while (Del(v.Folder + v.BaseName + ".z" + (n++).ToString().PadLeft(2, '0'), toRecycle)) { }
                    break;
                case "rar":
                    int p = 1;
                    while (Del(v.Folder + v.BaseName + ".part" + p + ".rar", toRecycle)) p++;
                    break;
                default:
                    int d = 1;
                    while (Del(v.Folder + v.BaseName + v.SecondExt + "." + (d++).ToString().PadLeft(3, '0'), toRecycle)) { }
                    break;
            }
        }

        static bool Del(string path, bool toRecycle)
        {
            if (!File.Exists(path)) return false;
            try { FilterService.Delete(path, toRecycle); }
            catch { }
            return true;
        }

        public static ArchiveInfo Inspect(string path)
        {
            var info = new ArchiveInfo
            {
                Path = path,
                RealExt = RealExtension(path),
                CanExtract = CanExtractByExtension(path)
            };
            info.IsArchive = info.RealExt != null;
            info.Volume = AnalyzeVolume(path);
            info.ExtractTargetPath = info.Volume.IsVolume && File.Exists(info.Volume.MainVolumePath)
                ? info.Volume.MainVolumePath : path;
            info.BaseName = info.Volume.BaseName;
            // ExtractUnknow=false 时按扩展名; true 时按魔数(worker 决定, 这里都给字段)
            return info;
        }
    }
}
