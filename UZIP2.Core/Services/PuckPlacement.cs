using System;

namespace UZIP2.Services
{
    /// <summary>与 UI 框架无关的矩形（像素或 DIP 都行，只要调用方保持同一坐标系）。</summary>
    public readonly struct ScreenRect
    {
        public ScreenRect(double left, double top, double width, double height)
        {
            Left = left; Top = top; Width = width; Height = height;
        }

        public double Left { get; }
        public double Top { get; }
        public double Width { get; }
        public double Height { get; }
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    /// <summary>
    /// 悬浮方块的位置落点：Negative 坐标表示"从没放过"，落到主屏右下角；
    /// 越界则按整个虚拟桌面夹回可见范围（副屏位置才不会一重启就被拽回主屏）。
    /// 纯函数，WPF 与 Avalonia 共用。
    /// </summary>
    public static class PuckPlacement
    {
        public const double DefaultRightMargin = 24;
        public const double DefaultBottomMargin = 96;

        public static (double X, double Y) Resolve(double left, double top, double width, double height,
            ScreenRect primaryWorkArea, ScreenRect virtualDesktop)
        {
            if (left < 0 || top < 0)
                return (primaryWorkArea.Right - width - DefaultRightMargin,
                        primaryWorkArea.Bottom - height - DefaultBottomMargin);

            return (Clamp(left, virtualDesktop.Left, virtualDesktop.Right - width),
                    Clamp(top, virtualDesktop.Top, virtualDesktop.Bottom - height));
        }

        static double Clamp(double v, double min, double max)
            => max < min ? min : (v < min ? min : (v > max ? max : v));
    }
}
