using System;

namespace UZIP2.Services
{
    /// <summary>
    /// 桌面能力探测结果。能力取决于会话类型与桌面环境，而不是我们写了多少代码，
    /// 所以界面要能把"为什么这项不可用"直接讲出来，而不是静默失败。
    /// </summary>
    public sealed class DesktopCapabilities
    {
        public string Session { get; private set; } = "x11";     // x11 | wayland
        public string Desktop { get; private set; } = "";        // KDE / GNOME / sway / Hyprland…
        public bool IsX11 => Session == "x11";
        public bool IsWayland => Session == "wayland";
        public bool IsKde { get; private set; }
        public bool IsGnome { get; private set; }
        public bool IsWlroots { get; private set; }
        public bool TraySupported { get; private set; }
        public bool GlobalHotkeys { get; private set; }
        public bool PuckPositioning { get; private set; }

        public static DesktopCapabilities Detect(string sessionType, string currentDesktop)
        {
            var desktop = (currentDesktop ?? "").Trim();
            var upper = desktop.ToUpperInvariant();
            bool kde = upper.Contains("KDE") || upper.Contains("PLASMA");
            bool gnome = upper.Contains("GNOME");
            bool wlroots = ContainsAny(upper, "SWAY", "HYPRLAND", "NIRI", "WAYFIRE", "RIVER", "LABWC", "MANGO");

            string session = (sessionType ?? "").Trim().ToLowerInvariant();
            // 没设 XDG_SESSION_TYPE 时：桌面名本身是合成器就按 Wayland，否则按 X11
            if (session != "x11" && session != "wayland")
                session = wlroots || upper.Contains("WAYLAND") ? "wayland" : "x11";

            var x11 = session == "x11";
            var c = new DesktopCapabilities
            {
                Session = session,
                Desktop = desktop,
                IsKde = kde,
                IsGnome = gnome,
                IsWlroots = wlroots
            };

            // 托盘：X11 与 KDE/XFCE 等有 SNI 宿主；GNOME 需要用户装 AppIndicator 扩展
            bool otherSni = ContainsAny(upper, "XFCE", "CINNAMON", "MATE", "LXQT", "BUDGIE", "UNITY");
            c.TraySupported = x11 || kde || otherSni || wlroots;

            // 全局热键：X11 直接抓键；Wayland 只有 KDE 的 GlobalShortcuts portal 稳定可用
            c.GlobalHotkeys = x11 || kde;

            // 方块定位：X11 可自由定位；wlroots 有 layer-shell；KDE 可用窗口规则；GNOME 无接口
            c.PuckPositioning = x11 || kde || wlroots;

            return c;
        }

        /// <summary>给设置页用的一句话说明（中文）。</summary>
        public string Summary()
        {
            var session = IsWayland ? "Wayland" : "X11";
            var name = string.IsNullOrEmpty(Desktop) ? "未知桌面" : Desktop;
            var parts = new System.Collections.Generic.List<string> { $"会话 {session} / {name}" };

            if (TraySupported) parts.Add("托盘可用");
            else parts.Add("托盘需要 AppIndicator 扩展（GNOME 默认不显示）");

            parts.Add(GlobalHotkeys ? "全局热键可用" : "全局热键不可用（Wayland 没有通用接口）");
            parts.Add(PuckPositioning ? "悬浮方块可定位" : "悬浮方块无法定位窗口（GNOME Wayland 限制）");
            return string.Join("；", parts) + "。";
        }

        static bool ContainsAny(string haystack, params string[] needles)
        {
            foreach (var n in needles)
                if (haystack.IndexOf(n, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
