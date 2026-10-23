using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>
    /// X11 全局热键：不抢键（XGrabKey），而是轮询键位图 + 修饰键掩码，
    /// 只在"按下那一刻"触发。Wayland 下没有等价接口，由 DesktopCapabilities 挡掉。
    /// 任何一步失败都返回 null —— 没有 DISPLAY、没有 libX11 时静默降级。
    /// </summary>
    public sealed class X11HotkeyWatcher : IDisposable
    {
        [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
        private static extern IntPtr XOpenDisplay(string name);

        [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport("libX11.so.6", EntryPoint = "XDefaultRootWindow")]
        private static extern IntPtr XDefaultRootWindow(IntPtr display);

        [DllImport("libX11.so.6", EntryPoint = "XQueryKeymap")]
        private static extern int XQueryKeymap(IntPtr display, byte[] keys);

        [DllImport("libX11.so.6", EntryPoint = "XQueryPointer")]
        private static extern int XQueryPointer(IntPtr display, IntPtr window,
            out IntPtr rootReturn, out IntPtr childReturn,
            out int rootX, out int rootY, out int winX, out int winY, out uint maskReturn);

        [DllImport("libX11.so.6", EntryPoint = "XStringToKeysym")]
        private static extern ulong XStringToKeysym(string name);

        [DllImport("libX11.so.6", EntryPoint = "XKeysymToKeycode")]
        private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);

        private readonly IntPtr _display;
        private readonly IntPtr _root;
        private readonly HotkeyMatcher _matcher;
        private readonly Action _onFire;
        private readonly DispatcherTimer _timer;
        private readonly byte[] _keys = new byte[32];

        private X11HotkeyWatcher(IntPtr display, IntPtr root, HotkeyMatcher matcher, Action onFire)
        {
            _display = display;
            _root = root;
            _matcher = matcher;
            _onFire = onFire;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            _timer.Tick += (_, __) => Poll();
            _timer.Start();
        }

        public static X11HotkeyWatcher TryCreate(string keysymName, bool ctrl, bool alt, bool shift, Action onFire)
        {
            if (!OperatingSystem.IsLinux() || onFire == null) return null;
            IntPtr display = IntPtr.Zero;
            try
            {
                display = XOpenDisplay(null);
                if (display == IntPtr.Zero) return null;

                ulong sym = XStringToKeysym(keysymName ?? "u");
                byte code = XKeysymToKeycode(display, sym);
                if (code == 0)
                {
                    XCloseDisplay(display);
                    return null;
                }
                return new X11HotkeyWatcher(display, XDefaultRootWindow(display),
                    new HotkeyMatcher(code, ctrl, alt, shift), onFire);
            }
            catch
            {
                if (display != IntPtr.Zero)
                {
                    try { XCloseDisplay(display); } catch { }
                }
                return null;
            }
        }

        private void Poll()
        {
            try
            {
                if (XQueryPointer(_display, _root, out _, out _, out _, out _, out _, out _, out uint mask) == 0)
                    return;
                if (XQueryKeymap(_display, _keys) == 0) return;
                if (_matcher.Feed(mask, _keys)) _onFire();
            }
            catch
            {
                // 轮询失败不拖垮界面
            }
        }

        public void Dispose()
        {
            _timer.Stop();
            try { XCloseDisplay(_display); } catch { }
        }
    }
}
