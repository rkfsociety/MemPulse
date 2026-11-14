using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using MemPulse.Core;
using Forms = System.Windows.Forms;

using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using DataGrid = System.Windows.Controls.DataGrid;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Window = System.Windows.Window;
using MessageBox = System.Windows.MessageBox;

namespace MemPulse;

public partial class MainWindow : Window
{
    private readonly MonitoringService _monitor = new();
    private readonly ProcessIconCache _processIcons = new();
    private readonly CleanupService _cleaner = new();
    private readonly SettingsStore _store = new(onLoadError: StartupLog.Warn);
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private AvailableUpdate? _availableUpdate;
    private TaskCompletionSource<bool>? _confirmation;
    private bool _checkingUpdates;
    private bool _installingUpdate;
    private readonly ProcessSnapshotCollection<ProcessGroupUsage> _groups = new(ProcessGrouping.SnapshotComparer);
    private readonly ListCollectionView _overviewView;
    private readonly ListCollectionView _detailView;
    private enum Section { Overview, Processes, Settings, About }
    private Section _activeSection;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _overviewSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _detailSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private int _lastProcessCount;
    private int _processSnapshotRevision;
    private AppSettings _settings = new();
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private int? _lastTrayPercent;
    private readonly List<double> _memoryHistory = [];
    private bool _refreshing;
    private bool _processesStale;
    private bool _cleaning;
    private bool _forceExit;
    private DateTime _lastAutoCleanUtc = DateTime.MinValue;
    private long? _lastAutoCleanFreedBytes;
    private ulong _lastUsedBytes;
    private readonly CleanupHistoryLog _cleanupHistory = new();
    private HwndSource? _hwndSource;
    private bool _hotkeyRegistered;
    private const int HotkeyId = 0x4D50;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkM = 0x4D;
    private bool _syncingGroupSort;

    private sealed record GroupSortOption(string Label, string MemberPath, ListSortDirection Direction)
    {
        public override string ToString() => Label;
    }

    private static readonly GroupSortOption[] GroupSortOptions =
    [
        new("ОЗУ · по убыванию", nameof(ProcessGroupUsage.WorkingBytes), ListSortDirection.Descending),
        new("ОЗУ · по возрастанию", nameof(ProcessGroupUsage.WorkingBytes), ListSortDirection.Ascending),
        new("Частная · по убыванию", nameof(ProcessGroupUsage.PrivateBytes), ListSortDirection.Descending),
        new("Частная · по возрастанию", nameof(ProcessGroupUsage.PrivateBytes), ListSortDirection.Ascending),
        new("Процессов · по убыванию", nameof(ProcessGroupUsage.Count), ListSortDirection.Descending),
        new("Процессов · по возрастанию", nameof(ProcessGroupUsage.Count), ListSortDirection.Ascending),
        new("Имя · А–Я", nameof(ProcessGroupUsage.Name), ListSortDirection.Ascending),
        new("Имя · Я–А", nameof(ProcessGroupUsage.Name), ListSortDirection.Descending)
    ];

    public MainWindow()
    {
        InitializeComponent();
        _overviewView = new ListCollectionView(_groups);
        _detailView = new ListCollectionView(_groups);
        _overviewView.Filter = FilterProcess;
        _detailView.Filter = FilterGroup;
        ProcessGrid.ItemsSource = _overviewView;
        GroupGrid.ItemsSource = _detailView;
        GroupSortCombo.ItemsSource = GroupSortOptions;
        GroupSortCombo.DisplayMemberPath = nameof(GroupSortOption.Label);
        ApplyGridSort(ProcessGrid, _overviewView, nameof(ProcessGroupUsage.WorkingBytes), ListSortDirection.Descending);
        ApplyGridSort(GroupGrid, _detailView, nameof(ProcessGroupUsage.WorkingBytes), ListSortDirection.Descending);
        ActivateSection(Section.Overview);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(false);
        _overviewSearchTimer.Tick += (_, _) => { _overviewSearchTimer.Stop(); ApplyOverviewSearch(); };
        _detailSearchTimer.Tick += (_, _) => { _detailSearchTimer.Stop(); ApplyDetailSearch(); };
        PreviewKeyDown += (_, e) =>
        {
            if (_confirmation is not null) return;
            if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                ActivateSection(Section.Processes);
                GroupSearchBox.Focus();
                GroupSearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape &&
                     Keyboard.FocusedElement is System.Windows.Controls.TextBox search &&
                     (ReferenceEquals(search, SearchBox) || ReferenceEquals(search, GroupSearchBox)) &&
                     !string.IsNullOrEmpty(search.Text))
            {
                search.Clear();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                _ = RefreshAsync();
                e.Handled = true;
            }
        };
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
        ElevateButton.Visibility = Visibility.Collapsed;
        CleanButton.IsEnabled = true;
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
        else SetStatus("Мониторинг включён · очистка запускается отдельным помощником с запросом UAC");
        await RefreshAsync();
        StartupLog.Info("Главное окно: первый опрос завершён");
        ConfigureUpdateTimer();
        ConfigureGlobalHotkey();
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
            bool includeProcesses = IsVisible && WindowState != WindowState.Minimized;
            var result = await Task.Run(() =>
            {
                var usage = _monitor.ReadMemory();
                if (!includeProcesses)
                    return (Usage: usage, Groups: (IReadOnlyList<ProcessGroupUsage>?)null, TotalProcesses: 0);

                var snapshot = _monitor.ReadProcesses();
                var decorated = _processIcons.Decorate(snapshot);
                return (Usage: usage, Groups: (IReadOnlyList<ProcessGroupUsage>?)ProcessGrouping.Build(decorated), TotalProcesses: snapshot.Count);
            });
            var memory = result.Usage;

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
            PageFileText.Text = memory.SwapTotal > 0
                ? $"Файл подкачки: ~{Format.Bytes(memory.SwapUsed)} / {Format.Bytes(memory.SwapTotal)}"
                : "Файл подкачки: не используется";
            _lastUsedBytes = memory.Used;

            if (result.Groups is not null)
            {
                _processesStale = false;
                ApplyProcessSnapshot(result.Groups, result.TotalProcesses);
            }
            else
            {
                _processesStale = true;
            }
            UpdateText.Text = "Обновлено " + DateTime.Now.ToString("HH:mm:ss");

            if (_settings.AutoClean &&
                AutoCleanupPolicy.ShouldCleanSmart(
                    memory.UsedPercent,
                    _settings.AutoCleanThreshold,
                    _settings.AutoCleanCooldownMinutes,
                    DateTime.UtcNow,
                    _lastAutoCleanUtc,
                    FullscreenDetector.IsForegroundFullscreen(),
                    _settings.SkipAutoCleanInFullscreen,
                    _lastAutoCleanFreedBytes))
            {
                // The automatic mode deliberately excludes more disruptive cleanup operations.
                _lastAutoCleanUtc = DateTime.UtcNow;
                await RunCleanupAsync(new CleanupOptions(true, false, false, _settings.ExcludedProcesses), true, "Авто");
                _lastAutoCleanFreedBytes = _cleanupHistory.Latest?.FreedBytes;
            }
        }
        catch (Exception ex)
        {
            StartupLog.Error("Ошибка обновления данных", ex);
            SetStatus("Ошибка обновления данных: " + ex.Message);
        }
        finally { _refreshing = false; }
    }

    // The DataGrid template owns the scrolling viewport. Restoring its offset after
    // the collection Reset prevents the dashboard from jumping to the first row
    // on every monitoring tick, while keeping row/column virtualization enabled.
    private static ScrollViewer? GridScrollViewer(DataGrid grid)
    {
        grid.ApplyTemplate();
        return grid.Template.FindName("DG_ScrollViewer", grid) as ScrollViewer;
    }

    private void ApplyProcessSnapshot(IReadOnlyList<ProcessGroupUsage> groups, int processCount)
    {
        var overviewScroll = GridScrollViewer(ProcessGrid);
        var detailScroll = GridScrollViewer(GroupGrid);
        double overviewOffset = overviewScroll?.VerticalOffset ?? 0;
        double detailOffset = detailScroll?.VerticalOffset ?? 0;
        string? overviewSelected = (ProcessGrid.SelectedItem as ProcessGroupUsage)?.Name;
        string? detailSelected = (GroupGrid.SelectedItem as ProcessGroupUsage)?.Name;
        _groups.ReplaceWith(groups);
        if (overviewSelected is not null)
            ProcessGrid.SelectedItem = _groups.FirstOrDefault(g =>
                g.Name.Equals(overviewSelected, StringComparison.OrdinalIgnoreCase));
        if (detailSelected is not null)
            GroupGrid.SelectedItem = _groups.FirstOrDefault(g =>
                g.Name.Equals(detailSelected, StringComparison.OrdinalIgnoreCase));
        _lastProcessCount = processCount;
        UpdateSearchSummaries();
        UpdateTrimButton();

        int revision = ++_processSnapshotRevision;
        if (overviewScroll is not null || detailScroll is not null)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Ignore stale restoration if a newer snapshot has arrived.
                if (revision != _processSnapshotRevision || _forceExit) return;
                overviewScroll?.ScrollToVerticalOffset(overviewOffset);
                detailScroll?.ScrollToVerticalOffset(detailOffset);
            }), DispatcherPriority.Loaded);
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
        if (ClearOverviewSearchButton is not null)
            ClearOverviewSearchButton.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
                ? Visibility.Collapsed : Visibility.Visible;
        _overviewSearchTimer.Stop();
        if (string.IsNullOrWhiteSpace(SearchBox.Text)) ApplyOverviewSearch();
        else _overviewSearchTimer.Start();
    }

    private void GroupSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (GroupSearchPlaceholder is not null)
            GroupSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(GroupSearchBox.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        if (ClearGroupSearchButton is not null)
            ClearGroupSearchButton.Visibility = string.IsNullOrWhiteSpace(GroupSearchBox.Text)
                ? Visibility.Collapsed : Visibility.Visible;
        _detailSearchTimer.Stop();
        if (string.IsNullOrWhiteSpace(GroupSearchBox.Text)) ApplyDetailSearch();
        else _detailSearchTimer.Start();
    }

    private void ApplyOverviewSearch()
    {
        _overviewView?.Refresh();
        UpdateSearchSummaries();
    }

    private void ApplyDetailSearch()
    {
        _detailView?.Refresh();
        UpdateSearchSummaries();
    }

    private void UpdateSearchSummaries()
    {
        if (_overviewView is null || _detailView is null) return;
        string total = $"{RussianText.Applications(_groups.Count)} · {RussianText.Processes(_lastProcessCount)}";
        ProcessCountText.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? total : $"Найдено {_overviewView.Count} из {RussianText.Applications(_groups.Count)}";
        GroupCountText.Text = string.IsNullOrWhiteSpace(GroupSearchBox.Text)
            ? total : $"Найдено {_detailView.Count} из {RussianText.Applications(_groups.Count)}";
    }

    private void ClearOverviewSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void ClearGroupSearch_Click(object sender, RoutedEventArgs e)
    {
        GroupSearchBox.Clear();
        GroupSearchBox.Focus();
    }

    private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateTrimButton();

    private void ApplyGridSort(DataGrid grid, ListCollectionView view, string memberPath, ListSortDirection direction)
    {
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(memberPath, direction));
        if (!string.Equals(memberPath, nameof(ProcessGroupUsage.WorkingBytes), StringComparison.Ordinal))
            view.SortDescriptions.Add(new SortDescription(nameof(ProcessGroupUsage.WorkingBytes), ListSortDirection.Descending));
        if (!string.Equals(memberPath, nameof(ProcessGroupUsage.Name), StringComparison.Ordinal))
            view.SortDescriptions.Add(new SortDescription(nameof(ProcessGroupUsage.Name), ListSortDirection.Ascending));

        foreach (var column in grid.Columns)
            column.SortDirection = string.Equals(column.SortMemberPath, memberPath, StringComparison.Ordinal)
                ? direction : null;

        if (ReferenceEquals(grid, GroupGrid))
        {
            _syncingGroupSort = true;
            try
            {
                GroupSortCombo.SelectedItem = GroupSortOptions.FirstOrDefault(o =>
                    string.Equals(o.MemberPath, memberPath, StringComparison.Ordinal) && o.Direction == direction);
            }
            finally { _syncingGroupSort = false; }
        }
    }

    private void HandleGridSorting(DataGrid grid, ListCollectionView view, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string? memberPath = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(memberPath)) return;

        var nextDirection = e.Column.SortDirection switch
        {
            ListSortDirection.Descending => ListSortDirection.Ascending,
            ListSortDirection.Ascending => ListSortDirection.Descending,
            _ => string.Equals(memberPath, nameof(ProcessGroupUsage.Name), StringComparison.Ordinal)
                ? ListSortDirection.Ascending
                : ListSortDirection.Descending
        };
        ApplyGridSort(grid, view, memberPath, nextDirection);
    }

    private void ProcessGrid_Sorting(object sender, DataGridSortingEventArgs e) =>
        HandleGridSorting(ProcessGrid, _overviewView, e);

    private void GroupGrid_Sorting(object sender, DataGridSortingEventArgs e) =>
        HandleGridSorting(GroupGrid, _detailView, e);

    private void GroupSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGroupSort || GroupSortCombo.SelectedItem is not GroupSortOption option) return;
        ApplyGridSort(GroupGrid, _detailView, option.MemberPath, option.Direction);
    }

    private static DependencyObject? GetParentElement(DependencyObject node) =>
        node is Visual or Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    private void GroupGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        var current = e.OriginalSource as DependencyObject;
        while (current is not null && !ReferenceEquals(current, GroupGrid))
        {
            // Keep interactions inside expanded details, headers, scrollbars, and buttons intact.
            if (current is DataGridDetailsPresenter or DataGridColumnHeader or ScrollBar or ButtonBase)
                return;
            if (current is DataGridRow row)
            {
                if (row.IsSelected)
                {
                    GroupGrid.SelectedItem = null;
                    e.Handled = true;
                }
                return;
            }
            current = GetParentElement(current);
        }
    }

    private void GroupGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape &&
            GroupGrid.SelectedItem is not null &&
            Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            GroupGrid.SelectedItem = null;
            e.Handled = true;
        }
    }

    private void CollapseGroup_Click(object sender, RoutedEventArgs e) =>
        GroupGrid.SelectedItem = null;

    private void UpdateTrimButton()
    {
        if (TrimButton is not null)
            TrimButton.IsEnabled = !_cleaning &&
                ProcessGrid.SelectedItem is ProcessGroupUsage { Count: 1 };
    }

    // Used by the Windows packaged-EXE startup test to exercise real navigation and grouping.
    internal void VerifyProcessRefreshForSmokeTest()
    {
        foreach (var column in GroupGrid.Columns.OfType<DataGridTemplateColumn>())
        {
            if (column.CellTemplate?.LoadContent() is null)
                throw new InvalidOperationException("Не удалось загрузить шаблон колонки таблицы процессов.");
        }
        if (GroupGrid.RowDetailsTemplate?.LoadContent() is null)
            throw new InvalidOperationException("Не удалось загрузить шаблон раскрытия процессов.");
        if (DetailWorkingColumn.SortDirection != ListSortDirection.Descending ||
            GroupSortCombo.SelectedItem is not GroupSortOption
            {
                MemberPath: nameof(ProcessGroupUsage.WorkingBytes),
                Direction: ListSortDirection.Descending
            })
            throw new InvalidOperationException("Начальная сортировка таблицы процессов не синхронизирована.");

        var first = ProcessGrouping.Build([
            new ProcessUsage(101, "alpha", 4000, 1000),
            new ProcessUsage(102, "ALPHA", 2000, 500),
            new ProcessUsage(103, "beta", 3000, 2000)
        ]);
        ApplyProcessSnapshot(first, 3);
        if (ProcessGrid.Items.Count != 2 || GroupGrid.Items.Count != 2)
            throw new InvalidOperationException("Группировка не отображается в двух разделах.");
        ProcessGrid.SelectedItem = first.Single(g => g.Name == "beta");
        GroupGrid.SelectedItem = first.Single(g => g.Name == "beta");
        var updated = ProcessGrouping.Build([
            new ProcessUsage(101, "alpha", 6000, 2000),
            new ProcessUsage(102, "ALPHA", 3000, 900),
            new ProcessUsage(103, "beta", 1000, 700)
        ]);
        ApplyProcessSnapshot(updated, 3);
        if ((ProcessGrid.SelectedItem as ProcessGroupUsage)?.Name != "beta" ||
            (GroupGrid.SelectedItem as ProcessGroupUsage)?.Name != "beta")
            throw new InvalidOperationException("Выделение процесса потеряно при обновлении списка.");

        CollapseGroup_Click(this, new RoutedEventArgs());
        if (GroupGrid.SelectedItem is not null)
            throw new InvalidOperationException("Сворачивание раскрытой группы процессов не работает.");
        ApplyProcessSnapshot(updated, 3);
        if (GroupGrid.SelectedItem is not null)
            throw new InvalidOperationException("Свёрнутая группа процессов не должна раскрываться после обновления.");

        GroupSortCombo.SelectedItem = GroupSortOptions.Single(o =>
            o.MemberPath == nameof(ProcessGroupUsage.PrivateBytes) && o.Direction == ListSortDirection.Ascending);
        if (DetailPrivateColumn.SortDirection != ListSortDirection.Ascending ||
            DetailWorkingColumn.SortDirection is not null ||
            (GroupGrid.Items[0] as ProcessGroupUsage)?.Name != "beta")
            throw new InvalidOperationException("Сортировка через переключатель не обновила таблицу процессов.");
        GroupSortCombo.SelectedItem = GroupSortOptions[0];

        SearchBox.Text = "102";
        ApplyOverviewSearch();
        if (ClearOverviewSearchButton.Visibility != Visibility.Visible ||
            ProcessCountText.Text != "Найдено 1 из 2 приложения")
            throw new InvalidOperationException("Сброс и количество результатов поиска в обзоре не работают.");
        if (ProcessGrid.Items.Count != 1)
            throw new InvalidOperationException("Поиск по PID в обзоре не работает.");
        GroupSearchBox.Text = "beta";
        ApplyDetailSearch();
        if (ClearGroupSearchButton.Visibility != Visibility.Visible ||
            GroupCountText.Text != "Найдено 1 из 2 приложения")
            throw new InvalidOperationException("Сброс и количество результатов поиска процессов не работают.");
        if (GroupGrid.Items.Count != 1)
            throw new InvalidOperationException("Поиск по приложению не работает.");
        SearchBox.Clear();
        GroupSearchBox.Clear();
        if (ClearOverviewSearchButton.Visibility != Visibility.Collapsed ||
            ClearGroupSearchButton.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Кнопки сброса не скрылись после очистки поиска.");
        if (!ProcessGrid.EnableRowVirtualization || !GroupGrid.EnableRowVirtualization ||
            !ProcessGrid.EnableColumnVirtualization || !GroupGrid.EnableColumnVirtualization ||
            VirtualizingPanel.GetVirtualizationMode(ProcessGrid) != VirtualizationMode.Recycling ||
            VirtualizingPanel.GetVirtualizationMode(GroupGrid) != VirtualizationMode.Recycling)
            throw new InvalidOperationException("Виртуализация таблиц процессов отключена.");
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
            ModifiedCheck.IsChecked == true,
            _settings.ExcludedProcesses);
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

    private async Task RunCleanupAsync(CleanupOptions options, bool automatic, string? reason = null)
    {
        if (_cleaning) return;
        if (automatic && !CleanupService.IsElevated)
        {
            SetStatus("Автоочистка пропущена: требуются права администратора (запустите от имени администратора или через Планировщик задач).");
            return;
        }
        _cleaning = true;
        CleanButton.IsEnabled = false;
        TrimButton.IsEnabled = false;
        try
        {
            ulong usedBefore = _lastUsedBytes;
            if (usedBefore == 0)
            {
                try { usedBefore = (await Task.Run(() => _monitor.ReadMemory())).Used; }
                catch { /* best effort */ }
            }
            var report = await Task.Run(() => _cleaner.Clean(options));
            ulong usedAfter = usedBefore;
            try { usedAfter = (await Task.Run(() => _monitor.ReadMemory())).Used; }
            catch { /* best effort */ }

            string effectiveReason = reason ?? (automatic ? "Авто" : "Вручную");
            _cleanupHistory.Record(new CleanupHistoryEntry(
                DateTimeOffset.Now,
                effectiveReason,
                usedBefore,
                usedAfter,
                report.AnySuccess,
                report.Summary));
            CleanupHistoryText.Text = _cleanupHistory.FormatCompact(3);

            SetStatus((automatic ? "Автоочистка: " : "Очистка: ") + report.Summary);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка очистки: " + ex.Message);
        }
        finally
        {
            _cleaning = false;
            CleanButton.IsEnabled = true;
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
        bool isExcluded = ProcessExclusion.IsExcluded(selected.Name, _settings.ExcludedProcesses);
        string warning = isExcluded
            ? $"Процесс {selected.Name} (PID {selected.Pid}) находится в списке исключений. Всё равно уменьшить его рабочий набор?"
            : "Уменьшить рабочий набор процесса " + selected.Name + " (PID " + selected.Pid +
              ")? Данные могут быть повторно загружены в ОЗУ, а процесс — временно замедлиться.";
        if (!await ConfirmAsync("Очистка памяти процесса", warning, "Очистить PID"))
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
            CleanButton.IsEnabled = true;
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
        HotkeyCheck.IsChecked = _settings.GlobalHotkeyEnabled;
        SkipFullscreenCheck.IsChecked = _settings.SkipAutoCleanInFullscreen;
        ExcludedProcessesText.Text = ProcessExclusion.FormatList(_settings.ExcludedProcesses);
        UpdateAutoCleanElevationHint();
        SettingsStatusText.Text = "Изменения вступят в силу после сохранения.";
    }

    private void AutoCleanCheck_Changed(object sender, RoutedEventArgs e) =>
        UpdateAutoCleanElevationHint();

    private void UpdateAutoCleanElevationHint()
    {
        if (CleanupService.IsElevated)
        {
            AutoCleanElevationHint.Text = "Права администратора активны — автоочистка выполняется тихо без запросов UAC.";
            AutoCleanElevationHint.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            return;
        }

        if (AutoCleanCheck.IsChecked == true)
        {
            AutoCleanElevationHint.Text =
                "Внимание: приложение запущено без прав администратора. Автоочистка в фоне будет пропущена, чтобы не вызывать окна UAC. Запустите MemPulse от имени администратора или настройте задачу с наивысшими правами в Планировщике задач.";
            AutoCleanElevationHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
        }
        else
        {
            AutoCleanElevationHint.Text =
                "Автоочистка выполняется только с правами администратора (запуск от имени администратора или Планировщик задач).";
            AutoCleanElevationHint.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        }
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
            CheckForUpdates = UpdateCheck.IsChecked == true,
            GlobalHotkeyEnabled = HotkeyCheck.IsChecked == true,
            SkipAutoCleanInFullscreen = SkipFullscreenCheck.IsChecked == true,
            ExcludedProcesses = ProcessExclusion.ParseList(ExcludedProcessesText.Text)
        };
        try
        {
            StartupService.SetEnabled(next.StartWithWindows);
            _store.Save(next);
            _settings = next;
            ConfigureTimer();
            ConfigureUpdateTimer();
            ConfigureGlobalHotkey();
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
                // `dotnet <assembly>.dll` puts the entry assembly first in the
                // command line. Assembly.Location is empty in single-file builds
                // (IL3000), and this branch is never reached there.
                var dll = System.IO.Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
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
        menu.Items.Add("Очистить сейчас", null, (_, _) => Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                await RunCleanupAsync(BuildSelectedCleanupOptions(fallbackToStandby: true), false, "Трей");
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
        if (_processesStale) _ = RefreshAsync();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray && _tray is not null)
            Hide();
        else if (WindowState != WindowState.Minimized && IsVisible && _processesStale)
            _ = RefreshAsync();
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
        _overviewSearchTimer.Stop();
        _detailSearchTimer.Stop();
        UnregisterGlobalHotkey();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
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

    private CleanupOptions BuildSelectedCleanupOptions(bool fallbackToStandby = false)
    {
        bool standby = StandbyCheck.IsChecked == true;
        bool working = WorkingCheck.IsChecked == true;
        bool modified = ModifiedCheck.IsChecked == true;
        if (fallbackToStandby && !standby && !working && !modified)
            standby = true;
        return new CleanupOptions(standby, working, modified, _settings.ExcludedProcesses);
    }

    private void ConfigureGlobalHotkey()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        if (_hwndSource is null)
        {
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);
        }

        UnregisterGlobalHotkey();
        if (!_settings.GlobalHotkeyEnabled)
            return;

        _hotkeyRegistered = Native.RegisterHotKey(handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkM);
        if (!_hotkeyRegistered)
            StartupLog.Warn("Не удалось зарегистрировать глобальную горячую клавишу Ctrl+Alt+M (возможно, занята другим приложением).");
    }

    private void UnregisterGlobalHotkey()
    {
        if (!_hotkeyRegistered) return;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            Native.UnregisterHotKey(handle, HotkeyId);
        _hotkeyRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await RunCleanupAsync(BuildSelectedCleanupOptions(fallbackToStandby: true), false, "Хоткей");
                await RefreshAsync();
            }));
        }
        return IntPtr.Zero;
    }

    private void ProcessGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        UpdateExcludeMenuItem(ProcessGrid.SelectedItem as ProcessGroupUsage, OverviewExcludeMenuItem, e);

    private void GroupGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e) =>
        UpdateExcludeMenuItem(GroupGrid.SelectedItem as ProcessGroupUsage, DetailExcludeMenuItem, e);

    private void UpdateExcludeMenuItem(ProcessGroupUsage? group, System.Windows.Controls.MenuItem item, ContextMenuEventArgs e)
    {
        if (group is null)
        {
            e.Handled = true;
            return;
        }
        bool excluded = ProcessExclusion.IsExcluded(group.Name, _settings.ExcludedProcesses);
        item.Header = excluded
            ? $"Убрать «{group.DisplayName}» из исключений"
            : $"Добавить «{group.DisplayName}» в исключения";
    }

    private void ToggleExcludeOverview_Click(object sender, RoutedEventArgs e) =>
        ToggleProcessExclusion(ProcessGrid.SelectedItem as ProcessGroupUsage);

    private void ToggleExcludeDetail_Click(object sender, RoutedEventArgs e) =>
        ToggleProcessExclusion(GroupGrid.SelectedItem as ProcessGroupUsage);

    private void ToggleProcessExclusion(ProcessGroupUsage? group)
    {
        if (group is null) return;
        string normalized = ProcessExclusion.NormalizeName(group.Name);
        if (normalized.Length == 0) return;

        var list = ProcessExclusion.NormalizeList(_settings.ExcludedProcesses);
        bool removed = list.RemoveAll(item => item.Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
            list.Add(normalized);

        var next = _settings.Copy();
        next.ExcludedProcesses = list;
        try
        {
            _store.Save(next);
            _settings = next;
            ExcludedProcessesText.Text = ProcessExclusion.FormatList(_settings.ExcludedProcesses);
            SetStatus(removed
                ? $"Процесс «{normalized}» удалён из исключений."
                : $"Процесс «{normalized}» добавлен в исключения очистки.");
        }
        catch (Exception ex)
        {
            StartupLog.Error("Не удалось обновить исключения процессов", ex);
            SetStatus("Ошибка сохранения исключений: " + ex.Message);
        }
    }
}
