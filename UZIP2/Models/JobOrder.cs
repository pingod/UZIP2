using System;
using System.Collections.Generic;

namespace UZIP2.Models
{
    // 主页任务列表排序策略。Id 全局自增，天然等价于加入顺序；所有 Compare 都是全序，
    // 列表在任何时刻保持有序，单项插入/移动直接用 InsertIndex 计算目标位置。
    public static class JobOrder
    {
        public const int AddOrder = 0;     // 加入顺序（早在前）
        public const int NewestFirst = 1;  // 最新加入在前
        public const int StatusFirst = 2;  // 按状态分组（活动在最上），组内按加入顺序
        public const int ByName = 3;       // 按文件名

        public static string[] Names { get; } = { "加入顺序", "最新在前", "按状态", "按文件名" };

        public static int Compare(JobEntry a, JobEntry b, int mode)
        {
            if (ReferenceEquals(a, b)) return 0;
            switch (mode)
            {
                case NewestFirst:
                    return b.Id.CompareTo(a.Id);
                case StatusFirst:
                    {
                        int c = a.SortRank.CompareTo(b.SortRank);
                        return c != 0 ? c : a.Id.CompareTo(b.Id);
                    }
                case ByName:
                    {
                        int c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
                        return c != 0 ? c : a.Id.CompareTo(b.Id);
                    }
                default:
                    return a.Id.CompareTo(b.Id);
            }
        }

        // job 在有序列表中的目标位置。skipIndex 供 Move 场景使用（跳过被移动项自身）：
        // ObservableCollection.Move(cur, target) 的 target 按"先移除后插入"语义解释，与本结果一致。
        public static int InsertIndex(IReadOnlyList<JobEntry> list, JobEntry job, int mode, int skipIndex = -1)
        {
            int pos = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (i == skipIndex) continue;
                if (Compare(list[i], job, mode) > 0) break;
                pos++;
            }
            return pos;
        }
    }
}
