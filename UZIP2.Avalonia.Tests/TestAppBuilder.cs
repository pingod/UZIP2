using Avalonia;
using Avalonia.Headless;
using UZIP2.Avalonia;

[assembly: AvaloniaTestApplication(typeof(UZIP2.Avalonia.Tests.TestAppBuilder))]

namespace UZIP2.Avalonia.Tests
{
    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
