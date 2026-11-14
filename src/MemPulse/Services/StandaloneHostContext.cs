using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using MemPulse.Core;
using MemPulse.PulseModule;
using PCPulse.Contracts;

namespace MemPulse.Services;

internal sealed class StandaloneLogger : IPulseLogger
{
    public void Info(string message) => StartupLog.Info(message);
    public void Warn(string message) => StartupLog.Warn(message);
    public void Error(string message, Exception? exception = null)
    {
        if (exception is null)
            StartupLog.Warn("ERROR: " + message);
        else
            StartupLog.Error(message, exception);
    }
}

internal sealed class StandaloneSettingsStore : IPulseSettingsStore
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly SettingsStore _legacySettingsStore;
    private readonly string _dataDirectory;

    public StandaloneSettingsStore(string dataDirectory, IPulseLogger logger)
    {
        _dataDirectory = dataDirectory;
        _legacySettingsStore = new SettingsStore(
            SettingsStore.DefaultStandalonePath,
            (msg, ex) => logger.Error(msg, ex));
    }

    public Task<T?> LoadAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(key, "settings", StringComparison.OrdinalIgnoreCase) && typeof(T) == typeof(AppSettings))
        {
            var settings = _legacySettingsStore.Load();
            return Task.FromResult<T?>(settings as T);
        }

        string path = ResolveKeyPath(key);
        if (!File.Exists(path))
            return Task.FromResult<T?>(null);

        try
        {
            string json = File.ReadAllText(path);
            return Task.FromResult(JsonSerializer.Deserialize<T>(json, ReadOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Task.FromResult<T?>(null);
        }
    }

    public Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(key, "settings", StringComparison.OrdinalIgnoreCase) && value is AppSettings settings)
        {
            _legacySettingsStore.Save(settings);
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(_dataDirectory);
        string path = ResolveKeyPath(key);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, WriteOptions));
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = string.Equals(key, "settings", StringComparison.OrdinalIgnoreCase)
            ? SettingsStore.DefaultStandalonePath
            : ResolveKeyPath(key);

        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string ResolveKeyPath(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(['/', '\\', '.']) >= 0)
            throw new ArgumentException("Недопустимый ключ настроек.", nameof(key));
        return Path.Combine(_dataDirectory, key + ".json");
    }
}

internal sealed class StandaloneElevationService : IPulseElevationService
{
    private readonly IPulseLogger _logger;

    public StandaloneElevationService(IPulseLogger logger) => _logger = logger;

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public async Task<int> RunElevatedHelperAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        string helperPath = ResolveHelperPath();
        if (OperatingSystem.IsWindows())
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = true,
                Verb = IsElevated ? string.Empty : "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            try
            {
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Не удалось запустить MemPulse.Helper.exe.");
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                _logger.Warn("Запрос прав администратора (UAC) отменён пользователем.");
                return 1223;
            }
        }

        if (OperatingSystem.IsLinux())
        {
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            if (IsElevated)
            {
                startInfo.FileName = helperPath;
            }
            else
            {
                startInfo.FileName = "pkexec";
                startInfo.ArgumentList.Add(helperPath);
            }

            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить привилегированный helper через pkexec.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }

        throw new PlatformNotSupportedException("Повышение прав поддерживается только на Windows и Linux.");
    }

    internal static string ResolveHelperPath()
    {
        string fileName = OperatingSystem.IsWindows() ? "MemPulse.Helper.exe" : "MemPulse.Helper";
        string besideApp = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(besideApp))
            return besideApp;

        // Dev-build fallback inside repository tree:
        string? dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            foreach (string config in new[] { "Release", "Debug" })
            {
                string candidate = Path.Combine(dir, "MemPulse.Helper", "bin", config, "net10.0", fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"Не найден исполняемый файл helper: {besideApp}", besideApp);
    }
}

internal sealed class StandaloneHostContext : IPulseHostContext, IStandaloneHostBridge
{
    public StandaloneHostContext(
        IPulseDialogService dialogs,
        IPulseLogger logger,
        Action<MemoryUsage, AppSettings, bool> onSnapshotUpdated,
        Action<AppSettings> onSettingsChanged,
        Func<Task> onCheckUpdates,
        Func<Task> onOpenLogs,
        Action? onSummaryRefresh = null)
    {
        ModuleId = "mempulse";
        ModuleDataDirectory = Path.GetDirectoryName(SettingsStore.DefaultStandalonePath)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemPulse");
        ModuleInstallDirectory = AppContext.BaseDirectory;
        HostVersion = typeof(StandaloneHostContext).Assembly.GetName().Version ?? new Version(0, 2, 42);
        ContractVersion = PulseContractVersion.Current;

        Dialogs = dialogs;
        Logger = logger;
        Elevation = new StandaloneElevationService(logger);
        Settings = new StandaloneSettingsStore(ModuleDataDirectory, logger);

        _onSnapshotUpdated = onSnapshotUpdated;
        _onSettingsChanged = onSettingsChanged;
        _onCheckUpdates = onCheckUpdates;
        _onOpenLogs = onOpenLogs;
        _onSummaryRefresh = onSummaryRefresh;
    }

    private readonly Action<MemoryUsage, AppSettings, bool> _onSnapshotUpdated;
    private readonly Action<AppSettings> _onSettingsChanged;
    private readonly Func<Task> _onCheckUpdates;
    private readonly Func<Task> _onOpenLogs;
    private readonly Action? _onSummaryRefresh;

    public string ModuleId { get; }
    public string ModuleDataDirectory { get; }
    public string ModuleInstallDirectory { get; }
    public Version HostVersion { get; }
    public string ContractVersion { get; }
    public IPulseDialogService Dialogs { get; }
    public IPulseElevationService Elevation { get; }
    public IPulseLogger Logger { get; }
    public IPulseSettingsStore Settings { get; }

    public bool SupportsWindowsStartup => OperatingSystem.IsWindows();
    public bool SupportsTrayAndHotkey => true;
    public bool SupportsSelfUpdate => true;

    public void RequestSummaryRefresh() => _onSummaryRefresh?.Invoke();

    public void ApplyStartupSetting(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            WindowsStartupService.SetEnabled(enabled);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Не удалось обновить автозапуск Windows: {ex.Message}");
        }
    }

    public void OnSnapshotUpdated(MemoryUsage usage, AppSettings settings, bool isCleaning) =>
        _onSnapshotUpdated(usage, settings, isCleaning);

    public void OnSettingsChanged(AppSettings settings) =>
        _onSettingsChanged(settings);

    public Task CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        _onCheckUpdates();

    public Task OpenLogsDirectoryAsync(CancellationToken cancellationToken = default) =>
        _onOpenLogs();
}
