using System.Runtime.CompilerServices;
using UZIP2.Services;

namespace UZIP2.Tests
{
    /// <summary>
    /// 宿主（WPF App / Avalonia）会在启动时装上平台接缝；单测进程没有 App，
    /// 这里补上同一套 Windows 实现，保证被测行为与线上一致（尤其是 DPAPI）。
    /// </summary>
    internal static class TestBootstrap
    {
        [ModuleInitializer]
        internal static void Init()
        {
            PasswordCipher.Current = new DpapiCipher();
            ShellIntegration.Factory = () => new ShellMenuService();
        }
    }
}
