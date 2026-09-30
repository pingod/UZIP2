using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UZIP2.Services;
using UZIP2.Shell;

namespace UZIP2
{
    public partial class App : Application
    {
        public static IServiceProvider Services { get; private set; }

        private InstanceBus _bus;

        private void OnStartup(object sender, StartupEventArgs e)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string configDir = Path.Combine(baseDir, "Config");

            _bus = new InstanceBus();
            if (!_bus.TryBecomePrimary())
            {
                if (InstanceBus.ForwardArgsToPrimary(e.Args))
                {
                    Shutdown();
                    return;
                }
                // 转发失败（主实例正在退出）：降级为独立实例继续启动
                _bus.Dispose();
                _bus = null;
            }

            var logger = new FileLogger(baseDir);
            var settings = new SettingsService(configDir, logger);

            // 一次性迁移旧版配置（UZip.config / PasswordNote / PasswordPage -> json）
            var passwords = new PasswordService(configDir, settings);
            var migration = LegacyConfigMigrator.TryMigrate(configDir, settings, passwords);
            if (migration.Performed)
            {
                logger.Info("配置迁移: " + migration.Reason);
                if (!migration.Success)
                    MessageBox.Show("旧配置迁移失败，将以默认配置启动。\n" + migration.Reason,
                        "UZIP 配置迁移", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (settings.Current.CleanTempOnStartup)
                TempManager.CleanupOnStartup(baseDir);

            var services = new ServiceCollection();
            services.AddSingleton<IFileLogger>(logger);
            services.AddSingleton<ISettingsService>(settings);
            services.AddSingleton(passwords);
            services.AddSingleton<CompressLogService>(sp => new CompressLogService(configDir));
            services.AddSingleton<SevenZipClient>();
            services.AddSingleton<ArchiveWorker>();
            services.AddSingleton<ClipboardService>();
            services.AddSingleton<HotKeyService>();
            services.AddSingleton<TrayService>();
            Services = services.BuildServiceProvider();

            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            if (_bus != null)
            {
                _bus.FilesReceived += args => Dispatcher.Invoke(() =>
                {
                    window.ShowFromTray();
                    var files = args.Where(File.Exists).ToArray();
                    if (files.Length > 0)
                        Services.GetRequiredService<ArchiveWorker>().EnqueueExtract(files);
                });
            }

            var startupFiles = e.Args.Where(File.Exists).ToArray();
            if (startupFiles.Length > 0)
                Services.GetRequiredService<ArchiveWorker>().EnqueueExtract(startupFiles);
        }

        private void OnExit(object sender, ExitEventArgs e)
        {
            _bus?.Dispose();
            (Services as IDisposable)?.Dispose();
        }
    }
}
