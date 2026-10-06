using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using UZIP2.Models;

namespace UZIP2.Services
{
    /// <summary>
    /// 迷你方块进度环的纯计算部分：把一批作业的进度汇总成一个百分比，再把百分比
    /// 映射成绕图标一周的几何（100% = 转完完整一圈）。与 WPF 控件无耦合，便于测试。
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
        /// 画出进度为 percent 的圆环：从 12 点起顺时针，半径按 size 与 thickness 内缩，
        /// 保证描边完整落在 size×size 的方框内。0%（含无进度）返回空几何。
        /// </summary>
        public static PathGeometry RingGeometry(double? percent, double size, double thickness)
        {
            double sweep = SweepAngle(percent);
            if (sweep <= 0 || size <= 0 || thickness <= 0 || thickness >= size) return null;

            double radius = (size - thickness) / 2;
            double center = size / 2;
            var top = new Point(center, center - radius);
            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = top };

            if (sweep >= 360)
            {
                // 一个 360° 的弧 WPF 画不出来（起点即终点会退化成空），拆成两个半圆
                AddArc(fig, radius, new Point(center, center + radius), largeArc: false);
                AddArc(fig, radius, top, largeArc: false);
            }
            else
            {
                double rad = sweep * Math.PI / 180.0;
                AddArc(fig, radius,
                    new Point(center + radius * Math.Sin(rad), center - radius * Math.Cos(rad)),
                    largeArc: sweep > 180);
            }

            geo.Figures.Add(fig);
            return geo;
        }

        static void AddArc(PathFigure fig, double radius, Point to, bool largeArc)
            => fig.Segments.Add(new ArcSegment
            {
                Point = to,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = largeArc
            });

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
