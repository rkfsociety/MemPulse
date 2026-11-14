using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using MemPulse.Core;
using MemPulse.Core.Helper;
using MemPulse.Core.Platform;
using MemPulse.Core.Platform.Linux;
using PCPulse.Contracts;

namespace MemPulse.PulseModule;

public sealed class CleanupHistoryState
{
    public List<CleanupHistoryEntry> Entries { get; set; } = [];
}

internal sealed class MemPulseController : IAsyncDisposable
{
    private const int HistoryCapacity = 60;
    private readonly IPulseHostContext _context;
    private readonly IStandaloneHostBridge? _standaloneBridge;
    private readonly IMemoryMonitorService _monitor;
    private readonly IFullscreenDetector _fullscreenDetector;
    private readonly IProcessIconProvider _iconProvider;
    private readonly Queue<double> _ramSamples = new();
    private readonly CleanupHistoryLog _historyLog = new(12);
    private readonly SemaphoreSlim _cleanGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    private DispatcherTimer? _timer;
    private AppSettings _settings = new();
    private MemoryUsage _latestMemory = new(0, 0, 0, 0, 0);
    private IReadOnlyList<ProcessGroupUsage> _latestGroups = [];
    private DateTime _lastAutoCleanUtc = DateTime.MinValue;
    private long? _lastAutoCleanFreedBytes;
    private string? _linuxAutoMarkerPath;
    private bool _isCleaning;
    private bool _disposed;

    public MemPulseController(
        IPulseHostContext context,
        IMemoryMonitorService? monitor = null,
        IFullscreenDetector? fullscreenDetector = null,
        IProcessIconProvider? iconProvider = null)
    {
        _context = context;
        _standaloneBridge = context as IStandaloneHostBridge;
        _monitor = monitor ?? PlatformServices.CreateMemoryMonitor();
        _fullscreenDetector = fullscreenDetector ?? PlatformServices.CreateFullscreenDetector();
        _iconProvider = iconProvider ?? PlatformServices.CreateProcessIconProvider();
    }

    public IPulseHostContext Context => _context;
    public IStandaloneHostBridge? StandaloneBridge => _standaloneBridge;
    public AppSettings Settings => _settings;
    public MemoryUsage LatestMemory => _latestMemory;
    public IReadOnlyList<ProcessGroupUsage> LatestGroups => _latestGroups;
    public IReadOnlyList<CleanupHistoryEntry> HistoryEntries => _historyLog.Entries;
    public IReadOnlyList<double> RamHistorySamples => _ramSamples.ToArray();
    public bool IsCleaning => _isCleaning;
    public string StatusBannerText { get; private set; } = "Мониторинг активен";

    public event Action? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_context.ModuleDataDirectory);
        Directory.CreateDirectory(GetTempDirectory());

        var loadedSettings = await _context.Settings
            .LoadAsync<AppSettings>("settings", cancellationToken)
            .ConfigureAwait(false);
        _settings = AppSettings.Normalize(loadedSettings);

        var loadedHistory = await _context.Settings
            .LoadAsync<CleanupHistoryState>("history", cancellationToken)
            .ConfigureAwait(false);
        if (loadedHistory?.Entries is { Count: > 0 } entries)
        {
            foreach (var entry in entries.Take(12).Reverse())
                _historyLog.Record(entry);
        }

        await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);

        if (_standaloneBridge?.SupportsWindowsStartup == true)
            _standaloneBridge.ApplyStartupSetting(_settings.StartWithWindows);

        _context.Logger.Info($"MemPulse инициализирован (платформа: {PulseContractVersion.GetCurrentPlatform()}, elevated={_context.Elevation.IsElevated}).");
    }

    public void StartPollingIfUiAvailable()
    {
        if (_disposed || _timer is not null || Application.Current is null)
            return;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_settings.RefreshSeconds)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public PulseStatusSummary BuildSummary()
    {
        var mem = _latestMemory;
        if (mem.Total == 0)
        {
            mem = _monitor.ReadMemory();
            _latestMemory = mem;
        }

        string headline = mem.Total > 0
            ? $"ОЗУ: {mem.UsedPercent:F0}% ({Format.Bytes(mem.Used)} / {Format.Bytes(mem.Total)})"
            : "ОЗУ: нет данных";

        string availablePart = mem.Total > 0
            ? $"Свободно {Format.Bytes(mem.Available)}"
            : "Ожидание первого замера";

        string details = _historyLog.Latest is { } latest
            ? $"{availablePart} · посл. очистка {latest.Timestamp.LocalDateTime:HH:mm} ({latest.FreedText})"
            : availablePart;

        var severity = mem.UsedPercent switch
        {
            >= 88 => PulseStatusSeverity.Attention,
            >= 72 => PulseStatusSeverity.Warning,
            _ => PulseStatusSeverity.Normal
        };

        return new PulseStatusSummary(headline, details, severity);
    }

    public async Task RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            var (usage, groups) = await Task.Run(() =>
            {
                var mem = _monitor.ReadMemory();
                var procs = _monitor.ReadProcesses();
                var decorated = _iconProvider.Decorate(procs);
                var grouped = ProcessGrouping.Build(decorated);
                return (mem, grouped);
            }, cancellationToken).ConfigureAwait(false);

            _latestMemory = usage;
            _latestGroups = groups;

            if (_ramSamples.Count >= HistoryCapacity)
                _ramSamples.Dequeue();
            _ramSamples.Enqueue(usage.UsedPercent);

            _standaloneBridge?.OnSnapshotUpdated(usage, _settings, _isCleaning);
            _context.RequestSummaryRefresh();
            NotifyStateChanged();

            await EvaluateAutoCleanupAsync(usage, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _context.Logger.Error("Ошибка обновления телеметрии памяти.", ex);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task SaveSettingsAsync(AppSettings updated, bool restartLinuxMonitorIfNeeded = true, CancellationToken cancellationToken = default)
    {
        var normalized = AppSettings.Normalize(updated.Copy());
        bool wasAutoClean = _settings.AutoClean;
        int prevThreshold = _settings.AutoCleanThreshold;
        int prevCooldown = _settings.AutoCleanCooldownMinutes;

        _settings = normalized;
        await _context.Settings.SaveAsync("settings", normalized, cancellationToken).ConfigureAwait(false);

        if (_timer is not null)
            _timer.Interval = TimeSpan.FromSeconds(normalized.RefreshSeconds);

        if (_standaloneBridge is not null)
        {
            if (_standaloneBridge.SupportsWindowsStartup)
                _standaloneBridge.ApplyStartupSetting(normalized.StartWithWindows);
            _standaloneBridge.OnSettingsChanged(normalized);
        }

        if (OperatingSystem.IsLinux() && restartLinuxMonitorIfNeeded)
        {
            bool changedAutoParams = wasAutoClean != normalized.AutoClean
                || prevThreshold != normalized.AutoCleanThreshold
                || prevCooldown != normalized.AutoCleanCooldownMinutes;
            if (changedAutoParams)
                await SyncLinuxAutoCleanMonitorAsync(normalized.AutoClean, interactiveConfirmation: true, cancellationToken).ConfigureAwait(false);
        }

        _context.RequestSummaryRefresh();
        NotifyStateChanged();
    }

    public async Task ToggleProcessExclusionAsync(string processName, CancellationToken cancellationToken = default)
    {
        string normalized = ProcessExclusion.NormalizeName(processName);
        if (string.IsNullOrEmpty(normalized))
            return;

        var copy = _settings.Copy();
        bool existed = copy.ExcludedProcesses.Any(x =>
            string.Equals(ProcessExclusion.NormalizeName(x), normalized, StringComparison.OrdinalIgnoreCase));

        if (existed)
        {
            copy.ExcludedProcesses.RemoveAll(x =>
                string.Equals(ProcessExclusion.NormalizeName(x), normalized, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            copy.ExcludedProcesses.Add(normalized);
        }

        copy.ExcludedProcesses = ProcessExclusion.NormalizeList(copy.ExcludedProcesses);
        await SaveSettingsAsync(copy, restartLinuxMonitorIfNeeded: false, cancellationToken).ConfigureAwait(false);

        StatusBannerText = existed
            ? $"Процесс «{normalized}» удалён из белого списка."
            : $"Процесс «{normalized}» добавлен в белый список очистки.";
        NotifyStateChanged();
    }

    public async Task<CleanupReport> RunCleanupAsync(CleanupOptions options, bool automatic = false, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return new CleanupReport();

        if (!await _cleanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            if (!automatic)
                await _context.Dialogs.ShowInfoAsync("Очистка уже выполняется", "Дождитесь завершения текущей операции очистки памяти.", cancellationToken).ConfigureAwait(false);
            return new CleanupReport();
        }

        _isCleaning = true;
        _standaloneBridge?.OnSnapshotUpdated(_latestMemory, _settings, true);
        StatusBannerText = automatic ? "Выполняется автоочистка памяти..." : "Выполняется очистка памяти...";
        NotifyStateChanged();

        string? excludePath = null;
        string? reportPath = null;
        try
        {
            ulong beforeUsed = _monitor.ReadMemory().Used;
            string tempDir = GetTempDirectory();
            Directory.CreateDirectory(tempDir);

            string id = Guid.NewGuid().ToString("N");
            reportPath = Path.Combine(tempDir, $"report-{id}.json");

            IReadOnlyList<string> args;
            if (OperatingSystem.IsWindows())
            {
                if (options.WorkingSets && _settings.ExcludedProcesses.Count > 0)
                {
                    excludePath = Path.Combine(tempDir, $"mempulse-exclude-{id}.json");
                    var normalizedExclusions = ProcessExclusion.NormalizeList(_settings.ExcludedProcesses);
                    string json = JsonSerializer.Serialize(normalizedExclusions, HelperJsonContext.Default.ListString);
                    await File.WriteAllTextAsync(excludePath, json, cancellationToken).ConfigureAwait(false);
                }
                args = HelperCommandBuilder.BuildWindowsCleanArgs(options, _context.ModuleDataDirectory, reportPath, excludePath);
            }
            else if (OperatingSystem.IsLinux())
            {
                args = HelperCommandBuilder.BuildLinuxCleanArgs(_context.ModuleDataDirectory, reportPath);
            }
            else
            {
                throw new PlatformNotSupportedException("Очистка памяти поддерживается только на Windows и Linux.");
            }

            int exitCode = await _context.Elevation.RunElevatedHelperAsync(args, cancellationToken).ConfigureAwait(false);
            var helperReport = await HelperReportIo.TryReadReportAsync(reportPath, cancellationToken).ConfigureAwait(false);

            ulong afterUsed = _monitor.ReadMemory().Used;
            var steps = helperReport?.Steps ?? [];
            bool success = exitCode == 0 && (helperReport?.Success ?? true);
            string details = helperReport?.Message ?? (success ? "Очистка выполнена" : $"Код возврата helper: {exitCode}");

            var entry = new CleanupHistoryEntry(
                DateTimeOffset.UtcNow,
                automatic ? "Автоочистка" : "Ручная очистка",
                beforeUsed,
                afterUsed,
                success,
                details);
            await RecordHistoryAsync(entry, cancellationToken).ConfigureAwait(false);

            if (automatic)
                _lastAutoCleanFreedBytes = entry.FreedBytes;

            if (success)
            {
                StatusBannerText = $"Очистка завершена ({entry.FreedText})";
                _context.Logger.Info($"Очистка памяти завершена: {details}, {entry.FreedText}");
            }
            else
            {
                StatusBannerText = details;
                _context.Logger.Warn($"Очистка завершилась с кодом {exitCode}: {details}");
                if (!automatic)
                    await _context.Dialogs.ShowErrorAsync("Очистка не выполнена", details, cancellationToken).ConfigureAwait(false);
            }

            await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
            var cleanupReport = new CleanupReport();
            cleanupReport.Steps.AddRange(steps);
            return cleanupReport;
        }
        catch (OperationCanceledException)
        {
            StatusBannerText = "Очистка отменена";
            return new CleanupReport();
        }
        catch (Exception ex)
        {
            StatusBannerText = $"Ошибка очистки: {ex.Message}";
            _context.Logger.Error("Ошибка запуска helper-утилиты очистки памяти.", ex);
            if (!automatic)
                await _context.Dialogs.ShowErrorAsync("Ошибка очистки памяти", ex.Message, cancellationToken).ConfigureAwait(false);
            return new CleanupReport();
        }
        finally
        {
            CleanupTempFile(excludePath);
            CleanupTempFile(reportPath);
            _isCleaning = false;
            _standaloneBridge?.OnSnapshotUpdated(_latestMemory, _settings, false);
            _cleanGate.Release();
            NotifyStateChanged();
        }
    }

    public async Task TrimProcessAsync(ProcessUsage process, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        if (!await _cleanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await _context.Dialogs.ShowInfoAsync("Операция уже выполняется", "Подождите завершения текущей операции очистки.", cancellationToken).ConfigureAwait(false);
            return;
        }

        _isCleaning = true;
        StatusBannerText = $"Сжатие памяти PID {process.Pid} ({process.Name})...";
        NotifyStateChanged();

        string? reportPath = null;
        try
        {
            ulong beforeUsed = _monitor.ReadMemory().Used;
            string tempDir = GetTempDirectory();
            Directory.CreateDirectory(tempDir);
            string id = Guid.NewGuid().ToString("N");
            reportPath = Path.Combine(tempDir, $"trim-{id}.json");

            IReadOnlyList<string> args;
            if (OperatingSystem.IsWindows())
            {
                args = HelperCommandBuilder.BuildWindowsTrimArgs(process.Pid, _context.ModuleDataDirectory, reportPath);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (!LinuxProcfs.TryReadStartTicks(process.Pid, out ulong startTicks))
                {
                    await _context.Dialogs.ShowErrorAsync("Процесс не найден", $"Процесс «{process.Name}» (PID {process.Pid}) уже завершился.", cancellationToken).ConfigureAwait(false);
                    return;
                }
                args = HelperCommandBuilder.BuildLinuxTrimArgs(process.Pid, startTicks, _context.ModuleDataDirectory, reportPath);
            }
            else
            {
                throw new PlatformNotSupportedException("Операция поддерживается только на Windows и Linux.");
            }

            int exitCode = await _context.Elevation.RunElevatedHelperAsync(args, cancellationToken).ConfigureAwait(false);
            var report = await HelperReportIo.TryReadReportAsync(reportPath, cancellationToken).ConfigureAwait(false);
            ulong afterUsed = _monitor.ReadMemory().Used;

            bool success = exitCode == 0 && (report?.Success ?? true);
            string msg = report?.Message ?? (success ? $"Память PID {process.Pid} оптимизирована" : $"Код возврата helper: {exitCode}");

            var entry = new CleanupHistoryEntry(
                DateTimeOffset.UtcNow,
                $"Процесс {process.Name}",
                beforeUsed,
                afterUsed,
                success,
                msg);
            await RecordHistoryAsync(entry, cancellationToken).ConfigureAwait(false);

            StatusBannerText = msg;
            if (!success)
                await _context.Dialogs.ShowErrorAsync("Не удалось сжать память процесса", msg, cancellationToken).ConfigureAwait(false);

            await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusBannerText = $"Ошибка: {ex.Message}";
            _context.Logger.Error($"Ошибка trim процесса PID {process.Pid}.", ex);
            await _context.Dialogs.ShowErrorAsync("Ошибка оптимизации процесса", ex.Message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CleanupTempFile(reportPath);
            _isCleaning = false;
            _cleanGate.Release();
            NotifyStateChanged();
        }
    }

    public async Task ResetSettingsAsync(CancellationToken cancellationToken = default)
    {
        bool confirm = await _context.Dialogs.ConfirmAsync(
            "Сбросить настройки?",
            "Все параметры MemPulse и белый список процессов вернутся к значениям по умолчанию.",
            "Сбросить",
            "Отмена",
            isDestructive: true,
            cancellationToken).ConfigureAwait(false);

        if (!confirm)
            return;

        await SaveSettingsAsync(new AppSettings(), restartLinuxMonitorIfNeeded: true, cancellationToken).ConfigureAwait(false);
        StatusBannerText = "Настройки сброшены к значениям по умолчанию.";
        NotifyStateChanged();
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        var fresh = new CleanupHistoryLog(12);
        // Clear via reloading empty state
        await _context.Settings.SaveAsync("history", new CleanupHistoryState(), cancellationToken).ConfigureAwait(false);
        // Re-create internal log is readonly; reflect on new instance by calling ResetHistoryState:
        ClearInMemoryHistory();
        StatusBannerText = "История очисток очищена.";
        _context.RequestSummaryRefresh();
        NotifyStateChanged();
    }

    private void ClearInMemoryHistory()
    {
        typeof(CleanupHistoryLog)
            .GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .SetValue(_historyLog, new List<CleanupHistoryEntry>());
    }

    private async Task RecordHistoryAsync(CleanupHistoryEntry entry, CancellationToken cancellationToken)
    {
        _historyLog.Record(entry);
        var state = new CleanupHistoryState { Entries = [.. _historyLog.Entries] };
        try
        {
            await _context.Settings.SaveAsync("history", state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _context.Logger.Warn($"Не удалось сохранить историю очисток: {ex.Message}");
        }
    }

    private async Task EvaluateAutoCleanupAsync(MemoryUsage usage, CancellationToken cancellationToken)
    {
        if (!_settings.AutoClean || OperatingSystem.IsLinux())
            return;

        // Without elevation on Windows, silent background RunElevatedHelperAsync would pop UAC repeatedly.
        if (OperatingSystem.IsWindows() && !_context.Elevation.IsElevated)
            return;

        bool fullscreen = _settings.SkipAutoCleanInFullscreen && _fullscreenDetector.IsForegroundFullscreen();
        DateTime utcNow = DateTime.UtcNow;

        bool shouldRun = AutoCleanupPolicy.ShouldCleanSmart(
            usage.UsedPercent,
            _settings.AutoCleanThreshold,
            _settings.AutoCleanCooldownMinutes,
            utcNow,
            _lastAutoCleanUtc,
            fullscreen,
            _settings.SkipAutoCleanInFullscreen,
            _lastAutoCleanFreedBytes);

        if (!shouldRun)
            return;

        _lastAutoCleanUtc = utcNow;
        await RunCleanupAsync(new CleanupOptions(Standby: true, WorkingSets: false, ModifiedPages: false), automatic: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SyncLinuxAutoCleanMonitorAsync(bool enable, bool interactiveConfirmation, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            return;

        StopLinuxAutoCleanMonitor();
        if (!enable)
            return;

        if (interactiveConfirmation && !_context.Elevation.IsElevated)
        {
            bool confirmed = await _context.Dialogs.ConfirmAsync(
                "Фоновая автоочистка на Linux",
                "Для автоматического сброса системного кэша в фоне будет запущен привилегированный helper через pkexec. Он остановится автоматически при отключении опции или закрытии приложения.",
                "Включить",
                "Отмена",
                isDestructive: false,
                cancellationToken).ConfigureAwait(false);

            if (!confirmed)
            {
                _settings.AutoClean = false;
                await _context.Settings.SaveAsync("settings", _settings, cancellationToken).ConfigureAwait(false);
                NotifyStateChanged();
                return;
            }
        }

        int selfPid = Environment.ProcessId;
        if (!LinuxProcfs.TryReadStartTicks(selfPid, out ulong startTicks))
            return;

        string tempDir = GetTempDirectory();
        Directory.CreateDirectory(tempDir);
        string id = Guid.NewGuid().ToString("N");
        string markerPath = Path.Combine(tempDir, $"mempulse-auto-{id}.marker");
        string reportPath = Path.Combine(tempDir, $"autoclean-{id}.json");
        await File.WriteAllTextAsync(markerPath, "1", cancellationToken).ConfigureAwait(false);
        _linuxAutoMarkerPath = markerPath;

        var args = HelperCommandBuilder.BuildLinuxAutoCleanArgs(
            _settings.AutoCleanThreshold,
            _settings.AutoCleanCooldownMinutes,
            selfPid,
            startTicks,
            _context.ModuleDataDirectory,
            markerPath,
            reportPath);

        _ = Task.Run(async () =>
        {
            try
            {
                int exitCode = await _context.Elevation.RunElevatedHelperAsync(args, _disposeCts.Token).ConfigureAwait(false);
                if (exitCode != 0 && _settings.AutoClean && !_disposed)
                    _context.Logger.Warn($"Фоновый helper автоочистки завершился с кодом {exitCode}.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _context.Logger.Error("Ошибка фонового helper автоочистки на Linux.", ex);
            }
        }, _disposeCts.Token);
    }

    private void StopLinuxAutoCleanMonitor()
    {
        if (!string.IsNullOrEmpty(_linuxAutoMarkerPath))
        {
            CleanupTempFile(_linuxAutoMarkerPath);
            _linuxAutoMarkerPath = null;
        }
    }

    private string GetTempDirectory() => Path.Combine(_context.ModuleDataDirectory, "tmp");

    private static void CleanupTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _ = RefreshOnceAsync(_disposeCts.Token);
    }

    private void NotifyStateChanged()
    {
        if (Application.Current is not null)
            Dispatcher.UIThread.Post(() => StateChanged?.Invoke());
        else
            StateChanged?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }

        StopLinuxAutoCleanMonitor();
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _cleanGate.Dispose();
        _refreshGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
