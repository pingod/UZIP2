using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UZIP2.Models
{
    // 某个目录的压缩偏好。-1 / "" 都表示"这一项跟随全局设置"。
    // 刻意不含密码: 密码只留在 DPAPI 加密的密码本里，settings.json 是明文。
    public sealed class CompressPreset
    {
        public string Name { get; set; } = "";
        public string Folder { get; set; } = "";
        public int CompressType { get; set; } = -1;
        public int CompressLevel { get; set; } = -1;
        public string CompressVolume { get; set; } = "";
        public string EncryptHeaders { get; set; } = "";   // ""|on|off
    }

    public sealed record CompressPlan(int Type, int Level, string Volume, bool EncryptHeaders, string PresetName);

    // 目录预设的匹配与取值。压缩作业只认 CompressPlan，不再直接读全局设置。
    public static class CompressPresetResolver
    {
        // 从源文件所在目录逐级上溯，取第一个（也就是最深的）命中预设
        public static CompressPreset Find(string sourcePath, IEnumerable<CompressPreset> presets)
        {
            if (string.IsNullOrEmpty(sourcePath) || presets == null) return null;
            var list = presets.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Folder)).ToList();
            if (list.Count == 0) return null;

            string dir = Directory.Exists(sourcePath) ? sourcePath : Path.GetDirectoryName(sourcePath);
            while (!string.IsNullOrEmpty(dir))
            {
                foreach (var p in list)
                    if (SameFolder(dir, p.Folder)) return p;
                var parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }
            return null;
        }

        static bool SameFolder(string a, string b)
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        static string Normalize(string path) => (path ?? "").Trim().TrimEnd('\\', '/');

        public static CompressPlan Plan(AppSettings global, CompressPreset preset)
        {
            var s = global ?? new AppSettings();
            string volume = string.IsNullOrWhiteSpace(preset?.CompressVolume) ? s.CompressVolume : preset.CompressVolume;
            bool encrypt = string.IsNullOrWhiteSpace(preset?.EncryptHeaders)
                ? s.HideZipContent
                : string.Equals(preset.EncryptHeaders, "on", StringComparison.OrdinalIgnoreCase);
            return new CompressPlan(
                preset != null && preset.CompressType >= 0 ? preset.CompressType : s.CompressType,
                preset != null && preset.CompressLevel >= 0 ? preset.CompressLevel : s.CompressLevel,
                volume,
                encrypt,
                preset?.Name);
        }
    }
}
