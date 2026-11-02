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
    private readonly TextBlock _usagePercent = new() { Foreground = Highlight, FontSize = 25, FontWeight = FontWeight.Bold };
    private readonly TextBlock _processCount = new() { Foreground = Muted, FontSize = 12 };
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _usageBar = new() { Minimum = 0, Maximum = 100, Height = 8, Foreground = Highlight, Background = Track };
    private readonly TextBox _filter = new() { Watermark = "Поиск имени или PID", Width = 240 };
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
    private readonly NumericUpDown _threshold = new()
    {
        Minimum = 70, Maximum = 98, Increment = 1, Value = 90, FormatString = "0", Width = 72
    };
    private readonly NumericUpDown _cooldown = new()
    {
        Minimum = 5, Maximum = 120, Increment = 1, Value = 15, FormatString = "0", Width = 72
    };
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
        _threshold.Value = _settings.AutoCleanThreshold;
        _cooldown.Value = _settings.AutoCleanCooldownMinutes;
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
        Grid.SetColumn(refresh, 1);
        titleLine.Children.Add(refresh);
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
                }
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
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _filterDebounce.Stop();
            _autoSession?.Dispose();
        };
        Refresh();
        _timer.Start();
        // A remembered enabled setting is re-authorized for every GUI session.
        if (_settings.AutoClean &&
            !Environment.GetCommandLineArgs().Contains("--startup-smoke-test", StringComparer.Ordinal))
            Dispatcher.UIThread.Post(async () => await ApplyAutoSettingsAsync(), DispatcherPriority.Background);
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
            ? $"{_groups.Count} приложений · {_processTotal} процессов"
            : $"Найдено {matches.Count} из {_groups.Count} приложений";
        int visible = Math.Min(matches.Count, _visibleGroupLimit);
        if (matches.Count > visible)
            _processCount.Text += $" · показано {visible}";

        // Refreshing live process numbers must not throw the reader back to the top.
        var previousOffset = _processScroll.Offset;
        foreach (var old in _rows.Children.OfType<Expander>())
        {
            if (old.Tag is not string name) continue;
            if (old.IsExpanded) _expandedGroups.Add(name);
            else _expandedGroups.Remove(name);
        }
        _rows.Children.Clear();
        foreach (var group in matches.Take(visible))
        {
            bool expanded = _expandedGroups.Contains(group.Name);
            var expander = new Expander
            {
                Tag = group.Name,
                IsExpanded = expanded,
                Header = CreateGroupHeader(group)
            };
            // Build PID rows only for open groups. Large process lists no longer
            // allocate hundreds of invisible buttons every two seconds.
            if (expanded)
                expander.Content = CreateProcessDetails(group);
            expander.Expanded += (_, _) =>
            {
                _expandedGroups.Add(group.Name);
                if (expander.Content is null)
                    expander.Content = CreateProcessDetails(group);
            };
            expander.Collapsed += (_, _) =>
            {
                _expandedGroups.Remove(group.Name);
                expander.Content = null;
            };
            _rows.Children.Add(expander);
        }
        if (matches.Count > visible)
        {
            int nextCount = Math.Min(ProcessPageSize, matches.Count - visible);
            var more = new Button
            {
                Content = $"Показать ещё {nextCount} приложений",
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12)
            };
            StyleAction(more);
            more.Click += (_, _) =>
            {
                _visibleGroupLimit += ProcessPageSize;
                RenderProcesses();
            };
            _rows.Children.Add(more);
        }
        if (_rows.Children.Count == 0)
            _rows.Children.Add(new TextBlock
            {
                Text = "Процессы не найдены.", Foreground = Muted,
                Margin = new Thickness(12)
            });
        Dispatcher.UIThread.Post(() =>
        {
            // Filtering should reveal the search field and results, not jump to
            // the top of the memory dashboard. Live refresh keeps its position.
            if (resetScroll) _filter.BringIntoView();
            else _processScroll.Offset = previousOffset;
        }, DispatcherPriority.Loaded);
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
                Margin = new Thickness(8, 4)
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
            trim.Click += async (_, _) => await TrimAsync(pid, trim);
            Grid.SetColumn(trim, 3);
            row.Children.Add(trim);
            details.Children.Add(row);
        }
        return details;
    }

    private async Task TrimAsync(int pid, Button button)
    {
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

    private async Task CleanAsync()
    {
        _cleanButton.IsEnabled = false;
        _status.Text = "Ожидание подтверждения привилегий Linux…";
        try
        {
            var (success, message) = await LinuxCleanupService.RequestCacheDropAsync();
            _operationStatus = (success ? "✓ " : "✕ ") + message;
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
}
