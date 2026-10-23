using System;

namespace UZIP2.Services
{
    public enum ShellMenuState { Missing, Current, Stale }

    /// <summary>
    /// 文件管理器右键菜单集成。Windows 走 HKCU 注册表（ShellMenuService），
    /// Linux 走 Nautilus/Dolphin/Thunar 三套脚本（P4），不支持时用无操作实现。
    /// </summary>
    public interface IShellIntegration : IDisposable
    {
        ShellMenuState State(string exePath);
        void Register(string exePath);
        void Unregister();
        string ProbeCommand();
    }

    /// <summary>
    /// 宿主在启动时替换 Factory（WPF 的 App / Avalonia 的 Program），核心与 CLI 只认这个接缝。
    /// </summary>
    public static class ShellIntegration
    {
        public static Func<IShellIntegration> Factory { get; set; } = () => new UnsupportedShellIntegration();

        public static string CurrentExe => Environment.ProcessPath;

        public static IShellIntegration Create() => (Factory ?? (() => new UnsupportedShellIntegration()))();
    }

    /// <summary>平台没有右键菜单集成时的占位：状态永远 Unknown，注册/移除给出明确错误。</summary>
    public sealed class UnsupportedShellIntegration : IShellIntegration
    {
        public ShellMenuState State(string exePath) => ShellMenuState.Missing;
        public void Register(string exePath) => throw new PlatformNotSupportedException("当前平台不支持右键菜单集成");
        public void Unregister() => throw new PlatformNotSupportedException("当前平台不支持右键菜单集成");
        public string ProbeCommand() => null;
        public void Dispose() { }
    }
}
