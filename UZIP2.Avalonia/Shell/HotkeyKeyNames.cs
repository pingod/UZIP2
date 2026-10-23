using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Input;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>
    /// 虚拟键码（Windows 约定，存进 <c>AppSettings.HotKeyKey</c>）与 Avalonia <see cref="Key"/>、
    /// X11 keysym 名三者的互转。
    ///
    /// 设置文件跨平台共用，所以两种宿主必须说同一套码：WPF 那边存的是 Win32 虚拟键码，
    /// Avalonia 这边也要能读同一份配置。历史上这里只映射了 A-Z / 0-9，其余键在
    /// <see cref="ToKeySymName"/> 里被静默退回 "u" —— 用户在设置里选了 F5 实际按的是 U。
    /// </summary>
    public static class HotkeyKeyNames
    {
        // 常用的可枚举键：虚拟键码 <-> (Avalonia Key, keysym 名)。顺序即设置页下拉框的展示顺序。
        private static readonly (uint Vk, Key Key, string KeySym, string Label)[] Table =
        {
            (0x20, Key.Space, "space", "空格"),
            (0x0D, Key.Enter, "Return", "回车"),
            (0x09, Key.Tab, "Tab", "Tab"),
            (0x1B, Key.Escape, "Escape", "Esc"),
            (0x08, Key.Back, "BackSpace", "退格"),
            (0x2D, Key.Insert, "Insert", "Insert"),
            (0x2E, Key.Delete, "Delete", "Delete"),
            (0x24, Key.Home, "Home", "Home"),
            (0x23, Key.End, "End", "End"),
            (0x21, Key.PageUp, "Prior", "PageUp"),
            (0x22, Key.PageDown, "Next", "PageDown"),
            (0x25, Key.Left, "Left", "左方向键"),
            (0x26, Key.Up, "Up", "上方向键"),
            (0x27, Key.Right, "Right", "右方向键"),
            (0x28, Key.Down, "Down", "下方向键"),
            (0x13, Key.Pause, "Pause", "Pause"),
            (0x2C, Key.PrintScreen, "Print", "PrintScreen"),
            (0x14, Key.CapsLock, "Caps_Lock", "CapsLock"),
            (0x70, Key.F1, "F1", "F1"),
            (0x71, Key.F2, "F2", "F2"),
            (0x72, Key.F3, "F3", "F3"),
            (0x73, Key.F4, "F4", "F4"),
            (0x74, Key.F5, "F5", "F5"),
            (0x75, Key.F6, "F6", "F6"),
            (0x76, Key.F7, "F7", "F7"),
            (0x77, Key.F8, "F8", "F8"),
            (0x78, Key.F9, "F9", "F9"),
            (0x79, Key.F10, "F10", "F10"),
            (0x7A, Key.F11, "F11", "F11"),
            (0x7B, Key.F12, "F12", "F12"),
        };

        private static readonly Dictionary<uint, (Key Key, string KeySym, string Label)> ByVk =
            new Dictionary<uint, (Key, string, string)>();

        private static readonly Dictionary<Key, uint> ByKey = new Dictionary<Key, uint>();

        static HotkeyKeyNames()
        {
            foreach (var row in Table)
            {
                ByVk[row.Vk] = (row.Key, row.KeySym, row.Label);
                ByKey[row.Key] = row.Vk;
            }
        }

        /// <summary>字母 / 数字 / 上表里的功能键都算可绑定键。</summary>
        public static bool IsSupported(uint vk)
            => ByVk.ContainsKey(vk) || (vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9');

        /// <summary>Avalonia 按键事件 -> 虚拟键码；不认识返回 0。</summary>
        public static uint FromKey(Key key)
        {
            if (ByKey.TryGetValue(key, out var vk)) return vk;
            if (key >= Key.A && key <= Key.Z) return (uint)('A' + (key - Key.A));
            if (key >= Key.D0 && key <= Key.D9) return (uint)('0' + (key - Key.D0));
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return 0x60 + (uint)(key - Key.NumPad0);
            return 0;
        }

        /// <summary>虚拟键码 -> X11 keysym 名（给 X11HotkeyWatcher 用）。</summary>
        public static string ToKeySymName(uint vk)
        {
            if (ByVk.TryGetValue(vk, out var row)) return row.KeySym;
            if (vk >= 'A' && vk <= 'Z') return ((char)('a' + (vk - 'A'))).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "KP_" + (vk - 0x60); // 小键盘
            // 实在不认识就退回 u：热键至少还能用，并且日志里会记下原始码
            return "u";
        }

        /// <summary>给人看的键名，例如 "F5"、"空格"。</summary>
        public static string Label(uint vk)
        {
            if (ByVk.TryGetValue(vk, out var row)) return row.Label;
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "小键盘" + (vk - 0x60);
            return vk == 0 ? "未设置" : "键码 0x" + vk.ToString("X2");
        }

        /// <summary>完整组合，例如 "Ctrl+Alt+F5"；与 WPF 版 FormatHotkey 的文案顺序一致。</summary>
        public static string Format(uint vk, bool ctrl, bool alt, bool shift)
        {
            if (vk == 0) return "未设置";
            var parts = new List<string>(4);
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            parts.Add(Label(vk));
            return string.Join("+", parts);
        }

        /// <summary>设置页下拉框用的候选键。</summary>
        public static IReadOnlyList<(uint Vk, string Label)> Choices
        {
            get
            {
                var list = new List<(uint, string)>();
                foreach (var row in Table) list.Add((row.Vk, row.Label));
                for (char c = 'A'; c <= 'Z'; c++) list.Add((c, c.ToString()));
                for (char c = '0'; c <= '9'; c++) list.Add((c, c.ToString()));
                return list;
            }
        }

        /// <summary>把实际按下的键写成 keysym 名，供日志/诊断使用。</summary>
        public static string Describe(uint vk, bool ctrl, bool alt, bool shift)
        {
            var sb = new StringBuilder(Format(vk, ctrl, alt, shift));
            if (!IsSupported(vk) && vk != 0)
            {
                sb.Append("（不在已知键表内，X11 下会退回 u）");
            }
            return sb.ToString();
        }
    }
}
