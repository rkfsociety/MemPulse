using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MemPulse.Core;

namespace MemPulse.Linux;

public sealed class MainWindow : Window
{
    // Keep the Linux palette aligned with the Windows dashboard.
    private static readonly IBrush Canvas = new SolidColorBrush(Color.Parse("#0A121C"));
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#101F2B"));
    private static readonly IBrush Raised = new SolidColorBrush(Color.Parse("#172B37"));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.Parse("#294653"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#EEF7FA"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A3B9C6"));
    private static readonly IBrush Highlight = new SolidColorBrush(Color.Parse("#35E0C2"));
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#274451"));
    private readonly LinuxProcfs _procfs = new();
    private readonly LinuxSettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private LinuxCleanupService.AutoCleanupSession? _autoSession;

    private readonly TextBlock _used = Metric();
    private readonly TextBlock _available = Metric();
    private readonly TextBlock _cache = Metric();
    private readonly TextBlock _total = Metric();
    private readonly TextBlock _swapText = new() { Foreground = Muted, FontSize = 12, Text = "Swap: —" };
    private readonly TextBlock _historyText = new() { Foreground = Muted, FontSize = 11, Text = "Последние очистки: нет", TextWrapping = TextWrapping.Wrap };
    private readonly CleanupHistoryLog _cleanupHistory = new();
    private ulong _lastUsedBytes;
    private readonly TextBlock _usagePercent = new() { Foreground = Highlight, FontSize = 25, FontWeight = FontWeight.Bold };
    private readonly TextBlock _processCount = new() { Foreground = Muted, FontSize = 12 };
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _usageBar = new() { Minimum = 0, Maximum = 100, Height = 8, Foreground = Highlight, Background = Track };
    private readonly TextBox _filter = new() { PlaceholderText = "Поиск имени или PID", Width = 240 };
    private readonly ComboBox _sort = new()
    {
        ItemsSource = new[] { "ОЗУ ↓", "Data ↓", "Имя А–Я", "Процессов ↓" },
        SelectedIndex = 0, Width = 165, MinHeight = 36
    };
    private readonly Button _clearFilter = new() { Content = "Сбросить", IsVisible = false };
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly ScrollViewer _processScroll = new();
    private int _processTotal;
    private readonly Button _cleanButton = new() { Content = "Очистить файловый кэш (root)" };
    private readonly Button _applyButton = new() { Content = "Применить автоочистку" };
    private readonly CheckBox _autoBox = new() { Content = "Автоочистка" };
    private readonly CheckBox _minimizeToTrayBox = new() { Content = "Сворачивать в трей" };
    private readonly CheckBox _closeToTrayBox = new() { Content = "Скрывать в трей при закрытии" };
    private readonly CheckBox _checkUpdatesBox = new() { Content = "Проверять обновления" };
    private readonly Button _updateButton = new() { Content = "Обновления", VerticalAlignment = VerticalAlignment.Center };
    private ReleaseInfo? _availableRelease;
    private bool _checkingUpdates;
    private TrayIcon? _trayIcon;
    private bool _forceExit;
    private bool _trayHintShown;
    private readonly NumericUpDown _threshold = new()
    {
        Minimum = 70, Maximum = 98, Increment = 1, Value = 90, FormatString = "0", Width = 72
    };
    private readonly NumericUpDown _cooldown = new()
    {
        Minimum = 5, Maximum = 120, Increment = 1, Value = 15, FormatString = "0", Width = 72
    };
    private readonly TextBox _excludedBox = new() { Watermark = "Исключения процессов (через запятую)", Width = 320 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _filterDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private const int ProcessPageSize = 100;
    private int _visibleGroupLimit = ProcessPageSize;
    private bool _refreshing;
    private bool _closed;
    private IReadOnlyList<ProcessGroupUsage> _groups = [];
    private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);
    private string _operationStatus = "Мониторинг работает без прав администратора.";

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        _autoBox.IsChecked = _settings.AutoClean;
        _minimizeToTrayBox.IsChecked = _settings.MinimizeToTray;
        _closeToTrayBox.IsChecked = _settings.CloseToTray;
        _checkUpdatesBox.IsChecked = _settings.CheckForUpdates;
        _minimizeToTrayBox.IsCheckedChanged += (_, _) =>
        {
            _settings.MinimizeToTray = _minimizeToTrayBox.IsChecked == true;
            TrySaveSettings();
        };
        _closeToTrayBox.IsCheckedChanged += (_, _) =>
        {
            _settings.CloseToTray = _closeToTrayBox.IsChecked == true;
            TrySaveSettings();
        };
        _checkUpdatesBox.IsCheckedChanged += (_, _) =>
        {
            _settings.CheckForUpdates = _checkUpdatesBox.IsChecked == true;
            TrySaveSettings();
            if (_settings.CheckForUpdates)
                _ = CheckUpdatesAsync(manual: false);
        };
        StyleAction(_updateButton);
        _updateButton.Click += async (_, _) => await UpdateButtonClickAsync();
        _threshold.Value = _settings.AutoCleanThreshold;
        _cooldown.Value = _settings.AutoCleanCooldownMinutes;
        _excludedBox.Text = ProcessExclusion.FormatList(_settings.ExcludedProcesses);
        _excludedBox.Background = Raised;
        _excludedBox.Foreground = Text;
        _excludedBox.BorderBrush = Stroke;
        _excludedBox.BorderThickness = new Thickness(1);
        _excludedBox.Padding = new Thickness(10, 6);
        _excludedBox.LostFocus += (_, _) =>
        {
            _settings.ExcludedProcesses = ProcessExclusion.ParseList(_excludedBox.Text);
            _excludedBox.Text = ProcessExclusion.FormatList(_settings.ExcludedProcesses);
            TrySaveSettings();
        };
        StyleAction(_cleanButton);
        StyleAction(_applyButton, primary: true);
        StyleAction(_clearFilter);
        _sort.Background = Raised;
        _sort.Foreground = Text;
        _sort.BorderBrush = Stroke;
        _sort.BorderThickness = new Thickness(1);
        _sort.Padding = new Thickness(11, 7);
        ToolTip.SetTip(_sort, "Сортировка приложений по использованию памяти, имени или числу процессов");
        ToolTip.SetTip(_clearFilter, "Очистить поиск процессов");
        _filter.Background = Raised;
        _filter.Foreground = Text;
        _filter.BorderBrush = Stroke;
        _filter.BorderThickness = new Thickness(1);
        _filter.Padding = new Thickness(11, 8);
        ToolTip.SetTip(_filter, "Ctrl+F — поиск · Esc — очистить поиск");
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _filter.Focus();
                _filter.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _filter.IsFocused && !string.IsNullOrEmpty(_filter.Text))
            {
                _filter.Clear();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                Refresh();
                e.Handled = true;
            }
            else if (e.Key == Key.M && e.KeyModifiers.HasFlag(KeyModifiers.Control | KeyModifiers.Alt))
            {
                _ = CleanAsync("Хоткей");
                e.Handled = true;
            }
        };

        Title = "MemPulse — Linux";
        Width = 1020;
        Height = 790;
        MinWidth = 760;
        MinHeight = 600;
        Background = Canvas;

        // Scroll the dashboard as one surface, keeping operation status visible
        // while navigating a long process list or using high display scaling.
        var shell = new DockPanel();
        Content = shell;
        var layout = new StackPanel { Spacing = 11, Margin = new Thickness(20) };
        _processScroll.Content = layout;
        shell.Children.Add(_processScroll);
        var header = new StackPanel { Spacing = 11 };
        layout.Children.Add(header);

        var titleLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleLine.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = "MemPulse", FontSize = 29, FontWeight = FontWeight.Bold, Foreground = Text },
                new TextBlock { Text = "МОНИТОРИНГ ПАМЯТИ  /  LINUX", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Muted }
            }
        });
        var refresh = new Button { Content = "↻  Обновить", VerticalAlignment = VerticalAlignment.Center };
        StyleAction(refresh);
        refresh.Click += (_, _) => Refresh();
        var titleActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _updateButton, refresh }
        };
        Grid.SetColumn(titleActions, 1);
        titleLine.Children.Add(titleActions);
        header.Children.Add(titleLine);

        var metrics = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), Margin = new Thickness(0, 4, 0, 0)
        };
        AddMetric(metrics, 0, "Занято", _used);
        AddMetric(metrics, 1, "Доступно", _available);
        AddMetric(metrics, 2, "Кэш (reclaimable)", _cache);
        AddMetric(metrics, 3, "Всего", _total);
        // Four cards on wide screens, two readable rows on compact windows.
        UpdateMetricColumns(metrics, Width < 920 ? 2 : 4);
        SizeChanged += (_, e) => UpdateMetricColumns(metrics, e.NewSize.Width < 920 ? 2 : 4);
        var overview = new StackPanel { Spacing = 13 };
        var memoryHeading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        memoryHeading.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = "Оперативная память", Foreground = Text, FontWeight = FontWeight.SemiBold, FontSize = 17 },
                new TextBlock { Text = "Физическая память · текущая загрузка", Foreground = Muted, FontSize = 12 }
            }
        });
        Grid.SetColumn(_usagePercent, 1);
        memoryHeading.Children.Add(_usagePercent);
        overview.Children.Add(memoryHeading);
        overview.Children.Add(_usageBar);
        overview.Children.Add(metrics);
        overview.Children.Add(_swapText);
        header.Children.Add(new Border
        {
            Background = Surface, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(17), Child = overview
        });

        var operations = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        operations.Children.Add(new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Очистка файлового кэша", Foreground = Text, FontSize = 15, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Требуется подтверждение прав через polkit.",
                    Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap
                },
                _historyText
            }
        });
        _cleanButton.Click += async (_, _) => await CleanAsync();
        _cleanButton.VerticalAlignment = VerticalAlignment.Center;
        _cleanButton.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_cleanButton, 1);
        operations.Children.Add(_cleanButton);
        header.Children.Add(new Border
        {
            Background = Surface, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(15), Child = operations
        });

        var automatic = new StackPanel { Spacing = 8 };
        automatic.Children.Add(new TextBlock
        {
            Text = "Автоочистка файлового кэша", FontSize = 16, FontWeight = FontWeight.SemiBold
        });
        var controls = new WrapPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center };
        _autoBox.Margin = new Thickness(0, 0, 12, 8);
        _threshold.Margin = new Thickness(0, 0, 12, 8);
        _cooldown.Margin = new Thickness(0, 0, 12, 8);
        _applyButton.Margin = new Thickness(0, 0, 0, 8);
        controls.Children.Add(_autoBox);
        controls.Children.Add(new TextBlock { Text = "Порог, %", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8) });
        controls.Children.Add(_threshold);
        controls.Children.Add(new TextBlock { Text = "Пауза, мин.", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8) });
        controls.Children.Add(_cooldown);
        _applyButton.Click += async (_, _) => await ApplyAutoSettingsAsync();
        controls.Children.Add(_applyButton);
        automatic.Children.Add(controls);
        var excludeRow = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        excludeRow.Children.Add(new TextBlock
        {
            Text = "Исключения (whitelist):", Foreground = Muted, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4)
        });
        _excludedBox.Margin = new Thickness(0, 0, 0, 4);
        excludeRow.Children.Add(_excludedBox);
        automatic.Children.Add(excludeRow);
        var trayRow = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _minimizeToTrayBox.Margin = new Thickness(0, 0, 14, 4);
        _closeToTrayBox.Margin = new Thickness(0, 0, 14, 4);
        _checkUpdatesBox.Margin = new Thickness(0, 0, 0, 4);
        trayRow.Children.Add(_minimizeToTrayBox);
        trayRow.Children.Add(_closeToTrayBox);
        trayRow.Children.Add(_checkUpdatesBox);
        automatic.Children.Add(trayRow);
        automatic.Children.Add(new TextBlock
        {
            Text = "По умолчанию выключена. После разрешения polkit помощник действует только пока MemPulse открыт; при закрытии приложения он завершается. Linux сам управляет файловым кэшем, поэтому принудительный сброс не рекомендуется без необходимости.",
            Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(new Border
        {
            Background = Surface, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(15), Child = automatic
        });

        // A separate wrapping control row stays usable on narrow or scaled displays.
        var processHeading = new StackPanel { Spacing = 9, Margin = new Thickness(0, 10, 0, 6) };
        processHeading.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Процессы", FontSize = 19, FontWeight = FontWeight.SemiBold },
                _processCount
            }
        });
        var processControls = new WrapPanel { Orientation = Orientation.Horizontal };
        _sort.Margin = new Thickness(0, 0, 9, 8);
        _filter.Margin = new Thickness(0, 0, 9, 8);
        _clearFilter.Margin = new Thickness(0, 0, 0, 8);
        processControls.Children.Add(_sort);
        processControls.Children.Add(_filter);
        processControls.Children.Add(_clearFilter);
        processHeading.Children.Add(processControls);
        _sort.SelectionChanged += (_, _) =>
        {
            _visibleGroupLimit = ProcessPageSize;
            RenderProcesses();
        };
        _clearFilter.Click += (_, _) =>
        {
            _filter.Clear();
            _filter.Focus();
        };
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            _visibleGroupLimit = ProcessPageSize;
            RenderProcesses(resetScroll: true);
        };
        _filter.TextChanged += (_, _) =>
        {
            _clearFilter.IsVisible = !string.IsNullOrWhiteSpace(_filter.Text);
            _filterDebounce.Stop();
            _filterDebounce.Start();
        };
        header.Children.Add(processHeading);

        var footer = new StackPanel { Spacing = 5, Margin = new Thickness(20, 8, 20, 16) };
        footer.Children.Add(new TextBlock
        {
            Text = "RSS — физическая память · Data — виртуальная память · Очистка PID не гарантирует уменьшение RSS",
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap
        });
        footer.Children.Add(_status);
        layout.Children.Add(new Border
        {
            Background = Surface, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(9), Child = _rows
        });
        // DockPanel processes children in order: status stays pinned below
        // the scrolling dashboard, even when hundreds of processes are listed.
        DockPanel.SetDock(footer, Dock.Bottom);
        shell.Children.Insert(0, footer);

        _timer.Tick += (_, _) => Refresh();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty &&
                WindowState == WindowState.Minimized &&
                _settings.MinimizeToTray &&
                _trayIcon is not null)
            {
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    LinuxNotifications.TrySend("MemPulse", "Приложение свёрнуто в системный трей.");
                }
            }
        };
        Closing += (_, e) =>
        {
            if (!_forceExit && _settings.CloseToTray && _trayIcon is not null)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    LinuxNotifications.TrySend("MemPulse", "MemPulse продолжает мониторинг в трее.");
                }
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _filterDebounce.Stop();
            _autoSession?.Dispose();
            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        };
        if (!Environment.GetCommandLineArgs().Contains("--startup-smoke-test", StringComparer.Ordinal))
            InitializeTray();
        Refresh();
        _timer.Start();
        // A remembered enabled setting is re-authorized for every GUI session.
        if (_settings.AutoClean &&
            !Environment.GetCommandLineArgs().Contains("--startup-smoke-test", StringComparer.Ordinal))
            Dispatcher.UIThread.Post(async () => await ApplyAutoSettingsAsync(), DispatcherPriority.Background);
        if (_settings.CheckForUpdates &&
            !Environment.GetCommandLineArgs().Contains("--startup-smoke-test", StringComparer.Ordinal))
            Dispatcher.UIThread.Post(async () => await CheckUpdatesAsync(manual: false), DispatcherPriority.Background);
    }

    // The Linux graphical smoke test verifies the scrollable dashboard and
    // permanently visible status footer both remain part of the window.
    internal bool VerifyScrollableLayoutForSmokeTest() =>
        Content is DockPanel shell &&
        shell.Children.Contains(_processScroll) &&
        _processScroll.Content is StackPanel dashboard &&
        dashboard.Children.OfType<Border>().Any(card => ReferenceEquals(card.Child, _rows)) &&
        shell.Children.OfType<StackPanel>().Any(section => section.Children.Contains(_status)) &&
        _sort.ItemsSource is string[] { Length: 4 } &&
        _sort.SelectedIndex == 0;

    // Startup smoke test: a live refresh must preserve expanded controls and
    // their PID buttons, including when sorting moves the group to another row.
    internal bool VerifyProcessRowReuseForSmokeTest()
    {
        _groups = ProcessGrouping.Build([
            new ProcessUsage(101, "alpha", 4096, 1024),
            new ProcessUsage(202, "beta", 2048, 512)
        ]);
        _processTotal = 2;
        RenderProcesses();
        var rows = _rows.Children.OfType<Expander>().ToArray();
        if (rows.Length != 2) return false;
        var alpha = rows[0];
        var beta = rows[1];
        alpha.IsExpanded = true;
        UpdateProcessDetails(alpha, _groups[0]);
        if (alpha.Content is not StackPanel details || details.Children.Count != 1 ||
            details.Children[0] is not Grid pidRow || pidRow.Children[3] is not Button trim)
            return false;

        _groups = ProcessGrouping.Build([
            new ProcessUsage(101, "alpha", 8192, 3072),
            new ProcessUsage(202, "beta", 16384, 4096)
        ]);
        RenderProcesses();
        rows = _rows.Children.OfType<Expander>().ToArray();
        return rows.Length == 2 &&
            ReferenceEquals(rows[0], beta) &&
            ReferenceEquals(rows[1], alpha) &&
            ReferenceEquals(alpha.Content, details) &&
            ReferenceEquals(((Grid)details.Children[0]).Children[3], trim) &&
            ((TextBlock)((Grid)alpha.Header!).Children[1]).Text == "RSS  " + _groups[1].WorkingText &&
            ((TextBlock)((Grid)details.Children[0]).Children[1]).Text == _groups[1].Members[0].WorkingText;
    }

    private static TextBlock Metric() =>
        new() { FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Text };

    private static void StyleAction(Button button, bool primary = false)
    {
        button.Background = primary ? Highlight : Raised;
        button.Foreground = primary ? Canvas : Text;
        button.BorderBrush = primary ? Highlight : Stroke;
        button.BorderThickness = new Thickness(1);
        button.Padding = new Thickness(13, 8);
        button.MinHeight = 36;
        button.FontWeight = FontWeight.SemiBold;
    }

    private static void UpdateMetricColumns(Grid metrics, int columns)
    {
        metrics.ColumnDefinitions = new ColumnDefinitions(columns == 2 ? "*,*" : "*,*,*,*");
        metrics.RowDefinitions = new RowDefinitions(columns == 2 ? "Auto,Auto" : "Auto");
        for (int index = 0; index < metrics.Children.Count; index++)
        {
            var card = metrics.Children[index];
            Grid.SetColumn(card, index % columns);
            Grid.SetRow(card, index / columns);
            if (card is Border border)
                border.Margin = new Thickness(0, 0, index % columns == columns - 1 ? 0 : 9,
                    columns == 2 && index < 2 ? 9 : 0);
        }
    }

    private static void AddMetric(Grid grid, int column, string caption, TextBlock value)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock { Text = caption, Foreground = Muted, FontSize = 11 });
        stack.Children.Add(value);
        var card = new Border
        {
            Background = Raised, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12),
            Margin = new Thickness(0, 0, column == 3 ? 0 : 9, 0), Child = stack
        };
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    private void Refresh()
    {
        if (!_refreshing && !_closed)
            _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _refreshing = true;
        try
        {
            // /proc enumeration and grouping can be expensive on busy systems.
            // Keep the Avalonia UI thread free for typing, scrolling and window resizing.
            var snapshot = await Task.Run(() =>
            {
                var memory = _procfs.ReadMemory();
                var processes = _procfs.ReadProcesses();
                return (memory, groups: ProcessGrouping.Build(processes), total: processes.Count);
            });
            if (_closed) return;

            _usageBar.Value = snapshot.memory.UsedPercent;
            _usagePercent.Text = $"{snapshot.memory.UsedPercent:0.0}%";
            _used.Text = Format.Bytes(snapshot.memory.Used);
            _available.Text = Format.Bytes(snapshot.memory.Available);
            _cache.Text = Format.Bytes(snapshot.memory.Cache);
            _total.Text = Format.Bytes(snapshot.memory.Total);
            _lastUsedBytes = snapshot.memory.Used;
            string swapPart = snapshot.memory.SwapTotal > 0
                ? $"Swap: {Format.Bytes(snapshot.memory.SwapUsed)} / {Format.Bytes(snapshot.memory.SwapTotal)} ({snapshot.memory.SwapUsedPercent:0}%)"
                : "Swap: не настроен";
            string zramPart = snapshot.memory.ZramTotal > 0
                ? $" · Zram: {Format.Bytes(snapshot.memory.ZramUsed)} / {Format.Bytes(snapshot.memory.ZramTotal)}"
                : "";
            _swapText.Text = swapPart + zramPart;
            if (_trayIcon is not null)
            {
                _trayIcon.ToolTipText =
                    $"MemPulse · ОЗУ {snapshot.memory.UsedPercent:0}% ({Format.Bytes(snapshot.memory.Used)} / {Format.Bytes(snapshot.memory.Total)})";
            }
            _groups = snapshot.groups;
            _processTotal = snapshot.total;
            RenderProcesses();

            if (_autoSession is { Running: false })
            {
                _autoSession.Dispose();
                _autoSession = null;
                _autoBox.IsChecked = false;
                _settings.AutoClean = false;
                TrySaveSettings();
                _operationStatus = "Привилегированный помощник завершился. Автоочистка отключена.";
            }
            _status.Text = "Обновлено: " + DateTime.Now.ToString("HH:mm:ss") + " · " + _operationStatus;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!_closed) _status.Text = "Ошибка мониторинга: " + ex.Message;
        }
        finally { _refreshing = false; }
    }

    private void RenderProcesses(bool resetScroll = false)
    {
        string query = _filter.Text?.Trim() ?? "";
        var matches = ProcessGrouping.Sort(ProcessGrouping.Filter(_groups, query),
            (ProcessSortOrder)Math.Clamp(_sort.SelectedIndex, 0, 3));
        _processCount.Text = query.Length == 0
            ? $"{RussianText.Applications(_groups.Count)} · {RussianText.Processes(_processTotal)}"
            : $"Найдено {matches.Count} из {RussianText.Applications(_groups.Count)}";
        int visible = Math.Min(matches.Count, _visibleGroupLimit);
        if (matches.Count > visible)
            _processCount.Text += $" · показано {visible}";

        // Keep visible Expander instances (and focused PID buttons) across live
        // snapshots. Only reorder rows when the actual sort order changes.
        var previousOffset = _processScroll.Offset;
        var previous = _rows.Children.OfType<Expander>()
            .Where(row => row.Tag is ProcessGroupUsage)
            .ToDictionary(row => ((ProcessGroupUsage)row.Tag!).Name, StringComparer.OrdinalIgnoreCase);
        foreach (var old in previous.Values)
        {
            var name = ((ProcessGroupUsage)old.Tag!).Name;
            if (old.IsExpanded) _expandedGroups.Add(name);
            else _expandedGroups.Remove(name);
        }

        var desired = new List<Control>(visible + 1);
        foreach (var group in matches.Take(visible))
        {
            if (!previous.TryGetValue(group.Name, out var expander))
            {
                expander = new Expander
                {
                    Tag = group,
                    IsExpanded = _expandedGroups.Contains(group.Name),
                    Header = CreateGroupHeader(group)
                };
                // Resolve the current snapshot from Tag instead of capturing a
                // stale group in the event handler.
                expander.Expanded += (_, _) =>
                {
                    if (expander.Tag is not ProcessGroupUsage current) return;
                    _expandedGroups.Add(current.Name);
                    UpdateProcessDetails(expander, current);
                };
                expander.Collapsed += (_, _) =>
                {
                    if (expander.Tag is ProcessGroupUsage current)
                        _expandedGroups.Remove(current.Name);
                    expander.Content = null;
                };
                var toggleExcludeItem = new MenuItem { Header = "Добавить в исключения" };
                toggleExcludeItem.Click += (_, _) =>
                {
                    if (expander.Tag is ProcessGroupUsage current)
                        ToggleLinuxProcessExclusion(current);
                };
                var menu = new ContextMenu();
                menu.Items.Add(toggleExcludeItem);
                menu.Opening += (_, _) =>
                {
                    if (expander.Tag is ProcessGroupUsage current)
                    {
                        bool excluded = ProcessExclusion.IsExcluded(current.Name, _settings.ExcludedProcesses);
                        toggleExcludeItem.Header = excluded
                            ? $"Убрать «{current.DisplayName}» из исключений"
                            : $"Добавить «{current.DisplayName}» в исключения";
                    }
                };
                expander.ContextMenu = menu;
            }
            else
            {
                expander.Tag = group;
                UpdateGroupHeader(expander, group);
            }

            if (expander.IsExpanded)
                UpdateProcessDetails(expander, group);
            desired.Add(expander);
        }
        if (matches.Count > visible)
        {
            int nextCount = Math.Min(ProcessPageSize, matches.Count - visible);
            var more = new Button
            {
                Content = $"Показать ещё {RussianText.Applications(nextCount)}",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12)
            };
            StyleAction(more);
            more.Click += (_, _) =>
            {
                _visibleGroupLimit += ProcessPageSize;
                RenderProcesses();
            };
            desired.Add(more);
        }
        if (desired.Count == 0)
            desired.Add(new TextBlock
            {
                Text = "Процессы не найдены.", Foreground = Muted,
                Margin = new Thickness(12)
            });

        bool layoutChanged = false;
        for (int index = 0; index < desired.Count; index++)
        {
            if (index < _rows.Children.Count && ReferenceEquals(_rows.Children[index], desired[index]))
                continue;
            if (_rows.Children.Contains(desired[index]))
                _rows.Children.Remove(desired[index]);
            _rows.Children.Insert(index, desired[index]);
            layoutChanged = true;
        }
        while (_rows.Children.Count > desired.Count)
        {
            _rows.Children.RemoveAt(_rows.Children.Count - 1);
            layoutChanged = true;
        }

        // Unchanged rows do not need a layout reset, preserving keyboard focus
        // and avoiding needless scroll jumps on the two-second refresh timer.
        if (resetScroll)
            Dispatcher.UIThread.Post(() => _filter.BringIntoView(), DispatcherPriority.Loaded);
        else if (layoutChanged)
            Dispatcher.UIThread.Post(() => _processScroll.Offset = previousOffset, DispatcherPriority.Loaded);
    }

    private static void UpdateGroupHeader(Expander expander, ProcessGroupUsage group)
    {
        if (expander.Header is not Grid header || header.Children.Count != 3)
        {
            expander.Header = CreateGroupHeader(group);
            return;
        }
        ((TextBlock)header.Children[0]).Text = group.DisplayName;
        ((TextBlock)header.Children[1]).Text = "RSS  " + group.WorkingText;
        ((TextBlock)header.Children[2]).Text = "Data  " + group.PrivateText;
    }

    private void UpdateProcessDetails(Expander expander, ProcessGroupUsage group)
    {
        if (expander.Content is not StackPanel details || details.Children.Count != group.Members.Count)
        {
            expander.Content = CreateProcessDetails(group);
            return;
        }
        for (int index = 0; index < group.Members.Count; index++)
        {
            if (details.Children[index] is not Grid row ||
                row.Tag is not int pid || pid != group.Members[index].Pid)
            {
                expander.Content = CreateProcessDetails(group);
                return;
            }
        }
        for (int index = 0; index < group.Members.Count; index++)
        {
            var row = (Grid)details.Children[index];
            var member = group.Members[index];
            ((TextBlock)row.Children[0]).Text = "PID " + member.Pid + " · " + member.Name;
            ((TextBlock)row.Children[1]).Text = member.WorkingText;
            ((TextBlock)row.Children[2]).Text = member.PrivateText;
        }
    }

    private static Grid CreateGroupHeader(ProcessGroupUsage group)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        heading.Children.Add(new TextBlock
        {
            Text = group.DisplayName, Foreground = Text,
            FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        var rss = new TextBlock
        {
            Text = "RSS  " + group.WorkingText, Foreground = Highlight,
            FontSize = 12, Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(rss, 1);
        heading.Children.Add(rss);
        var data = new TextBlock
        {
            Text = "Data  " + group.PrivateText, Foreground = Muted,
            FontSize = 12, Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(data, 2);
        heading.Children.Add(data);
        return heading;
    }

    private StackPanel CreateProcessDetails(ProcessGroupUsage group)
    {
        var details = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var member in group.Members)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,120,120,145"),
                Margin = new Thickness(8, 4),
                Tag = member.Pid
            };
            row.Children.Add(new TextBlock
            {
                Text = "PID " + member.Pid + " · " + member.Name,
                Foreground = Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var rss = new TextBlock
            {
                Text = member.WorkingText, Foreground = Highlight,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(rss, 1);
            row.Children.Add(rss);
            var data = new TextBlock
            {
                Text = member.PrivateText, Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(data, 2);
            row.Children.Add(data);
            var trim = new Button { Content = "Очистить PID", Margin = new Thickness(8, 0, 0, 0) };
            StyleAction(trim);
            int pid = member.Pid;
            string procName = member.Name;
            trim.Click += async (_, _) => await TrimAsync(pid, procName, trim);
            Grid.SetColumn(trim, 3);
            row.Children.Add(trim);
            details.Children.Add(row);
        }
        return details;
    }

    private void ToggleLinuxProcessExclusion(ProcessGroupUsage group)
    {
        string normalized = ProcessExclusion.NormalizeName(group.Name);
        if (normalized.Length == 0) return;

        var list = ProcessExclusion.NormalizeList(_settings.ExcludedProcesses);
        bool removed = list.RemoveAll(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
            list.Add(normalized);

        _settings.ExcludedProcesses = list;
        _excludedBox.Text = ProcessExclusion.FormatList(list);
        TrySaveSettings();
        _operationStatus = removed
            ? $"Процесс «{normalized}» удалён из исключений."
            : $"Процесс «{normalized}» добавлен в исключения.";
        _status.Text = _operationStatus;
    }

    private async Task TrimAsync(int pid, string processName, Button button)
    {
        if (ProcessExclusion.IsExcluded(processName, _settings.ExcludedProcesses))
        {
            _operationStatus = $"Процесс «{processName}» находится в списке исключений.";
            _status.Text = _operationStatus;
            return;
        }
        if (!LinuxProcfs.TryReadStartTicks(pid, out ulong startTicks))
        {
            _operationStatus = "Процесс " + pid + " завершился. Обновите список.";
            Refresh();
            return;
        }
        button.IsEnabled = false;
        _operationStatus = "Запрос разрешения на MADV_PAGEOUT для PID " + pid + "…";
        _status.Text = _operationStatus;
        try
        {
            var (success, message) = await LinuxCleanupService.RequestTrimProcessAsync(pid, startTicks);
            _operationStatus = (success ? "✓ " : "✕ ") + "PID " + pid + ": " + message;
            Refresh();
        }
        finally { button.IsEnabled = true; }
    }

    private async Task CleanAsync(string reason = "Вручную")
    {
        _cleanButton.IsEnabled = false;
        _status.Text = "Ожидание подтверждения привилегий Linux…";
        try
        {
            ulong usedBefore = _lastUsedBytes;
            var (success, message) = await LinuxCleanupService.RequestCacheDropAsync();
            ulong usedAfter = usedBefore;
            try { usedAfter = _procfs.ReadMemory().Used; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }

            _cleanupHistory.Record(new CleanupHistoryEntry(
                DateTimeOffset.Now,
                reason,
                usedBefore,
                usedAfter,
                success,
                message));
            _historyText.Text = "Последние очистки: " + string.Join("  |  ", _cleanupHistory.Entries.Take(3).Select(e => e.DisplayLine));

            _operationStatus = (success ? "✓ " : "✕ ") + message;
            if (success)
                LinuxNotifications.TrySend("MemPulse — очистка завершена", _cleanupHistory.Latest?.DisplayLine ?? message);
            Refresh();
        }
        finally { _cleanButton.IsEnabled = true; }
    }

    private async Task ApplyAutoSettingsAsync()
    {
        _applyButton.IsEnabled = false;
        try
        {
            _autoSession?.Dispose();
            _autoSession = null;
            int threshold = (int)(_threshold.Value ?? 90);
            int cooldown = (int)(_cooldown.Value ?? 15);
            _settings.AutoCleanThreshold = Math.Clamp(threshold, 70, 98);
            _settings.AutoCleanCooldownMinutes = Math.Clamp(cooldown, 5, 120);
            _settings.AutoClean = _autoBox.IsChecked == true;
            _settings.ExcludedProcesses = ProcessExclusion.ParseList(_excludedBox.Text);
            _excludedBox.Text = ProcessExclusion.FormatList(_settings.ExcludedProcesses);
            if (_settings.AutoClean)
            {
                _status.Text = "Ожидание разрешения polkit для автоочистки…";
                var (session, message) = await LinuxCleanupService.StartAutoAsync(
                    _settings.AutoCleanThreshold, _settings.AutoCleanCooldownMinutes);
                _autoSession = session;
                if (session is null)
                {
                    _settings.AutoClean = false;
                    _autoBox.IsChecked = false;
                }
                _operationStatus = (session is null ? "✕ " : "✓ ") + message;
            }
            else _operationStatus = "Автоочистка отключена.";
            TrySaveSettings();
            Refresh();
        }
        finally { _applyButton.IsEnabled = true; }
    }

    private void TrySaveSettings()
    {
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _operationStatus += " Не удалось сохранить настройки: " + ex.Message;
        }
    }

    private void InitializeTray()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "MemPulse.png");
            var menu = new NativeMenu();

            var openItem = new NativeMenuItem("Открыть MemPulse");
            openItem.Click += (_, _) => Dispatcher.UIThread.Post(ShowFromTray);
            menu.Items.Add(openItem);

            var cleanItem = new NativeMenuItem("Очистить файловый кэш (root)");
            cleanItem.Click += (_, _) => Dispatcher.UIThread.Post(async () => await CleanAsync("Трей"));
            menu.Items.Add(cleanItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("Выход");
            exitItem.Click += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                _forceExit = true;
                Close();
            });
            menu.Items.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                ToolTipText = "MemPulse — мониторинг памяти",
                Menu = menu,
                IsVisible = true
            };
            if (File.Exists(iconPath))
                _trayIcon.Icon = new WindowIcon(iconPath);

            _trayIcon.Clicked += (_, _) => Dispatcher.UIThread.Post(ShowFromTray);
            if (Application.Current is not null)
                TrayIcon.SetIcons(Application.Current, new TrayIcons { _trayIcon });
        }
        catch (Exception ex)
        {
            _operationStatus = "Системный трей недоступен: " + ex.Message;
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Refresh();
    }

    private async Task UpdateButtonClickAsync()
    {
        if (_availableRelease is null)
        {
            await CheckUpdatesAsync(manual: true);
            return;
        }
        Uri target = _availableRelease.AssetUrl ?? _availableRelease.ReleaseUrl;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", target.AbsoluteUri)
            {
                UseShellExecute = false
            });
            _operationStatus = $"Открыта страница загрузки MemPulse v{_availableRelease.Version}.";
            _status.Text = _operationStatus;
        }
        catch (Exception ex)
        {
            _operationStatus = $"Не удалось открыть ссылку ({target}): {ex.Message}";
            _status.Text = _operationStatus;
        }
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_checkingUpdates || _closed) return;
        _checkingUpdates = true;
        _updateButton.IsEnabled = false;
        try
        {
            var release = await ReleaseChecker.CheckLatestLinuxAsync();
            if (_closed) return;
            _availableRelease = release;
            if (release is not null)
            {
                _updateButton.Content = $"Скачать v{release.Version}";
                StyleAction(_updateButton, primary: true);
                _operationStatus = $"Доступно обновление MemPulse v{release.Version} ({release.AssetName ?? "GitHub Releases"}).";
                _status.Text = _operationStatus;
                if (!manual)
                    LinuxNotifications.TrySend("Доступно обновление MemPulse", $"Вышла версия v{release.Version}.");
            }
            else if (manual)
            {
                _updateButton.Content = "Обновления";
                StyleAction(_updateButton, primary: false);
                _operationStatus = $"Установлена актуальная версия MemPulse (v{ReleaseChecker.CurrentVersion}).";
                _status.Text = _operationStatus;
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (manual && !_closed)
            {
                _operationStatus = "Не удалось проверить обновления: " + ex.Message;
                _status.Text = _operationStatus;
            }
        }
        finally
        {
            _checkingUpdates = false;
            _updateButton.IsEnabled = true;
        }
    }
}
