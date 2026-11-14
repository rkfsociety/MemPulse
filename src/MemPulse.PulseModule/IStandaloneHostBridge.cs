using MemPulse.Core;

namespace MemPulse.PulseModule;

/// <summary>
/// Опциональный контракт интеграции для standalone-оболочки MemPulse.
/// При запуске внутри хаба PCPulse хост не реализует этот интерфейс,
/// и модуль работает исключительно через стандартный IPulseHostContext.
/// </summary>
public interface IStandaloneHostBridge
{
    bool SupportsWindowsStartup { get; }
    bool SupportsTrayAndHotkey { get; }
    bool SupportsSelfUpdate { get; }

    void ApplyStartupSetting(bool enabled);
    void OnSnapshotUpdated(MemoryUsage usage, AppSettings settings, bool isCleaning);
    void OnSettingsChanged(AppSettings settings);

    Task CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task OpenLogsDirectoryAsync(CancellationToken cancellationToken = default);
}
