using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using MemPulse.Core;
using PCPulse.Contracts;

namespace MemPulse.PulseModule.UI;

internal sealed class MemPulseRootView : UserControl
{
    private enum ModuleTab
    {
        Overview,
        Processes,
        Settings,
        About
    }

    private const int PageSize = 16;

    private readonly MemPulseController _controller;
    private readonly RamDonutGauge _donutGauge = new();
    private readonly RamHistoryChart _historyChart = new();

    private readonly Button[] _tabButtons = new Button[4];
    private readonly ContentControl _pageHost = new();
    private readonly ScrollViewer _scrollViewer = new();

    private readonly TextBlock _headerStatusBadge = new()
    {
        FontSize = 12,
        FontWeight = FontWeight.SemiBold,
        Foreground = ModuleTheme.TextPrimary
    };
    private readonly Border _headerBadgeBorder = new()
    {
        Background = ModuleTheme.CardBackground,
        BorderBrush = ModuleTheme.BorderBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(10, 5),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button _quickCleanButton = ModuleTheme.CreatePrimaryButton("Очистить память");

    // Overview elements
    private readonly TextBlock _overviewRamHeadline = new() { FontSize = 20, FontWeight = FontWeight.Bold, Foreground = ModuleTheme.TextPrimary };
    private readonly TextBlock _overviewRamSubline = ModuleTheme.CreateMutedLabel("—", 12.5);
    private readonly TextBlock _metricUsed = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = ModuleTheme.TextPrimary };
    private readonly TextBlock _metricAvailable = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = ModuleTheme.SuccessBrush };
    private readonly TextBlock _metricPageFile = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = ModuleTheme.TextPrimary };
    private readonly TextBlock _metricProcessCount = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = ModuleTheme.TextPrimary };
    private readonly TextBlock _statusBanner = ModuleTheme.CreateMutedLabel("Готово к работе", 12);
    private readonly StackPanel _historyRows = new() { Spacing = 6 };
    private readonly StackPanel _topProcessRows = new() { Spacing = 6 };
    private readonly CheckBox _optStandby = new() { Content = "Кэш ожидания (Standby List)", IsChecked = true, Foreground = ModuleTheme.TextPrimary };
    private readonly CheckBox _optWorkingSets = new() { Content = "Рабочие наборы процессов (Working Sets)", IsChecked = false, Foreground = ModuleTheme.TextPrimary };
    private readonly CheckBox _optModified = new() { Content = "Изменённые страницы (Modified Pages)", IsChecked = false, Foreground = ModuleTheme.TextPrimary };

    // Processes elements
    private readonly TextBox _searchBox = new() { PlaceholderText = "Поиск процесса по имени или PID...", Width = 280 };
    private readonly ComboBox _sortCombo = new() { Width = 210 };
    private readonly TextBlock _processSummaryLabel = ModuleTheme.CreateMutedLabel("Процессы загружаются...", 12);
    private readonly StackPanel _processListPanel = new() { Spacing = 6 };
    private readonly Button _prevPageButton = ModuleTheme.CreateCompactButton("← Назад");
    private readonly Button _nextPageButton = ModuleTheme.CreateCompactButton("Вперёд →");
    private readonly TextBlock _pageLabel = ModuleTheme.CreateMutedLabel("1 / 1", 12);

    // Settings controls
    private readonly Slider _refreshSlider = new() { Minimum = 1, Maximum = 10, Value = 2, Width = 220, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly TextBlock _refreshValueLabel = new() { FontWeight = FontWeight.SemiBold, Foreground = ModuleTheme.AccentBrush, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _autoCleanToggle = new() { OnContent = "Вкл", OffContent = "Выкл" };
    private readonly Slider _thresholdSlider = new() { Minimum = AutoCleanupPolicy.MinThreshold, Maximum = AutoCleanupPolicy.MaxThreshold, Value = 85, Width = 220, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly TextBlock _thresholdValueLabel = new() { FontWeight = FontWeight.SemiBold, Foreground = ModuleTheme.AccentBrush, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _cooldownSlider = new() { Minimum = AutoCleanupPolicy.MinCooldownMinutes, Maximum = AutoCleanupPolicy.MaxCooldownMinutes, Value = 15, Width = 220, IsSnapToTickEnabled = true, TickFrequency = 5 };
    private readonly TextBlock _cooldownValueLabel = new() { FontWeight = FontWeight.SemiBold, Foreground = ModuleTheme.AccentBrush, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleSwitch _fullscreenToggle = new() { OnContent = "Вкл", OffContent = "Выкл" };
    private readonly TextBox _whitelistBox = new() { PlaceholderText = "chrome, Code, Telegram", Height = 72, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };

    // Standalone-only settings controls
    private readonly ToggleSwitch _startupToggle = new() { OnContent = "Вкл", OffContent = "Выкл" };
    private readonly ToggleSwitch _minimizeTrayToggle = new() { OnContent = "Вкл", OffContent = "Выкл" };
    private readonly ToggleSwitch _closeTrayToggle = new() { OnContent = "Вкл", OffContent = "Выкл" };
    private readonly ToggleSwitch _hotkeyToggle = new() { OnContent = "Вкл", OffContent = "Выкл" };
    private readonly ToggleSwitch _checkUpdatesToggle = new() { OnContent = "Вкл", OffContent = "Выкл" };

    private readonly Dictionary<string, Bitmap?> _iconCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);

    private Control? _overviewView;
    private Control? _processesView;
    private Control? _settingsView;
    private Control? _aboutView;

    private ModuleTab _activeTab = ModuleTab.Overview;
    private int _currentPage = 1;
    private bool _suppressSettingsEvents;

    public MemPulseRootView(MemPulseController controller)
    {
        _controller = controller;
        Background = ModuleTheme.SurfaceBackground;

        _controller.StateChanged += OnControllerStateChanged;
        Unloaded += OnViewUnloaded;

        _headerBadgeBorder.Child = _headerStatusBadge;
        _quickCleanButton.Click += async (_, _) => await ExecuteCleanupAsync();

        _sortCombo.Items.Add("По физической памяти (RAM)");
        _sortCombo.Items.Add("По приватной памяти");
        _sortCombo.Items.Add("По имени (А–Я)");
        _sortCombo.Items.Add("По числу процессов");
        _sortCombo.SelectedIndex = 0;
        _sortCombo.SelectionChanged += (_, _) =>
        {
            _currentPage = 1;
            RenderProcessesList();
        };

        _searchBox.TextChanged += (_, _) =>
        {
            _currentPage = 1;
            RenderProcessesList();
        };

        _prevPageButton.Click += (_, _) =>
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                RenderProcessesList();
            }
        };
        _nextPageButton.Click += (_, _) =>
        {
            _currentPage++;
            RenderProcessesList();
        };

        WireSettingsEvents();
        Content = new ThemeVariantScope
        {
            RequestedThemeVariant = ThemeVariant.Dark,
            Child = BuildShellLayout()
        };
        SyncSettingsControlsFromModel();
        SwitchTab(ModuleTab.Overview);
        RefreshAllViews();
    }

    private Control BuildShellLayout()
    {
        var tabTitles = new[] { "Обзор", "Процессы", "Настройки", "О программе" };
        var tabsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        for (int i = 0; i < tabTitles.Length; i++)
        {
            int index = i;
            var btn = new Button
            {
                Content = tabTitles[i],
                Padding = new Thickness(12, 7),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                FontSize = 13,
                FontWeight = FontWeight.Medium
            };
            btn.Click += (_, _) => SwitchTab((ModuleTab)index);
            _tabButtons[i] = btn;
            tabsPanel.Children.Add(btn);
        }

        var titleStack = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = "MemPulse",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Foreground = ModuleTheme.TextPrimary
                },
                ModuleTheme.CreateMutedLabel("Контроль оперативной памяти и процессов", 12)
            }
        };

        var rightActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _headerBadgeBorder, _quickCleanButton }
        };

        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        headerRow.Children.Add(titleStack);
        Grid.SetColumn(titleStack, 0);

        tabsPanel.HorizontalAlignment = HorizontalAlignment.Center;
        headerRow.Children.Add(tabsPanel);
        Grid.SetColumn(tabsPanel, 1);

        headerRow.Children.Add(rightActions);
        Grid.SetColumn(rightActions, 2);

        var headerBorder = new Border
        {
            Background = ModuleTheme.PanelBackground,
            BorderBrush = ModuleTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 14),
            Child = new StackPanel { Children = { headerRow } }
        };

        _scrollViewer.Content = new Border
        {
            Padding = new Thickness(20, 16, 20, 22),
            Child = _pageHost
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        root.Children.Add(headerBorder);
        Grid.SetRow(headerBorder, 0);

        root.Children.Add(_scrollViewer);
        Grid.SetRow(_scrollViewer, 1);
        return root;
    }

    private void SwitchTab(ModuleTab tab)
    {
        _activeTab = tab;
        for (int i = 0; i < _tabButtons.Length; i++)
        {
            bool active = i == (int)tab;
            _tabButtons[i].Background = active ? ModuleTheme.AccentSoftBrush : Brushes.Transparent;
            _tabButtons[i].BorderBrush = active ? ModuleTheme.AccentBrush : ModuleTheme.BorderSubtleBrush;
            _tabButtons[i].Foreground = active ? ModuleTheme.TextPrimary : ModuleTheme.TextMuted;
        }

        _pageHost.Content = tab switch
        {
            ModuleTab.Overview => _overviewView ??= BuildOverviewPage(),
            ModuleTab.Processes => _processesView ??= BuildProcessesPage(),
            ModuleTab.Settings => _settingsView ??= BuildSettingsPage(),
            _ => _aboutView ??= BuildAboutPage()
        };

        if (tab == ModuleTab.Settings)
            SyncSettingsControlsFromModel();
        else if (tab == ModuleTab.Processes)
            RenderProcessesList();

        _scrollViewer.Offset = Vector.Zero;
    }

    private Control BuildOverviewPage()
    {
        // Top hero card: Donut gauge + details + clean options
        var gaugeGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 20
        };
        gaugeGrid.Children.Add(_donutGauge);
        Grid.SetColumn(_donutGauge, 0);

        var metricsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 12, 0, 0)
        };
        AddMetricBox(metricsGrid, 0, "Занято", _metricUsed);
        AddMetricBox(metricsGrid, 1, "Доступно", _metricAvailable);
        AddMetricBox(metricsGrid, 2, OperatingSystem.IsLinux() ? "Swap" : "Файл подкачки", _metricPageFile);
        AddMetricBox(metricsGrid, 3, "Процессов", _metricProcessCount);

        var infoStack = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _overviewRamHeadline,
                _overviewRamSubline,
                metricsGrid,
                new Border
                {
                    Background = ModuleTheme.PanelBackground,
                    BorderBrush = ModuleTheme.BorderSubtleBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7),
                    Margin = new Thickness(0, 8, 0, 0),
                    Child = _statusBanner
                }
            }
        };
        gaugeGrid.Children.Add(infoStack);
        Grid.SetColumn(infoStack, 1);

        var heroCard = ModuleTheme.CreateCard(gaugeGrid);

        // Chart + Cleanup Options row
        var chartCard = ModuleTheme.CreateCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                ModuleTheme.CreateSectionTitle("Динамика использования ОЗУ"),
                _historyChart
            }
        });

        Control optionsCard;
        if (OperatingSystem.IsWindows())
        {
            var clearHistoryBtn = ModuleTheme.CreateCompactButton("Очистить журнал");
            clearHistoryBtn.Click += async (_, _) => await _controller.ClearHistoryAsync();

            optionsCard = ModuleTheme.CreateCard(new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    ModuleTheme.CreateSectionTitle("Режим очистки"),
                    ModuleTheme.CreateMutedLabel("Очистка запускается через отдельный защищённый helper по запросу.", 12),
                    _optStandby,
                    _optWorkingSets,
                    _optModified,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(0, 4, 0, 0),
                        Children = { clearHistoryBtn }
                    }
                }
            });
        }
        else
        {
            var clearHistoryBtn = ModuleTheme.CreateCompactButton("Очистить журнал");
            clearHistoryBtn.Click += async (_, _) => await _controller.ClearHistoryAsync();

            optionsCard = ModuleTheme.CreateCard(new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    ModuleTheme.CreateSectionTitle("Сброс кэша ядра Linux"),
                    ModuleTheme.CreateMutedLabel("Синхронизирует ФС (sync) и очищает page/dentry/inode кэш (/proc/sys/vm/drop_caches) через pkexec.", 12.5),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(0, 4, 0, 0),
                        Children = { clearHistoryBtn }
                    }
                }
            });
        }

        var middleGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.45*,*"),
            ColumnSpacing = 14
        };
        middleGrid.Children.Add(chartCard);
        Grid.SetColumn(chartCard, 0);
        middleGrid.Children.Add(optionsCard);
        Grid.SetColumn(optionsCard, 1);

        // Bottom row: Top 5 consumers + History
        var topProcsCard = ModuleTheme.CreateCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                ModuleTheme.CreateSectionTitle("Топ потребителей памяти"),
                _topProcessRows
            }
        });

        var historyCard = ModuleTheme.CreateCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                ModuleTheme.CreateSectionTitle("Журнал очисток"),
                _historyRows
            }
        });

        var bottomGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 14
        };
        bottomGrid.Children.Add(topProcsCard);
        Grid.SetColumn(topProcsCard, 0);
        bottomGrid.Children.Add(historyCard);
        Grid.SetColumn(historyCard, 1);

        return new StackPanel
        {
            Spacing = 14,
            Children = { heroCard, middleGrid, bottomGrid }
        };
    }

    private Control BuildProcessesPage()
    {
        var refreshBtn = ModuleTheme.CreateSecondaryButton("Обновить");
        refreshBtn.Click += async (_, _) => await _controller.RefreshOnceAsync();

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 10
        };
        toolbar.Children.Add(_searchBox);
        Grid.SetColumn(_searchBox, 0);
        toolbar.Children.Add(_sortCombo);
        Grid.SetColumn(_sortCombo, 1);
        toolbar.Children.Add(_processSummaryLabel);
        _processSummaryLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_processSummaryLabel, 2);
        toolbar.Children.Add(refreshBtn);
        Grid.SetColumn(refreshBtn, 3);

        var pager = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _prevPageButton, _pageLabel, _nextPageButton }
        };

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                ModuleTheme.CreateCard(toolbar, new Thickness(12)),
                _processListPanel,
                pager
            }
        };
    }

    private Control BuildSettingsPage()
    {
        var saveWhitelistBtn = ModuleTheme.CreateSecondaryButton("Сохранить список исключений");
        saveWhitelistBtn.Click += async (_, _) => await SaveWhitelistAsync();

        var resetBtn = ModuleTheme.CreateCompactButton("Сбросить по умолчанию");
        resetBtn.Click += async (_, _) => await _controller.ResetSettingsAsync();

        var monitoringCard = ModuleTheme.CreateCard(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                ModuleTheme.CreateSectionTitle("Мониторинг"),
                CreateSettingRow(
                    "Интервал обновления",
                    "Частота опроса памяти и списка процессов.",
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children = { _refreshSlider, _refreshValueLabel }
                    }),
                CreateSettingRow(
                    "Не запускать автоочистку в полноэкранном режиме",
                    "Пропускает автоматическую очистку во время игр и полноэкранных приложений.",
                    _fullscreenToggle)
            }
        });

        string autoCleanNote = OperatingSystem.IsWindows()
            ? "Срабатывает автоматически только когда приложение запущено с правами администратора; в обычном режиме без UAC используется ручная очистка."
            : "На Linux запускает фоновый helper через pkexec, который автоматически сбрасывает кэш при превышении порога.";

        var autoCleanCard = ModuleTheme.CreateCard(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                ModuleTheme.CreateSectionTitle("Автоматическая очистка"),
                ModuleTheme.CreateMutedLabel(autoCleanNote, 12),
                CreateSettingRow(
                    "Включить автоочистку",
                    "Автоматический запуск очистки при высокой нагрузке ОЗУ.",
                    _autoCleanToggle),
                CreateSettingRow(
                    "Порог срабатывания ОЗУ",
                    "Минимальный процент занятой памяти для запуска автоочистки.",
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children = { _thresholdSlider, _thresholdValueLabel }
                    }),
                CreateSettingRow(
                    "Пауза между срабатываниями",
                    "Минимальный интервал между автоматическими очистками.",
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children = { _cooldownSlider, _cooldownValueLabel }
                    })
            }
        });

        var whitelistCard = ModuleTheme.CreateCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                ModuleTheme.CreateSectionTitle("Белый список процессов (исключения)"),
                ModuleTheme.CreateMutedLabel("Имена процессов через запятую или с новой строки. Эти процессы не сжимаются при очистке Working Sets.", 12),
                _whitelistBox,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { saveWhitelistBtn, resetBtn }
                }
            }
        });

        var cards = new StackPanel
        {
            Spacing = 14,
            Children = { monitoringCard, autoCleanCard, whitelistCard }
        };

        if (_controller.StandaloneBridge is { } bridge)
        {
            var standaloneStack = new StackPanel { Spacing = 12 };
            standaloneStack.Children.Add(ModuleTheme.CreateSectionTitle("Интеграция автономной версии"));

            if (bridge.SupportsWindowsStartup)
            {
                standaloneStack.Children.Add(CreateSettingRow(
                    "Запускать вместе с Windows",
                    "Регистрирует автозапуск в реестре текущего пользователя (HKCU).",
                    _startupToggle));
            }

            if (bridge.SupportsTrayAndHotkey)
            {
                standaloneStack.Children.Add(CreateSettingRow(
                    "Сворачивать в системный трей",
                    "При сворачивании окно скрывается в область уведомлений.",
                    _minimizeTrayToggle));
                standaloneStack.Children.Add(CreateSettingRow(
                    "Закрывать в трей вместо выхода",
                    "Кнопка закрытия окна оставляет мониторинг в трее.",
                    _closeTrayToggle));

                if (OperatingSystem.IsWindows())
                {
                    standaloneStack.Children.Add(CreateSettingRow(
                        "Глобальная клавиша Ctrl+Alt+M",
                        "Быстрый вызов или скрытие окна MemPulse из любого приложения.",
                        _hotkeyToggle));
                }
            }

            if (bridge.SupportsSelfUpdate)
            {
                standaloneStack.Children.Add(CreateSettingRow(
                    "Проверять обновления на GitHub",
                    "Проверяет наличие новых релизов MemPulse при запуске и по кнопке.",
                    _checkUpdatesToggle));
            }

            cards.Children.Add(ModuleTheme.CreateCard(standaloneStack));
        }

        return cards;
    }

    private Control BuildAboutPage()
    {
        var version = typeof(MemPulseModule).Assembly.GetName().Version?.ToString(3) ?? "0.2.42";
        var stack = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"MemPulse v{version}",
                    FontSize = 18,
                    FontWeight = FontWeight.Bold,
                    Foreground = ModuleTheme.TextPrimary
                },
                ModuleTheme.CreateMutedLabel(
                    "Кроссплатформенный модуль и автономный инструмент контроля оперативной памяти для Windows и Linux на Avalonia 12.",
                    13),
                ModuleTheme.CreateMutedLabel($"Идентификатор модуля: {_controller.Context.ModuleId}", 12),
                ModuleTheme.CreateMutedLabel($"Версия контракта PCPulse: {_controller.Context.ContractVersion}", 12),
                ModuleTheme.CreateMutedLabel($"Каталог данных модуля: {_controller.Context.ModuleDataDirectory}", 12),
                ModuleTheme.CreateMutedLabel($"Повышенные права процесса: {(_controller.Context.Elevation.IsElevated ? "Да" : "Нет (используется helper по запросу)")}", 12)
            }
        };

        if (_controller.StandaloneBridge is { } bridge)
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Margin = new Thickness(0, 4, 0, 0)
            };

            if (bridge.SupportsSelfUpdate)
            {
                var checkUpdatesBtn = ModuleTheme.CreatePrimaryButton("Проверить обновления");
                checkUpdatesBtn.Click += async (_, _) => await bridge.CheckForUpdatesAsync();
                buttons.Children.Add(checkUpdatesBtn);
            }

            var logsBtn = ModuleTheme.CreateSecondaryButton("Открыть папку логов");
            logsBtn.Click += async (_, _) => await bridge.OpenLogsDirectoryAsync();
            buttons.Children.Add(logsBtn);

            stack.Children.Add(buttons);
        }

        return ModuleTheme.CreateCard(stack);
    }

    private void WireSettingsEvents()
    {
        _refreshSlider.PropertyChanged += async (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                int sec = (int)Math.Round(_refreshSlider.Value);
                _refreshValueLabel.Text = $"{sec} с";
                if (!_suppressSettingsEvents)
                    await UpdateSettingAsync(s => s.RefreshSeconds = sec, restartLinuxMonitor: false);
            }
        };

        _thresholdSlider.PropertyChanged += async (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                int threshold = (int)Math.Round(_thresholdSlider.Value);
                _thresholdValueLabel.Text = $"{threshold}%";
                if (!_suppressSettingsEvents)
                    await UpdateSettingAsync(s => s.AutoCleanThreshold = threshold, restartLinuxMonitor: true);
            }
        };

        _cooldownSlider.PropertyChanged += async (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                int minutes = (int)Math.Round(_cooldownSlider.Value);
                _cooldownValueLabel.Text = $"{minutes} мин";
                if (!_suppressSettingsEvents)
                    await UpdateSettingAsync(s => s.AutoCleanCooldownMinutes = minutes, restartLinuxMonitor: true);
            }
        };

        _autoCleanToggle.IsCheckedChanged += async (_, _) =>
        {
            if (!_suppressSettingsEvents)
                await UpdateSettingAsync(s => s.AutoClean = _autoCleanToggle.IsChecked == true, restartLinuxMonitor: true);
        };

        _fullscreenToggle.IsCheckedChanged += async (_, _) =>
        {
            if (!_suppressSettingsEvents)
                await UpdateSettingAsync(s => s.SkipAutoCleanInFullscreen = _fullscreenToggle.IsChecked == true, restartLinuxMonitor: false);
        };

        _startupToggle.IsCheckedChanged += async (_, _) =>
        {
            if (!_suppressSettingsEvents)
                await UpdateSettingAsync(s => s.StartWithWindows = _startupToggle.IsChecked == true, restartLinuxMonitor: false);
        };

        _minimizeTrayToggle.IsCheckedChanged += async (_, _) =>
        {
            if (!_suppressSettingsEvents)
                await UpdateSettingAsync(s => s.MinimizeToTray = _minimizeTrayToggle.IsChecked == true, restartLinuxMonitor: false);
        };

        _closeTrayToggle.IsCheckedChanged += async (_, _) =>
        {
            if (!_suppressSettingsEvents)
                await UpdateSettingAsync(s => s.CloseToTray = _closeTrayToggle.IsChecked == true, restartLinuxMonitor: false);
        };

        _hotkeyToggle.IsCheckedChanged += async (_, _) =>
        {
            if (!_suppressSettingsEvents)
                await UpdateSettingAsync(s => s.GlobalHotkeyEnabled = _hotkeyToggle.IsChecked == true, restartLinuxMonitor: false);
        };

        _checkUpdatesToggle.IsCheckedChanged += async (_, _) =>
        {
            if (!_suppressSettingsEvents)
                await UpdateSettingAsync(s => s.CheckForUpdates = _checkUpdatesToggle.IsChecked == true, restartLinuxMonitor: false);
        };
    }

    private void SyncSettingsControlsFromModel()
    {
        _suppressSettingsEvents = true;
        try
        {
            var s = _controller.Settings;
            _refreshSlider.Value = s.RefreshSeconds;
            _refreshValueLabel.Text = $"{s.RefreshSeconds} с";
            _autoCleanToggle.IsChecked = s.AutoClean;
            _thresholdSlider.Value = s.AutoCleanThreshold;
            _thresholdValueLabel.Text = $"{s.AutoCleanThreshold}%";
            _cooldownSlider.Value = s.AutoCleanCooldownMinutes;
            _cooldownValueLabel.Text = $"{s.AutoCleanCooldownMinutes} мин";
            _fullscreenToggle.IsChecked = s.SkipAutoCleanInFullscreen;
            _whitelistBox.Text = ProcessExclusion.FormatList(s.ExcludedProcesses);

            _startupToggle.IsChecked = s.StartWithWindows;
            _minimizeTrayToggle.IsChecked = s.MinimizeToTray;
            _closeTrayToggle.IsChecked = s.CloseToTray;
            _hotkeyToggle.IsChecked = s.GlobalHotkeyEnabled;
            _checkUpdatesToggle.IsChecked = s.CheckForUpdates;
        }
        finally
        {
            _suppressSettingsEvents = false;
        }
    }

    private async Task UpdateSettingAsync(Action<AppSettings> mutate, bool restartLinuxMonitor)
    {
        var copy = _controller.Settings.Copy();
        mutate(copy);
        await _controller.SaveSettingsAsync(copy, restartLinuxMonitor);
    }

    private async Task SaveWhitelistAsync()
    {
        var copy = _controller.Settings.Copy();
        copy.ExcludedProcesses = ProcessExclusion.ParseList(_whitelistBox.Text);
        await _controller.SaveSettingsAsync(copy, restartLinuxMonitorIfNeeded: false);
        await _controller.Context.Dialogs.ShowInfoAsync(
            "Исключения сохранены",
            copy.ExcludedProcesses.Count > 0
                ? $"Исключённые процессы ({copy.ExcludedProcesses.Count}): {ProcessExclusion.FormatList(copy.ExcludedProcesses)}"
                : "Белый список процессов очищен.");
    }

    private async Task ExecuteCleanupAsync()
    {
        var options = OperatingSystem.IsWindows()
            ? new CleanupOptions(
                Standby: _optStandby.IsChecked == true,
                WorkingSets: _optWorkingSets.IsChecked == true,
                ModifiedPages: _optModified.IsChecked == true)
            : new CleanupOptions(Standby: true, WorkingSets: false, ModifiedPages: false);

        if (OperatingSystem.IsWindows() && !options.Standby && !options.WorkingSets && !options.ModifiedPages)
        {
            await _controller.Context.Dialogs.ShowInfoAsync(
                "Ничего не выбрано",
                "Отметьте хотя бы один тип памяти для очистки.");
            return;
        }

        await _controller.RunCleanupAsync(options, automatic: false);
    }

    private void OnControllerStateChanged()
    {
        RefreshAllViews();
    }

    private void RefreshAllViews()
    {
        var mem = _controller.LatestMemory;
        var groups = _controller.LatestGroups;

        _quickCleanButton.IsEnabled = !_controller.IsCleaning;
        _quickCleanButton.Content = _controller.IsCleaning ? "Очистка..." : "Очистить память";

        if (mem.Total > 0)
        {
            _headerStatusBadge.Text = $"ОЗУ: {mem.UsedPercent:F0}% · {Format.Bytes(mem.Used)} / {Format.Bytes(mem.Total)}";
            _headerStatusBadge.Foreground = ModuleTheme.SeverityBrush(mem.UsedPercent);
            _donutGauge.Update(mem.UsedPercent, $"{Format.Bytes(mem.Used)} из {Format.Bytes(mem.Total)}");

            _overviewRamHeadline.Text = $"Занято {Format.Bytes(mem.Used)} из {Format.Bytes(mem.Total)} ({mem.UsedPercent:F1}%)";
            _overviewRamSubline.Text = $"Свободно {Format.Bytes(mem.Available)} · пик истории {(_controller.RamHistorySamples.Count > 0 ? _controller.RamHistorySamples.Max() : mem.UsedPercent):F0}%";
            _metricUsed.Text = Format.Bytes(mem.Used);
            _metricAvailable.Text = Format.Bytes(mem.Available);
            if (OperatingSystem.IsLinux())
            {
                _metricPageFile.Text = mem.SwapTotal > 0
                    ? $"{Format.Bytes(mem.SwapUsed)} / {Format.Bytes(mem.SwapTotal)}"
                    : "Откл. / 0 Б";
            }
            else
            {
                _metricPageFile.Text = mem.CommitLimit > 0
                    ? $"{Format.Bytes(mem.Committed)} / {Format.Bytes(mem.CommitLimit)}"
                    : "Откл. / 0 Б";
            }
        }
        else
        {
            _headerStatusBadge.Text = "Замер памяти...";
            _donutGauge.Update(0, "Ожидание");
            _overviewRamHeadline.Text = "Загрузка данных памяти...";
        }

        int totalProcessCount = groups.Sum(g => g.Count);
        _metricProcessCount.Text = $"{totalProcessCount} ({groups.Count} групп)";
        _statusBanner.Text = _controller.StatusBannerText;

        _historyChart.Update(_controller.RamHistorySamples, mem.UsedPercent);
        RenderTopProcesses(groups);
        RenderHistoryEntries(_controller.HistoryEntries);

        if (_activeTab == ModuleTab.Processes)
            RenderProcessesList();
    }

    private void RenderTopProcesses(IReadOnlyList<ProcessGroupUsage> groups)
    {
        _topProcessRows.Children.Clear();
        if (groups.Count == 0)
        {
            _topProcessRows.Children.Add(ModuleTheme.CreateMutedLabel("Список процессов загружается..."));
            return;
        }

        foreach (var group in groups.Take(5))
        {
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = group.RelativeLoad,
                Height = 5,
                Foreground = ModuleTheme.AccentBrush,
                Background = ModuleTheme.TrackBrush
            };

            var rowHeader = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 8
            };

            var icon = CreateGroupIconControl(group, 20);
            rowHeader.Children.Add(icon);
            Grid.SetColumn(icon, 0);

            var nameText = new TextBlock
            {
                Text = group.DisplayName,
                FontSize = 13,
                FontWeight = FontWeight.Medium,
                Foreground = ModuleTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            rowHeader.Children.Add(nameText);
            Grid.SetColumn(nameText, 1);

            var memText = new TextBlock
            {
                Text = group.WorkingText,
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = ModuleTheme.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            rowHeader.Children.Add(memText);
            Grid.SetColumn(memText, 2);

            _topProcessRows.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children = { rowHeader, bar }
            });
        }
    }

    private void RenderHistoryEntries(IReadOnlyList<CleanupHistoryEntry> entries)
    {
        _historyRows.Children.Clear();
        if (entries.Count == 0)
        {
            _historyRows.Children.Add(ModuleTheme.CreateMutedLabel("Очисток в истории пока нет."));
            return;
        }

        foreach (var entry in entries.Take(6))
        {
            var badgeBrush = entry.Success ? ModuleTheme.SuccessBrush : ModuleTheme.DangerBrush;
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8
            };

            var left = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{entry.Timestamp.LocalDateTime:dd.MM HH:mm:ss} · {entry.Reason}",
                        FontSize = 12.5,
                        FontWeight = FontWeight.Medium,
                        Foreground = ModuleTheme.TextPrimary
                    },
                    ModuleTheme.CreateMutedLabel(entry.Details, 11.5)
                }
            };
            grid.Children.Add(left);
            Grid.SetColumn(left, 0);

            var freed = new TextBlock
            {
                Text = entry.FreedText,
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = badgeBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(freed);
            Grid.SetColumn(freed, 1);

            _historyRows.Children.Add(new Border
            {
                Background = ModuleTheme.PanelBackground,
                BorderBrush = ModuleTheme.BorderSubtleBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7),
                Child = grid
            });
        }
    }

    private void RenderProcessesList()
    {
        var sortOrder = _sortCombo.SelectedIndex switch
        {
            1 => ProcessSortOrder.PrivateMemory,
            2 => ProcessSortOrder.Name,
            3 => ProcessSortOrder.ProcessCount,
            _ => ProcessSortOrder.WorkingMemory
        };

        var filtered = ProcessGrouping.Filter(_controller.LatestGroups, _searchBox.Text);
        var sorted = ProcessGrouping.Sort(filtered, sortOrder);

        int totalGroups = sorted.Count;
        int totalPages = Math.Max(1, (totalGroups + PageSize - 1) / PageSize);
        if (_currentPage > totalPages)
            _currentPage = totalPages;

        _prevPageButton.IsEnabled = _currentPage > 1;
        _nextPageButton.IsEnabled = _currentPage < totalPages;
        _pageLabel.Text = $"{_currentPage} / {totalPages}";
        _processSummaryLabel.Text = $"Групп: {totalGroups} · процессов: {sorted.Sum(g => g.Count)}";

        _processListPanel.Children.Clear();
        var pageItems = sorted.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToList();

        if (pageItems.Count == 0)
        {
            _processListPanel.Children.Add(ModuleTheme.CreateCard(
                ModuleTheme.CreateMutedLabel("По вашему запросу процессы не найдены.", 13)));
            return;
        }

        foreach (var group in pageItems)
            _processListPanel.Children.Add(BuildProcessGroupCard(group));
    }

    private Control BuildProcessGroupCard(ProcessGroupUsage group)
    {
        bool isExcluded = ProcessExclusion.IsExcluded(group.Name, _controller.Settings.ExcludedProcesses);
        bool isExpanded = _expandedGroups.Contains(group.Name);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
            ColumnSpacing = 10
        };

        var icon = CreateGroupIconControl(group, 28);
        row.Children.Add(icon);
        Grid.SetColumn(icon, 0);

        var nameStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = isExcluded ? $"{group.DisplayName}  [в исключениях]" : group.DisplayName,
                    FontSize = 13.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = isExcluded ? ModuleTheme.SuccessBrush : ModuleTheme.TextPrimary
                },
                ModuleTheme.CreateMutedLabel($"PID: {group.PidsText}", 11.5)
            }
        };
        row.Children.Add(nameStack);
        Grid.SetColumn(nameStack, 1);

        var memStack = new StackPanel
        {
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = group.WorkingText,
                    FontSize = 13.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = ModuleTheme.AccentBrush,
                    HorizontalAlignment = HorizontalAlignment.Right
                },
                ModuleTheme.CreateMutedLabel($"приват.: {group.PrivateText}", 11)
            }
        };
        row.Children.Add(memStack);
        Grid.SetColumn(memStack, 2);

        var whitelistBtn = ModuleTheme.CreateCompactButton(isExcluded ? "Убрать из искл." : "В исключения");
        whitelistBtn.Click += async (_, _) => await _controller.ToggleProcessExclusionAsync(group.Name);
        row.Children.Add(whitelistBtn);
        Grid.SetColumn(whitelistBtn, 3);

        var trimTarget = group.Members[0];
        var trimBtn = ModuleTheme.CreateCompactButton("Сжать RAM");
        trimBtn.IsEnabled = !_controller.IsCleaning;
        trimBtn.Click += async (_, _) => await _controller.TrimProcessAsync(trimTarget);
        row.Children.Add(trimBtn);
        Grid.SetColumn(trimBtn, 4);

        var cardStack = new StackPanel { Spacing = 8 };
        cardStack.Children.Add(row);

        if (group.Members.Count > 1)
        {
            var expandToggle = ModuleTheme.CreateCompactButton(isExpanded ? "Скрыть PID ▲" : $"Показать PID ({group.Members.Count}) ▼");
            expandToggle.HorizontalAlignment = HorizontalAlignment.Left;
            expandToggle.Click += (_, _) =>
            {
                if (!_expandedGroups.Add(group.Name))
                    _expandedGroups.Remove(group.Name);
                RenderProcessesList();
            };
            cardStack.Children.Add(expandToggle);

            if (isExpanded)
            {
                var membersPanel = new StackPanel { Spacing = 4, Margin = new Thickness(38, 4, 0, 0) };
                foreach (var member in group.Members)
                {
                    var memberGrid = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                        ColumnSpacing = 10
                    };
                    memberGrid.Children.Add(ModuleTheme.CreateMutedLabel($"PID {member.Pid}", 12));

                    var memberMem = new TextBlock
                    {
                        Text = $"{Format.Bytes(member.WorkingBytes)} · приват. {Format.Bytes(member.PrivateBytes)}",
                        FontSize = 12,
                        Foreground = ModuleTheme.TextSecondary,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    memberGrid.Children.Add(memberMem);
                    Grid.SetColumn(memberMem, 1);

                    var memberTrim = ModuleTheme.CreateCompactButton("Сжать PID");
                    memberTrim.IsEnabled = !_controller.IsCleaning;
                    memberTrim.Click += async (_, _) => await _controller.TrimProcessAsync(member);
                    memberGrid.Children.Add(memberTrim);
                    Grid.SetColumn(memberTrim, 2);

                    membersPanel.Children.Add(memberGrid);
                }
                cardStack.Children.Add(membersPanel);
            }
        }

        return ModuleTheme.CreateCard(cardStack, new Thickness(12, 10));
    }

    private Control CreateGroupIconControl(ProcessGroupUsage group, double size)
    {
        if (group.IconBytes is { Length: > 0 } bytes)
        {
            if (!_iconCache.TryGetValue(group.Name, out var bitmap))
            {
                try
                {
                    using var ms = new MemoryStream(bytes);
                    bitmap = new Bitmap(ms);
                }
                catch
                {
                    bitmap = null;
                }
                _iconCache[group.Name] = bitmap;
            }

            if (bitmap is not null)
            {
                return new Image
                {
                    Source = bitmap,
                    Width = size,
                    Height = size,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 3.0),
            Background = ModuleTheme.AccentSoftBrush,
            BorderBrush = ModuleTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = group.Monogram,
                FontSize = Math.Max(10, size * 0.45),
                FontWeight = FontWeight.Bold,
                Foreground = ModuleTheme.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static Control CreateSettingRow(string title, string description, Control editor)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 16
        };

        var labels = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeight.Medium, Foreground = ModuleTheme.TextPrimary },
                ModuleTheme.CreateMutedLabel(description, 12)
            }
        };
        grid.Children.Add(labels);
        Grid.SetColumn(labels, 0);

        editor.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(editor);
        Grid.SetColumn(editor, 1);

        return new Border
        {
            Background = ModuleTheme.PanelBackground,
            BorderBrush = ModuleTheme.BorderSubtleBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 10),
            Child = grid
        };
    }

    private static void AddMetricBox(Grid grid, int col, string label, TextBlock valueControl)
    {
        var box = new Border
        {
            Background = ModuleTheme.PanelBackground,
            BorderBrush = ModuleTheme.BorderSubtleBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    ModuleTheme.CreateMutedLabel(label, 11),
                    valueControl
                }
            }
        };
        grid.Children.Add(box);
        Grid.SetColumn(box, col);
    }

    private void OnViewUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Note: In PCPulse single-view navigation, switching modules detaches and reattaches the cached view.
        // Do not unsubscribe controller state permanently on tab/module switches; icons remain lightweight.
    }

    internal void DetachForDispose()
    {
        _controller.StateChanged -= OnControllerStateChanged;
        Unloaded -= OnViewUnloaded;
        foreach (var bitmap in _iconCache.Values)
            bitmap?.Dispose();
        _iconCache.Clear();
    }
}
