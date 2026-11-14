using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace MemPulse.Core;

/// <summary>
/// Best-effort local diagnostics for standalone mode. Logging failures must never prevent the UI from starting.
/// No telemetry or external upload is performed.
/// </summary>
public static class StartupLog
{
    private static readonly object Sync = new();
    private static string? _filePath;
    private static bool _initialized;
    private const long MaxLogBytes = 2 * 1024 * 1024;

    public static string FilePath
    {
        get
        {
            EnsureInitialized();
            return _filePath ?? "(журнал недоступен)";
        }
    }

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized) return;
            _initialized = true;
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
            var primary = Path.Combine(root, "MemPulse", "Logs");
            _filePath = TryPrepare(primary) ?? TryPrepare(Path.Combine(Path.GetTempPath(), "MemPulse", "Logs"));

            WriteCore("INFO", "=== START === " +
                "pid=" + Environment.ProcessId +
                "; version=" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown") +
                "; OS=" + Environment.OSVersion.VersionString +
                "; arch=" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture +
                "; 64bit=" + Environment.Is64BitProcess);
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message, Exception? ex = null) =>
        Write("WARN", ex is null ? message : message + Environment.NewLine + ex);
    public static void Error(string message, Exception ex) =>
        Write("ERROR", message + Environment.NewLine + ex);
    public static void Fatal(string message, Exception ex) =>
        Write("FATAL", message + Environment.NewLine + ex);

    private static void EnsureInitialized()
    {
        if (!_initialized) Initialize();
    }

    private static string? TryPrepare(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "startup-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
            using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }

            foreach (var old in Directory.GetFiles(directory, "startup-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7)) File.Delete(old);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void Write(string level, string message)
    {
        EnsureInitialized();
        lock (Sync) WriteCore(level, message);
    }

    private static void WriteCore(string level, string message)
    {
        var line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) +
            " [" + level + "] [pid:" + Environment.ProcessId + ", thread:" +
            Environment.CurrentManagedThreadId + "] " + message + Environment.NewLine;
        try
        {
            if (_filePath is not null)
            {
                if (File.Exists(_filePath) && new FileInfo(_filePath).Length > MaxLogBytes)
                {
                    string rotated = _filePath + ".old";
                    File.Move(_filePath, rotated, true);
                }
                File.AppendAllText(_filePath, line, Encoding.UTF8);
            }
            else Debug.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine("MemPulse logging failure: " + ex.Message);
            Debug.WriteLine(line);
        }
    }
}
