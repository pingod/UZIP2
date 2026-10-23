using System;
using System.IO;
using System.Threading.Tasks;
using UZIP2.Cli;
using UZIP2.Services;

namespace UZIP2.CliHost
{
    /// <summary>
    /// Linux 侧入口：不建窗口、不碰单实例总线，解析参数后交给跨平台 CliRunner。
    /// 配置与日志按 XDG 规范落盘（程序目录通常是只读的 /usr/lib/uzip2）。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var layout = PlatformPaths.Resolve(
                    OperatingSystem.IsWindows(),
                    AppContext.BaseDirectory.TrimEnd('/', '\\'),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
                    Environment.GetEnvironmentVariable("XDG_STATE_HOME"));

                Directory.CreateDirectory(layout.ConfigDirectory);
                PasswordCipher.Current = new FileSecretCipher(Path.Combine(layout.ConfigDirectory, "secret.key"));
                // P4: Linux 的文件管理器右键菜单（Nautilus/Dolphin/Thunar 脚本）在这里接管
                ShellIntegration.Factory = () => new UnsupportedShellIntegration();

                var req = CliParser.Parse(args);
                var runner = new CliRunner(layout.LogBaseDirectory, layout.ConfigDirectory,
                    s => Console.Out.WriteLine(s), s => Console.Error.WriteLine(s));
                return Task.Run(() => runner.RunAsync(req)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("UZIP: " + ex.Message);
                return 1;
            }
        }
    }
}
