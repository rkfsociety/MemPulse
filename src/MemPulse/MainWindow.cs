using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using MemPulse.Core;
using MemPulse.PulseModule;
using MemPulse.Services;
using PCPulse.Contracts;

namespace MemPulse;

internal sealed class MainWindow : Window
{
    private readonly MemPulseModule _module = new();
    private readonly Panel _overlayHost = new() { ZIndex = 100, IsVisible = false };
    private readonly ContentControl _moduleHost = new();
    private readonly Button _updateBannerButton = new()
    {
        IsVisible = false,
        Background = Brush.Parse("#16324A"),
        Foreground = Brush.Parse("#38BDF8"),
        BorderBrush = Brush.Parse("#38BDF8"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 6),
        FontSize = 12.5,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(16, 10, 16, 0),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
    };

    private StandaloneDialogService? _dialogService;
    private StandaloneHostContext? _hostContext;
    private WindowsGlobalHotkey? _hotkey;
    private TrayIcon? _trayIcon;
    private AppSettings _currentSettings = new();
    private AvailableUpdate? _pendingWindowsUpdate;
    private ReleaseInfo? _pendingLinuxUpdate;
    private bool _allowClose;
    private bool _disposed;
    private int _lastTrayPercent = -1;
    private bool _lastTrayCleaning;

    public MainWindow()
    {
        Title = "MemPulse — Монитор оперативной памяти";
        Width = 1040;
        Height = 720;
        MinWidth = 840;
        MinHeight = 600;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = Brush.Parse("#0B1118");

        TryLoadWindowIcon();

        var contentGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        contentGrid.Children.Add(_updateBannerButton);
        Grid.SetRow(_updateBannerButton, 0);

        contentGrid.Children.Add(_moduleHost);
        Grid.SetRow(_moduleHost, 1);

        var rootOverlayGrid = new Grid();
        rootOverlayGrid.Children.Add(contentGrid);
        rootOverlayGrid.Children.Add(_overlayHost);
        Content = rootOverlayGrid;

        _updateBannerButton.Click += async (_, _) => await InstallOrOpenPendingUpdateAsync();

        Opened += OnWindowOpened;
        PropertyChanged += OnWindowPropertyChanged;
        Closing += OnWindowClosing;
    }

    public async Task InitializeHostAsync()
    {
        var logger = new StandaloneLogger();
        _dialogService = new StandaloneDialogService(_overlayHost);
        _hostContext = new StandaloneHostContext(
            _dialogService,
            logger,
            OnSnapshotUpdated,
            OnSettingsChanged,
            () => CheckForUpdatesAsync(interactive: true),
            OpenLogsDirectoryAsync);

        await _module.InitializeAsync(_hostContext);
        _currentSettings = await _hostContext.Settings.LoadAsync<AppSettings>("settings") ?? new AppSettings();

        _moduleHost.Content = _module.CreateView();
        SetupTrayIcon();

        if (_currentSettings.CheckForUpdates)
            _ = CheckForUpdatesAsync(interactive: false);
    }

    public async Task<bool> RunSmokeTestAsync()
    {
        await InitializeHostAsync();
        var summary = await _module.GetSummaryAsync();
        await _module.DisposeAsync();
        _disposed = true;
        return summary is not null && !string.IsNullOrWhiteSpace(summary.Headline);
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (OperatingSystem.IsWindows())
        {
            _hotkey ??= new WindowsGlobalHotkey(this, ToggleFromHotkey);
            _hotkey.SetEnabled(_currentSettings.GlobalHotkeyEnabled);
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized && _currentSettings.MinimizeToTray)
            Hide();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_allowClose && _currentSettings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (!_disposed)
        {
            _disposed = true;
            _hotkey?.Dispose();
            if (_trayIcon is not null && Application.Current is not null)
            {
                try { TrayIcon.SetIcons(Application.Current, new TrayIcons()); } catch { }
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _ = _module.DisposeAsync().AsTask();
        }
    }

    private void ToggleFromHotkey()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && WindowState != WindowState.Minimized)
                Hide();
            else
                RestoreFromTray();
        });
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void OnSnapshotUpdated(MemoryUsage usage, AppSettings settings, bool isCleaning)
    {
        _currentSettings = settings;
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTray(usage, isCleaning);
        });
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        _currentSettings = settings;
        Dispatcher.UIThread.Post(() =>
        {
            _hotkey?.SetEnabled(settings.GlobalHotkeyEnabled);
        });
    }

    private void SetupTrayIcon()
    {
        if (Application.Current is null)
            return;

        try
        {
            var menu = new NativeMenu();
            var openItem = new NativeMenuItem("Открыть MemPulse");
            openItem.Click += (_, _) => RestoreFromTray();
            menu.Items.Add(openItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("Выход");
            exitItem.Click += (_, _) =>
            {
                _allowClose = true;
                Close();
            };
            menu.Items.Add(exitItem);

            byte[] png = TrayIconRenderer.RenderPng(0, false);
            using var ms = new MemoryStream(png);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(ms),
                ToolTipText = "MemPulse — Монитор ОЗУ",
                Menu = menu,
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => ToggleFromHotkey();

            TrayIcon.SetIcons(Application.Current, new TrayIcons { _trayIcon });
        }
        catch (Exception ex)
        {
            StartupLog.Warn($"Трей недоступен в текущем окружении: {ex.Message}");
        }
    }

    private void UpdateTray(MemoryUsage usage, bool isCleaning)
    {
        if (_trayIcon is null)
            return;

        int pct = (int)Math.Round(usage.UsedPercent);
        _trayIcon.ToolTipText = usage.Total > 0
            ? $"MemPulse — ОЗУ {usage.UsedPercent:F0}% ({Format.Bytes(usage.Used)} / {Format.Bytes(usage.Total)})"
            : "MemPulse — Монитор ОЗУ";

        if (pct == _lastTrayPercent && isCleaning == _lastTrayCleaning)
            return;

        _lastTrayPercent = pct;
        _lastTrayCleaning = isCleaning;

        try
        {
            byte[] png = TrayIconRenderer.RenderPng(pct, isCleaning);
            using var ms = new MemoryStream(png);
            _trayIcon.Icon = new WindowIcon(ms);
        }
        catch
        {
        }
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_dialogService is null)
            return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var service = new UpdateService();
                var update = await service.CheckAsync();
                if (update is not null)
                {
                    _pendingWindowsUpdate = update;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _updateBannerButton.Content = $"Доступна версия v{update.Version.ToString(3)} — Установить";
                        _updateBannerButton.IsVisible = true;
                    });

                    if (interactive)
                        await InstallOrOpenPendingUpdateAsync();
                }
                else if (interactive)
                {
                    await _dialogService.ShowInfoAsync(
                        "Обновления не найдены",
                        $"Установлена актуальная версия MemPulse v{UpdateService.CurrentVersion.ToString(3)}.");
                }
            }
            else
            {
                var release = await ReleaseChecker.CheckLatestLinuxAsync();
                if (release is not null && release.Version > ReleaseChecker.CurrentVersion)
                {
                    _pendingLinuxUpdate = release;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _updateBannerButton.Content = $"Доступна версия {release.Tag} — Открыть релиз";
                        _updateBannerButton.IsVisible = true;
                    });

                    if (interactive)
                        await InstallOrOpenPendingUpdateAsync();
                }
                else if (interactive)
                {
                    await _dialogService.ShowInfoAsync(
                        "Обновления не найдены",
                        $"Установлена актуальная версия MemPulse v{ReleaseChecker.CurrentVersion.ToString(3)}.");
                }
            }
        }
        catch (Exception ex)
        {
            StartupLog.Warn($"Не удалось проверить обновления: {ex.Message}");
            if (interactive)
                await _dialogService.ShowErrorAsync("Ошибка проверки обновлений", ex.Message);
        }
    }

    private async Task InstallOrOpenPendingUpdateAsync()
    {
        if (_dialogService is null)
            return;

        if (_pendingWindowsUpdate is { } winUpdate)
        {
            bool ok = await _dialogService.ConfirmAsync(
                $"Установить обновление v{winUpdate.Version.ToString(3)}?",
                "Будет загружен и запущен официальный установщик MemPulse-Setup-x64.exe с проверкой SHA-256.",
                "Установить",
                "Позже");

            if (!ok)
                return;

            try
            {
                var service = new UpdateService();
                string installerPath = await service.DownloadAsync(winUpdate);
                Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                    UseShellExecute = true
                });
                _allowClose = true;
                Close();
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync("Не удалось установить обновление", ex.Message);
            }
            return;
        }

        if (_pendingLinuxUpdate is { } linuxRelease)
        {
            bool open = await _dialogService.ConfirmAsync(
                $"Доступен релиз {linuxRelease.Tag}",
                $"Открыть страницу релиза на GitHub?\n{linuxRelease.ReleaseUrl}",
                "Открыть",
                "Закрыть");

            if (open)
                OpenUrl(linuxRelease.ReleaseUrl.ToString());
        }
    }

    private Task OpenLogsDirectoryAsync()
    {
        string? logDir = Path.GetDirectoryName(StartupLog.FilePath);
        if (!string.IsNullOrEmpty(logDir) && Directory.Exists(logDir))
            OpenUrl(logDir);
        return Task.CompletedTask;
    }

    private static void OpenUrl(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StartupLog.Warn($"Не удалось открыть {target}: {ex.Message}");
        }
    }

    private void TryLoadWindowIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://MemPulse/Assets/MemPulse.png"));
            Icon = new WindowIcon(stream);
        }
        catch
        {
        }
    }
}
