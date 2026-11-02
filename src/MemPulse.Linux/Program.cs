using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using System.Globalization;

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
        if (args.Length == 3 && args[0] == "--trim-process")
        {
            if (int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                ulong.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                return LinuxCleanupService.TrimProcessElevated(pid, ticks);
            Console.Error.WriteLine("Некорректные аргументы --trim-process.");
            return 1;
        }
        if (args.Length == 6 && args[0] == "--auto-clean")
            return LinuxCleanupService.RunAutoElevatedAsync(args[1..]).GetAwaiter().GetResult();
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
        {
            desktop.MainWindow = new MainWindow();
            if (Environment.GetCommandLineArgs().Contains("--startup-smoke-test", StringComparer.Ordinal))
                Dispatcher.UIThread.Post(() => desktop.Shutdown(0), DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
