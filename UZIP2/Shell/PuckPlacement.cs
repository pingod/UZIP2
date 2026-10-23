using System.Windows;

namespace UZIP2.Shell
{
    /// <summary>WPF 侧适配：真正的落点算法在 UZIP2.Core 的 PuckPlacement（Avalonia 共用）。</summary>
    public static class PuckPlacement
    {
        public static Point Resolve(double left, double top, double width, double height,
            Rect primaryWorkArea, Rect virtualDesktop)
        {
            var p = UZIP2.Services.PuckPlacement.Resolve(left, top, width, height,
                new UZIP2.Services.ScreenRect(primaryWorkArea.X, primaryWorkArea.Y, primaryWorkArea.Width, primaryWorkArea.Height),
                new UZIP2.Services.ScreenRect(virtualDesktop.X, virtualDesktop.Y, virtualDesktop.Width, virtualDesktop.Height));
            return new Point(p.X, p.Y);
        }
    }
}
