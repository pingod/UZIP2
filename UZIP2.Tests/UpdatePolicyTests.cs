using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // 自更新是把新的 .exe 换上去，只有 Windows 的单文件框架依赖版适用。
    public class UpdatePolicyTests
    {
        [Fact]
        public void In_place_update_is_windows_only()
            => Assert.True(UpdatePolicy.CanApplyInPlaceOn(isWindows: true));

        [Fact]
        public void Linux_refuses_in_place_update_and_points_at_the_package_manager()
        {
            Assert.False(UpdatePolicy.CanApplyInPlaceOn(isWindows: false));
            Assert.Contains("pacman", UpdatePolicy.PackageManagerHint);
        }
    }
}
