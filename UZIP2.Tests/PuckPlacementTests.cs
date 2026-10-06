using System.Windows;
using UZIP2.Shell;
using Xunit;

namespace UZIP2.Tests
{
    // 方块落点的纯函数：多屏下"记忆位置"不能被主屏的工作区拽回来。
    public class PuckPlacementTests
    {
        // 主屏 2560x1440（工作区扣掉 48 高的任务栏），副屏在右侧竖屏
        static readonly Rect PrimaryWork = new Rect(0, 0, 2560, 1392);
        static readonly Rect Virtual = new Rect(0, 0, 4000, 2560);

        const double W = 104, H = 104;

        [Fact]
        public void Keeps_a_position_on_the_monitor_to_the_right()
        {
            var p = PuckPlacement.Resolve(2700, 200, W, H, PrimaryWork, Virtual);
            Assert.Equal(2700, p.X);
            Assert.Equal(200, p.Y);
        }

        [Fact]
        public void Keeps_the_saved_spot_of_a_real_user_on_the_second_screen()
        {
            // 线上实况：用户把方块拖到副屏后存下 2624,1421，旧实现按主屏工作区夹回 2456,1288
            var p = PuckPlacement.Resolve(2624, 1421, W, H, PrimaryWork, Virtual);
            Assert.Equal(2624, p.X);
            Assert.Equal(1421, p.Y);
        }

        [Fact]
        public void Pulls_a_position_outside_the_virtual_desktop_back_inside()
        {
            var p = PuckPlacement.Resolve(9000, 9000, W, H, PrimaryWork, Virtual);
            Assert.Equal(Virtual.Right - W, p.X);
            Assert.Equal(Virtual.Bottom - H, p.Y);
        }

        [Fact]
        public void Keeps_the_whole_square_visible_when_it_straddles_the_right_edge()
        {
            var p = PuckPlacement.Resolve(3950, 200, W, H, PrimaryWork, Virtual);
            Assert.Equal(Virtual.Right - W, p.X);
            Assert.Equal(200, p.Y);
        }

        [Fact]
        public void Never_placed_lands_in_the_primary_bottom_right_corner()
        {
            var p = PuckPlacement.Resolve(-1, -1, W, H, PrimaryWork, Virtual);
            Assert.Equal(PrimaryWork.Right - W - 24, p.X);
            Assert.Equal(PrimaryWork.Bottom - H - 96, p.Y);
        }

        [Fact]
        public void A_negative_saved_spot_still_means_never_placed()
        {
            // 哨兵语义保持原样：负值 = 从没放过（宁可回到显眼处，也不要把方块丢到屏幕外）
            var p = PuckPlacement.Resolve(-1, 500, W, H, PrimaryWork, Virtual);
            Assert.Equal(PrimaryWork.Right - W - 24, p.X);
            Assert.Equal(PrimaryWork.Bottom - H - 96, p.Y);
        }
    }
}
