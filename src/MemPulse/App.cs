using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using MemPulse.Core;

namespace MemPulse;

public sealed class App : Application
{
    public static bool IsSmokeTestMode { get; set; }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            if (IsSmokeTestMode)
            {
                _ = RunSmokeTestAndExitAsync(mainWindow, desktop);
            }
            else
            {
                _ = mainWindow.InitializeHostAsync();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunSmokeTestAndExitAsync(
        MainWindow window,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            bool ok = await window.RunSmokeTestAsync();
            if (!ok)
            {
                StartupLog.Warn("Smoke test завершился с пустой сводкой модуля.");
                Environment.ExitCode = 1;
            }
            else
            {
                StartupLog.Info("Startup smoke test passed.");
                Console.WriteLine("[MemPulse] Startup smoke test passed.");
                Environment.ExitCode = 0;
            }
        }
        catch (Exception ex)
        {
            StartupLog.Fatal("Startup smoke test failed", ex);
            Console.Error.WriteLine($"[MemPulse] Startup smoke test failed: {ex}");
            Environment.ExitCode = 1;
        }
        finally
        {
            desktop.Shutdown(Environment.ExitCode);
        }
    }
}
