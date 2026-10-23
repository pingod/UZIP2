using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // Wayland 能力不是"有没有实现"的问题，而是桌面环境给不给：这里把结论固化成可测的判定表，
    // 界面据此显示"能用/为什么不能用"，而不是静默失败。
    public class DesktopCapabilitiesTests
    {
        [Fact]
        public void Kde_wayland_gets_tray_hotkeys_and_puck_positioning()
        {
            var c = DesktopCapabilities.Detect("wayland", "KDE");
            Assert.True(c.IsWayland);
            Assert.True(c.IsKde);
            Assert.True(c.TraySupported);
            Assert.True(c.GlobalHotkeys);
            Assert.True(c.PuckPositioning);
        }

        [Fact]
        public void Gnome_wayland_has_no_positioning_and_no_sni_tray()
        {
            var c = DesktopCapabilities.Detect("wayland", "ubuntu:GNOME");
            Assert.True(c.IsWayland);
            Assert.True(c.IsGnome);
            Assert.False(c.PuckPositioning);
            Assert.False(c.TraySupported);
        }

        [Fact]
        public void Wlroots_compositors_can_position_a_layer_shell_puck()
        {
            var c = DesktopCapabilities.Detect("wayland", "sway");
            Assert.True(c.IsWlroots);
            Assert.True(c.PuckPositioning);
            Assert.True(c.TraySupported);      // 有 SNI 宿主（waybar 等）
        }

        [Fact]
        public void X11_gets_everything_back()
        {
            var c = DesktopCapabilities.Detect("x11", "GNOME");
            Assert.True(c.IsX11);
            Assert.True(c.TraySupported);
            Assert.True(c.GlobalHotkeys);
            Assert.True(c.PuckPositioning);
        }

        [Fact]
        public void Missing_session_variable_falls_back_to_x11_semantics()
        {
            var c = DesktopCapabilities.Detect(null, null);
            Assert.True(c.IsX11);
            Assert.True(c.PuckPositioning);
        }

        [Fact]
        public void Empty_session_but_hyprland_is_treated_as_wayland()
        {
            var c = DesktopCapabilities.Detect("", "Hyprland");
            Assert.True(c.IsWayland);
            Assert.True(c.IsWlroots);
        }

        [Fact]
        public void Summary_explains_what_is_missing()
        {
            var c = DesktopCapabilities.Detect("wayland", "GNOME");
            Assert.Contains("Wayland", c.Summary());
            Assert.Contains("GNOME", c.Summary());
            Assert.Contains("托盘", c.Summary());
            Assert.Contains("定位", c.Summary());
        }
    }
}
