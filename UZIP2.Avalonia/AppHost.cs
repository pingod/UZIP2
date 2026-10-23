using System;
using System.IO;
using UZIP2.Avalonia.Services;
using UZIP2.Avalonia.Shell;
using UZIP2.Services;

namespace UZIP2.Avalonia
{
    /// <summary>
    /// 手工装配服务（不引入 DI 容器：组装点只有这一处，显式比注册表好读）。
    /// 平台差异集中在这里：密码加密、配置目录、界面线程。
    /// </summary>
    public static class AppHost
    {
        public static PlatformLayout Layout { get; private set; }
        public static IFileLogger Logger { get; private set; }
        public static SettingsService Settings { get; private set; }
        public static PasswordService Passwords { get; private set; }
        public static SevenZipClient Zip { get; private set; }
        public static ArchiveWorker Worker { get; private set; }
        public static IHistoryService History { get; private set; }
        public static WatchFolderService Watcher { get; private set; }

        public static void Initialize(PlatformLayout? layoutOverride = null)
        {
            Layout = layoutOverride ?? PlatformPaths.Resolve(
                OperatingSystem.IsWindows(),
                AppContext.BaseDirectory.TrimEnd('/', '\\'),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
                Environment.GetEnvironmentVariable("XDG_STATE_HOME"));
            Directory.CreateDirectory(Layout.ConfigDirectory);

            PasswordCipher.Current = OperatingSystem.IsWindows()
                ? new WindowsDpapiCipher()
                : new FileSecretCipher(Path.Combine(Layout.ConfigDirectory, "secret.key"));
            // P4: Linux 右键菜单脚本在这里接管
            ShellIntegration.Factory = () => new UnsupportedShellIntegration();

            Logger = new FileLogger(Layout.LogBaseDirectory);
            Settings = new SettingsService(Layout.ConfigDirectory, Logger);
            Passwords = new PasswordService(Layout.ConfigDirectory, Settings);
            Zip = new SevenZipClient(Settings, Logger);
            var compressLog = new CompressLogService(Layout.ConfigDirectory, Settings);
            History = new HistoryService(Layout.ConfigDirectory, Settings);
            Worker = new ArchiveWorker(Zip, Passwords, Settings, Logger, compressLog, History,
                new AvaloniaUiDispatcher());
            Watcher = new WatchFolderService(Settings, Worker);

            if (Settings.Current.CleanTempOnStartup)
                TempManager.CleanupOnStartup(AppContext.BaseDirectory, Layout.ConfigDirectory);
            Watcher.Apply();
        }

        public static void Shutdown()
        {
            try { History?.Flush(); } catch { }
            try { Watcher?.Apply(); } catch { }
        }
    }
}
