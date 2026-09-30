using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UZIP2.Services;
using UZIP2.Shell;
using UZIP2.ViewModel;

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

            var request = ShellArgs.Parse(e.Args);
            if (request.RegisterShell || request.UnregisterShell)
            {
                ApplyShellRegistration(request);
                Shutdown();
                return;
            }

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
            services.AddSingleton<CompressLogService>(sp => new CompressLogService(configDir, settings));
            services.AddSingleton<SevenZipClient>();
            services.AddSingleton<ArchiveWorker>();
            services.AddSingleton<ClipboardService>();
            services.AddSingleton<HotKeyService>();
            services.AddSingleton<WatchFolderService>();
            services.AddSingleton<TrayService>();
            services.AddSingleton<ShellMenuService>();
            services.AddSingleton<HomeViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<PasswordBookViewModel>(sp => new PasswordBookViewModel(
                sp.GetRequiredService<PasswordService>(),
                Current.Dispatcher,
                sp.GetRequiredService<ClipboardService>()));
            Services = services.BuildServiceProvider();

            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            if (_bus != null)
            {
                _bus.FilesReceived += args => Dispatcher.Invoke(() =>
                {
                    window.ShowFromTray();
                    Handle(ShellArgs.Parse(args));
                });
            }

            Handle(request);

            // 监听目录跟随设置即时生效
            var watcher = Services.GetRequiredService<WatchFolderService>();
            settings.Changed += _ => Dispatcher.Invoke(watcher.Apply);
            watcher.Apply();
        }

        // 右键菜单/命令行进来的路径按动词分流；解压只吃文件，压缩允许目录
        private static void Handle(ShellRequest request)
        {
            var paths = request.Files.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (paths.Length == 0) return;
            var worker = Services.GetRequiredService<ArchiveWorker>();
            switch (request.Verb)
            {
                case ShellVerb.Extract:
                    worker.EnqueueExtract(paths.Where(File.Exists).ToArray());
                    break;
                case ShellVerb.ExtractHere:
                    foreach (var f in paths.Where(File.Exists))
                        worker.EnqueueExtract(new[] { f }, Path.GetDirectoryName(f), null, true);
                    break;
                case ShellVerb.Compress:
                    worker.EnqueueCompress(paths);
                    break;
            }
        }

        private static void ApplyShellRegistration(ShellRequest request)
        {
            try
            {
                using (var svc = new ShellMenuService())
                {
                    if (request.UnregisterShell) svc.Unregister();
                    else svc.Register(ShellMenuService.CurrentExePath);
                }
                Environment.ExitCode = 0;
            }
            catch
            {
                Environment.ExitCode = 1;
            }
        }

        private void OnExit(object sender, ExitEventArgs e)
        {
            _bus?.Dispose();
            (Services as IDisposable)?.Dispose();
        }
    }
}
