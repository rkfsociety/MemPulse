using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MemPulse.Core;

namespace MemPulse.Linux;

public sealed class MainWindow : Window
{
    private static readonly IBrush Surface = new SolidColorBrush(Color.Parse("#1C2530"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#AAB8C4"));
    private static readonly IBrush Highlight = new SolidColorBrush(Color.Parse("#73DDC2"));
    private readonly LinuxProcfs _procfs = new();
    private readonly LinuxSettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private LinuxCleanupService.AutoCleanupSession? _autoSession;

    private readonly TextBlock _used = Metric();
    private readonly TextBlock _available = Metric();
    private readonly TextBlock _cache = Metric();
    private readonly TextBlock _total = Metric();
    private readonly TextBlock _processCount = new() { Foreground = Muted, FontSize = 14 };
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _usageBar = new() { Minimum = 0, Maximum = 100, Height = 14 };
    private readonly TextBox _filter = new() { Watermark = "Поиск процесса", Width = 230 };
    private readonly StackPanel _rows = new() { Spacing = 3 };
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
    private IReadOnlyList<ProcessGroupUsage> _groups = [];
    private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);
    private string _operationStatus = "Мониторинг работает без прав администратора.";

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        _autoBox.IsChecked = _settings.AutoClean;
        _threshold.Value = _settings.AutoCleanThreshold;
        _cooldown.Value = _settings.AutoCleanCooldownMinutes;

        Title = "MemPulse — Linux";
        Width = 1020;
        Height = 790;
        MinWidth = 760;
        MinHeight = 600;
        Background = new SolidColorBrush(Color.Parse("#121B25"));

        var layout = new DockPanel { Margin = new Thickness(24) };
        Content = layout;
        var header = new StackPanel { Spacing = 12 };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var titleLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleLine.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = "MemPulse", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Highlight },
                new TextBlock { Text = "Linux · мониторинг и управление оперативной памятью", Foreground = Muted }
            }
        });
        var refresh = new Button { Content = "Обновить", VerticalAlignment = VerticalAlignment.Center };
        refresh.Click += (_, _) => Refresh();
        Grid.SetColumn(refresh, 1);
        titleLine.Children.Add(refresh);
        header.Children.Add(titleLine);

        var metrics = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), Margin = new Thickness(0, 8)
        };
        AddMetric(metrics, 0, "Занято", _used);
        AddMetric(metrics, 1, "Доступно", _available);
        AddMetric(metrics, 2, "Кэш (reclaimable)", _cache);
        AddMetric(metrics, 3, "Всего", _total);
        var overview = new StackPanel { Spacing = 12 };
        overview.Children.Add(new TextBlock
        {
            Text = "Оперативная память", FontWeight = FontWeight.SemiBold, FontSize = 19
        });
        overview.Children.Add(_usageBar);
        overview.Children.Add(metrics);
        header.Children.Add(new Border
        {
            Background = Surface, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18), Child = overview
        });

        var operations = new StackPanel { Spacing = 7 };
        _cleanButton.Click += async (_, _) => await CleanAsync();
        operations.Children.Add(_cleanButton);
        operations.Children.Add(new TextBlock
        {
            Text = "Ручной сброс файлового кэша. Потребуется polkit (pkexec) и подтверждение прав root.",
            Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(operations);

        var automatic = new StackPanel { Spacing = 8 };
        automatic.Children.Add(new TextBlock
        {
            Text = "Автоочистка файлового кэша", FontSize = 16, FontWeight = FontWeight.SemiBold
        });
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(_autoBox);
        controls.Children.Add(new TextBlock { Text = "Порог, %", VerticalAlignment = VerticalAlignment.Center });
        controls.Children.Add(_threshold);
        controls.Children.Add(new TextBlock { Text = "Пауза, мин.", VerticalAlignment = VerticalAlignment.Center });
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
            Background = Surface, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14), Child = automatic
        });

        var processHeading = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 6)
        };
        processHeading.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Процессы", FontSize = 19, FontWeight = FontWeight.SemiBold },
                _processCount
            }
        });
        Grid.SetColumn(_filter, 1);
        processHeading.Children.Add(_filter);
        _filter.TextChanged += (_, _) => RenderProcesses();
        header.Children.Add(processHeading);

        var footer = new StackPanel { Spacing = 5, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(new TextBlock
        {
            Text = "RSS — физическая память; Data — приватный виртуальный сегмент (не PSS). MADV_PAGEOUT запрашивает вытеснение до 128 МиБ подходящих страниц выбранного PID, но не гарантирует уменьшение RSS.",
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap
        });
        footer.Children.Add(_status);
        layout.Children.Add(footer);
        layout.Children.Add(new Border
        {
            Background = Surface, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10), Child = new ScrollViewer { Content = _rows }
        });

        _timer.Tick += (_, _) => Refresh();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _autoSession?.Dispose();
        };
        Refresh();
        _timer.Start();
        // A remembered enabled setting is re-authorized for every GUI session.
        if (_settings.AutoClean &&
            !Environment.GetCommandLineArgs().Contains("--startup-smoke-test", StringComparer.Ordinal))
            Dispatcher.UIThread.Post(async () => await ApplyAutoSettingsAsync(), DispatcherPriority.Background);
    }

    private static TextBlock Metric() =>
        new() { FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Highlight };

    private static void AddMetric(Grid grid, int column, string caption, TextBlock value)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(new TextBlock { Text = caption, Foreground = Muted, FontSize = 12 });
        stack.Children.Add(value);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private void Refresh()
    {
        try
        {
            var memory = _procfs.ReadMemory();
            _usageBar.Value = memory.UsedPercent;
            _used.Text = Format.Bytes(memory.Used) + $" ({memory.UsedPercent:0.0}%)";
            _available.Text = Format.Bytes(memory.Available);
            _cache.Text = Format.Bytes(memory.Cache);
            _total.Text = Format.Bytes(memory.Total);

            var processes = _procfs.ReadProcesses();
            _groups = ProcessGrouping.Build(processes);
            _processCount.Text = $"{processes.Count} процессов · {_groups.Count} приложений · RSS / Data · раскройте группу для выбора PID";
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
            _status.Text = "Ошибка мониторинга: " + ex.Message;
        }
    }

    private void RenderProcesses()
    {
        string query = _filter.Text?.Trim() ?? "";
        // Preserve expanded PID groups across the two-second refresh cycle.
        foreach (var old in _rows.Children.OfType<Expander>())
        {
            if (old.Tag is not string name) continue;
            if (old.IsExpanded) _expandedGroups.Add(name);
            else _expandedGroups.Remove(name);
        }
        _rows.Children.Clear();
        foreach (var group in _groups.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .Take(100))
        {
            var details = new StackPanel { Spacing = 3 };
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
                int pid = member.Pid;
                trim.Click += async (_, _) => await TrimAsync(pid, trim);
                Grid.SetColumn(trim, 3);
                row.Children.Add(trim);
                details.Children.Add(row);
            }
            var expander = new Expander
            {
                Tag = group.Name,
                IsExpanded = _expandedGroups.Contains(group.Name),
                Header = new TextBlock
                {
                    Text = group.DisplayName + "  ·  RSS " + group.WorkingText +
                        "  ·  Data " + group.PrivateText,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                Content = details
            };
            _rows.Children.Add(expander);
        }
        if (_rows.Children.Count == 0)
            _rows.Children.Add(new TextBlock { Text = "Процессы не найдены.", Foreground = Muted });
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
