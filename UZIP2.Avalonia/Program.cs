using System;
using Avalonia;
using Avalonia.Wayland;
using UZIP2.Cli;
using UZIP2.Services;

namespace UZIP2.Avalonia
{
    internal static class Program
    {
        /// <summary>主实例的单实例总线；第二进程在这里转发后直接退出。</summary>
        internal static InstanceBus Bus { get; private set; }

        /// <summary>启动时带的文件参数（图形模式），交给 App 在服务就绪后入队。</summary>
        internal static string[] StartupFiles { get; private set; } = Array.Empty<string>();

        [STAThread]
        public static int Main(string[] args)
        {
            // 与 Windows 版一致：显式命令词走 headless CLI，其余进图形界面
            if (CliParser.IsCli(args))
                return UZIP2.Cli.CliEntry.Run(args);

            // 单实例：文件管理器里双击多个文件会拉起多个进程，第二个只负责转发。
            Bus = new InstanceBus();
            if (!Bus.TryBecomePrimary())
            {
                InstanceBus.ForwardArgsToPrimary(args);
                Bus.Dispose();
                Bus = null;
                return 0;
            }

            StartupFiles = args ?? Array.Empty<string>();
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                Bus?.Dispose();
                Bus = null;
            }
            return 0;
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            var builder = AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();

            // Wayland 优先：Avalonia 12 的 UsePlatformDetect 不认识 Wayland（只探测 Win32/X11/macOS），
            // 不叠这一层，Linux 上就永远退回 XWayland。UseWaylandWithFallback 要求前面已经配好兜底后端，
            // 所以必须排在 UsePlatformDetect 之后；它只在 Linux 上有意义。
            if (OperatingSystem.IsLinux())
                builder = builder.UseWaylandWithFallback();

            return builder;
        }
    }
}
