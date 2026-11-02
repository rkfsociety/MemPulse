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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IReadOnlyList<ProcessGroupUsage> _groups = [];

    public MainWindow()
    {
        Title = "MemPulse — Linux";
        Width = 980;
        Height = 730;
        MinWidth = 720;
        MinHeight = 520;
        Background = new SolidColorBrush(Color.Parse("#121B25"));

        var layout = new DockPanel { Margin = new Thickness(24) };
        Content = layout;

        var header = new StackPanel { Spacing = 14 };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var titleLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleLine.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = "MemPulse", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = Highlight },
                new TextBlock { Text = "Linux · мониторинг оперативной памяти и процессов", Foreground = Muted }
            }
        });
        var refresh = new Button { Content = "Обновить", VerticalAlignment = VerticalAlignment.Center };
        refresh.Click += (_, _) => Refresh();
        Grid.SetColumn(refresh, 1);
        titleLine.Children.Add(refresh);
        header.Children.Add(titleLine);

        var metrics = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), Margin = new Thickness(0, 8) };
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

        var operations = new StackPanel { Spacing = 5 };
        _cleanButton.Click += async (_, _) => await CleanAsync();
        operations.Children.Add(_cleanButton);
        operations.Children.Add(new TextBlock
        {
            Text = "Очистка кэша не увеличивает объём ОЗУ и может замедлить работу. Запрашивается разрешение через polkit.",
            Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(operations);

        var processHeading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 6) };
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
            Text = "RSS — физическая память процесса; Data — размер его приватного виртуального сегмента (не PSS).",
            Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap
        });
        footer.Children.Add(_status);
        layout.Children.Add(footer);

        layout.Children.Add(new Border
        {
            Background = Surface,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Child = new ScrollViewer { Content = _rows }
        });

        _timer.Tick += (_, _) => Refresh();
        Closed += (_, _) => _timer.Stop();
        Refresh();
        _timer.Start();
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
            _processCount.Text = $"{processes.Count} процессов · {_groups.Count} приложений · RSS / Data";
            RenderProcesses();
            _status.Text = "Обновлено: " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = "Ошибка мониторинга: " + ex.Message;
        }
    }

    private void RenderProcesses()
    {
        string query = _filter.Text?.Trim() ?? "";
        _rows.Children.Clear();
        var filtered = _groups.Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(120);
        foreach (var group in filtered)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,140,140"), Margin = new Thickness(8, 5) };
            row.Children.Add(new TextBlock
            {
                Text = group.DisplayName + "  ·  PID: " + string.Join(", ", group.Members.Take(5).Select(p => p.Pid)) +
                    (group.Count > 5 ? "…" : ""),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            });
            var rss = new TextBlock { Text = group.WorkingText, Foreground = Highlight, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(rss, 1);
            row.Children.Add(rss);
            var data = new TextBlock { Text = group.PrivateText, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(data, 2);
            row.Children.Add(data);
            _rows.Children.Add(row);
        }
        if (_rows.Children.Count == 0)
            _rows.Children.Add(new TextBlock { Text = "Процессы не найдены.", Foreground = Muted });
    }

    private async Task CleanAsync()
    {
        _cleanButton.IsEnabled = false;
        _status.Text = "Ожидание подтверждения привилегий Linux…";
        try
        {
            var (success, message) = await LinuxCleanupService.RequestCacheDropAsync();
            Refresh();
            _status.Text = (success ? "✓ " : "✕ ") + message;
        }
        finally
        {
            _cleanButton.IsEnabled = true;
        }
    }
}
