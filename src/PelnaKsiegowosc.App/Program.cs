using Avalonia;
using Avalonia.Headless;

namespace PelnaKsiegowosc.App;

public static class Program
{
    /// <summary>Tryb kontroli uruchomienia (bez okna): tworzy księgi w katalogu tymczasowym i buduje wszystkie ekrany. Kod wyjścia 0 = sukces.</summary>
    public const string SelfTestArgument = "--test-uruchomienia";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains(SelfTestArgument))
        {
            var screenshots = Environment.GetEnvironmentVariable("PK_SELFTEST_SCREENSHOTS");
            BuildAvaloniaApp().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = string.IsNullOrEmpty(screenshots) }).SetupWithoutStarting();
            return SelfTest.Run(Console.Out);
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
