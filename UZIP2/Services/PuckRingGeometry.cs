using System;
using System.Windows;
using System.Windows.Media;
using UZIP2.Services;

namespace UZIP2.Services
{
    /// <summary>
    /// 把进度百分比画成绕图标一周的 WPF 几何。角度计算在核心层的 PuckProgress，
    /// 这里只负责 WPF 的路径构造（Avalonia 侧会有对应实现）。
    /// </summary>
    public static class PuckRingGeometry
    {
        /// <summary>
        /// 画出进度为 percent 的圆环：从 12 点起顺时针，半径按 size 与 thickness 内缩，
        /// 保证描边完整落在 size×size 的方框内。0%（含无进度）返回空几何。
        /// </summary>
        public static PathGeometry RingGeometry(double? percent, double size, double thickness)
        {
            double sweep = PuckProgress.SweepAngle(percent);
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
    }
}
