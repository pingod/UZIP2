using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // Linux 下程序目录通常不可写（/usr/lib/uzip2），配置与日志必须落到 XDG 目录。
    public class PlatformLayoutTests
    {
        [Fact]
        public void Windows_keeps_everything_next_to_the_exe()
        {
            var p = PlatformPaths.Resolve(isWindows: true, baseDirectory: @"C:\apps\UZIP2",
                homeDirectory: @"C:\Users\me", xdgConfigHome: null, xdgStateHome: null);
            Assert.Equal(@"C:\apps\UZIP2\Config", p.ConfigDirectory);
            Assert.Equal(@"C:\apps\UZIP2", p.LogBaseDirectory);
        }

        [Fact]
        public void Linux_honours_xdg_config_and_state_home()
        {
            var p = PlatformPaths.Resolve(false, "/opt/uzip2", "/home/me", "/home/me/.cfg", "/home/me/.state");
            Assert.Equal("/home/me/.cfg/uzip2", p.ConfigDirectory);
            Assert.Equal("/home/me/.state/uzip2", p.LogBaseDirectory);
        }

        [Fact]
        public void Linux_falls_back_to_the_spec_defaults()
        {
            var p = PlatformPaths.Resolve(false, "/opt/uzip2", "/home/me", null, "");
            Assert.Equal("/home/me/.config/uzip2", p.ConfigDirectory);
            Assert.Equal("/home/me/.local/state/uzip2", p.LogBaseDirectory);
        }

        [Fact]
        public void Empty_or_relative_xdg_values_are_ignored()
        {
            // XDG 规范：空值等于没设，相对路径必须忽略
            var p = PlatformPaths.Resolve(false, "/opt/uzip2", "/home/me", "", "relative/dir");
            Assert.Equal("/home/me/.config/uzip2", p.ConfigDirectory);
            Assert.Equal("/home/me/.local/state/uzip2", p.LogBaseDirectory);
        }

        [Fact]
        public void No_double_separator_when_xdg_path_ends_with_one()
        {
            var p = PlatformPaths.Resolve(false, "/opt/uzip2", "/home/me", "/home/me/.cfg/", null);
            Assert.Equal("/home/me/.cfg/uzip2", p.ConfigDirectory);
        }
    }
}
