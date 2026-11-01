using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MemPulse.Core;
using Forms = System.Windows.Forms;

using Button = System.Windows.Controls.Button;
using Window = System.Windows.Window;
using MessageBox = System.Windows.MessageBox;

namespace MemPulse;

public partial class MainWindow : Window
{
    private readonly MonitoringService _monitor = new();
    private readonly ProcessIconCache _processIcons = new();
    private readonly CleanupService _cleaner = new();
    private readonly SettingsStore _store = new();
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private AvailableUpdate? _availableUpdate;
    private TaskCompletionSource<bool>? _confirmation;
    private bool _checkingUpdates;
    private bool _installingUpdate;
    private readonly ProcessSnapshotCollection<ProcessGroupUsage> _groups = [];
    private readonly ListCollectionView _overviewView;
    private readonly ListCollectionView _detailView;
    private enum Section { Overview, Processes, Settings, About }
    private Section _activeSection;
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
        _overviewView = new ListCollectionView(_groups);
        _detailView = new ListCollectionView(_groups);
        _overviewView.SortDescriptions.Add(new SortDescription(nameof(ProcessGroupUsage.WorkingBytes),
            ListSortDirection.Descending));
        _detailView.SortDescriptions.Add(new SortDescription(nameof(ProcessGroupUsage.WorkingBytes),
            ListSortDirection.Descending));
        _overviewView.Filter = FilterProcess;
        _detailView.Filter = FilterGroup;
        ProcessGrid.ItemsSource = _overviewView;
        GroupGrid.ItemsSource = _detailView;
        ActivateSection(Section.Overview);
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
        AboutVersionText.Text = "Версия " + UpdateService.CurrentVersion + " · Windows x64";
        LoadSettingsControls();
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
        if (!await ConfirmAsync("Обновление MemPulse",
                "Скачать и установить MemPulse v" + update.Version +
                "? Программа закроется и запустит мастер установки.", "Установить"))
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
            StartupLog.Error("Ошибка установки обновления", ex);
            SetStatus("Ошибка установки обновления: " + ex.Message);
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
            var result = await Task.Run(() =>
            {
                var usage = _monitor.ReadMemory();
                var snapshot = _monitor.ReadProcesses();
                var decorated = _processIcons.Decorate(snapshot);
                return (usage, ProcessGrouping.Build(decorated), snapshot.Count);
            });
            var memory = result.Item1;
            var groups = result.Item2;
            var totalProcesses = result.Item3;

            UsedText.Text = Format.Bytes(memory.Used);
            PercentText.Text = memory.UsedPercent.ToString("0") + "%";
            TotalText.Text = Format.Bytes(memory.Total) + " всего";
            UpdateMemoryVisual(memory.UsedPercent);
            SideUsageText.Text = memory.UsedPercent.ToString("0") + "%";
            SideUsageBar.Value = memory.UsedPercent;
            CommitBar.Value = memory.CommitLimit == 0 ? 0 :
                Math.Clamp((double)memory.Committed / memory.CommitLimit * 100, 0, 100);
            UpdateTray(memory);
            AvailableText.Text = Format.Bytes(memory.Available);
            CacheText.Text = Format.Bytes(memory.Cache);
            CommitText.Text = Format.Bytes(memory.Committed);
            LimitText.Text = Format.Bytes(memory.CommitLimit);

            ApplyProcessSnapshot(groups, totalProcesses);
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

    private void ApplyProcessSnapshot(IReadOnlyList<ProcessGroupUsage> groups, int processCount)
    {
        string? overviewSelected = (ProcessGrid.SelectedItem as ProcessGroupUsage)?.Name;
        string? detailSelected = (GroupGrid.SelectedItem as ProcessGroupUsage)?.Name;
        _groups.ReplaceWith(groups);
        if (overviewSelected is not null)
            ProcessGrid.SelectedItem = _groups.FirstOrDefault(g =>
                g.Name.Equals(overviewSelected, StringComparison.OrdinalIgnoreCase));
        if (detailSelected is not null)
            GroupGrid.SelectedItem = _groups.FirstOrDefault(g =>
                g.Name.Equals(detailSelected, StringComparison.OrdinalIgnoreCase));
        string summary = $"{groups.Count} приложений · {RussianText.Processes(processCount)}";
        ProcessCountText.Text = summary;
        GroupCountText.Text = summary;
        UpdateTrimButton();
    }

    private bool MatchesGroup(ProcessGroupUsage group, string? query)
    {
        string search = query?.Trim() ?? "";
        return search.Length == 0 ||
            group.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            group.Members.Any(p => p.Pid.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private bool FilterProcess(object item) =>
        item is ProcessGroupUsage g && MatchesGroup(g, SearchBox?.Text);

    private bool FilterGroup(object item) =>
        item is ProcessGroupUsage g && MatchesGroup(g, GroupSearchBox?.Text);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
            SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        _overviewView?.Refresh();
    }

    private void GroupSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (GroupSearchPlaceholder is not null)
            GroupSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(GroupSearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        _detailView?.Refresh();
    }

    private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateTrimButton();

    private void UpdateTrimButton()
    {
        if (TrimButton is not null)
            TrimButton.IsEnabled = !_cleaning &&
                ProcessGrid.SelectedItem is ProcessGroupUsage { Count: 1 };
    }

    // Used by the Windows packaged-EXE startup test to exercise real navigation and grouping.
    internal void VerifyProcessRefreshForSmokeTest()
    {
        var first = ProcessGrouping.Build([
            new ProcessUsage(101, "alpha", 4000, 1000),
            new ProcessUsage(102, "ALPHA", 2000, 500),
            new ProcessUsage(103, "beta", 3000, 2000)
        ]);
        ApplyProcessSnapshot(first, 3);
        if (ProcessGrid.Items.Count != 2 || GroupGrid.Items.Count != 2)
            throw new InvalidOperationException("Группировка не отображается в двух разделах.");
        SearchBox.Text = "102";
        if (ProcessGrid.Items.Count != 1)
            throw new InvalidOperationException("Поиск по PID в обзоре не работает.");
        GroupSearchBox.Text = "beta";
        if (GroupGrid.Items.Count != 1)
            throw new InvalidOperationException("Поиск по приложению не работает.");
        SearchBox.Clear();
        GroupSearchBox.Clear();
        ActivateSection(Section.Processes);
        if (ProcessesPage.Visibility != Visibility.Visible || OverviewPage.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Раздел процессов не открылся.");
        ActivateSection(Section.Settings);
        if (SettingsPage.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Раздел настроек не открылся.");
        ActivateSection(Section.About);
        if (AboutPage.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Раздел «О программе» не открылся.");
        ActivateSection(Section.Overview);
        ApplyProcessSnapshot([], 0);
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
            !await ConfirmAsync("Подтверждение очистки",
                "Очистка рабочих наборов и изменённых страниц может временно замедлить работу Windows и программ. Продолжить?",
                "Очистить"))
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
            UpdateTrimButton();
        }
    }

    private async void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (_cleaning || ProcessGrid.SelectedItem is not ProcessGroupUsage { Count: 1 } selected) return;
        await TrimProcessAsync(selected.Members[0]);
    }

    private async void TrimMember_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProcessUsage selected })
            await TrimProcessAsync(selected);
    }

    private async Task TrimProcessAsync(ProcessUsage selected)
    {
        if (_cleaning) return;
        if (!await ConfirmAsync("Очистка памяти процесса",
                "Уменьшить рабочий набор процесса " + selected.Name + " (PID " + selected.Pid +
                ")? Данные могут быть повторно загружены в ОЗУ, а процесс — временно замедлиться.",
                "Очистить PID"))
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
            UpdateTrimButton();
        }
        await RefreshAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        ActivateSection(Section.Settings);

    private void LoadSettingsControls()
    {
        RefreshText.Text = _settings.RefreshSeconds.ToString();
        ThresholdText.Text = _settings.AutoCleanThreshold.ToString();
        CooldownText.Text = _settings.AutoCleanCooldownMinutes.ToString();
        AutoCleanCheck.IsChecked = _settings.AutoClean;
        StartupCheck.IsChecked = _settings.StartWithWindows;
        MinimizeCheck.IsChecked = _settings.MinimizeToTray;
        CloseCheck.IsChecked = _settings.CloseToTray;
        UpdateCheck.IsChecked = _settings.CheckForUpdates;
        SettingsStatusText.Text = "Изменения вступят в силу после сохранения.";
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsControls();
        SetStatus("Несохранённые изменения отменены.");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RefreshText.Text, out int refresh) || refresh is < 1 or > 10 ||
            !int.TryParse(ThresholdText.Text, out int threshold) || threshold is < 70 or > 98 ||
            !int.TryParse(CooldownText.Text, out int cooldown) || cooldown is < 5 or > 120)
        {
            SettingsStatusText.Text = "Проверьте значения: обновление 1–10 сек., порог 70–98%, пауза 5–120 мин.";
            SetStatus("Некорректные настройки.");
            return;
        }

        var next = new AppSettings
        {
            RefreshSeconds = refresh,
            AutoCleanThreshold = threshold,
            AutoCleanCooldownMinutes = cooldown,
            AutoClean = AutoCleanCheck.IsChecked == true,
            StartWithWindows = StartupCheck.IsChecked == true,
            MinimizeToTray = MinimizeCheck.IsChecked == true,
            CloseToTray = CloseCheck.IsChecked == true,
            CheckForUpdates = UpdateCheck.IsChecked == true
        };
        try
        {
            StartupService.SetEnabled(next.StartWithWindows);
            _store.Save(next);
            _settings = next;
            ConfigureTimer();
            ConfigureUpdateTimer();
            UpdateAutomationSummary();
            LoadSettingsControls();
            SettingsStatusText.Text = "Настройки сохранены.";
            SetStatus("Настройки сохранены.");
            if (next.CheckForUpdates) _ = CheckUpdatesAsync(false);
        }
        catch (Exception ex)
        {
            StartupLog.Error("Не удалось сохранить настройки", ex);
            SettingsStatusText.Text = "Ошибка: " + ex.Message;
            SetStatus("Не удалось сохранить настройки.");
        }
    }

    private void ActivateSection(Section section)
    {
        _activeSection = section;
        OverviewPage.Visibility = section == Section.Overview ? Visibility.Visible : Visibility.Collapsed;
        ProcessesPage.Visibility = section == Section.Processes ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = section == Section.Settings ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = section == Section.About ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in new (Button button, Section page)[]
        {
            (OverviewNav, Section.Overview), (ProcessesNav, Section.Processes),
            (SettingsNav, Section.Settings), (AboutNav, Section.About)
        })
        {
            item.button.Background = (System.Windows.Media.Brush)FindResource(
                item.page == section ? "SelectedNavBrush" : "TransparentNavBrush");
            item.button.Foreground = (System.Windows.Media.Brush)FindResource(
                item.page == section ? "AccentBrush" : "TextBrush");
        }
        if (section == Section.Settings) LoadSettingsControls();
    }

    private void OpenGithub_Click(object sender, RoutedEventArgs e) =>
        OpenExternal("https://github.com/rkfsociety/MemPulse");

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        string directory = System.IO.Path.GetDirectoryName(StartupLog.FilePath) ??
            throw new InvalidOperationException("Папка журнала недоступна.");
        OpenExternal(directory);
    }

    private void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            StartupLog.Error("Не удалось открыть адрес или папку", ex);
            SetStatus("Не удалось открыть: " + ex.Message);
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

        // Axis range is explicitly shown: the history is scaled to real recent samples.
        double minimum = _memoryHistory.Min();
        double maximum = _memoryHistory.Max();
        double padding = Math.Max(3, (maximum - minimum) * 0.25);
        double low = Math.Max(0, Math.Floor(minimum - padding));
        double high = Math.Min(100, Math.Ceiling(maximum + padding));
        if (high - low < 4)
        {
            low = Math.Max(0, minimum - 2);
            high = Math.Min(100, maximum + 2);
        }
        HistoryRangeText.Text = $"Шкала {low:0.#}–{high:0.#}%";
        double span = Math.Max(0.001, high - low);
        var points = new System.Windows.Media.PointCollection();
        if (_memoryHistory.Count == 1)
        {
            double y = height * (1 - (_memoryHistory[0] - low) / span);
            points.Add(new System.Windows.Point(0, y));
            points.Add(new System.Windows.Point(width, y));
        }
        else
        {
            for (int i = 0; i < _memoryHistory.Count; i++)
            {
                double x = i * width / (_memoryHistory.Count - 1);
                double y = height * (1 - (_memoryHistory[i] - low) / span);
                points.Add(new System.Windows.Point(x, Math.Clamp(y, 0, height)));
            }
        }
        var last = points[^1];
        System.Windows.Controls.Canvas.SetLeft(HistoryDot, last.X - HistoryDot.Width / 2);
        System.Windows.Controls.Canvas.SetTop(HistoryDot, last.Y - HistoryDot.Height / 2);
        HistoryDot.Visibility = Visibility.Visible;
        HistoryLine.Points = points;
        var filled = new System.Windows.Media.PointCollection
        {
            new System.Windows.Point(0, height)
        };
        foreach (var point in points) filled.Add(point);
        filled.Add(new System.Windows.Point(width, height));
        HistoryFill.Points = filled;
    }

    private void Overview_Click(object sender, RoutedEventArgs e) =>
        ActivateSection(Section.Overview);

    private void Processes_Click(object sender, RoutedEventArgs e) =>
        ActivateSection(Section.Processes);

    private void About_Click(object sender, RoutedEventArgs e) =>
        ActivateSection(Section.About);

    private Task<bool> ConfirmAsync(string title, string description, string accept)
    {
        if (_confirmation is not null) return Task.FromResult(false);
        _confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfirmTitle.Text = title;
        ConfirmDescription.Text = description;
        ConfirmAcceptButton.Content = accept;
        ConfirmOverlay.Visibility = Visibility.Visible;
        ConfirmCancelButton.Focus();
        return _confirmation.Task;
    }

    private void ConfirmYes_Click(object sender, RoutedEventArgs e) => CloseConfirmation(true);
    private void ConfirmNo_Click(object sender, RoutedEventArgs e) => CloseConfirmation(false);

    private void CloseConfirmation(bool accepted)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        var completion = _confirmation;
        _confirmation = null;
        completion?.TrySetResult(accepted);
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
