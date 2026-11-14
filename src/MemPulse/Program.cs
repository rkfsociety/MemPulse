using Avalonia;
using MemPulse.Core;

namespace MemPulse;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\MemPulse.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        StartupLog.Initialize();

        bool smokeTest = args.Any(a => string.Equals(a, "--startup-smoke-test", StringComparison.OrdinalIgnoreCase));
        App.IsSmokeTestMode = smokeTest;

        Mutex? instanceMutex = null;
        if (!smokeTest)
        {
            bool createdNew;
            instanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            if (!createdNew)
            {
                StartupLog.Info("Обнаружен уже запущенный экземпляр MemPulse. Выход.");
                instanceMutex.Dispose();
                return 0;
            }
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            StartupLog.Fatal("Необработанное исключение при запуске MemPulse", ex);
            return 1;
        }
        finally
        {
            instanceMutex?.ReleaseMutex();
            instanceMutex?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
