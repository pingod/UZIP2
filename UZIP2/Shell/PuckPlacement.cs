using System.Windows;

namespace UZIP2.Shell
{
    // 迷你方块落点的纯函数。旧实现按 SystemParameters.WorkArea（只有主屏）夹坐标，
    // 于是"把方块拖到副屏"这件事一重启就被拽回主屏右下角——多屏用户的记忆位置必然丢失。
    // 这里改成按整个虚拟桌面夹，副屏位置原样保留。
    public static class PuckPlacement
    {
        // 首次放置时离主屏右下角的边距，沿用旧版的观感
        public const double DefaultRightMargin = 24;
        public const double DefaultBottomMargin = 96;

        public static Point Resolve(double left, double top, double width, double height,
            Rect primaryWorkArea, Rect virtualDesktop)
        {
            // 负值是"从没放过"的哨兵（AppSettings 默认 -1）。宁可回到显眼处，
            // 也不要把方块丢到看不见的地方——主屏左侧还挂着副屏时这点会误判一次，
            // 但那比开机后找不到方块便宜。
            if (left < 0 || top < 0)
                return new Point(primaryWorkArea.Right - width - DefaultRightMargin,
                    primaryWorkArea.Bottom - height - DefaultBottomMargin);

            return new Point(Clamp(left, virtualDesktop.Left, virtualDesktop.Right - width),
                Clamp(top, virtualDesktop.Top, virtualDesktop.Bottom - height));
        }

        static double Clamp(double v, double min, double max) => max < min ? min : (v < min ? min : (v > max ? max : v));
    }
}
