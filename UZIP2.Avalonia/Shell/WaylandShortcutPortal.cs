using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>
    /// Wayland 全局热键：走 XDG 的 <c>org.freedesktop.portal.GlobalShortcuts</c>。
    /// 用 gdbus 调 portal（而不是内置一整套 D-Bus 栈），监听 Activated 信号唤醒窗口。
    /// 没有 portal（GNOME/wlroots）或没有 gdbus 时返回 null —— 由能力探测决定要不要尝试。
    /// </summary>
    public sealed class WaylandShortcutPortal : IDisposable
    {
        private const string Dest = "org.freedesktop.portal.Desktop";
        private const string ObjPath = "/org/freedesktop/portal/desktop";
        private const string Iface = "org.freedesktop.portal.GlobalShortcuts";

        private readonly string _gdbus;
        private readonly string _sessionPath;
        private readonly string _shortcutId;
        private readonly Action _onActivated;
        private Process _monitor;

        private WaylandShortcutPortal(string gdbus, string sessionPath, string shortcutId, Action onActivated)
        {
            _gdbus = gdbus;
            _sessionPath = sessionPath;
            _shortcutId = shortcutId;
            _onActivated = onActivated;
        }

        public static WaylandShortcutPortal TryCreate(string shortcutId, string description,
            string preferredTrigger, Action onActivated)
        {
            if (!OperatingSystem.IsLinux() || onActivated == null) return null;

            var caps = DesktopCapabilities.Detect(
                Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"));
            if (!caps.IsWayland || !caps.GlobalHotkeys) return null;

            var gdbus = FindOnPath("gdbus");
            if (gdbus == null) return null;

            try
            {
                var token = "uzip2_" + Guid.NewGuid().ToString("N");
                var create = Call(gdbus, 5000, "call", "--session", "--dest", Dest, "--object-path", ObjPath,
                    "--method", Iface + ".CreateSession",
                    "{'handle_token': <'" + token + "'>, 'session_handle_token': <'" + token + "'>}");
                var session = PortalOutput.ParseSessionPath(create);
                if (session == null) return null;

                var trigger = string.IsNullOrEmpty(preferredTrigger)
                    ? ""
                    : ", 'preferred_trigger': <'" + preferredTrigger + "'>";
                Call(gdbus, 5000, "call", "--session", "--dest", Dest, "--object-path", ObjPath,
                    "--method", Iface + ".BindShortcuts",
                    session,
                    "[('" + shortcutId + "', {'description': <'" + description + "'>" + trigger + "})]",
                    "", "{}");

                var portal = new WaylandShortcutPortal(gdbus, session, shortcutId, onActivated);
                portal.StartMonitor();
                return portal;
            }
            catch
            {
                return null;
            }
        }

        private void StartMonitor()
        {
            var psi = new ProcessStartInfo(_gdbus)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var a in new[] { "monitor", "--session", "--dest", Dest }) psi.ArgumentList.Add(a);

            _monitor = Process.Start(psi);
            if (_monitor == null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    string line;
                    while ((line = await _monitor.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (PortalOutput.IsActivated(line, _shortcutId)) _onActivated();
                    }
                }
                catch { /* 监测进程退出即结束 */ }
            });
        }

        public void Dispose()
        {
            try
            {
                if (_monitor != null && !_monitor.HasExited) _monitor.Kill(true);
            }
            catch { }
            try
            {
                Call(_gdbus, 2000, "call", "--session", "--dest", Dest, "--object-path", ObjPath,
                    "--method", Iface + ".CloseSession", _sessionPath);
            }
            catch { }
        }

        // ---- 进程辅助 ----

        private static string Call(string gdbus, int timeoutMs, params string[] args)
        {
            var psi = new ProcessStartInfo(gdbus)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            return stdout.GetAwaiter().GetResult();
        }

        private static string FindOnPath(string exe)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
            return null;
        }
    }
}
