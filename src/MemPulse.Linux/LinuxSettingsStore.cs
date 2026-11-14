using MemPulse.Core;

namespace MemPulse.Linux;

/// <summary>Per-user Linux settings; delegates storage to MemPulse.Core.SettingsStore and never touches Windows Registry.</summary>
public sealed class LinuxSettingsStore
{
    private readonly SettingsStore _inner;

    public LinuxSettingsStore(string? path = null) =>
        _inner = new SettingsStore(
            path ?? SettingsPath,
            (_, ex) => Console.Error.WriteLine("Не удалось прочитать настройки MemPulse: " + ex.Message));

    public static string SettingsPath
    {
        get
        {
            string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config) || !Path.IsPathFullyQualified(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "MemPulse", "linux-settings.json");
        }
    }

    public AppSettings Load() => _inner.Load();

    public void Save(AppSettings settings) => _inner.Save(settings);
}
