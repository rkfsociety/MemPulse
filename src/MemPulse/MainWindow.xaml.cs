using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MemPulse.Core;
using Forms = System.Windows.Forms;

using Window = System.Windows.Window;
using MessageBox = System.Windows.MessageBox;

namespace MemPulse;

public partial class MainWindow : Window
{
    private readonly MonitoringService _monitor = new();
    private readonly CleanupService _cleaner = new();
    private readonly SettingsStore _store = new();
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private AvailableUpdate? _availableUpdate;
    private bool _checkingUpdates;
    private bool _installingUpdate;
    private readonly ObservableCollection<ProcessUsage> _processes = [];
    private readonly DispatcherTimer _timer = new();
    private AppSettings _settings = new();
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private int? _lastTrayPercent;
    private readonly List<double> _memoryHistory = [];
    private bool _refreshing;
    private bool _cleaning;
    private bool _forceExit;
    private DateTime _lastAutoCleanUtc = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        ProcessGrid.ItemsSource = _processes;
        var view = CollectionViewSource.GetDefaultView(_processes);
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessUsage.WorkingBytes),
            ListSortDirection.Descending));
        view.Filter = FilterProcess;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(false);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartupLog.Info("Главное окно: событие Loaded");
        _settings = _store.Load();
        StartupLog.Info("Главное окно: настройки загружены");
        try { _settings.StartWithWindows = StartupService.IsEnabled(); }
        catch (Exception ex) { SetStatus("Не удалось прочитать автозапуск: " + ex.Message); }

        VersionText.Text = "MemPulse  ·  v" + UpdateService.CurrentVersion;
        UpdateAutomationSummary();
        ElevateButton.Visibility = CleanupService.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        CleanButton.IsEnabled = CleanupService.IsElevated;
        try
        {
            InitializeTray();
            StartupLog.Info("Главное окно: трей инициализирован");
        }
        catch (Exception ex)
        {
            // Notification area support is optional; never prevent the main window from appearing.
            StartupLog.Error("Не удалось создать значок трея", ex);
            SetStatus("Не удалось создать значок трея: " + ex.Message);
        }
        ConfigureTimer();
        StartupLog.Info("Главное окно: таймер запущен");
        if (CleanupService.IsElevated) SetStatus("Режим администратора · очистка доступна");
        else SetStatus("Мониторинг включён · для системной очистки нужны права администратора");
        await RefreshAsync();
        StartupLog.Info("Главное окно: первый опрос завершён");
        ConfigureUpdateTimer();
        if (_settings.CheckForUpdates) await CheckUpdatesAsync(false);
        StartupLog.Info("Главное окно: завершено событие Loaded");
    }

    private void ConfigureUpdateTimer()
    {
        _updateTimer.Stop();
        if (_settings.CheckForUpdates) _updateTimer.Start();
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_checkingUpdates || _installingUpdate) return;
        _checkingUpdates = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var update = await _updates.CheckAsync();
            _availableUpdate = update;
            if (update is not null)
            {
                UpdateButton.Content = "Установить v" + update.Version;
                UpdateButton.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
                UpdateButton.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(16, 33, 40));
                SetStatus("Доступна новая версия MemPulse: " + update.Version);
            }
            else
            {
                UpdateButton.Content = "Обновления";
                UpdateButton.Background = (System.Windows.Media.Brush)FindResource("RaisedBrush");
                UpdateButton.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
                if (manual) SetStatus("Установлена актуальная версия MemPulse (v" + UpdateService.CurrentVersion + ").");
            }
        }
        catch (Exception ex)
        {
            if (manual) SetStatus("Не удалось проверить обновления: " + ex.Message);
        }
        finally
        {
            _checkingUpdates = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_installingUpdate || _checkingUpdates) return;
        if (_availableUpdate is null)
        {
            await CheckUpdatesAsync(true);
            return;
        }

        var update = _availableUpdate;
        if (MessageBox.Show(this,
                "Скачать и установить MemPulse v" + update.Version +
                "? Программа закроется и запустит мастер установки.",
                "Обновление MemPulse", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _installingUpdate = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(percent =>
                SetStatus("Загрузка обновления (с проверкой): " + percent.ToString("0") + "%"));
            string setup = await _updates.DownloadAsync(update, progress);
            var process = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true });
            if (process is null) throw new InvalidOperationException("Не удалось запустить установщик.");

            _forceExit = true;
            Close(); // Exit even if close-to-tray is enabled so installer can replace the executable.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка установки обновления",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _installingUpdate = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private void ConfigureTimer()
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds);
        _timer.Start();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _cleaning) return;
        _refreshing = true;
        try
        {
            var result = await Task.Run(() => (_monitor.ReadMemory(), _monitor.ReadProcesses()));
            var memory = result.Item1;
            var processes = result.Item2;

            UsedText.Text = Format.Bytes(memory.Used);
            PercentText.Text = memory.UsedPercent.ToString("0") + "%";
            TotalText.Text = Format.Bytes(memory.Total) + " всего";
            UpdateMemoryVisual(memory.UsedPercent);
            UpdateTray(memory);
            AvailableText.Text = Format.Bytes(memory.Available);
            CacheText.Text = Format.Bytes(memory.Cache);
            CommitText.Text = Format.Bytes(memory.Committed);
            LimitText.Text = Format.Bytes(memory.CommitLimit);

            int? selectedPid = (ProcessGrid.SelectedItem as ProcessUsage)?.Pid;
            _processes.Clear();
            foreach (var row in processes) _processes.Add(row);
            if (selectedPid.HasValue)
                ProcessGrid.SelectedItem = _processes.FirstOrDefault(row => row.Pid == selectedPid.Value);
            ProcessCountText.Text = RussianText.Processes(processes.Count) + " · память процессов";
            UpdateText.Text = "Обновлено " + DateTime.Now.ToString("HH:mm:ss");

            if (_settings.AutoClean && CleanupService.IsElevated &&
                memory.UsedPercent >= _settings.AutoCleanThreshold &&
                DateTime.UtcNow - _lastAutoCleanUtc >=
                    TimeSpan.FromMinutes(_settings.AutoCleanCooldownMinutes))
            {
                // The automatic mode deliberately excludes more disruptive cleanup operations.
                _lastAutoCleanUtc = DateTime.UtcNow;
                await RunCleanupAsync(new CleanupOptions(true, false, false), true);
            }
        }
        catch (Exception ex)
        {
            StartupLog.Error("Ошибка обновления данных", ex);
            SetStatus("Ошибка обновления данных: " + ex.Message);
        }
        finally { _refreshing = false; }
    }

    private bool FilterProcess(object item)
    {
        if (item is not ProcessUsage process) return false;
        string search = SearchBox?.Text?.Trim() ?? "";
        return search.Length == 0 ||
               process.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               process.Pid.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
            SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        if (ProcessGrid?.ItemsSource is not null)
            CollectionViewSource.GetDefaultView(ProcessGrid.ItemsSource).Refresh();
    }

    private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrimButton is not null)
            TrimButton.IsEnabled = !_cleaning && ProcessGrid.SelectedItem is ProcessUsage;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var options = new CleanupOptions(
            StandbyCheck.IsChecked == true,
            WorkingCheck.IsChecked == true,
            ModifiedCheck.IsChecked == true);
        if (!options.Standby && !options.WorkingSets && !options.ModifiedPages)
        {
            SetStatus("Выберите хотя бы одну операцию очистки.");
            return;
        }
        if ((options.WorkingSets || options.ModifiedPages) &&
            MessageBox.Show(this,
                "Очистка рабочих наборов и изменённых страниц может временно замедлить работу Windows и программ. Продолжить?",
                "Подтверждение очистки", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunCleanupAsync(options, false);
        await RefreshAsync();
    }

    private async Task RunCleanupAsync(CleanupOptions options, bool automatic)
    {
        if (_cleaning) return;
        _cleaning = true;
        CleanButton.IsEnabled = false;
        TrimButton.IsEnabled = false;
        try
        {
            var report = await Task.Run(() => _cleaner.Clean(options));
            SetStatus((automatic ? "Автоочистка: " : "Очистка: ") + report.Summary);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка очистки: " + ex.Message);
        }
        finally
        {
            _cleaning = false;
            CleanButton.IsEnabled = CleanupService.IsElevated;
            TrimButton.IsEnabled = ProcessGrid.SelectedItem is ProcessUsage;
        }
    }

    private async void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (_cleaning || ProcessGrid.SelectedItem is not ProcessUsage selected) return;
        if (MessageBox.Show(this,
                "Уменьшить рабочий набор процесса " + selected.Name + " (PID " + selected.Pid +
                ")? Данные могут быть повторно загружены в ОЗУ, а процесс — временно замедлиться.",
                "Очистка памяти процесса", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _cleaning = true;
        CleanButton.IsEnabled = false;
        TrimButton.IsEnabled = false;
        try
        {
            var report = await Task.Run(() => _cleaner.TrimProcess(selected.Pid));
            SetStatus(report.Summary);
        }
        catch (Exception ex) { SetStatus("Ошибка очистки процесса: " + ex.Message); }
        finally
        {
            _cleaning = false;
            CleanButton.IsEnabled = CleanupService.IsElevated;
            TrimButton.IsEnabled = ProcessGrid.SelectedItem is ProcessUsage;
        }
        await RefreshAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var next = dialog.Result;
            StartupService.SetEnabled(next.StartWithWindows);
            _store.Save(next);
            _settings = next;
            UpdateAutomationSummary();
            ConfigureTimer();
            ConfigureUpdateTimer();
            if (next.CheckForUpdates) _ = CheckUpdatesAsync(false);
            SetStatus("Настройки сохранены.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось сохранить настройки",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath ??
                throw new InvalidOperationException("Не удалось найти исполняемый файл программы.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            if (string.Equals(System.IO.Path.GetFileName(executable), "dotnet.exe",
                StringComparison.OrdinalIgnoreCase))
            {
                var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(dll))
                    throw new InvalidOperationException("Для запуска от администратора сначала опубликуйте приложение.");
                start.Arguments = "\"" + dll + "\"";
            }
            if (Process.Start(start) is not null)
            {
                _forceExit = true;
                Close();
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus("Запрос прав администратора отменён.");
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось перезапустить от администратора: " + ex.Message);
        }
    }

    private void InitializeTray()
    {
        // Loading the WPF pack URI / ICO decoder must not be a single point of failure.
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/MemPulse.ico"))
                ?? throw new InvalidOperationException("Не найдена встроенная иконка MemPulse.");
            using (resource.Stream)
            using (var brandIcon = new System.Drawing.Icon(resource.Stream, new System.Drawing.Size(32, 32)))
                _trayIcon = (System.Drawing.Icon)brandIcon.Clone();
        }
        catch (Exception ex)
        {
            StartupLog.Error("Загрузка иконки трея не удалась. Используется системная иконка.", ex);
            _trayIcon = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "MemPulse — ожидание данных ОЗУ",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть MemPulse", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray)));
        menu.Items.Add("Очистить кэш ожидания", null, (_, _) => Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                ShowFromTray();
                await RunCleanupAsync(new CleanupOptions(true, false, false), false);
                await RefreshAsync();
            })));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _forceExit = true;
            Close();
        })));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
    }

    private void UpdateTray(MemoryUsage memory)
    {
        if (_tray is null) return;

        int percent = Math.Clamp((int)Math.Round(memory.UsedPercent,
            MidpointRounding.AwayFromZero), 0, 100);
        _tray.Text = $"MemPulse | ОЗУ {percent}% ({Format.Bytes(memory.Used)} / {Format.Bytes(memory.Total)})";

        if (_lastTrayPercent == percent) return;

        System.Drawing.Icon nextIcon;
        try { nextIcon = TrayIconRenderer.Create(percent); }
        catch (Exception ex)
        {
            StartupLog.Error("Не удалось нарисовать процент ОЗУ в трее", ex);
            return;
        }
        var previousIcon = _trayIcon;
        try
        {
            _tray.Icon = nextIcon;
            _trayIcon = nextIcon;
            _lastTrayPercent = percent;
            previousIcon?.Dispose();
        }
        catch (Exception ex)
        {
            nextIcon.Dispose();
            StartupLog.Error("Не удалось обновить значок трея", ex);
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray && _tray is not null)
            Hide();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        StartupLog.Info("Главное окно: закрытие (выход=" + _forceExit + ")");
        if (!_forceExit && _settings.CloseToTray && _tray is not null)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _timer.Stop();
        _updateTimer.Stop();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _tray = null;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
    }


    private void UpdateAutomationSummary()
    {
        AutoCleanStateText.Text = _settings.AutoClean ? "Включена" : "Отключена";
        AutoCleanStateText.Foreground = (System.Windows.Media.Brush)FindResource(
            _settings.AutoClean ? "AccentBrush" : "MutedBrush");
        AutoCleanThresholdText.Text =
            $"Порог: {_settings.AutoCleanThreshold}% · пауза: {_settings.AutoCleanCooldownMinutes} мин.";
    }

    private void UpdateMemoryVisual(double percent)
    {
        // Ring is drawn from actual GlobalMemoryStatusEx measurements, never mock values.
        double value = Math.Clamp(percent, 0, 100);
        FullUsageRing.Visibility = value >= 99.95 ? Visibility.Visible : Visibility.Collapsed;
        UsageArc.Visibility = value > 0 && value < 99.95 ? Visibility.Visible : Visibility.Collapsed;
        var color = value >= 90 ? System.Windows.Media.Color.FromRgb(255, 112, 120)
            : value >= 80 ? System.Windows.Media.Color.FromRgb(255, 199, 104)
            : System.Windows.Media.Color.FromRgb(46, 226, 193);
        UsageArc.Stroke = new System.Windows.Media.SolidColorBrush(color);
        if (value > 0 && value < 99.95)
        {
            const double radius = 74;
            const double cx = 83;
            const double cy = 83;
            double angle = (value / 100.0) * (2.0 * Math.PI) - Math.PI / 2;
            var end = new System.Windows.Point(cx + radius * Math.Cos(angle),
                                               cy + radius * Math.Sin(angle));
            var figure = new System.Windows.Media.PathFigure
            {
                StartPoint = new System.Windows.Point(cx, cy - radius),
                IsClosed = false,
                IsFilled = false
            };
            figure.Segments.Add(new System.Windows.Media.ArcSegment(
                end, new System.Windows.Size(radius, radius), 0, value > 50,
                System.Windows.Media.SweepDirection.Clockwise, true));
            var geometry = new System.Windows.Media.PathGeometry();
            geometry.Figures.Add(figure);
            UsageArc.Data = geometry;
        }

        // Keep a bounded rolling history; no background sampling or extra polling.
        _memoryHistory.Add(value);
        if (_memoryHistory.Count > 90) _memoryHistory.RemoveAt(0);
        RedrawHistory();
    }

    private void HistoryCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawHistory();

    private void RedrawHistory()
    {
        double width = HistoryCanvas.ActualWidth;
        double height = HistoryCanvas.ActualHeight;
        if (width <= 1 || height <= 1 || _memoryHistory.Count == 0) return;

        var points = new System.Windows.Media.PointCollection();
        if (_memoryHistory.Count == 1)
        {
            double y = height * (1 - _memoryHistory[0] / 100.0);
            points.Add(new System.Windows.Point(0, y));
            points.Add(new System.Windows.Point(width, y));
        }
        else
        {
            for (int i = 0; i < _memoryHistory.Count; i++)
            {
                double x = i * width / (_memoryHistory.Count - 1);
                double y = height * (1 - _memoryHistory[i] / 100.0);
                points.Add(new System.Windows.Point(x, Math.Clamp(y, 0, height)));
            }
        }
        HistoryLine.Points = points;
        var filled = new System.Windows.Media.PointCollection
        {
            new System.Windows.Point(0, height)
        };
        foreach (var point in points) filled.Add(point);
        filled.Add(new System.Windows.Point(width, height));
        HistoryFill.Points = filled;
    }

    private void Overview_Click(object sender, RoutedEventArgs e)
    {
        OverviewCard.BringIntoView();
        SetStatus("Обзор текущего использования памяти.");
    }

    private void Processes_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SetStatus("Введите имя процесса или PID для поиска.");
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "MemPulse v" + UpdateService.CurrentVersion +
            "\\nМониторинг памяти и управление её очисткой в Windows." +
            "\\n\\nПотребление ОЗУ измеряется средствами Windows. " +
            "Очистка кэша не гарантирует ускорения системы.",
            "О программе MemPulse", MessageBoxButton.OK, MessageBoxImage.Information);

    private void SetStatus(string text) => StatusText.Text = text;
}
