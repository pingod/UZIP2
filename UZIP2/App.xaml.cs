using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UZIP2.Cli;
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

            // CLI 模式: 只在显式命令词/help/version 时触发，headless 跑完即退，
            // 绝不建窗口也不碰单实例总线。右键菜单的 --extract/--compress/--register-shell
            // 不算 CLI，保持走下面的 GUI/IPC 路径。
            if (CliParser.IsCli(e.Args))
            {
                RunCli(e.Args, baseDir, configDir);
                return;
            }

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

        // ---------- CLI 头less 入口 ----------

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int dwFlags);
        const int ATTACH_PARENT_PROCESS = 0x02;

        void RunCli(string[] args, string baseDir, string configDir)
        {
            TryAttachConsole();
            var req = CliParser.Parse(args);
            var runner = new CliRunner(baseDir, configDir,
                s => Console.Out.WriteLine(s), s => Console.Error.WriteLine(s));
            int code;
            try
            {
                // 必须在无 SynchronizationContext 的线程池线程上跑：OnStartup 期间 WPF UI 线程
                // 的 DispatcherSynchronizationContext 已就位，但 dispatcher 还没开始泵消息，
                // 直接在 UI 线程 GetResult() 会让内部 await 的续体永远等不到调度 → 死锁。
                code = System.Threading.Tasks.Task.Run(() => runner.RunAsync(req)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("错误: " + ex.Message);
                code = 1;
            }
            Console.Out.Flush();
            Console.Error.Flush();
            // 无窗口 + OnExplicitShutdown 下，OnStartup 里的 Shutdown() 不会终止
            // dispatcher 循环(它还没开始跑)，进程会挂住。CLI 是纯 headless，直接终止进程。
            Environment.Exit(code);
        }

        static void TryAttachConsole()
        {
            try
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch { /* 无控制台(双击启动)时忽略，退出码仍有效 */ }
        }
    }
}
