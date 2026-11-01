using System.Windows;
using MemPulse.Core;

namespace MemPulse;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        StartupLog.Info("App.OnStartup: инициализация основного окна");
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        StartupLog.Info("App.OnStartup: главное окно создано, Show()");
        window.Show();
        StartupLog.Info("App.OnStartup: главное окно показано");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StartupLog.Info("App.OnExit: код=" + e.ApplicationExitCode);
        base.OnExit(e);
    }
}
