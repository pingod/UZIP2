using System.Globalization;
using System.Text.RegularExpressions;

namespace UZIP2.Services
{
    // 7z 的 -v 只接受 "数字 + 可选 b/k/m/g"。这里容忍大小写和中间空格，
    // 并把 0、超长这类 7z 只会回一句 Command Line Error 的输入提前挡掉。
    public static class VolumeSize
    {
        static readonly Regex Pattern = new Regex(@"^(\d+)\s*([bkmg]?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        const long MaxBytes = 1L << 40; // 1 TB

        // 返回可直接拼到 -v 后面的规范写法；false = 输入不可用（含空输入=不分卷）
        public static bool TryNormalize(string text, out string arg)
            => TryParse(text, out arg, out _);

        public static bool TryParse(string text, out string arg, out long bytes)
        {
            arg = null;
            bytes = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = Pattern.Match(text.Trim());
            if (!m.Success) return false;
            if (!long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long n)) return false;
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "k": bytes = n * 1024L; break;
                case "m": bytes = n * 1024L * 1024L; break;
                case "g": bytes = n * 1024L * 1024L * 1024L; break;
                default: bytes = n; break; // b 和纯数字都按字节
            }
            if (bytes <= 0 || bytes > MaxBytes) return false;
            arg = n + m.Groups[2].Value.ToLowerInvariant();
            return true;
        }

        public static string NormalizeOrDefault(string text, string fallback = null)
            => TryNormalize(text, out var arg) ? arg : fallback;
    }
}
