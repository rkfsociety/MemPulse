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
        ConfigureUpdateTimer();
        if (_settings.CheckForUpdates) await CheckUpdatesAsync(false);
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
                UpdateButton.Content = "Install v" + update.Version;
                UpdateButton.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
                UpdateButton.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(16, 33, 40));
                SetStatus("MemPulse " + update.Version + " is available.");
            }
            else
            {
                UpdateButton.Content = "Check updates";
                UpdateButton.Background = (System.Windows.Media.Brush)FindResource("RaisedBrush");
                UpdateButton.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
                if (manual) SetStatus("MemPulse is up to date (v" + UpdateService.CurrentVersion + ").");
            }
        }
        catch (Exception ex)
        {
            if (manual) SetStatus("Update check failed: " + ex.Message);
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
                "Download and install MemPulse v" + update.Version +
                "? The application will close and the installer will open.",
                "Install MemPulse update", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _installingUpdate = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(percent =>
                SetStatus("Downloading verified update: " + percent.ToString("0") + "%"));
            string setup = await _updates.DownloadAsync(update, progress);
            var process = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true });
            if (process is null) throw new InvalidOperationException("Installer did not start.");

            _forceExit = true;
            Close(); // Exit even if close-to-tray is enabled so installer can replace the executable.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Update could not be installed",
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
            TotalText.Text = Format.Bytes(memory.Total) + " installed";
            UsageBar.Value = memory.UsedPercent;
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
            ConfigureUpdateTimer();
            if (next.CheckForUpdates) _ = CheckUpdatesAsync(false);
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
        // Load the same embedded icon that is compiled into MemPulse.exe and used by WPF windows.
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/MemPulse.ico"))
            ?? throw new InvalidOperationException("Embedded MemPulse icon was not found.");
        using (resource.Stream)
        using (var brandIcon = new System.Drawing.Icon(resource.Stream, new System.Drawing.Size(32, 32)))
            _trayIcon = (System.Drawing.Icon)brandIcon.Clone();

        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "MemPulse — ожидание данных ОЗУ",
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

    private void UpdateTray(MemoryUsage memory)
    {
        if (_tray is null) return;

        int percent = Math.Clamp((int)Math.Round(memory.UsedPercent,
            MidpointRounding.AwayFromZero), 0, 100);
        _tray.Text = $"MemPulse | ОЗУ {percent}% ({Format.Bytes(memory.Used)} / {Format.Bytes(memory.Total)})";

        if (_lastTrayPercent == percent) return;

        var nextIcon = TrayIconRenderer.Create(percent);
        var previousIcon = _trayIcon;
        try
        {
            _tray.Icon = nextIcon;
            _trayIcon = nextIcon;
            _lastTrayPercent = percent;
            previousIcon?.Dispose();
        }
        catch
        {
            nextIcon.Dispose();
            throw;
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

    private void SetStatus(string text) => StatusText.Text = text;
}
