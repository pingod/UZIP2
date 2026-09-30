using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UZIP2.Services
{
    // 全局热键（移植 UHotKey + MainWindow.WndProc，修正 64 位 wParam 比较）
    public sealed class HotKeyService : IDisposable
    {
        const int WM_HOTKEY = 0x0312;
        static int _nextId = 0x9999;

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        class HotKeyRegistration
        {
            public int Id;
            public uint Modifiers;
            public uint Key;
            public Action Callback;
            public bool Registered;
        }

        private readonly Dictionary<int, HotKeyRegistration> _registrations = new Dictionary<int, HotKeyRegistration>();
        private HwndSource _source;
        private IntPtr _hwnd;
        private readonly object _sync = new object();

        // 绑定主窗口句柄后才可以注册系统级热键
        public void Attach(Window window)
        {
            Detach();
            var helper = new WindowInteropHelper(window);
            _hwnd = helper.Handle == IntPtr.Zero ? throw new InvalidOperationException("窗口句柄尚未创建") : helper.Handle;
            _source = HwndSource.FromHwnd(_hwnd);
            _source.AddHook(WndProc);
            lock (_sync)
            {
                foreach (var r in _registrations.Values)
                    r.Registered = RegisterHotKey(_hwnd, r.Id, r.Modifiers, r.Key);
            }
        }

        public void Detach()
        {
            if (_source == null) return;
            lock (_sync)
            {
                foreach (var r in _registrations.Values)
                    if (r.Registered) { UnregisterHotKey(_hwnd, r.Id); r.Registered = false; }
            }
            _source.RemoveHook(WndProc);
            _source = null;
            _hwnd = IntPtr.Zero;
        }

        // 注册(替换)唯一热键组合；未 Attach 时先记录，Attach 后自动生效
        public bool Register(uint vk, bool alt, bool shift, bool ctrl, Action fired)
        {
            uint mod = 0;
            if (alt) mod |= 0x0001;
            if (ctrl) mod |= 0x0002;
            if (shift) mod |= 0x0004;
            if (mod == 0 || vk == 0) return false;

            lock (_sync)
            {
                foreach (var r in _registrations.Values)
                {
                    if (r.Key == vk && r.Modifiers == mod)
                    {
                        r.Callback = fired;
                        return true;
                    }
                }
                var reg = new HotKeyRegistration
                {
                    Id = _nextId--,
                    Modifiers = mod,
                    Key = vk,
                    Callback = fired
                };
                _registrations[reg.Id] = reg;
                if (_hwnd != IntPtr.Zero)
                    reg.Registered = RegisterHotKey(_hwnd, reg.Id, mod, vk);
                return reg.Registered || _hwnd == IntPtr.Zero;
            }
        }

        public void Unregister(uint vk, bool alt, bool shift, bool ctrl)
        {
            uint mod = 0;
            if (alt) mod |= 0x0001;
            if (ctrl) mod |= 0x0002;
            if (shift) mod |= 0x0004;
            lock (_sync)
            {
                var hit = new List<int>();
                foreach (var kv in _registrations)
                    if (kv.Value.Key == vk && kv.Value.Modifiers == mod) hit.Add(kv.Key);
                foreach (var id in hit)
                {
                    if (_registrations[id].Registered && _hwnd != IntPtr.Zero)
                        UnregisterHotKey(_hwnd, id);
                    _registrations.Remove(id);
                }
            }
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handle)
        {
            if (msg == WM_HOTKEY)
            {
                // 64 位进程 wParam 比较必须走 ulong，避免 ToInt32 溢出
                lock (_sync)
                {
                    foreach (var r in _registrations.Values)
                        if (r.Registered && (ulong)wParam == (ulong)r.Id)
                        {
                            try { r.Callback?.Invoke(); } catch { }
                            handle = true;
                            break;
                        }
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose() => Detach();
    }
}
