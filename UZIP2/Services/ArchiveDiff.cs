using System;
using System.Collections.Generic;
using System.Linq;

namespace UZIP2.Models
{
    // 两个归档的清单差异。只做清单级对比（名字+大小），不读文件内容，
    // 所以百 MB 包也是 7z l -slt 的几十毫秒成本；内容真变了但大小没变，这里看不出。
    public sealed class ArchiveDiff
    {
        public sealed record Change(string Path, long LeftSize, long RightSize);

        public List<string> OnlyInLeft { get; } = new List<string>();
        public List<string> OnlyInRight { get; } = new List<string>();
        public List<Change> Changed { get; } = new List<Change>();
        public int SameCount { get; set; }

        public bool Identical => OnlyInLeft.Count == 0 && OnlyInRight.Count == 0 && Changed.Count == 0;

        public string Summarize()
            => Identical
                ? "内容一致（" + SameCount + " 个文件）"
                : "仅左 " + OnlyInLeft.Count + "，仅右 " + OnlyInRight.Count
                  + "，大小不同 " + Changed.Count + "，相同 " + SameCount;

        // 目录条目两边 Size 恒为 0，且列不列目录取决于打包器，带进来只产生噪音
        public static ArchiveDiff Compare(IReadOnlyList<ArchiveEntry> left, IReadOnlyList<ArchiveEntry> right)
        {
            var d = new ArchiveDiff();
            var l = Index(left);
            var r = Index(right);

            foreach (var kv in l)
            {
                if (!r.TryGetValue(kv.Key, out var other)) { d.OnlyInLeft.Add(kv.Value.Path); continue; }
                if (kv.Value.Size == other.Size) d.SameCount++;
                else d.Changed.Add(new Change(kv.Value.Path, kv.Value.Size, other.Size));
            }
            foreach (var kv in r)
                if (!l.ContainsKey(kv.Key)) d.OnlyInRight.Add(kv.Value.Path);

            d.OnlyInLeft.Sort(StringComparer.OrdinalIgnoreCase);
            d.OnlyInRight.Sort(StringComparer.OrdinalIgnoreCase);
            d.Changed.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return d;
        }

        // zip 存 "a/b"、7z 存 "a\b"，Windows 又不区分大小写；不统一就会满屏假差异
        static Dictionary<string, ArchiveEntry> Index(IReadOnlyList<ArchiveEntry> entries)
        {
            var map = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            if (entries == null) return map;
            foreach (var e in entries)
            {
                if (e == null || e.IsFolder || string.IsNullOrEmpty(e.Path)) continue;
                map[NormalizeKey(e.Path)] = e;
            }
            return map;
        }

        static string NormalizeKey(string path)
        {
            var p = path.Trim().Replace('\\', '/');
            int i = p.Length;
            while (i > 0 && p[i - 1] == '/') i--;
            return p.Substring(0, i);
        }
    }
}
