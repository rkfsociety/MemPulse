using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MemPulse.Core;

namespace MemPulse;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) StartupLog.Fatal("AppDomain.UnhandledException", ex);
            else StartupLog.Warn("AppDomain.UnhandledException: non-Exception object");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            StartupLog.Error("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            StartupLog.Info("Создание экземпляра WPF Application");
            var app = new App();
            app.DispatcherUnhandledException += OnDispatcherUnhandledException;
            StartupLog.Info("Загрузка ресурсов App.xaml");
            app.InitializeComponent();
            StartupLog.Info("Ресурсы App.xaml загружены");

            if (args.Contains("--startup-smoke-test", StringComparer.OrdinalIgnoreCase))
            {
                StartupLog.Info("Начало проверки создания главного окна");
                var window = new MainWindow();
                // Realize the DataGrid templates even without showing the window, so the
                // packaged startup test catches broken bindings/resources from UI redesigns.
                int templateCount = 0;
                foreach (var column in window.ProcessGrid.Columns.OfType<DataGridTemplateColumn>())
                {
                    if (column.CellTemplate?.LoadContent() is null)
                        throw new InvalidOperationException("Не удалось загрузить шаблон колонки таблицы.");
                    templateCount++;
                }
                if (templateCount < 2)
                    throw new InvalidOperationException("Не найдены шаблоны иконок и показателей памяти.");
                StartupLog.Info("Проверены " + templateCount + " шаблона таблицы процессов.");
                StartupLog.Info("Проверка создания главного окна пройдена");
                window.Close();
                app.Shutdown();
                return 0;
            }

            StartupLog.Info("Запуск цикла сообщений WPF");
            int result = app.Run();
            StartupLog.Info("Цикл WPF завершён: " + result);
            return result;
        }
        catch (Exception ex)
        {
            StartupLog.Fatal("Не удалось запустить MemPulse", ex);
            try
            {
                System.Windows.MessageBox.Show("Ошибка запуска MemPulse.\n\nПодробности записаны в:\n" +
                    StartupLog.FilePath + "\n\n" + ex.Message,
                    "MemPulse — ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception dialogError)
            {
                StartupLog.Error("Не удалось показать сообщение о сбое", dialogError);
            }
            return 1;
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        // Do not silently mask unknown WPF errors. Log and let the process terminate.
        StartupLog.Fatal("WPF.DispatcherUnhandledException", args.Exception);
    }
}
