using System;
using UZIP2.Services;
using Xunit;

namespace UZIP2.Tests
{
    // X11 没有"注册热键"的低成本路径，这里用轮询：按键位图 + 修饰键掩码。
    // 纯逻辑单独测，真正的 X11 调用在 Avalonia 宿主里。
    public class HotkeyMatcherTests
    {
        const uint ShiftMask = 1;
        const uint ControlMask = 4;
        const uint Mod1Mask = 8;     // Alt
        const uint KeyU = 0x55;

        static byte[] Keys(params uint[] codes)
        {
            var map = new byte[32];
            foreach (var c in codes) map[c / 8] |= (byte)(1 << (int)(c % 8));
            return map;
        }

        [Fact]
        public void Fires_once_on_the_key_down_edge()
        {
            var m = new HotkeyMatcher(KeyU, ctrl: true, alt: true, shift: false);
            Assert.True(m.Feed(ControlMask | Mod1Mask, Keys(KeyU)));
            Assert.False(m.Feed(ControlMask | Mod1Mask, Keys(KeyU)), "按住不应重复触发");
            Assert.False(m.Feed(ControlMask | Mod1Mask, Keys()));
            Assert.True(m.Feed(ControlMask | Mod1Mask, Keys(KeyU)), "松开再按应再次触发");
        }

        [Theory]
        [InlineData(0u)]                       // 没有修饰键
        [InlineData(ControlMask)]              // 少了 Alt
        [InlineData(Mod1Mask)]                 // 少了 Ctrl
        [InlineData(ControlMask | Mod1Mask | ShiftMask)]  // 多了 Shift
        public void Wrong_modifiers_do_not_fire(uint mask)
        {
            var m = new HotkeyMatcher(KeyU, ctrl: true, alt: true, shift: false);
            Assert.False(m.Feed(mask, Keys(KeyU)));
        }

        [Fact]
        public void Other_keys_do_not_fire()
        {
            var m = new HotkeyMatcher(KeyU, ctrl: true, alt: true, shift: false);
            Assert.False(m.Feed(ControlMask | Mod1Mask, Keys(0x41)));
        }

        [Fact]
        public void Shift_only_hotkey_matches_exactly()
        {
            var m = new HotkeyMatcher(KeyU, ctrl: false, alt: false, shift: true);
            Assert.True(m.Feed(ShiftMask, Keys(KeyU)));
            Assert.False(m.Feed(ShiftMask, Keys(KeyU)));
            Assert.False(new HotkeyMatcher(KeyU, false, false, true).Feed(0, Keys(KeyU)));
        }

        [Fact]
        public void Invalid_keycode_never_fires()
        {
            var m = new HotkeyMatcher(0, ctrl: true, alt: true, shift: false);
            Assert.False(m.Feed(ControlMask | Mod1Mask, Keys(KeyU)));
        }
    }
}
