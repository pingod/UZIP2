using System;
using System.IO;

namespace UZIP2.Services
{
    /// <summary>配置与日志的落盘位置。</summary>
    public readonly struct PlatformLayout
    {
        public PlatformLayout(string configDirectory, string logBaseDirectory)
        {
            ConfigDirectory = configDirectory;
            LogBaseDirectory = logBaseDirectory;
        }

        public string ConfigDirectory { get; }
        public string LogBaseDirectory { get; }
    }

    /// <summary>
    /// 按平台决定配置与日志目录。Windows 沿用"程序目录下 Config\ + logs\"的老约定；
    /// Linux 程序目录通常在 /usr 下不可写，配置走 XDG_CONFIG_HOME，日志走 XDG_STATE_HOME。
    /// 分隔符由 isWindows 决定（而不是宿主 OS），这样 Linux 布局在 Windows 上也能被单测和
    /// 交叉构建验证。
    /// </summary>
    public static class PlatformPaths
    {
        public const string AppDirectoryName = "uzip2";

        public static PlatformLayout Resolve(bool isWindows, string baseDirectory,
            string homeDirectory, string xdgConfigHome, string xdgStateHome)
        {
            if (isWindows)
                return new PlatformLayout(Join(true, baseDirectory, "Config"), baseDirectory);

            string configRoot = XdgDir(xdgConfigHome, homeDirectory, ".config", isWindows: false);
            string stateRoot = XdgDir(xdgStateHome, homeDirectory, ".local/state", isWindows: false);
            return new PlatformLayout(
                Join(false, configRoot, AppDirectoryName),
                Join(false, stateRoot, AppDirectoryName));
        }

        static string XdgDir(string xdg, string home, string fallbackUnderHome, bool isWindows)
        {
            // XDG 规范：空值与相对路径都视为未设置
            if (!string.IsNullOrWhiteSpace(xdg) && IsRooted(xdg))
                return TrimTrailing(xdg.Trim());
            return Join(isWindows, home, fallbackUnderHome);
        }

        static bool IsRooted(string path)
            => path.StartsWith("/", StringComparison.Ordinal)
               || (path.Length > 1 && path[1] == ':');   // C:\… 这类 Windows 绝对路径

        static string TrimTrailing(string path)
            => path.Length > 1 ? path.TrimEnd('/', '\\') : path;

        static string Join(bool isWindows, string left, string right)
        {
            var l = TrimTrailing(left ?? "");
            if (l.Length == 0) return right;
            // Windows 绝对路径用反斜杠，其余一律正斜杠 —— 与 isWindows 标记保持一致
            char sep = isWindows ? '\\' : '/';
            return l[l.Length - 1] == sep ? l + right : l + sep + right;
        }
    }
}
