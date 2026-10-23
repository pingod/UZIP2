using System;
using Avalonia;
using UZIP2.Cli;

namespace UZIP2.Avalonia
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // 与 Windows 版一致：显式命令词走 headless CLI，其余进图形界面
            if (CliParser.IsCli(args))
                return UZIP2.Cli.CliEntry.Run(args);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
