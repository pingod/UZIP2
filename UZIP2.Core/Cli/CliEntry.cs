using System;
using System.IO;
using UZIP2.Cli;
using UZIP2.Services;

namespace UZIP2.Cli
{
    /// <summary>
    /// 无界面入口的共用实现：两个宿主（UZIP2.Cli 与 Avalonia 版的 CLI 模式）都用它，
    /// 保证配置目录、密钥实现、7z 行为完全一致。
    /// </summary>
    public static class CliEntry
    {
        public static int Run(string[] args, Func<string, IPasswordCipher> cipherFactory = null)
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
                PasswordCipher.Current = cipherFactory != null
                    ? cipherFactory(layout.ConfigDirectory)
                    : new FileSecretCipher(Path.Combine(layout.ConfigDirectory, "secret.key"));
                ShellIntegration.Factory = () => new UnsupportedShellIntegration();

                var req = CliParser.Parse(args);
                var runner = new CliRunner(layout.LogBaseDirectory, layout.ConfigDirectory,
                    s => Console.Out.WriteLine(s), s => Console.Error.WriteLine(s));
                return System.Threading.Tasks.Task.Run(() => runner.RunAsync(req)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("UZIP: " + ex.Message);
                return 1;
            }
        }
    }
}
