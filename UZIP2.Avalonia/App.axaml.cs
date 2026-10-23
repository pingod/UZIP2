using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UZIP2.Avalonia.Shell;

namespace UZIP2.Avalonia
{
    public sealed class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppHost.Initialize();
                desktop.MainWindow = new MainWindow();
                desktop.ShutdownRequested += (_, __) => AppHost.Shutdown();
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
