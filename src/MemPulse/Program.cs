using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
                // Validate themed scrollbars and dark selection: default Windows
                // templates caused white-on-white rows and bright legacy scrollbars.
                var cell = new DataGridCell
                {
                    Style = (Style)app.Resources[typeof(DataGridCell)],
                    Content = new TextBlock { Text = "Проверка выделения" },
                    IsSelected = true
                };
                cell.Measure(new Size(240, 40));
                cell.Arrange(new Rect(0, 0, 240, 40));
                if (cell.Background is not SolidColorBrush selectedBrush ||
                    selectedBrush.Color.R > 100 || selectedBrush.Color.G > 160)
                    throw new InvalidOperationException("Выделение строки не использует тёмную тему.");

                foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
                {
                    var scroll = new ScrollBar
                    {
                        Orientation = orientation,
                        Style = (Style)app.Resources[typeof(ScrollBar)]
                    };
                    scroll.Measure(new Size(120, 120));
                    scroll.Arrange(new Rect(0, 0, 120, 120));
                    if (scroll.Template.FindName("PART_Track", scroll) is not Track)
                        throw new InvalidOperationException("Скроллбар не использует оформленный трек.");
                }
                var primary = new Button
                {
                    Content = "Очистить", Style = (Style)app.Resources["PrimaryButton"]
                };
                primary.Measure(new Size(180, 48));
                primary.Arrange(new Rect(0, 0, 180, 48));
                if (primary.Foreground is not SolidColorBrush labelBrush ||
                    labelBrush.Color.R > 100 || labelBrush.Color.G > 120)
                    throw new InvalidOperationException("Акцентная кнопка не использует тёмный контрастный текст.");
                StartupLog.Info("Проверены контраст выделения, кнопок и оба типа скроллбаров.");
                window.VerifyProcessRefreshForSmokeTest();
                StartupLog.Info("Проверено многократное обновление и фильтрация списка процессов.");
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
