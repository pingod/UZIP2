using System;

namespace UZIP2.Services
{
    /// <summary>
    /// GlobalShortcuts portal 的文本解析。gdbus 的输出格式比 D-Bus 线协议稳定，
    /// 而且不需要在应用里内置一整套 D-Bus 客户端。
    /// </summary>
    public static class PortalOutput
    {
        public const string SessionPrefix = "/org/freedesktop/portal/desktop/session/";

        /// <summary>从 <c>gdbus call CreateSession</c> 的返回里抠出 session 对象路径。</summary>
        public static string ParseSessionPath(string output)
        {
            if (string.IsNullOrEmpty(output)) return null;
            int at = output.IndexOf(SessionPrefix, StringComparison.Ordinal);
            if (at < 0) return null;
            int end = output.IndexOf('\'', at);
            if (end < 0) end = output.IndexOf(')', at);
            if (end < 0) end = output.Length;
            var path = output.Substring(at, end - at).Trim();
            return path.Length > SessionPrefix.Length ? path : null;
        }

        /// <summary>监测行是否是"我们的快捷键被按下"。</summary>
        public static bool IsActivated(string line, string shortcutId)
        {
            if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(shortcutId)) return false;
            if (line.IndexOf("org.freedesktop.portal.GlobalShortcuts.Activated", StringComparison.Ordinal) < 0)
                return false;
            return line.IndexOf("'" + shortcutId + "'", StringComparison.Ordinal) >= 0;
        }
    }
}
