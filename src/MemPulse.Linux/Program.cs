using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MemPulse.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("Версия MemPulse.Linux предназначена только для Linux.");
            return 1;
        }
        if (args.Length == 1 && args[0] == "--clean-cache")
            return LinuxCleanupService.CleanCacheElevated();

        return AppBuilder.Configure<LinuxApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class LinuxApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
