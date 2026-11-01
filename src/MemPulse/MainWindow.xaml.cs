using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MemPulse.Core;
using Forms = System.Windows.Forms;

namespace MemPulse;

public partial class MainWindow : Window
{
    private readonly MonitoringService _monitor = new();
    private readonly CleanupService _cleaner = new();
    private readonly SettingsStore _store = new();
    private readonly ObservableCollection<ProcessUsage> _processes = [];
    private readonly DispatcherTimer _timer = new();
    private AppSettings _settings = new();
    private Forms.NotifyIcon? _tray;
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
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _store.Load();
        try { _settings.StartWithWindows = StartupService.IsEnabled(); }
        catch (Exception ex) { SetStatus("Startup setting unavailable: " + ex.Message); }

        ElevateButton.Visibility = CleanupService.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        CleanButton.IsEnabled = CleanupService.IsElevated;
        InitializeTray();
        ConfigureTimer();
        if (CleanupService.IsElevated) SetStatus("Administrator mode · cleanup available");
        else SetStatus("Monitoring active · administrator access required for system cleanup");
        await RefreshAsync();
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
            TotalText.Text = Format.Bytes(memory.Total) + " installed";
            UsageBar.Value = memory.UsedPercent;
            AvailableText.Text = Format.Bytes(memory.Available);
            CacheText.Text = Format.Bytes(memory.Cache);
            CommitText.Text = Format.Bytes(memory.Committed);
            LimitText.Text = Format.Bytes(memory.CommitLimit);

            int? selectedPid = (ProcessGrid.SelectedItem as ProcessUsage)?.Pid;
            _processes.Clear();
            foreach (var row in processes) _processes.Add(row);
            if (selectedPid.HasValue)
                ProcessGrid.SelectedItem = _processes.FirstOrDefault(row => row.Pid == selectedPid.Value);
            ProcessCountText.Text = processes.Count + " processes · working-set RAM";
            UpdateText.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss");

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
            SetStatus("Refresh failed: " + ex.Message);
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
            SetStatus("Choose at least one cleanup operation.");
            return;
        }
        if ((options.WorkingSets || options.ModifiedPages) &&
            MessageBox.Show(this,
                "Trimming working sets or flushing modified pages can temporarily slow down Windows and applications. Continue?",
                "Confirm advanced cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
            SetStatus((automatic ? "Auto-clean: " : "Cleanup: ") + report.Summary);
        }
        catch (Exception ex)
        {
            SetStatus("Cleanup failed: " + ex.Message);
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
                "Trim the working set of " + selected.Name + " (PID " + selected.Pid +
                ")? Its memory may be paged back in and the process may slow down.",
                "Trim process memory", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _cleaning = true;
        CleanButton.IsEnabled = false;
        TrimButton.IsEnabled = false;
        try
        {
            var report = await Task.Run(() => _cleaner.TrimProcess(selected.Pid));
            SetStatus(report.Summary);
        }
        catch (Exception ex) { SetStatus("Trim failed: " + ex.Message); }
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
            ConfigureTimer();
            SetStatus("Settings saved.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save settings",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath ??
                throw new InvalidOperationException("Application executable not found.");
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
                    throw new InvalidOperationException("Publish the application before elevating.");
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
            SetStatus("Administrator request canceled.");
        }
        catch (Exception ex)
        {
            SetStatus("Unable to restart elevated: " + ex.Message);
        }
    }

    private void InitializeTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "MemPulse — memory monitor",
            Visible = true
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open MemPulse", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray)));
        menu.Items.Add("Clean standby cache", null, (_, _) => Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                ShowFromTray();
                await RunCleanupAsync(new CleanupOptions(true, false, false), false);
                await RefreshAsync();
            })));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _forceExit = true;
            Close();
        })));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
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
        if (!_forceExit && _settings.CloseToTray && _tray is not null)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _timer.Stop();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            _tray = null;
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
