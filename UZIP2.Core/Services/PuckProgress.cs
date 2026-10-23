using System;
using System.Collections.Generic;
using UZIP2.Models;

namespace UZIP2.Services
{
    /// <summary>
    /// 迷你方块进度环的纯计算部分：把一批作业的进度汇总成一个百分比，并决定提示位写什么。
    /// 与任何 UI 框架无耦合（几何绘制由各界面自己的 PuckRingGeometry 负责），便于跨平台共享与测试。
    /// </summary>
    public static class PuckProgress
    {
        const double DegreesPerPercent = 3.6;

        static double ClampPercent(double? percent)
        {
            double p = percent ?? 0;
            if (double.IsNaN(p) || p < 0) return 0;
            return p > 100 ? 100 : p;
        }

        /// <summary>0% → 0°，100% → 360°（一整圈），越界一律夹紧。</summary>
        public static double SweepAngle(double? percent) => ClampPercent(percent) * DegreesPerPercent;

        /// <summary>
        /// 一批作业的总进度：只统计排队中与运行中的作业，取算术平均；
        /// 排队视为 0%，7z 没给出百分比的作业也按 0% 计。没有活跃作业返回 null（环不显示）。
        /// </summary>
        public static double? Overall(IEnumerable<JobEntry> jobs)
        {
            if (jobs == null) return null;
            double sum = 0;
            int count = 0;
            foreach (var job in jobs)
            {
                if (job == null) continue;
                if (job.Status != JobStatus.Queued && job.Status != JobStatus.Running) continue;
                sum += ClampPercent(job.Percent);
                count++;
            }
            return count == 0 ? null : sum / count;
        }

        /// <summary>
        /// 方块提示位该写什么。优先级：拖拽预览 &gt; 完成闪示 &gt; 进度 &gt; 空闲。
        /// 关键在于"完成"只是临时的——闪示结束（finishing 变 false）必须自动回到空闲文案，
        /// 否则提示会永远卡在"完成"。percent 为 null 表示当前没有活跃作业。
        /// </summary>
        public static string ResolveHint(bool previewing, int runningCount, double? percent,
                                         bool finishing, string previewText = null)
        {
            if (previewing && !string.IsNullOrEmpty(previewText)) return previewText;
            if (finishing) return "完成";
            if (percent.HasValue)
            {
                int p = (int)Math.Round(percent.Value, MidpointRounding.AwayFromZero);
                if (percent.Value <= 0) return runningCount > 1 ? $"{runningCount} 个任务" : "准备中";
                return runningCount > 1 ? $"{runningCount} 个任务 {p}%" : $"{p}%";
            }
            return runningCount > 0 ? $"{runningCount} 个任务" : "拖到这里";
        }
    }
}
