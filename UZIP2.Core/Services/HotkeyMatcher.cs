using System;

namespace UZIP2.Services
{
    /// <summary>
    /// 全局热键的"边沿检测"逻辑：X11 下轮询键位图与修饰键掩码，只在按下那一刻触发一次。
    /// 与平台 API 解耦，便于单测。
    /// </summary>
    public sealed class HotkeyMatcher
    {
        public const uint ShiftMask = 1;
        public const uint ControlMask = 4;
        public const uint Mod1Mask = 8;   // X11 的 Alt

        private readonly uint _keycode;
        private readonly bool _ctrl;
        private readonly bool _alt;
        private readonly bool _shift;
        private bool _wasDown;

        public HotkeyMatcher(uint keycode, bool ctrl, bool alt, bool shift)
        {
            _keycode = keycode;
            _ctrl = ctrl;
            _alt = alt;
            _shift = shift;
        }

        /// <summary>喂一次当前状态；返回 true 表示"此刻刚按下且修饰键匹配"。</summary>
        public bool Feed(uint modifierMask, byte[] keys)
        {
            bool down = IsDown(keys);
            bool fire = down && !_wasDown && ModifiersOk(modifierMask);
            _wasDown = down;
            return fire;
        }

        private bool IsDown(byte[] keys)
        {
            if (_keycode == 0 || keys == null || keys.Length < 32) return false;
            int index = (int)(_keycode / 8);
            if (index >= keys.Length) return false;
            return (keys[index] & (1 << (int)(_keycode % 8))) != 0;
        }

        private bool ModifiersOk(uint mask)
            => ((mask & ControlMask) != 0) == _ctrl
            && ((mask & Mod1Mask) != 0) == _alt
            && ((mask & ShiftMask) != 0) == _shift;
    }
}
