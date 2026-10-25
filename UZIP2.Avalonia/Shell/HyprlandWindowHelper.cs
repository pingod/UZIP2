using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using UZIP2.Services;

namespace UZIP2.Avalonia.Shell
{
    /// <summary>
    /// Hyprland 窗口修正：把窗口改成浮动并固定到指定坐标。
    ///
    /// 为什么要绕这一圈：Wayland 的 xdg-shell 协议里，客户端<b>无法</b>声明自己是浮动窗口，
    /// 也<b>无法</b>指定自己的绝对坐标 —— 这两件事完全由合成器决定。Avalonia 的 Wayland
    /// 后端同样没有 layer-shell（`Avalonia.Wayland.dll` 里搜不到 `zwlr_layer_shell`），
    /// 所以一个 112×112 的`WindowDecorations=None` 窗口在 Hyprland 的 dwindle 布局下会被
    /// 当成普通平铺窗口塞进布局树，被拉伸成 1268×688。
    ///
    /// 对策是走 Hyprland 自己的 IPC（`hyprctl`）：先用 `hl.dsp.window.float` 让窗口进位
    /// 浮动层，再用 `hl.dsp.window.move` 摆到目标坐标。实测这两条命令能把窗口从
    /// 1268×688 立刻变回 112×112 并落到指定位置。
    ///
    /// 只在 Hyprland 上启用；其它合成器不认识 hyprctl（sway 另有 swaymsg、KDE 走
    /// 窗口规则），保持原有行为即可。
    /// </summary>
    public static class HyprlandWindowHelper
    {
        private static bool? _available;
        private static string _hyprctl;
        // 本进程窗口的 Hyprland address。窗口从创建到销毁都不变，缓存起来；
        // 否则 50ms 一次的摆位追踪每轮都要起一个 hyprctl 进程去查它，纯浪费。
        private static string _selfAddress;

        /// <summary>Hyprland 会话里才有 hyprctl 可用；探测结果缓存。<summary>
        public static bool IsAvailable
        {
            get
            {
                if (_available.HasValue) return _available.Value;
                _available = Detect();
                return _available.Value;
            }
        }

        private static bool Detect()
        {
            if (!OperatingSystem.IsLinux()) return false;
            var desktop = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").ToUpperInvariant();
            var sig = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
            if (!desktop.Contains("HYPRLAND") && string.IsNullOrEmpty(sig)) return false;
            _hyprctl = FindOnPath("hyprctl");
            return _hyprctl != null;
        }

        /// <summary>
        /// 让窗口进入浮动层（只做这一步，不摆位置）。
        /// 和摆位分开是因为摆位需要反复纠正（窗口映射前后合成器会改写落点），
        /// 而「进位浮动层」只该做一次 —— 每 50ms 重复发一遍纯属浪费进程。
        /// </summary>
        public static bool Float()
        {
            if (!IsAvailable) return false;
            var addr = SelfAddress();
            if (addr == null) return false;
            return Dispatch(BuildFloatCommand(addr))
                || Dispatch("setfloating address:" + addr); // 老版本兜底
        }

        /// <summary>只把浮动窗口摆到 (x, y)，不改浮动状态。</summary>
        public static bool MoveTo(int x, int y)
        {
            if (!IsAvailable) return false;
            var addr = SelfAddress();
            if (addr == null) return false;
            return Dispatch(BuildMoveCommand(addr, x, y));
        }

        /// <summary>
        /// 本进程窗口的 address，查一次就缓存。
        /// 窗口刚创建、还没被合成器收进 clients 列表时查不到，这时<b>不</b>缓存失败结果，
        /// 让调用方下次再试。
        /// </summary>
        private static string SelfAddress()
        {
            if (_selfAddress != null) return _selfAddress;
            _selfAddress = FindAddressByPid(Environment.ProcessId);
            return _selfAddress;
        }

        /// <summary>
        /// 让本进程的窗口进入浮动层并摆到 (x, y)。
        /// 返回 false 表示这一步没做成（不是 Hyprland、找不到 hyprctl、或者 IPC 拒绝），
        /// 调用方不要因此报错 —— 用户仍然可以手动拖动方块。
        /// </summary>
        public static bool FloatAt(int x, int y)
        {
            if (!IsAvailable) return false;
            var addr = FindAddressByPid(Environment.ProcessId);
            if (addr == null) return false;
            return FloatAtAddress(addr, x, y);
        }

        /// <summary>
        /// 按窗口地址让窗口浮动并摆位。地址来自 <c>hyprctl clients -j</c> 的 <c>address</c> 字段
        /// （形如 <c>0x5601564cf680</c>）。抽出来是为了让无头测试只验命令拼装、不碰真进程。
        /// </summary>
        public static bool FloatAtAddress(string address, int x, int y)
        {
            if (string.IsNullOrEmpty(address) || _hyprctl == null) return false;
            // 一条命令里连做两步：先进浮动层，再摆坐标。
            // 现代 Hyprland（0.5x，Lua 配置解析器）只认 hl.dsp.* 这种 Lua 形式；
            // 老的 setfloating/movewindowpixel 裸动词会直接语法报错。
            var floatOk = Dispatch(BuildFloatCommand(address))
                       || Dispatch("setfloating address:" + address); // 老版本兜底
            var moveOk = Dispatch(BuildMoveCommand(address, x, y));
            return floatOk && moveOk;
        }

        /// <summary>
        /// 读出本进程窗口此刻的真实坐标。Wayland 下 Avalonia 的 <c>Window.Position</c> 读回值
        /// 没有意义（合成器不告诉客户端绝对位置，实测恒为 0,0），只能问合成器要。
        /// 返回 false 表示读不到。
        /// </summary>
        public static bool TryGetPosition(out int x, out int y)
        {
            x = 0;
            y = 0;
            if (!IsAvailable) return false;
            var json = Run(_hyprctl, 3000, args: new[] { "-j", "clients" });
            return TryParsePosition(json, Environment.ProcessId, out x, out y);
        }

        /// <summary>从 <c>hyprctl clients -j</c> 的 JSON 里取出指定 pid 窗口的 at 坐标。测试用。</summary>
        internal static bool TryParsePosition(string clientsJson, int pid, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (string.IsNullOrEmpty(clientsJson)) return false;
            try
            {
                using var doc = JsonDocument.Parse(clientsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    if (!c.TryGetProperty("pid", out var pidEl) || pidEl.ValueKind != JsonValueKind.Number) continue;
                    if (pidEl.GetInt32() != pid) continue;
                    if (!c.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Array) continue;
                    if (at.GetArrayLength() < 2) continue;
                    x = at[0].GetInt32();
                    y = at[1].GetInt32();
                    return true;
                }
            }
            catch (JsonException)
            {
                // 输出不是 JSON：当作读不到。
            }
            catch (InvalidOperationException)
            {
                // at 数组里不是数字：同上。
            }
            return false;
        }

        /// <summary>拼出「让窗口浮动」的那条 dispatch 命令。测试用。</summary>
        internal static string BuildFloatCommand(string address) =>
            "hl.dsp.window.float({ window = \"address:" + address + "\", action = \"enable\" })";

        /// <summary>拼出「把窗口移到 (x, y)」的那条 dispatch 命令。测试用。</summary>
        internal static string BuildMoveCommand(string address, int x, int y) =>
            "hl.dsp.window.move({ window = \"address:" + address + "\", x = " + x + ", y = " + y + " })";

        /// <summary>
        /// 从 <c>hyprctl clients -j</c> 的 JSON 里挑出属于本进程的那个窗口地址。
        /// 用 pid 匹配（比窗口标题稳，标题会随界面语言/状态变）。
        /// </summary>
        internal static string PickAddress(string clientsJson, int pid)
        {
            if (string.IsNullOrEmpty(clientsJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(clientsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    if (!c.TryGetProperty("pid", out var pidEl) || pidEl.ValueKind != JsonValueKind.Number) continue;
                    if (pidEl.GetInt32() != pid) continue;
                    if (!c.TryGetProperty("address", out var addrEl)) continue;
                    var addr = addrEl.GetString();
                    if (!string.IsNullOrEmpty(addr)) return addr;
                }
            }
            catch (JsonException)
            {
                // hyprctl 的输出不是 JSON（版本不匹配 / 被别的工具占用），当作没有答案。
            }
            return null;
        }

        private static string FindAddressByPid(int pid)
        {
            var json = Run(_hyprctl, 3000, args: new[] { "-j", "clients" });
            return PickAddress(json, pid);
        }

        private static bool Dispatch(string command)
        {
            var output = Run(_hyprctl, 3000, args: new[] { "dispatch", command });
            return output != null && !output.TrimStart().StartsWith("error", StringComparison.OrdinalIgnoreCase);
        }

        private static string Run(string exe, int timeoutMs, string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p == null) return null;
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    return null;
                }
                // hyprctl 的失败信息走 stderr，成功时 stdout 是 "ok"。
                if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout)) return stderr ?? "";
                return stdout + stderr;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       || ex is InvalidOperationException
                                       || ex is IOException)
            {
                return null;
            }
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
