using System;
using Avalonia;
using Avalonia.Media;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>
    /// 进度环的 Avalonia 画法。角度仍由核心层 PuckProgress 决定；
    /// 与 WPF 版同样，360° 必须拆两段弧（起点即终点会画不出来）。
    /// </summary>
    public static class PuckRingGeometry
    {
        public static StreamGeometry Build(double? percent, double size, double thickness)
        {
            double sweep = PuckProgress.SweepAngle(percent);
            if (sweep <= 0 || size <= 0 || thickness <= 0 || thickness >= size) return null;

            double radius = (size - thickness) / 2;
            double center = size / 2;
            var top = new Point(center, center - radius);

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(top, isFilled: false);
                if (sweep >= 360)
                {
                    ctx.ArcTo(new Point(center, center + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                    ctx.ArcTo(top, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                }
                else
                {
                    double rad = sweep * Math.PI / 180.0;
                    ctx.ArcTo(new Point(center + radius * Math.Sin(rad), center - radius * Math.Cos(rad)),
                              new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
                }
                ctx.EndFigure(isClosed: false);
            }
            return geo;
        }
    }
}
